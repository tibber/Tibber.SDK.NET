using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Tibber.Client
{
    internal class LiveMeasurementListener : IObservable<LiveMeasurement>, IDisposable
    {
        private readonly ArraySegment<byte> _receiveBuffer = new ArraySegment<byte>(new byte[4096]);
        private readonly HashSet<IObserver<LiveMeasurement>> _liveMeasurementObservers = new HashSet<IObserver<LiveMeasurement>>();
        private readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();

        private readonly string _accessToken;
        private readonly Guid _homeId;

        private ClientWebSocket _wssClient;

        public LiveMeasurementListener(string accessToken, Guid homeId)
        {
            _accessToken = accessToken;
            _homeId = homeId;
        }

        public async Task Initialize(CancellationToken cancellationToken)
        {
            _wssClient?.Dispose();

            _wssClient = new ClientWebSocket();

            var init = new ArraySegment<byte>(Encoding.ASCII.GetBytes($@"{{""type"":""init"",""payload"":""token={_accessToken}""}}"));
            var subscriptionRequest =
                new ArraySegment<byte>(
                    Encoding.ASCII.GetBytes(
                        $@"{{""query"":""subscription{{liveMeasurement(homeId:\""{_homeId}\""){{timestamp,power,accumulatedConsumption,accumulatedCost,currency,minPower,averagePower,maxPower}}}}"",""variables"":null,""type"":""subscription_start"",""id"":0}}"));

            _wssClient.Options.SetRequestHeader("Sec-WebSocket-Protocol", "graphql-subscriptions");
            await _wssClient.ConnectAsync(new Uri($"{TibberApiClient.BaseUrl.Replace("https", "wss").Replace("http", "ws")}gql/subscriptions"), cancellationToken);
            await _wssClient.SendAsync(init, WebSocketMessageType.Text, true, cancellationToken);

            var result = await _wssClient.ReceiveAsync(_receiveBuffer, cancellationToken);
            if (result.CloseStatus.HasValue)
                throw new InvalidOperationException($"web socket initialization failed: {result.CloseStatus}");

            await _wssClient.SendAsync(subscriptionRequest, WebSocketMessageType.Text, true, cancellationToken);
            result = await _wssClient.ReceiveAsync(_receiveBuffer, cancellationToken);
            var data = Encoding.ASCII.GetString(_receiveBuffer.Array, 0, result.Count);
            var message = JsonConvert.DeserializeObject<WebSocketMessage>(data);

            if (!String.Equals(message.Type, "subscription_success"))
                throw new InvalidOperationException($"web socket initialization failed: {message.Payload?.Error ?? data}");

            StartListening();
        }

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
            if (_wssClient == null)
                throw new InvalidOperationException("Initialize the reader first. ");

            do
            {
                var stringBuilder = new StringBuilder();

                WebSocketReceiveResult result;

                do
                {
                    try
                    {
                        result = await _wssClient.ReceiveAsync(_receiveBuffer, _cancellationTokenSource.Token);
                    }
                    catch (WebSocketException exception) when (exception.InnerException is OperationCanceledException)
                    {
                        Dispose();
                        return;
                    }
                    catch (Exception exception)
                    {
                        foreach (var observer in _liveMeasurementObservers)
                            observer.OnError(exception);

                        Dispose();
                        return;
                    }

                    var json = Encoding.ASCII.GetString(_receiveBuffer.Array, 0, result.Count);
                    stringBuilder.Append(json);
                } while (!result.EndOfMessage);

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
            foreach (var observer in _liveMeasurementObservers)
                observer.OnCompleted();

            _cancellationTokenSource.Dispose();
            _wssClient.Dispose();
        }

        private class Unsubscriber : IDisposable
        {
            private readonly Action _unsubscribeAction;

            public Unsubscriber(Action unsubscribeAction) => _unsubscribeAction = unsubscribeAction;

            public void Dispose() => _unsubscribeAction();
        }
    }

    public class WebSocketMessage
    {
        public string Id { get; set; }
        public string Type { get; set; }
        public WebSocketPayload Payload { get; set; }
    }

    public class WebSocketPayload
    {
        public WebSocketData Data { get; set; }
        public string Error { get; set; }
    }

    public class WebSocketData
    {
        public LiveMeasurement LiveMeasurement { get; set; }
    }

    public class LiveMeasurement
    {
        /// <summary>
        /// When usage occured
        /// </summary>
        public DateTimeOffset Timestamp { get; set; }
        /// <summary>
        /// Wattage consumed
        /// </summary>
        public decimal Power { get; set; }
        /// <summary>
        /// kWh consumed since midnight
        /// </summary>
        public decimal AccumulatedConsumption { get; set; }
        /// <summary>
        /// Accumulated cost since midnight
        /// </summary>
        public decimal? AccumulatedCost { get; set; }
        /// <summary>
        /// Currency of displayed cost
        /// </summary>
        public string Currency { get; set; }
        /// <summary>
        /// Min power since midnight
        /// </summary>
        public decimal MinPower { get; set; }
        /// <summary>
        /// Average power since midnight
        /// </summary>
        public decimal AveragePower { get; set; }
        /// <summary>
        /// Max power since midnight
        /// </summary>
        public decimal MaxPower { get; set; }
    }
}
