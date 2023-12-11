using System.Text;
using System.Net.Http;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using System;
using Newtonsoft.Json.Linq;
using System.Text.Json;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Linq;
using Polly;
using System.Net;
using System.Collections.Concurrent;

namespace FHIRSubscriptionProcessor
{
    public static class FHIRUtils
    {
        //AD Settings
        private static bool isMsi = Utils.GetBoolEnvironmentVariable("FS-ISMSI", false);
        private static string resource = Utils.GetEnvironmentVariable("FS-RESOURCE");
        private static string tenant = Utils.GetEnvironmentVariable("FS-TENANT-NAME");
        private static string clientid = Utils.GetEnvironmentVariable("FS-CLIENT-ID");
        private static string secret = Utils.GetEnvironmentVariable("FS-SECRET");
        private static string authority = Utils.GetEnvironmentVariable("FS-AUTHORITY", "https://login.microsoftonline.com");
        private static string fsurl = Utils.GetEnvironmentVariable("FS-URL");
        private static ConcurrentDictionary<string, string> _tokens = new ConcurrentDictionary<string, string>();
        private static readonly HttpStatusCode[] httpStatusCodesWorthRetrying = {
            HttpStatusCode.RequestTimeout, // 408
            HttpStatusCode.InternalServerError, // 500
            HttpStatusCode.BadGateway, // 502
            HttpStatusCode.ServiceUnavailable, // 503
            HttpStatusCode.GatewayTimeout, // 504
            HttpStatusCode.TooManyRequests //429
        };
        private static HttpClient _fhirClient = new HttpClient(
            new SocketsHttpHandler()
            {
                ResponseDrainTimeout = TimeSpan.FromSeconds(Utils.GetIntEnvironmentVariable("FBI-POOLEDCON-RESPONSEDRAINSECS", "60")),
                PooledConnectionLifetime = TimeSpan.FromMinutes(Utils.GetIntEnvironmentVariable("FBI-POOLEDCON-LIFETIME", "5")),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(Utils.GetIntEnvironmentVariable("FBI-POOLEDCON-IDLETO", "2")),
                MaxConnectionsPerServer = Utils.GetIntEnvironmentVariable("FBI-POOLEDCON-MAXCONNECTIONS", "20"),

            });
        public static async System.Threading.Tasks.Task<FHIRResponse> CallFHIRServer(string path, string body, HttpMethod method, ILogger log, string customAudience = null)
        {
            string _bearerToken = null;

            string tokenName = $"fhirtoken:{fsurl}";
            if (path.StartsWith("http"))
            {
                tokenName = $"fhirtoken:{new Uri(path).GetLeftPart(UriPartial.Authority)}";
            }

            _tokens.TryGetValue(tokenName, out _bearerToken);
            if (ADUtils.isTokenExpired(_bearerToken))
            {
                log.LogInformation("CallFHIRServer:Bearer Token is expired...Obtaining new bearer token...");
                _bearerToken = await ADUtils.GetAADAccessToken($"{authority}/{tenant}", clientid, secret, customAudience ?? resource, isMsi, log);
                _tokens[tokenName] = _bearerToken;
            }
            var retryPolicy = Policy
                .Handle<HttpRequestException>()
                .OrResult<HttpResponseMessage>(r => httpStatusCodesWorthRetrying.Contains(r.StatusCode))
                .WaitAndRetryAsync(Utils.GetIntEnvironmentVariable("FBI-POLLY-MAXRETRIES", "3"), retryAttempt =>
                   TimeSpan.FromMilliseconds(Utils.GetIntEnvironmentVariable("FBI-POLLY-RETRYMS", "500")), (result, timeSpan, retryCount, context) =>
                   {
                       log.LogWarning($"FHIR Request failed on a retryable status...Waiting {timeSpan} before next retry. Attempt {retryCount}");
                   }
                );

            if (Utils.GetBoolEnvironmentVariable("FBI-POLLY-EXPONENTIAL"))
            {
                int maxRetries = Utils.GetIntEnvironmentVariable("FBI-POLLY-MAXRETRIES", "3");
                double retryMs = Utils.GetIntEnvironmentVariable("FBI-POLLY-RETRYMS", "500");
                double jitterFactor = 0.2; // You can adjust this value as needed
                Random jitterRandom = new Random();

                retryPolicy = Policy
                    .Handle<HttpRequestException>()
                    .OrResult<HttpResponseMessage>(r => httpStatusCodesWorthRetrying.Contains(r.StatusCode))
                    .WaitAndRetryAsync(maxRetries, retryAttempt =>
                    {
                        double retryDelay = retryMs * Math.Pow(2, retryAttempt - 1);
                        double jitter = (jitterRandom.NextDouble() * 2 - 1) * jitterFactor * retryDelay; // Generates a value between -jitterFactor and +jitterFactor
                        return TimeSpan.FromMilliseconds(retryDelay + jitter);
                    },
                        (result, timeSpan, retryCount, context) =>
                        {
                            log.LogWarning($"FHIR Request failed on a retryable status...Waiting {timeSpan} before next retry. Attempt {retryCount}");
                        }
                    );
            }

            HttpResponseMessage _fhirResponse =
            await retryPolicy.ExecuteAsync(async () =>
            {
                HttpRequestMessage _fhirRequest;
                var fhirurl = path;
                if (!fhirurl.StartsWith("http")) fhirurl = $"{fsurl}/{path}";
                _fhirRequest = new HttpRequestMessage(method, fhirurl);
                _fhirRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _bearerToken);
                _fhirRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                _fhirRequest.Content = new StringContent(body, Encoding.UTF8, "application/json");
                return await _fhirClient.SendAsync(_fhirRequest);

            });
            return await FHIRResponse.FromHttpResponseMessage(_fhirResponse, log);
        }
        
    }
    public class FHIRResponse
    {
        public static async Task<FHIRResponse> FromHttpResponseMessage(HttpResponseMessage resp, ILogger log)
        {
            var retVal = new FHIRResponse();

            if (resp != null)
            {
                retVal.Content = await resp.Content.ReadAsStringAsync();
                retVal.Status = resp.StatusCode;
                retVal.Success = resp.IsSuccessStatusCode;
                retVal.ResponseHeaders = resp.Headers.ToDictionary(a => a.Key, a => string.Join(";", a.Value));
                if (!retVal.Success)
                {
                    if (string.IsNullOrEmpty(retVal.Content))
                        retVal.Content = resp.ReasonPhrase;
                    if (retVal.Status == System.Net.HttpStatusCode.TooManyRequests)
                    {
                        string s_retry = null;
                        retVal.ResponseHeaders.TryGetValue("x-ms-retry-after-ms", out s_retry);
                        if (s_retry == null) s_retry = Environment.GetEnvironmentVariable("FBI-DEFAULTRETRY");
                        int i = 0;
                        if (!int.TryParse(s_retry, out i))
                        {
                            i = 500;
                        }
                        retVal.RetryAfterMS = i;
                    }
                }

            }
            return retVal;
        }
        public FHIRResponse()
        {
            Status = System.Net.HttpStatusCode.InternalServerError;
            Success = false;
            Content = null;
            RetryAfterMS = 500;
        }
        public string Content { get; set; }
        public System.Net.HttpStatusCode Status { get; set; }
        public bool Success { get; set; }
        public int RetryAfterMS { get; set; }
        public Dictionary<string, string> ResponseHeaders { get; set; }
    }
}
