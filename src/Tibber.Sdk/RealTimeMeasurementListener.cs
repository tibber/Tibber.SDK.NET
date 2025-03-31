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
        private static readonly Random Random = new();

        private const int StreamReSubscriptionCheckPeriodMs = 60000;

        private readonly TibberApiClient _tibberApiClient;

        private readonly Dictionary<Guid, HomeStreamObserverCollection> _homeObservables = new();
        private readonly ArraySegment<byte> _receiveBuffer = new(new byte[16384]);
        private readonly CancellationTokenSource _cancellationTokenSource = new();
        private readonly SemaphoreSlim _semaphore = new(0);

        private readonly string _accessToken;
        private readonly Timer _streamRestartTimer;

        private Uri _websocketSubscriptionUrl;
        private ClientWebSocket _wssClient;
        private bool _isInitialized;
        private bool _isDisposed;
        private int _streamId;

        public RealTimeMeasurementListener(TibberApiClient tibberApiClient, Uri websocketSubscriptionUrl, string accessToken)
        {
            _tibberApiClient = tibberApiClient;
            _accessToken = accessToken;
            _websocketSubscriptionUrl = websocketSubscriptionUrl;

            _streamRestartTimer = new Timer(CheckDataStreamAlive, null, -1, 0);
        }

        public async Task<IObservable<RealTimeMeasurement>> SubscribeHome(Guid homeId, CancellationToken cancellationToken, TibberApiSubscriptionQueryBuilder queryBuilder = null)
        {
            CheckObjectNotDisposed();

            int subscriptionId;
            bool shouldInitialize;
            HomeRealTimeMeasurementObservable observable;
            lock (_homeObservables)
            {
                shouldInitialize = !_homeObservables.Any();

                if (_homeObservables.TryGetValue(homeId, out var collection))
                    throw new InvalidOperationException($"Home {homeId} is already subscribed. ");

                subscriptionId = Interlocked.Increment(ref _streamId);
                _homeObservables.Add(homeId, collection = new HomeStreamObserverCollection { Observable = new HomeRealTimeMeasurementObservable(this, homeId, subscriptionId) });

                observable = collection.Observable;
            }

            try
            {
                if (shouldInitialize)
                {
                    await Initialize(_websocketSubscriptionUrl, cancellationToken);
                    StartListening();

                    _streamRestartTimer.Change(StreamReSubscriptionCheckPeriodMs, 5000);
                }

                await SubscribeStream(homeId, subscriptionId, cancellationToken, queryBuilder);

                if (!observable.IsInitialized)
                    throw new InvalidOperationException($"real-time measurement subscription initialization failed{(observable.ErrorMessage is null ? null : $": {observable.ErrorMessage}")}");
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
                        throw new ArgumentException("Observer has been subscribed already.", nameof(observer));
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

        private async Task ResubscribeStream(Guid homeId, int subscriptionId, CancellationToken cancellationToken)
        {
            await UnsubscribeStream(subscriptionId, cancellationToken);
            await SubscribeStream(homeId, subscriptionId, cancellationToken);
        }

        private async Task SubscribeStream(Guid homeId, int subscriptionId, CancellationToken cancellationToken, TibberApiSubscriptionQueryBuilder queryBuilder = null)
        {
            Trace.WriteLine($"subscribe to home id {homeId} with subscription id {subscriptionId}");

            queryBuilder ??= new TibberApiSubscriptionQueryBuilder().WithLiveMeasurement(new LiveMeasurementQueryBuilder().WithAllScalarFields(), homeId);
            var query = queryBuilder.Build().Replace(@"""", @"\""");
            await ExecuteStreamRequest($@"{{""payload"":{{""query"":""{query}"",""variables"":{{}},""extensions"":{{}}}},""type"":""subscribe"",""id"":""{subscriptionId}""}}", cancellationToken);

            if (cancellationToken == default)
                await _semaphore.WaitAsync(30000);
            else
                await _semaphore.WaitAsync(cancellationToken);
        }

        private async Task UnsubscribeStream(int subscriptionId, CancellationToken cancellationToken)
        {
            Trace.WriteLine($"unsubscribe subscription with id {subscriptionId}");
            await ExecuteStreamRequest($@"{{""type"":""complete"",""id"":""{subscriptionId}""}}", cancellationToken);
        }

        private Task ExecuteStreamRequest(string request, CancellationToken cancellationToken)
        {
            Trace.WriteLine($"send message; client state {_wssClient.State} {_wssClient.CloseStatus} {_wssClient.CloseStatusDescription} {request}");
            var requestBytes = new ArraySegment<byte>(Encoding.ASCII.GetBytes(request));
            return _wssClient.SendAsync(requestBytes, WebSocketMessageType.Text, true, cancellationToken);
        }

        private async Task Initialize(Uri websocketSubscriptionUrl, CancellationToken cancellationToken)
        {
            const string webSocketSubProtocol = "graphql-transport-ws";

            _wssClient?.Dispose();
            _wssClient = new ClientWebSocket();
            _wssClient.Options.AddSubProtocol(webSocketSubProtocol);

            var connectionInitPayload = new WebSocketConnectionInitPayload { Token = _accessToken };
#if !NETFRAMEWORK
            _wssClient.Options.SetRequestHeader("User-Agent", TibberApiClient.UserAgent.ToString());
#else
            connectionInitPayload.UserAgent = TibberApiClient.UserAgent.ToString();
#endif
            await _wssClient.ConnectAsync(websocketSubscriptionUrl, cancellationToken);

            Trace.WriteLine("web socket connected");

            var connectionInitMessage = new WebSocketConnectionInitMessage { Payload = connectionInitPayload };
            var json = JsonConvert.SerializeObject(connectionInitMessage, TibberApiClient.JsonSerializerSettings);
            var init = new ArraySegment<byte>(Encoding.UTF8.GetBytes(json));

            await _wssClient.SendAsync(init, WebSocketMessageType.Text, true, cancellationToken);

            Trace.WriteLine("web socket initialization message sent");

            var result = await _wssClient.ReceiveAsync(_receiveBuffer, cancellationToken);
            if (result.CloseStatus.HasValue)
                throw new InvalidOperationException($"web socket initialization failed: {result.CloseStatus}");

            json = Encoding.ASCII.GetString(_receiveBuffer.Array, 0, result.Count);
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
                        Trace.WriteLine($"receive message; client state {_wssClient.State} {_wssClient.CloseStatus} {_wssClient.CloseStatusDescription}");
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
                                ResubscribeStreams(c => true);
                                continue;
                            }
                        }
                    }

                    Dispose();
                    return;
                }

                var stringRecords = stringBuilder.ToString();

                stringBuilder.Clear();

                var measurementGroups = stringRecords.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(JsonConvert.DeserializeObject<WebSocketMessage>).GroupBy(m => m.Id);

                foreach (var measurementGroup in measurementGroups)
                {
                    HomeStreamObserverCollection homeStreamObserverCollection;
                    lock (_homeObservables)
                        homeStreamObserverCollection = _homeObservables.Values.SingleOrDefault(v => v.Observable.SubscriptionId == measurementGroup.Key);

                    if (homeStreamObserverCollection is null)
                        continue;

                    homeStreamObserverCollection.LastMessageReceivedAt = DateTimeOffset.UtcNow;
                    homeStreamObserverCollection.ReconnectionAttempts = 0;

                    foreach (var message in measurementGroup)
                    {
                        switch (message.Type)
                        {
                            case "next":
                                if (!homeStreamObserverCollection.Observable.IsInitialized)
                                {
                                    homeStreamObserverCollection.Observable.Initialize();
                                    _semaphore.Release();
                                }

                                if (message.Payload?.Errors?.Count > 0)
                                {
                                    foreach (var error in message.Payload.Errors)
                                        homeStreamObserverCollection.Observable.Error(error.Message);

                                    continue;
                                }

                                var measurement = message.Payload?.Data?.RealTimeMeasurement;
                                if (measurement is null)
                                    continue;

                                ExecuteObserverAction(homeStreamObserverCollection.Observers.ToArray(), o => o.OnNext(measurement));

                                break;

                            case "complete":
                                ExecuteObserverAction(homeStreamObserverCollection.Observers.ToArray(), o => o.OnCompleted());
                                lock (_homeObservables)
                                    _homeObservables.Remove(homeStreamObserverCollection.Observable.HomeId);

                                break;

                            case "error":
                                Trace.WriteLine($"web socket error message received: {String.Join("; ", message.Payload.Errors.Select(e => e.Message))}");
                                foreach (var error in message.Payload.Errors)
                                    homeStreamObserverCollection.Observable.Error(error.Message);

                                _semaphore.Release();
                                break;
                        }
                    }
                }
            } while (!_cancellationTokenSource.IsCancellationRequested);
        }

        private void ResubscribeStreams(Func<HomeStreamObserverCollection, bool> predicate)
        {
            lock (_homeObservables)
            {
                var subscriptionTask = (Task)Task.FromResult(0);
                foreach (var collection in _homeObservables.Values.Where(predicate))
                    subscriptionTask = subscriptionTask.ContinueWith(_ => ResubscribeStream(collection.Observable.HomeId, collection.Observable.SubscriptionId, _cancellationTokenSource.Token));
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

            if (_wssClient.State is WebSocketState.Open or WebSocketState.CloseReceived or WebSocketState.CloseSent)
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
                    var delay = GetDelay(failures);
                    Trace.WriteLine($"retrying to connect in {delay.TotalSeconds} seconds");
                    await Task.Delay(delay, _cancellationTokenSource.Token);

                    Trace.WriteLine("check there is a valid real time device");
                    var homes = await _tibberApiClient.ValidateRealtimeDevice();
                    _websocketSubscriptionUrl = new Uri(homes.Data.Viewer.WebsocketSubscriptionUrl);

                    Trace.WriteLine("retrying to connect in...");
                    await Initialize(_websocketSubscriptionUrl, _cancellationTokenSource.Token);
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

            ResubscribeStreams(c =>
            {
                var sinceLastMessageMs = (now - c.LastMessageReceivedAt).TotalMilliseconds;
                if (sinceLastMessageMs <= StreamReSubscriptionCheckPeriodMs)
                    return false;

                // Data not received during past minute; delay exponentially and then resubscribe
                var sinceLastReconnectionMs = (now - c.LastReconnectionAttemptAt).TotalMilliseconds;
                var delay = GetDelay(c.ReconnectionAttempts);
                if (sinceLastReconnectionMs <= delay.TotalMilliseconds)
                {
                    Trace.WriteLine(
                        $"{now:yyyy-MM-dd HH:mm:ss.fff zzz} home {c.Observable.HomeId} subscription {c.Observable.SubscriptionId}: no data received during last {sinceLastMessageMs:N0} ms; reconnection attempts {c.ReconnectionAttempts}; resubscription delay {delay.TotalSeconds}s not passed yet"
                    );
                    return false;
                }

                Trace.WriteLine(
                    $"{now:yyyy-MM-dd HH:mm:ss.fff zzz} home {c.Observable.HomeId} subscription {c.Observable.SubscriptionId}: no data received during last {sinceLastMessageMs:N0} ms; reconnection attempts {c.ReconnectionAttempts}; re-initialize data stream"
                );
                c.ReconnectionAttempts++;
                c.LastReconnectionAttemptAt = now;

                return true;
            });
        }

        private static TimeSpan GetDelay(int failures)
        {
            // Jitter of 5 to 60 seconds
            var jitter = Random.Next(5, 60);

            // Exponential backoff
            var delay = Math.Pow(failures, 2);

            // Max one day 60 * 60 * 24
            const double oneDayInSeconds = (double)60 * 60 * 24;
            return TimeSpan.FromSeconds(jitter + (int)Math.Min(delay, oneDayInSeconds));
        }

        private class WebSocketConnectionInitMessage
        {
            public string Type => "connection_init";
            public WebSocketConnectionInitPayload Payload { get; set; }
        }

        private class WebSocketConnectionInitPayload
        {
            public string Token { get; set; }
            public string UserAgent { get; set; }
        }

        private class WebSocketMessage
        {
            public int Id { get; set; }
            public string Type { get; set; }
            public WebSocketPayload Payload { get; set; }
        }

        private class WebSocketPayload : GraphQlResponse<WebSocketData> { }

        private class WebSocketData
        {
            [JsonProperty("liveMeasurement")]
            public RealTimeMeasurement RealTimeMeasurement { get; set; }

            [JsonProperty("testMeasurement")]
            public RealTimeMeasurement TestMeasurement
            {
                set { RealTimeMeasurement = value; }
            }
        }

        private class HomeStreamObserverCollection
        {
            public readonly List<IObserver<RealTimeMeasurement>> Observers = new();
            public HomeRealTimeMeasurementObservable Observable;
            public DateTimeOffset LastMessageReceivedAt = DateTimeOffset.MaxValue;
            public DateTimeOffset LastReconnectionAttemptAt = DateTimeOffset.MinValue;
            public int ReconnectionAttempts = 0;
        }

        private class Unsubscriber : IDisposable
        {
            private readonly Action _unsubscribeAction;

            public Unsubscriber(Action unsubscribeAction) => _unsubscribeAction = unsubscribeAction;

            public void Dispose() => _unsubscribeAction();
        }
    }
}
