using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Tibber.Sdk
{
    internal class HomeRealTimeMeasurementObservable : IObservable<RealTimeMeasurement>
    {
        private readonly RealTimeMeasurementListener _listener;

        public Guid HomeId { get; }
        public int SubscriptionId { get; }
        public bool IsInitialized { get; private set; }
        public string ErrorMessage { get; private set; }

        public HomeRealTimeMeasurementObservable(RealTimeMeasurementListener listener, Guid homeId, int subscriptionId)
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
        public IDisposable Subscribe(IObserver<RealTimeMeasurement> observer) => _listener.SubscribeObserver(this, observer);

        public void Initialize() => IsInitialized = true;

        public void Reset()
        {
            IsInitialized = false;
            ErrorMessage = null;
        }

        public void Error(string data)
        {
            ErrorMessage = data;
            Initialize();
        }
    }

    internal class RealTimeMeasurementListener : IDisposable
    {
        private const int StreamReSubscriptionCheckPeriodMs = 60000;

        private readonly Dictionary<Guid, HomeStreamObserverCollection> _homeObservables = new Dictionary<Guid, HomeStreamObserverCollection>();
        private readonly ArraySegment<byte> _receiveBuffer = new ArraySegment<byte>(new byte[16384]);
        private readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();
        private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(0);

        private readonly string _accessToken;
        private readonly Timer _streamRestartTimer;

        private ClientWebSocket _wssClient;
        private bool _isInitialized;
        private bool _isDisposed;
        private int _streamId;

        public RealTimeMeasurementListener(string accessToken)
        {
            _accessToken = accessToken;
            _streamRestartTimer = new Timer(CheckDataStreamAlive, null, -1, 0);
        }

        public async Task<IObservable<RealTimeMeasurement>> SubscribeHome(Guid homeId, CancellationToken cancellationToken)
        {
            CheckObjectNotDisposed();

            int subscriptionId;
            bool shouldInitialize;
            HomeRealTimeMeasurementObservable observable;
            lock (_homeObservables)
            {
                shouldInitialize = !_homeObservables.Any();

                if (_homeObservables.TryGetValue(homeId, out var collection))
                    throw new InvalidOperationException($"Home '{homeId}' is already subscribed. ");

                subscriptionId = Interlocked.Increment(ref _streamId);
                _homeObservables.Add(
                    homeId,
                    collection = new HomeStreamObserverCollection { Observable = new HomeRealTimeMeasurementObservable(this, homeId, subscriptionId) });

                observable = collection.Observable;
            }

            try
            {
                if (shouldInitialize)
                {
                    await Initialize(cancellationToken);
                    StartListening();
                    _streamRestartTimer.Change(StreamReSubscriptionCheckPeriodMs, 5000);
                }

                await SubscribeStream(homeId, subscriptionId, cancellationToken);

                if (!observable.IsInitialized)
                    throw new InvalidOperationException($"real-time measurement subscription initialization failed{(observable.ErrorMessage == null ? null : $": {observable.ErrorMessage}")}");
            }
            catch
            {
                lock (_homeObservables)
                    _homeObservables.Remove(homeId);

                throw;
            }

            return observable;
        }

        public async Task UnsubscribeHome(Guid homeId, CancellationToken cancellationToken)
        {
            CheckObjectNotDisposed();

            HomeRealTimeMeasurementObservable observable;

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

        public IDisposable SubscribeObserver(HomeRealTimeMeasurementObservable observable, IObserver<RealTimeMeasurement> observer)
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

        private void UnsubscribeObserver(HomeStreamObserverCollection collection, IObserver<RealTimeMeasurement> observer)
        {
            lock (_homeObservables)
                collection.Observers.Remove(observer);
        }

        private async Task SubscribeStream(Guid homeId, int subscriptionId, CancellationToken cancellationToken)
        {
            await ExecuteStreamRequest(
                $@"{{""payload"":{{""query"":""subscription{{liveMeasurement(homeId:\""{homeId}\""){{timestamp,power,powerReactive,powerProduction,powerProductionReactive,accumulatedConsumption,accumulatedConsumptionLastHour,accumulatedProduction,accumulatedProductionLastHour,accumulatedCost,accumulatedReward,currency,minPower,averagePower,maxPower,minPowerProduction,maxPowerProduction,voltagePhase1,voltagePhase2,voltagePhase3,currentL1,currentL2,currentL3,lastMeterConsumption,lastMeterProduction,powerFactor,signalStrength}}}}"",""variables"":{{}},""extensions"":{{}},""operationName"":null}},""type"":""start"",""id"":{subscriptionId}}}",
                cancellationToken);

            if (cancellationToken == default)
                await _semaphore.WaitAsync(30000);
            else
                await _semaphore.WaitAsync(cancellationToken);
        }

        private Task UnsubscribeStream(int subscriptionId, CancellationToken cancellationToken) =>
            ExecuteStreamRequest($@"{{""type"":""stop"",""id"":{subscriptionId}}}", cancellationToken);

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

            Trace.WriteLine("web socket connected");

            var init = new ArraySegment<byte>(Encoding.ASCII.GetBytes($@"{{""type"":""connection_init"",""payload"":""token={_accessToken}""}}"));
            await _wssClient.SendAsync(init, WebSocketMessageType.Text, true, cancellationToken);

            Trace.WriteLine("web socket initialization message sent");

            var result = await _wssClient.ReceiveAsync(_receiveBuffer, cancellationToken);
            if (result.CloseStatus.HasValue)
                throw new InvalidOperationException($"web socket initialization failed: {result.CloseStatus}");

            var json = Encoding.ASCII.GetString(_receiveBuffer.Array, 0, result.Count);
            var message = JsonConvert.DeserializeObject<WebSocketMessage>(json);
            if (message.Type != "connection_ack")
                throw new InvalidOperationException($"web socket initialization failed: {json}");

            _isInitialized = true;

            Trace.WriteLine("web socket initialization completed");
        }

        private async void StartListening()
        {
            if (!_isInitialized)
                throw new InvalidOperationException("Initialize the listener first. ");

            var stringBuilder = new StringBuilder();

            do
            {
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
                    Trace.WriteLine("web socket operation canceled");
                    return;
                }
                catch (Exception exception)
                {
                    Trace.WriteLine("web socket operation failed " + exception);

                    lock (_homeObservables)
                    {
                        foreach (var homeStreamObservableCollection in _homeObservables.Values)
                        {
                            homeStreamObservableCollection.LastMessageReceivedAt = DateTimeOffset.MaxValue;
                            ExecuteObserverAction(homeStreamObservableCollection.Observers.ToArray(), o => o.OnError(exception));
                        }
                    }

                    if (exception.InnerException is IOException)
                    {
                        if (!_cancellationTokenSource.IsCancellationRequested)
                        {
                            await TryReconnect();

                            if (!_cancellationTokenSource.IsCancellationRequested)
                            {
                                Trace.WriteLine("connection re-established; re-initialize data streams");
                                SubscribeStreams(c => true);
                                continue;
                            }
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

                    homeStreamObserverCollection.LastMessageReceivedAt = DateTimeOffset.UtcNow;

                    foreach (var message in measurementGroup)
                    {
                        switch (message.Type)
                        {
                            case "data":
                                if (!homeStreamObserverCollection.Observable.IsInitialized)
                                {
                                    homeStreamObserverCollection.Observable.Initialize();
                                    _semaphore.Release();
                                }

                                var measurement = message.Payload?.Data?.RealTimeMeasurement;
                                if (measurement == null)
                                    continue;

                                ExecuteObserverAction(homeStreamObserverCollection.Observers.ToArray(), o => o.OnNext(measurement));

                                break;

                            /*case "complete":
                                ExecuteObserverAction(homeStreamObserverCollection.Observers.ToArray(), o => o.OnCompleted());
                                lock (_homeObservables)
                                    _homeObservables.Remove(homeStreamObserverCollection.Observable.HomeId);

                                break;*/

                            case "error":
                                Trace.WriteLine("web socket error message received: " + message.Payload.Error);
                                homeStreamObserverCollection.Observable.Error(message.Payload.Error);
                                _semaphore.Release();
                                break;
                        }
                    }
                }

            } while (!_cancellationTokenSource.IsCancellationRequested);
        }

        private void SubscribeStreams(Func<HomeStreamObserverCollection, bool> predicate)
        {
            lock (_homeObservables)
            {
                var subscriptionTask = (Task)Task.FromResult(0);
                foreach (var collection in _homeObservables.Values.Where(predicate))
                    subscriptionTask = subscriptionTask.ContinueWith(t => SubscribeStream(collection.Observable.HomeId, collection.Observable.SubscriptionId, _cancellationTokenSource.Token));
            }
        }

        public void Dispose()
        {
            ICollection<IObserver<RealTimeMeasurement>> observers;

            lock (_homeObservables)
            {
                if (_isDisposed)
                    return;

                Trace.WriteLine("listener disposal started");

                _isDisposed = true;

                _cancellationTokenSource.Cancel();

                observers = _homeObservables.Values.SelectMany(c => c.Observers).ToArray();

                _homeObservables.Clear();
            }

            ExecuteObserverAction(observers, o => o.OnCompleted());

            _streamRestartTimer.Dispose();

            if (_wssClient.State == WebSocketState.Open || _wssClient.State == WebSocketState.CloseReceived || _wssClient.State == WebSocketState.CloseSent)
                try
                {
                    _wssClient.CloseAsync(WebSocketCloseStatus.NormalClosure, "closed by client", CancellationToken.None).GetAwaiter().GetResult();
                }
                catch
                {
                    // prevent any exception during disposal
                }

            _wssClient.Dispose();
            _cancellationTokenSource.Dispose();

            Trace.WriteLine("listener disposal finished");
        }

        private void CheckObjectNotDisposed()
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(RealTimeMeasurementListener));
        }

        private static void ExecuteObserverAction(IEnumerable<IObserver<RealTimeMeasurement>> observers, Action<IObserver<RealTimeMeasurement>> observerAction)
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
                    Trace.WriteLine("retrying to connect... ");
                    await Initialize(_cancellationTokenSource.Token);
                    return;
                }
                catch (Exception)
                {
                    failures++;
                }
            }
        }

        private void CheckDataStreamAlive(object state)
        {
            var now = DateTimeOffset.UtcNow;

            SubscribeStreams(
                c =>
                {
                    var sinceLastMessageMs = (now - c.LastMessageReceivedAt).TotalMilliseconds;
                    if (sinceLastMessageMs <= StreamReSubscriptionCheckPeriodMs)
                        return false;

                    Trace.WriteLine($"home {c.Observable.HomeId} subscription {c.Observable.SubscriptionId}: no data received during last {sinceLastMessageMs:N0} ms; re-initialize data stream");
                    c.LastMessageReceivedAt = now;
                    return true;
                });
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
            [JsonProperty("liveMeasurement")]
            public RealTimeMeasurement RealTimeMeasurement { get; set; }
        }

        private class HomeStreamObserverCollection
        {
            public readonly List<IObserver<RealTimeMeasurement>> Observers = new List<IObserver<RealTimeMeasurement>>();
            public HomeRealTimeMeasurementObservable Observable;
            public DateTimeOffset LastMessageReceivedAt = DateTimeOffset.MaxValue;
        }

        private class Unsubscriber : IDisposable
        {
            private readonly Action _unsubscribeAction;

            public Unsubscriber(Action unsubscribeAction) => _unsubscribeAction = unsubscribeAction;

            public void Dispose() => _unsubscribeAction();
        }
    }
}
