﻿using System.Text;
using System.Net.Http;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using System;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Linq;
using Microsoft.AspNetCore.Http;
using System.Net;

namespace FHIRSubscriptionProcessor
{
    public static class FHIRClient
    {
        private static object lockobj = new object();
        private static string _bearerToken = null;
        private static HttpClient _fhirClient = new HttpClient();
        public static async System.Threading.Tasks.Task<FHIRResponse> CallFHIRServer(HttpRequest req, string body, ILogger log)
        {
            string path = string.Empty;
            if (req.Path.HasValue)
            {
                path = req.Path;
                if (path.StartsWith("/fhir/")) path = path.Substring(6);
            }
            path += (req.QueryString.HasValue ? req.QueryString.Value : "");
            return await FHIRClient.CallFHIRServer(path, body, req.Method, req.Headers, log);
        }
        public static async System.Threading.Tasks.Task<FHIRResponse> CallFHIRServer(string path, string body, string method, ILogger log)
        {
            HeaderDictionary dict = new HeaderDictionary();
            dict.Add("Content-Type", "application/json");
            dict.Add("Accept", "application/json");
            return await FHIRClient.CallFHIRServer(path, body, method, dict, log);
        }
        public static async System.Threading.Tasks.Task<FHIRResponse> CallFHIRServer(string path, string body, string method, IHeaderDictionary headers, ILogger log)
        {
            if (!string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("FS-RESOURCE")) && ADUtils.isTokenExpired(_bearerToken))
            {
                lock (lockobj)
                {
                    if (ADUtils.isTokenExpired(_bearerToken))
                    {
                        log.LogInformation("Token is expired...Obtaining new bearer token...");
                        _bearerToken = ADUtils.GetOAUTH2BearerToken(Utils.GetEnvironmentVariable("FS-RESOURCE"), Utils.GetEnvironmentVariable("FS-TENANT-NAME"),
                                                                    Utils.GetEnvironmentVariable("FS-CLIENT-ID"), Utils.GetEnvironmentVariable("FS-SECRET")).GetAwaiter().GetResult();
                        _fhirClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _bearerToken);
 
                    }
                }
            }
            HttpMethod rm = HttpMethod.Put;
            switch (method)
            {
                case "GET":
                    rm = HttpMethod.Get;
                    break;
                case "POST":
                    rm = HttpMethod.Post;
                    break;
                case "PUT":
                    rm = HttpMethod.Put;
                    break;
                case "PATCH":
                    rm = HttpMethod.Patch;
                    break;
                case "DELETE":
                    rm = HttpMethod.Delete;
                    break;
                default:
                    throw new Exception($"{method} is not supported");

            }
           
                HttpRequestMessage _fhirRequest;
                HttpResponseMessage _fhirResponse;
                string fsurl = Utils.GetEnvironmentVariable("FS-URL", "");
                if (!path.StartsWith(fsurl, StringComparison.InvariantCultureIgnoreCase)) path = fsurl + "/" + path;
                _fhirRequest = new HttpRequestMessage(rm, path);
                string ct = "application/json";
                if (headers.TryGetValue("Content-Type", out Microsoft.Extensions.Primitives.StringValues ctvalues))
                {
                    ct = ctvalues.First();
                }
                foreach (string headerKey in headers.Keys)
                {
                    try
                    {
                        if (headerKey.StartsWith("x-ms", StringComparison.InvariantCultureIgnoreCase) ||
                            headerKey.StartsWith("prefer", StringComparison.InvariantCultureIgnoreCase) ||
                            headerKey.StartsWith("etag", StringComparison.InvariantCultureIgnoreCase) ||
                            headerKey.StartsWith("If-", StringComparison.InvariantCultureIgnoreCase) ||
                            headerKey.StartsWith("Accept",StringComparison.InvariantCultureIgnoreCase))
                        {
                            _fhirRequest.Headers.Add(headerKey, headers[headerKey].FirstOrDefault());
                        }
                    }
                    catch (Exception ex)
                    {
                        log.LogError($"Error Adding Headers to FHIR Request {headerKey}:{ex.Message}");
                    }
                }
                if (!headers.TryGetValue("Accept", out Microsoft.Extensions.Primitives.StringValues acvalue))
                {
                    _fhirRequest.Headers.Add("Accept", "application/json");
                }
                if (!string.IsNullOrEmpty(body))
                {
                    _fhirRequest.Content = new StringContent(body, Encoding.UTF8, ct);
                    _fhirRequest.Content.Headers.ContentType = new MediaTypeHeaderValue(ct);
                }
                _fhirResponse = await _fhirClient.SendAsync(_fhirRequest);
                // Read Response Content (this will usually be JSON content)
                var content = await _fhirResponse.Content.ReadAsStringAsync();
                return new FHIRResponse(content, _fhirResponse.Headers, _fhirResponse.StatusCode);
            

        }
       
    }
    public class FHIRResponse
    {
        public FHIRResponse()
        {
            Headers = new Dictionary<string, HeaderParm>();
        }
        public FHIRResponse(string content, HttpResponseHeaders respheaders, HttpStatusCode status, bool parse = false) : this()
        {
            string[] filterheaders = Utils.GetEnvironmentVariable("FS-RESPONSE-HEADER-NAME", "x-ms-retry-after-ms,x-ms-session-token,x-ms-request-charge,Date,Last-Modified,ETag,Location,Content-Location").Split(",");
            if (parse) this.Content = JObject.Parse(content);
            else this.Content = content;
            foreach (string head in filterheaders)
            {
                IEnumerable<string> values = null;
                if (respheaders.TryGetValues(head, out values))
                {
                    this.Headers.Add(head, new HeaderParm(head, values.First()));

                }
            }
            this.StatusCode = status;
        }
        public IDictionary<string, HeaderParm> Headers { get; set; }
        public object Content { get; set; }
        public HttpStatusCode StatusCode { get; set; }
        public bool IsSuccess()
        {
            int s = (int)StatusCode;
            return (s > 199 && s < 300);
        }
        public override string ToString()
        {
            if (Content == null) return "";
            if (Content is string) return (string)Content;
            if (Content is JToken)
            {
                return ((JToken)Content).ToString();
            }
            return base.ToString();
        }
        public JToken toJToken()
        {
            if (Content is string) return JObject.Parse((string)Content);
            if (Content == null) return new JObject();
            return (JToken)Content;
        }

    }
    public class HeaderParm
    {
        public HeaderParm()
        {

        }
        public HeaderParm(string name, string value)
        {
            this.Name = name;
            this.Value = value;
        }
        public string Name { get; set; }
        public string Value { get; set; }
    }

}