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
    public class TibberApiClient : IDisposable
    {
        private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(59);
        private static readonly JsonSerializerSettings JsonSerializerSettings =
            new JsonSerializerSettings
            {
                ContractResolver = new CamelCasePropertyNamesContractResolver(),
                Converters = { new StringEnumConverter() },
                DateParseHandling = DateParseHandling.DateTimeOffset
            };

        private static readonly JsonSerializer Serializer = JsonSerializer.Create(JsonSerializerSettings);

        private readonly HttpClient _httpClient;
        private readonly string _accessToken;

        public TibberApiClient(string accessToken, HttpMessageHandler messageHandler = null, TimeSpan? timeout = null)
        {
            _accessToken = accessToken;

            messageHandler = messageHandler ?? new HttpClientHandler { AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate };

            _httpClient =
                new HttpClient(messageHandler)
                {
                    BaseAddress = new Uri("https://api.tibber.com/v1-beta/"),
                    Timeout = timeout ?? DefaultTimeout,
                    DefaultRequestHeaders =
                    {
                        AcceptEncoding = { new StringWithQualityHeaderValue("gzip") }
                    }
                };
        }

        public void Dispose() => _httpClient.Dispose();

        public Task<TibberApiQueryResult> GetBasicData(CancellationToken cancellationToken = default) =>
            Query(new TibberQueryBuilder().WithDefaults().Build(), cancellationToken);

        public async Task<TibberApiQueryResult> Query(string query, CancellationToken cancellationToken = default)
        {
            using (var response = await _httpClient.PostAsync($"gql?token={_accessToken}", JsonContent(new { query }), cancellationToken))
                return await JsonResult(response);
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
