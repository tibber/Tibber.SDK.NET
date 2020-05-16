using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;

namespace Tibber.Sdk
{
    /// <inheritdoc />
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

        private readonly RealTimeMeasurementListener _realTimeMeasurementListener;

        private readonly HttpClient _httpClient;
        private readonly string _accessToken;

        public TibberApiClient(string accessToken, HttpMessageHandler messageHandler = null, TimeSpan? timeout = null)
        {
            if (String.IsNullOrWhiteSpace(accessToken))
                throw new ArgumentException("access token required", nameof(accessToken));

            _accessToken = accessToken;

            messageHandler ??= new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate };

            _httpClient =
                new HttpClient(messageHandler)
                {
                    BaseAddress = new Uri(BaseUrl),
                    Timeout = timeout ?? DefaultTimeout,
                    DefaultRequestHeaders =
                    {
                        AcceptEncoding = { new StringWithQualityHeaderValue("gzip") },
                        UserAgent = { new ProductInfoHeaderValue("Tibber-SDK.NET", "1.0") }
                    }
                };

            _realTimeMeasurementListener = new RealTimeMeasurementListener(accessToken);
        }

        public void Dispose()
        {
            _realTimeMeasurementListener.Dispose();
            _httpClient.Dispose();
        }

        /// <summary>
        /// Gets base data about customer, his/her homes and subscriptions.
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <exception cref="TibberApiHttpException"></exception>
        /// <returns></returns>
        public async Task<TibberApiQueryResult> GetBasicData(CancellationToken cancellationToken = default)
        {
            var result = await Query(new TibberQueryBuilder().WithHomesAndSubscriptions().Build(), cancellationToken);
            ValidateResult(result);
            return result;
        }

        /// <summary>
        /// Gets home consumption.
        /// </summary>
        /// <param name="homeId"></param>
        /// <param name="resolution"></param>
        /// <param name="lastEntries">how many last entries to fetch; if no value provider a default will be used - hourly: 24; daily: 30; weekly: 4; monthly: 12; annually: 1</param>
        /// <param name="cancellationToken"></param>
        /// <exception cref="TibberApiHttpException"></exception>
        /// <returns>consumption entries</returns>
        public async Task<ICollection<ConsumptionEntry>> GetHomeConsumption(Guid homeId, EnergyResolution resolution, int? lastEntries = null, CancellationToken cancellationToken = default)
        {
            var result = await Query(new TibberQueryBuilder().WithHomeConsumption(homeId, resolution, lastEntries).Build(), cancellationToken);
            ValidateResult(result);
            return result.Data?.Viewer?.Home?.Consumption?.Nodes;
        }

        /// <summary>
        /// Gets home consumption.
        /// </summary>
        /// <param name="homeId"></param>
        /// <param name="resolution"></param>
        /// <param name="lastEntries">how many last entries to fetch; if no value provider a default will be used - hourly: 24; daily: 30; weekly: 4; monthly: 12; annually: 1</param>
        /// <param name="cancellationToken"></param>
        /// <exception cref="TibberApiHttpException"></exception>
        /// <returns>consumption entries</returns>
        public async Task<ICollection<ProductionEntry>> GetHomeProduction(Guid homeId, EnergyResolution resolution, int? lastEntries = null, CancellationToken cancellationToken = default)
        {
            var result = await Query(new TibberQueryBuilder().WithHomeProduction(homeId, resolution, lastEntries).Build(), cancellationToken);
            ValidateResult(result);
            return result.Data?.Viewer?.Home?.Production?.Nodes;
        }

        /// <summary>
        /// Executes raw GraphQL query.
        /// </summary>
        /// <param name="query">query text</param>
        /// <param name="cancellationToken"></param>
        /// <exception cref="TibberApiHttpException"></exception>
        /// <returns></returns>
        public Task<TibberApiQueryResult> Query(string query, CancellationToken cancellationToken = default) =>
            Request<TibberApiQueryResult>(query, cancellationToken);

        /// <summary>
        /// Executes raw GraphQL mutation.
        /// </summary>
        /// <param name="mutation">query text</param>
        /// <param name="cancellationToken"></param>
        /// <exception cref="TibberApiHttpException"></exception>
        /// <returns></returns>
        public Task<TibberApiMutationResult> Mutation(string mutation, CancellationToken cancellationToken = default) =>
            Request<TibberApiMutationResult>(mutation, cancellationToken);

        /// <summary>
        /// Starts real-time measurement listener. You must have active Tibber Pulse device to get any values.
        /// </summary>
        /// <param name="homeId"></param>
        /// <param name="cancellationToken"></param>
        /// <exception cref="TibberApiHttpException"></exception>
        /// <returns>Return observable object providing values; you have to subscribe observer(s) to access the values. </returns>
        public async Task<IObservable<RealTimeMeasurement>> StartRealTimeMeasurementListener(Guid homeId, CancellationToken cancellationToken = default)
        {
            await Semaphore.WaitAsync(cancellationToken);

            try
            {
                return await _realTimeMeasurementListener.SubscribeHome(homeId, cancellationToken);
            }
            finally
            {
                Semaphore.Release();
            }
        }

        /// <summary>
        /// Stops real-time measurement listener.
        /// </summary>
        /// <param name="homeId"></param>
        public async Task StopRealTimeMeasurementListener(Guid homeId)
        {
            await Semaphore.WaitAsync();

            try
            {
                await _realTimeMeasurementListener.UnsubscribeHome(homeId, CancellationToken.None);
            }
            finally
            {
                Semaphore.Release();
            }
        }

        private async Task<TResult> Request<TResult>(string query, CancellationToken cancellationToken)
        {
            var relativeUri = $"gql?token={_accessToken}";

            var requestStart = DateTimeOffset.UtcNow;

            HttpResponseMessage response;

            try
            {
                response = await _httpClient.PostAsync(relativeUri, JsonContent(new { query }), cancellationToken);
            }
            catch (Exception exception)
            {
                throw new TibberApiHttpException(new Uri(_httpClient.BaseAddress, relativeUri), HttpMethod.Post, DateTimeOffset.Now - requestStart, exception.Message, exception);
            }

            if (!response.IsSuccessStatusCode)
                throw await TibberApiHttpException.Create(new Uri(new Uri(BaseUrl), relativeUri), HttpMethod.Post, response, DateTimeOffset.Now - requestStart).ConfigureAwait(false);

            using var stream = await response.Content.ReadAsStreamAsync();
            using var streamReader = new StreamReader(stream);
            using var jsonReader = new JsonTextReader(streamReader);
            return Serializer.Deserialize<TResult>(jsonReader);
        }

        private static HttpContent JsonContent(object data) =>
            new StringContent(JsonConvert.SerializeObject(data, JsonSerializerSettings), Encoding.UTF8, "application/json");

        private static void ValidateResult(TibberApiQueryResult result)
        {
            if (result.Errors != null && result.Errors.Any())
                throw new TibberApiException($"Query execution failed:{Environment.NewLine}{String.Join(Environment.NewLine, result.Errors.Select(e => $"{e.Message} (locations: {String.Join(";",  e.Locations.Select(l => $"line: {l.Line}, column: {l.Column}"))})"))}");
        }
    }

    public abstract class GraphQlResult<TDataContract>
    {
        public TDataContract Data { get; set; }
        public ICollection<QueryError> Errors { get; set; }
    }

    public class TibberApiQueryResult : GraphQlResult<QueryData>
    {
    }

    public class TibberApiMutationResult : GraphQlResult<TibberMutation>
    {
    }

    public class QueryData
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
