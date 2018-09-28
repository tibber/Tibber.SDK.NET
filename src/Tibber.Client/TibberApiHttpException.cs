using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;

namespace Tibber.Client
{
    public class TibberApiException : Exception
    {
        public TibberApiException(string message) : base(message)
        {
        }

        public TibberApiException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }

    public class TibberApiHttpException : TibberApiException
    {
        private const int MaximumBodyLength = 131071;

        public HttpStatusCode? StatusCode { get; }
        public HttpMethod HttpMethod { get; }
        public TimeSpan RequestDuration { get; }
        public HttpRequestHeaders RequestHeaders { get; }
        public HttpResponseHeaders ResponseHeaders { get; }
        public HttpContentHeaders RequestContentHeaders { get; private set; }
        public HttpContentHeaders ResponseContentHeaders { get; private set; }
        public Uri Uri { get; }
        public string ReasonPhrase { get; }
        public string RequestContent { get; private set; }
        public string ResponseContent { get; private set; }

        private TibberApiHttpException(
            Uri uri,
            HttpMethod httpMethod,
            HttpStatusCode statusCode,
            string reasonPhrase,
            HttpRequestHeaders requestHeaders,
            HttpResponseHeaders responseHeaders,
            TimeSpan requestDuration,
            string message)
            : base(message)
        {
            Uri = uri;
            HttpMethod = httpMethod;
            StatusCode = statusCode;
            ReasonPhrase = reasonPhrase;
            RequestHeaders = requestHeaders;
            ResponseHeaders = responseHeaders;
            RequestDuration = requestDuration;
        }

        internal static async Task<TibberApiHttpException> Create(Uri uri, HttpMethod httpMethod, HttpResponseMessage response, TimeSpan duration, string message = null)
        {
            var exception =
                new TibberApiHttpException(
                    uri,
                    httpMethod,
                    response.StatusCode,
                    response.ReasonPhrase,
                    response.RequestMessage.Headers,
                    response.Headers,
                    duration,
                    message ?? $"HTTP '{httpMethod} {uri}' call failed with status '{response.ReasonPhrase}' ({(int)response.StatusCode})");

            if (response.RequestMessage.Content != null)
            {
                exception.RequestContentHeaders = response.RequestMessage.Content.Headers;

                try
                {
                    exception.RequestContent = await response.RequestMessage.Content.ReadAsStringAsync().ConfigureAwait(false);
                }
                catch
                {
                    // We're already handling an exception at this point, so we want to make sure we don't throw another one that hides the real error.
                }

                response.RequestMessage.Content.Dispose();
            }

            if (response.Content != null)
            {
                exception.ResponseContentHeaders = response.Content.Headers;

                try
                {
                    exception.ResponseContent = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                }
                catch
                {
                    // We're already handling an exception at this point, so we want to make sure we don't throw another one that hides the real error.
                }

                response.Content.Dispose();
            }

            response.Dispose();

            return exception;
        }

        public override string ToString()
        {
            var builder = new StringBuilder(512);
            builder.AppendLine($"{GetType().FullName}: {Message}");
            builder.AppendLine($"Request: {HttpMethod} {Uri}");

            if (RequestHeaders != null && RequestHeaders.Any())
            {
                builder.AppendLine("Request headers:");
                builder.AppendLine(String.Join(Environment.NewLine, RequestHeaders.Select(h => $"{h.Key}: [{String.Join(", ", h.Value)}]")));
            }

            if (RequestContentHeaders != null && RequestContentHeaders.Any())
            {
                builder.AppendLine("Request content headers:");
                builder.AppendLine(String.Join(Environment.NewLine, RequestContentHeaders.Select(h => $"{h.Key}: [{String.Join(", ", h.Value)}]")));
            }

            if (!String.IsNullOrEmpty(RequestContent))
            {
                builder.AppendLine("Request content:");
                if (RequestContent.Length > MaximumBodyLength)
                {
                    builder.Append(RequestContent.Substring(0, MaximumBodyLength));
                    builder.AppendLine("\u2026");
                }
                else
                    builder.AppendLine(RequestContent);
            }

            if (StatusCode.HasValue)
                builder.AppendLine($"Status: {ReasonPhrase} ({(int)StatusCode})");

            if (ResponseHeaders != null && ResponseHeaders.Any())
            {
                builder.AppendLine("Response headers:");
                builder.AppendLine(String.Join(Environment.NewLine, ResponseHeaders.Select(h => $"{h.Key}: [{String.Join(", ", h.Value)}]")));
            }

            if (ResponseContentHeaders != null && ResponseContentHeaders.Any())
            {
                builder.AppendLine("Response content headers:");
                builder.AppendLine(String.Join(Environment.NewLine, ResponseContentHeaders.Select(h => $"{h.Key}: [{String.Join(", ", h.Value)}]")));
            }

            if (!String.IsNullOrEmpty(ResponseContent))
            {
                builder.AppendLine("Response content:");

                if (ResponseContent.Length > MaximumBodyLength)
                {
                    builder.Append(ResponseContent.Substring(0, MaximumBodyLength));
                    builder.AppendLine("\u2026");
                }
                else
                    builder.AppendLine(ResponseContent);
            }

            if (InnerException != null)
            {
                builder.AppendLine("Inner exception:");
                builder.AppendLine(InnerException.ToString());
            }
            else
            {
                builder.AppendLine("Stack trace:");
                builder.AppendLine(StackTrace);
            }

            return builder.ToString();
        }

        private static string GetOriginalExceptionMessage(Exception exception)
        {
            while (exception.InnerException != null)
                exception = exception.InnerException;

            return exception.Message;
        }
    }
}
