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
    public class TibberLiveMeasurementReader : IDisposable
    {
        private readonly ArraySegment<byte> _receiveBuffer = new ArraySegment<byte>(new byte[4096]);

        private readonly string _accessToken;
        private readonly Guid _homeId;

        private ClientWebSocket _wssClient;

        internal TibberLiveMeasurementReader(string accessToken, Guid homeId)
        {
            _accessToken = accessToken;
            _homeId = homeId;
        }

        internal async Task Initialize(CancellationToken cancellationToken)
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
            var json = Encoding.ASCII.GetString(_receiveBuffer.Array, 0, result.Count);
            var message = JsonConvert.DeserializeObject<WebSocketMessage>(json);

            if (!String.Equals(message.Type, "subscription_success"))
                throw new InvalidOperationException($"web socket initialization failed: {message.Payload?.Error ?? json}");
        }

        public async Task<IEnumerable<LiveMeasurement>> GetNewValues(CancellationToken cancellationToken = default)
        {
            if (_wssClient == null)
                throw new InvalidOperationException("Initialize the reader first. ");

            var stringBuilder = new StringBuilder();

            WebSocketReceiveResult result;

            do
            {
                result = await _wssClient.ReceiveAsync(_receiveBuffer, cancellationToken);
                var json = Encoding.ASCII.GetString(_receiveBuffer.Array, 0, result.Count);
                stringBuilder.Append(json);
            } while (!result.EndOfMessage);

            var stringRecords = stringBuilder.ToString();

            stringBuilder.Clear();

            return
                stringRecords
                    .Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(l => JsonConvert.DeserializeObject<WebSocketMessage>(l).Payload.Data.LiveMeasurement)
                    .Where(r => r.AccumulatedConsumption.HasValue);
        }

        public void Dispose() => _wssClient.Dispose();
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
        public DateTimeOffset? Timestamp { get; set; }
        public decimal? Power { get; set; }
        public decimal? AccumulatedConsumption { get; set; }
        public decimal? AccumulatedCost { get; set; }
        public string Currency { get; set; }
        public decimal? MinPower { get; set; }
        public decimal? AveragePower { get; set; }
        public decimal? MaxPower { get; set; }
    }
}
