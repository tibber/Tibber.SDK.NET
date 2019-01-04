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
    internal class LiveMeasurementListener : IObservable<LiveMeasurement>, IDisposable
    {
        private readonly ArraySegment<byte> _receiveBuffer = new ArraySegment<byte>(new byte[16384]);
        private readonly HashSet<IObserver<LiveMeasurement>> _liveMeasurementObservers = new HashSet<IObserver<LiveMeasurement>>();
        private readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();
        private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1);

        private readonly string _accessToken;
        private readonly Guid _homeId;

        private ClientWebSocket _wssClient;
        private bool _isInitialized;
        private bool _isDisposed;

        public LiveMeasurementListener(string accessToken, Guid homeId)
        {
            _accessToken = accessToken;
            _homeId = homeId;
        }

        public async Task Initialize(CancellationToken cancellationToken)
        {
            await _semaphore.WaitAsync(cancellationToken);

            if (_isInitialized)
                throw new InvalidOperationException("listener already initialized");

            try
            {
                await InitializeInternal(cancellationToken);
            }
            finally
            {
                _semaphore.Release();
            }

            StartListening();
        }

        private async Task InitializeInternal(CancellationToken cancellationToken)
        {
            _wssClient?.Dispose();
            _wssClient = new ClientWebSocket();
            _wssClient.Options.SetRequestHeader("Sec-WebSocket-Protocol", "graphql-subscriptions");
            await _wssClient.ConnectAsync(new Uri($"{TibberApiClient.BaseUrl.Replace("https", "wss").Replace("http", "ws")}gql/subscriptions"), cancellationToken);

            var init = new ArraySegment<byte>(Encoding.ASCII.GetBytes($@"{{""type"":""init"",""payload"":""token={_accessToken}""}}"));
            await _wssClient.SendAsync(init, WebSocketMessageType.Text, true, cancellationToken);

            var result = await _wssClient.ReceiveAsync(_receiveBuffer, cancellationToken);
            if (result.CloseStatus.HasValue)
                throw new InvalidOperationException($"web socket initialization failed: {result.CloseStatus}");

            var subscriptionRequest =
                new ArraySegment<byte>(
                    Encoding.ASCII.GetBytes(
                        $@"{{""query"":""subscription{{liveMeasurement(homeId:\""{_homeId}\""){{timestamp,power,accumulatedConsumption,accumulatedCost,currency,minPower,averagePower,maxPower,voltagePhase1,voltagePhase2,voltagePhase3,currentPhase1,currentPhase2,currentPhase3}}}}"",""variables"":null,""type"":""subscription_start"",""id"":0}}"));

            await _wssClient.SendAsync(subscriptionRequest, WebSocketMessageType.Text, true, cancellationToken);
            result = await _wssClient.ReceiveAsync(_receiveBuffer, cancellationToken);
            var data = Encoding.ASCII.GetString(_receiveBuffer.Array, 0, result.Count);
            var message = JsonConvert.DeserializeObject<WebSocketMessage>(data);

            if (!String.Equals(message.Type, "subscription_success"))
                throw new InvalidOperationException($"web socket initialization failed: {message.Payload?.Error ?? data}");

            _isInitialized = true;
        }

        /// <inheritdoc />
        /// <summary>
        /// </summary>
        /// <param name="observer"></param>
        /// <exception cref="T:System.ArgumentException"></exception>
        /// <returns></returns>
        public IDisposable Subscribe(IObserver<LiveMeasurement> observer)
        {
            lock (_liveMeasurementObservers)
            {
                if (!_liveMeasurementObservers.Add(observer))
                    throw new ArgumentException("Observer has been subscribed already. ", nameof(observer));

                return new Unsubscriber(() => _liveMeasurementObservers.Remove(observer));
            }
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
                    foreach (var observer in _liveMeasurementObservers)
                        observer.OnError(exception);

                    if (exception.InnerException is IOException)
                    {
                        await TryReconnect();

                        if (!_cancellationTokenSource.IsCancellationRequested)
                            continue;
                    }

                    Dispose();
                    return;
                }

                var stringRecords = stringBuilder.ToString();

                stringBuilder.Clear();

                var measurements =
                    stringRecords
                        .Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(l => JsonConvert.DeserializeObject<WebSocketMessage>(l).Payload?.Data?.LiveMeasurement);

                foreach (var measurement in measurements.Where(m => m != null))
                foreach (var observer in _liveMeasurementObservers)
                    observer.OnNext(measurement);

            } while (!_cancellationTokenSource.IsCancellationRequested);
        }

        public void Dispose()
        {
            ICollection<IObserver<LiveMeasurement>> observers;

            lock (_liveMeasurementObservers)
            {
                if (_isDisposed)
                    return;

                _isDisposed = true;

                _cancellationTokenSource.Cancel();

                observers = _liveMeasurementObservers.ToArray();
                _liveMeasurementObservers.Clear();
            }

            foreach (var observer in observers)
                try
                {
                    observer.OnCompleted();
                }
                catch (Exception)
                {
                    // disposing not supposed to throw
                }

            _wssClient.Dispose();
            _cancellationTokenSource.Dispose();
        }

        private async Task TryReconnect()
        {
            var failures = 0;

            while (!_cancellationTokenSource.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(GetDelaySeconds(failures)));
                    await InitializeInternal(_cancellationTokenSource.Token);
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

        private class Unsubscriber : IDisposable
        {
            private readonly Action _unsubscribeAction;

            public Unsubscriber(Action unsubscribeAction) => _unsubscribeAction = unsubscribeAction;

            public void Dispose() => _unsubscribeAction();
        }

        private class WebSocketMessage
        {
            public string Id { get; set; }
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
    }
}
