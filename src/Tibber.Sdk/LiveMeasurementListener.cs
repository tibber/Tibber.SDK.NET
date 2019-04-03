using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Tibber.Sdk
{
    internal class HomeLiveMeasurementObservable : IObservable<LiveMeasurement>
    {
        private readonly LiveMeasurementListener _listener;

        public Guid HomeId { get; }
        public int SubscriptionId { get; }
        public bool IsInitialized { get; private set; }

        public HomeLiveMeasurementObservable(LiveMeasurementListener listener, Guid homeId, int subscriptionId)
        {
            _listener = listener;
            HomeId = homeId;
            SubscriptionId = subscriptionId;
        }

        /// <inheritdoc />
        /// <summary>
        /// </summary>
        /// <param name="observer"></param>
        /// <exception cref="T:System.ArgumentException"></exception>
        /// <returns></returns>
        public IDisposable Subscribe(IObserver<LiveMeasurement> observer) => _listener.SubscribeObserver(this, observer);

        public void Initialize() => IsInitialized = true;
    }

    internal class LiveMeasurementListener : IDisposable
    {
        private readonly Dictionary<Guid, HomeStreamObserverCollection> _homeObservables = new Dictionary<Guid, HomeStreamObserverCollection>();
        private readonly ArraySegment<byte> _receiveBuffer = new ArraySegment<byte>(new byte[16384]);
        private readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();
        private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(0);

        private readonly string _accessToken;

        private ClientWebSocket _wssClient;
        private bool _isInitialized;
        private bool _isDisposed;
        private int _streamId;

        public LiveMeasurementListener(string accessToken)
        {
            _accessToken = accessToken;
        }

        public async Task<IObservable<LiveMeasurement>> SubscribeHome(Guid homeId, CancellationToken cancellationToken)
        {
            CheckObjectNotDisposed();

            int subscriptionId;
            bool shouldInitialize;
            HomeLiveMeasurementObservable observable;
            lock (_homeObservables)
            {
                shouldInitialize = !_homeObservables.Any();

                if (_homeObservables.TryGetValue(homeId, out var collection))
                    throw new InvalidOperationException($"Home '{homeId}' is already subscribed. ");

                subscriptionId = Interlocked.Increment(ref _streamId);
                _homeObservables.Add(
                    homeId,
                    collection = new HomeStreamObserverCollection { Observable = new HomeLiveMeasurementObservable(this, homeId, subscriptionId) });

                observable = collection.Observable;
            }

            if (shouldInitialize)
            {
                await Initialize(cancellationToken);
                StartListening();
            }

            await SubscribeStream(homeId, subscriptionId, cancellationToken);

            if (!observable.IsInitialized)
                throw new InvalidOperationException("live measurement subscription initialization failed");

            return observable;
        }

        public async Task UnsubscribeHome(Guid homeId, CancellationToken cancellationToken)
        {
            CheckObjectNotDisposed();

            HomeLiveMeasurementObservable observable;

            lock (_homeObservables)
            {
                if (!_homeObservables.TryGetValue(homeId, out var collection))
                    return;

                _homeObservables.Remove(homeId);

                ExecuteObserverAction(collection.Observers, o => o.OnCompleted());

                observable = collection.Observable;
            }

            await UnsubscribeStream(observable.SubscriptionId, cancellationToken);
        }

        public IDisposable SubscribeObserver(HomeLiveMeasurementObservable observable, IObserver<LiveMeasurement> observer)
        {
            lock (_homeObservables)
            {
                foreach (var homeObserverCollection in _homeObservables.Values)
                {
                    if (homeObserverCollection.Observers.Contains(observer))
                        throw new ArgumentException("Observer has been subscribed already. ", nameof(observer));
                }

                var collection = _homeObservables[observable.HomeId];
                collection.Observers.Add(observer);
                return new Unsubscriber(() => UnsubscribeObserver(collection, observer));
            }
        }

        private void UnsubscribeObserver(HomeStreamObserverCollection collection, IObserver<LiveMeasurement> observer)
        {
            lock (_homeObservables)
                collection.Observers.Remove(observer);
        }

        private async Task SubscribeStream(Guid homeId, int subscriptionId, CancellationToken cancellationToken)
        {
            await ExecuteStreamRequest(
                $@"{{""query"":""subscription{{liveMeasurement(homeId:\""{homeId}\""){{timestamp,power,powerProduction,accumulatedConsumption,accumulatedProduction,accumulatedCost,accumulatedReward,currency,minPower,averagePower,maxPower,voltagePhase1,voltagePhase2,voltagePhase3,currentPhase1,currentPhase2,currentPhase3,lastMeterConsumption,lastMeterProduction}}}}"",""variables"":null,""type"":""subscription_start"",""id"":{subscriptionId}}}",
                cancellationToken);

            await _semaphore.WaitAsync(cancellationToken);
        }

        private Task UnsubscribeStream(int subscriptionId, CancellationToken cancellationToken) =>
            ExecuteStreamRequest($@"{{""type"":""subscription_end"",""id"":{subscriptionId}}}", cancellationToken);

        private Task ExecuteStreamRequest(string request, CancellationToken cancellationToken)
        {
            var stopSubscriptionRequest = new ArraySegment<byte>(Encoding.ASCII.GetBytes(request));
            return _wssClient.SendAsync(stopSubscriptionRequest, WebSocketMessageType.Text, true, cancellationToken);
        }

        private async Task Initialize(CancellationToken cancellationToken)
        {
            const string webSocketSubProtocol = "graphql-subscriptions";

            _wssClient?.Dispose();
            _wssClient = new ClientWebSocket();
            _wssClient.Options.AddSubProtocol(webSocketSubProtocol);
            _wssClient.Options.SetRequestHeader("Sec-WebSocket-Protocol", webSocketSubProtocol);
            await _wssClient.ConnectAsync(new Uri($"{TibberApiClient.BaseUrl.Replace("https", "wss").Replace("http", "ws")}gql/subscriptions"), cancellationToken);

            var init = new ArraySegment<byte>(Encoding.ASCII.GetBytes($@"{{""type"":""init"",""payload"":""token={_accessToken}""}}"));
            await _wssClient.SendAsync(init, WebSocketMessageType.Text, true, cancellationToken);

            var result = await _wssClient.ReceiveAsync(_receiveBuffer, cancellationToken);
            if (result.CloseStatus.HasValue)
                throw new InvalidOperationException($"web socket initialization failed: {result.CloseStatus}");

            _isInitialized = true;
        }

        private async void StartListening()
        {
            if (!_isInitialized)
                throw new InvalidOperationException("Initialize the listener first. ");

            do
            {
                var stringBuilder = new StringBuilder();

                try
                {
                    WebSocketReceiveResult result;

                    do
                    {
                        result = await _wssClient.ReceiveAsync(_receiveBuffer, _cancellationTokenSource.Token);
                        var json = Encoding.ASCII.GetString(_receiveBuffer.Array, 0, result.Count);
                        stringBuilder.Append(json);
                    } while (!result.EndOfMessage);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception exception)
                {
                    lock (_homeObservables)
                    {
                        foreach (var homeStreamObservableCollection in _homeObservables.Values)
                            ExecuteObserverAction(homeStreamObservableCollection.Observers.ToArray(), o => o.OnError(exception));
                    }

                    if (exception.InnerException is IOException)
                    {
                        await TryReconnect();

                        if (!_cancellationTokenSource.IsCancellationRequested)
                        {
                            lock (_homeObservables)
                            {
                                var subscriptionTask = (Task)Task.FromResult(0);
                                foreach (var collection in _homeObservables.Values)
                                    subscriptionTask = subscriptionTask.ContinueWith(t => SubscribeStream(collection.Observable.HomeId, collection.Observable.SubscriptionId, _cancellationTokenSource.Token));
                            }

                            continue;
                        }
                    }

                    Dispose();
                    return;
                }

                var stringRecords = stringBuilder.ToString();

                stringBuilder.Clear();

                var measurementGroups =
                    stringRecords
                        .Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(JsonConvert.DeserializeObject<WebSocketMessage>)
                        .GroupBy(m => m.Id);

                foreach (var measurementGroup in measurementGroups)
                {
                    HomeStreamObserverCollection homeStreamObserverCollection;
                    lock (_homeObservables)
                        homeStreamObserverCollection = _homeObservables.Values.SingleOrDefault(v => v.Observable.SubscriptionId == measurementGroup.Key);

                    if (homeStreamObserverCollection == null)
                        continue;

                    foreach (var message in measurementGroup)
                    {
                        switch (message.Type)
                        {
                            case "subscription_data":
                                var measurement = message.Payload?.Data?.LiveMeasurement;
                                if (measurement == null)
                                    continue;

                                ExecuteObserverAction(homeStreamObserverCollection.Observers.ToArray(), o => o.OnNext(measurement));

                                break;
                            case "subscription_success":
                                homeStreamObserverCollection.Observable.Initialize();
                                _semaphore.Release();
                                break;
                            case "subscription_fail":
                                _semaphore.Release();
                                break;
                        }
                    }
                }

            } while (!_cancellationTokenSource.IsCancellationRequested);
        }

        public void Dispose()
        {
            ICollection<IObserver<LiveMeasurement>> observers;

            lock (_homeObservables)
            {
                if (_isDisposed)
                    return;

                _isDisposed = true;

                _cancellationTokenSource.Cancel();

                observers = _homeObservables.Values.SelectMany(c => c.Observers).ToArray();

                _homeObservables.Clear();
            }

            ExecuteObserverAction(observers, o => o.OnCompleted());

            _wssClient.Dispose();
            _cancellationTokenSource.Dispose();
        }

        private void CheckObjectNotDisposed()
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(LiveMeasurementListener));
        }

        private static void ExecuteObserverAction(IEnumerable<IObserver<LiveMeasurement>> observers, Action<IObserver<LiveMeasurement>> observerAction)
        {
            foreach (var observer in observers)
                try
                {
                    observerAction(observer);
                }
                catch (Exception)
                {
                    // disposing not supposed to throw
                }
        }

        private async Task TryReconnect()
        {
            var failures = 0;

            while (!_cancellationTokenSource.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(GetDelaySeconds(failures)));
                    await Initialize(_cancellationTokenSource.Token);
                    return;
                }
                catch (Exception)
                {
                    failures++;
                }
            }
        }

        private static int GetDelaySeconds(int failures)
        {
            if (failures == 0)
                return 0;

            if (failures == 1)
                return 1;

            if (failures <= 3)
                return 5;

            return 60;
        }

        private class WebSocketMessage
        {
            public int Id { get; set; }
            public string Type { get; set; }
            public WebSocketPayload Payload { get; set; }
        }

        private class WebSocketPayload
        {
            public WebSocketData Data { get; set; }
            public string Error { get; set; }
        }

        private class WebSocketData
        {
            public LiveMeasurement LiveMeasurement { get; set; }
        }

        private class HomeStreamObserverCollection
        {
            public HomeLiveMeasurementObservable Observable;
            public readonly List<IObserver<LiveMeasurement>> Observers = new List<IObserver<LiveMeasurement>>();
        }

        private class Unsubscriber : IDisposable
        {
            private readonly Action _unsubscribeAction;

            public Unsubscriber(Action unsubscribeAction) => _unsubscribeAction = unsubscribeAction;

            public void Dispose() => _unsubscribeAction();
        }
    }
}
