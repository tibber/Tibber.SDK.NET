using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;

namespace Tibber.Client
{
    /// <summary>
    /// GraphQL client towards Tibber API
    /// </summary>
    public class TibberApiClient : IDisposable
    {
        public const string BaseUrl = "https://api.tibber.com/v1-beta/";

        private static readonly SemaphoreSlim Semaphore = new SemaphoreSlim(1);
        private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(59);
        private static readonly JsonSerializerSettings JsonSerializerSettings =
            new JsonSerializerSettings
            {
                ContractResolver = new CamelCasePropertyNamesContractResolver(),
                Converters = { new StringEnumConverter() },
                DateParseHandling = DateParseHandling.DateTimeOffset
            };

        private static readonly JsonSerializer Serializer = JsonSerializer.Create(JsonSerializerSettings);

        private readonly Dictionary<Guid, LiveMeasurementListener> _liveMeasurementListeners = new Dictionary<Guid, LiveMeasurementListener>();

        private readonly HttpClient _httpClient;
        private readonly string _accessToken;

        public TibberApiClient(string accessToken, HttpMessageHandler messageHandler = null, TimeSpan? timeout = null)
        {
            _accessToken = accessToken;

            messageHandler = messageHandler ?? new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate };

            _httpClient =
                new HttpClient(messageHandler)
                {
                    BaseAddress = new Uri(BaseUrl),
                    Timeout = timeout ?? DefaultTimeout,
                    DefaultRequestHeaders =
                    {
                        AcceptEncoding = { new StringWithQualityHeaderValue("gzip") }
                    }
                };
        }

        public void Dispose()
        {
            foreach (var liveMeasurementListener in _liveMeasurementListeners.Values)
                liveMeasurementListener.Dispose();

            _liveMeasurementListeners.Clear();

            _httpClient.Dispose();
        }

        /// <summary>
        /// Gets base data about customer, his/her homes and subscriptions.
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public Task<TibberApiQueryResult> GetBasicData(CancellationToken cancellationToken = default) =>
            Query(new TibberQueryBuilder().WithHomesAndSubscriptions().Build(), cancellationToken);

        /// <summary>
        /// Gets home consumption.
        /// </summary>
        /// <param name="homeId"></param>
        /// <param name="resolution"></param>
        /// <param name="lastEntries">how many last entries to fetch; if no value provider a default will be used - hourly: 24; daily: 30; weekly: 4; monthly: 12; annually: 1</param>
        /// <param name="cancellationToken"></param>
        /// <returns>consumption entries</returns>
        public async Task<ICollection<ConsumptionEntry>> GetHomeConsumption(Guid homeId, ConsumptionResolution resolution, int? lastEntries = null, CancellationToken cancellationToken = default) =>
            (await Query(new TibberQueryBuilder().WithHomeConsumption(homeId, resolution, lastEntries).Build(), cancellationToken)).Data?.Viewer?.Home?.Consumption?.Nodes;

        /// <summary>
        /// Executes raw GraphQL query.
        /// </summary>
        /// <param name="query">query text</param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async Task<TibberApiQueryResult> Query(string query, CancellationToken cancellationToken = default)
        {
            using (var response = await _httpClient.PostAsync($"gql?token={_accessToken}", JsonContent(new { query }), cancellationToken))
                return await JsonResult(response);
        }

        /// <summary>
        /// Starts live measurement listener. You have to have active Tibber Pulse device to get any values.
        /// </summary>
        /// <param name="homeId"></param>
        /// <param name="cancellationToken"></param>
        /// <returns>Return observable object providing values; you have to subscribe observer(s) to access the values. </returns>
        public async Task<IObservable<LiveMeasurement>> StartLiveMeasurementListener(Guid homeId, CancellationToken cancellationToken = default)
        {
            await Semaphore.WaitAsync(cancellationToken);

            if (_liveMeasurementListeners.TryGetValue(homeId, out var listener))
                listener.Dispose();

            var liveMeasurementReader = new LiveMeasurementListener(_accessToken, homeId);
            _liveMeasurementListeners[homeId] = liveMeasurementReader;

            await liveMeasurementReader.Initialize(cancellationToken);

            Semaphore.Release();

            return liveMeasurementReader;
        }

        /// <summary>
        /// Stops live measurement listener.
        /// </summary>
        /// <param name="homeId"></param>
        public void StopLiveMeasurementListener(Guid homeId)
        {
            Semaphore.Wait();

            if (_liveMeasurementListeners.TryGetValue(homeId, out var listener))
            {
                _liveMeasurementListeners.Remove(homeId);
                listener.Dispose();
            }

            Semaphore.Release();
        }

        private static HttpContent JsonContent(object data) =>
            new StringContent(JsonConvert.SerializeObject(data, JsonSerializerSettings), Encoding.UTF8, "application/json");

        private static async Task<TibberApiQueryResult> JsonResult(HttpResponseMessage response)
        {
            response.EnsureSuccessStatusCode();
            using (var stream = await response.Content.ReadAsStreamAsync())
            using (var streamReader = new StreamReader(stream))
            using (var jsonReader = new JsonTextReader(streamReader))
                return Serializer.Deserialize<TibberApiQueryResult>(jsonReader);
        }
    }

    public class TibberApiQueryResult
    {
        public Data Data { get; set; }
        public ICollection<QueryError> Errors { get; set; }
    }

    public class Data
    {
        public Viewer Viewer { get; set; }
    }

    public class QueryError
    {
        public string Message { get; set; }
        public ICollection<ErrorLocation> Locations { get; set; }
    }

    public class ErrorLocation
    {
        public int Line { get; set; }
        public int Column { get; set; }
    }
}
