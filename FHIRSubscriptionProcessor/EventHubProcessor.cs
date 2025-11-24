using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Azure.Messaging.EventHubs;
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.ServiceBus;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace FHIRSubscriptionProcessor
{
    public static class EventHubProcessor
    {
        public static readonly string RESCACHEPREFIX = "sx-resources-";
        public static readonly string TYPECACHEPREFIX = "sx-types-";
        private static bool isR5Backport = Utils.GetBoolEnvironmentVariable("FS-ISR5BACKPORT", false);
        private static string fsurl = Utils.GetEnvironmentVariable("FS-URL");
        private static readonly HttpClient httpClient = new HttpClient();

        [FunctionName("SubscriptionEventHubProcessor")]
        public static async Task Run([EventHubTrigger("%FP-MOD-EVENTHUB-NAME%", ConsumerGroup = "%FSP-CONSUMERGROUPNAME%", Connection = "FP-MOD-EVENTHUB-CONNECTION")] EventData[] events,
                                     [ServiceBus("%FSP-NOTIFYSB-TOPIC%", Connection = "FSP-NOTIFYSB-CONNECTION", EntityType = ServiceBusEntityType.Topic)] IAsyncCollector<ServiceBusMessage> outputTopic,
                                     ILogger log)
        {
            var exceptions = new List<Exception>();

            foreach (EventData eventData in events)
            {
                try
                {
                    string messageBody = Encoding.UTF8.GetString(eventData.EventBody);
                    //  string msg = "{\"action\":\"" + action + "\",\"resourcetype\":\"" + resource.FHIRResourceType() + "\",\"id\":\"" + resource.FHIRResourceId() + "\",\"version\":\"" + resource.FHIRVersionId() + "\",\"lastupdated\":\"" + resource.FHIRLastUpdated() + "\"}";
                    //Parse Msg into JObject
                    log.LogInformation($"Processing message:{messageBody}");
                    JObject m = JObject.Parse(messageBody);
                    //Handle Subscription Create/Updates and Deletes
                    if (!m["resourcetype"].IsNullOrEmpty() && m["resourcetype"].ToString().Equals("Subscription"))
                    {
                        await ProcessSubscription(m["id"].ToString(), m["action"].ToString(), log);
                    } else
                    {
                        await ProcessResourceEvent(m["resourcetype"].ToString(),m["id"].ToString(), m["action"].ToString(), outputTopic,log);
                    }

                }
                catch (Exception e)
                {
                    // We need to keep processing the rest of the batch - capture this exception and continue.
                    // Also, consider capturing details of the message that failed processing so it can be processed again later.
                    exceptions.Add(e);
                }
            }

            // Once processing of the batch is complete, if any messages in the batch failed processing throw an exception so that there is a record of the failure.

            if (exceptions.Count > 1)
                throw new AggregateException(exceptions);

            if (exceptions.Count == 1)
                throw exceptions.Single();
        }
        public static async Task ProcessResourceEvent(string restype,string resid, string action, IAsyncCollector<ServiceBusMessage> outputTopic,ILogger log)
        {
            List<string> idsbytype = loadSubscriptionIdsByType(restype, log);
            if (idsbytype.Count == 0)
            {
                log.LogInformation($"ProcessResourceEvent: No subscriptions for resource type {restype} are active");
                return;
            }
            log.LogInformation($"ProcessResourceEvent: Evaluating criteria for {idsbytype.Count()} Subscriptions for resource type {restype}...");  
            foreach (string id in idsbytype)
            {
                var sr = loadCachedSubscription(id, log);
                var criteria = isR5Backport ? sr["_criteria"]["extension"][0]["valueString"].ToString() : sr["criteria"].ToString();
                DateTime? end = (DateTime)sr["end"];
                if (end.HasValue)
                {
                    DateTime now = DateTime.UtcNow;
                    if (now > end)
                    {
                        sr["status"] = "off";
                        removeSubscriptionCache(id, log);
                        var saveresult = await updateFHIRSubscription(id, sr.ToString(), log);
                        log.LogInformation($"Subscription/{id} has expired..Status updated to off");
                        return;
                    }
                    
                }
                if (string.IsNullOrEmpty(criteria)) return;
                criteria += $"&_id={resid}";
                if (isR5Backport)
                {
                    var topic = sr["criteria"].ToString();
                    Uri uri = new Uri(topic);
                    string topicid = uri.Segments.Last().TrimEnd('/');
                    var trs = await FHIRUtils.CallFHIRServer($"Basic/{topicid}", "", HttpMethod.Get, log);
                    if (trs.Success)
                    {
                        JToken fhirresp = JObject.Parse(trs.Content); 
                        var (resourceType, currentValue) = GetResourceTriggerAsync(fhirresp, log);
                        if (!string.IsNullOrEmpty(resourceType) && !string.IsNullOrEmpty(currentValue))
                        {
                            criteria += $"&{currentValue}";
                        }
                        else
                        {
                            log.LogInformation($"Not able to fetch the SubscriptionTopic/{topicid} filter condition. Using the filter from subscription resource ");
                        }
                    }
                }
                log.LogInformation($"ProcessResourceEvent: Evalutating Subscription/{id} criteria:{criteria}");
                var result = await FHIRUtils.CallFHIRServer(criteria, "", HttpMethod.Get, log);
                if (result.Success)
                {
                    JToken fhirresp = JObject.Parse(result.Content);
                    if (!fhirresp["entry"].IsNullOrEmpty())
                    {
                        //Resource met criteria so lets queue it to the notify processor
                        log.LogInformation($"ProcessResourceEvent: {restype}/{resid} met criteria for Subscription/{id} Adding to notify queue...");

                        string resourceRef = $"{restype}/{resid}";
                        var payload = new
                        {
                            subscriptionId = id,
                            resource = resourceRef
                        };

                        // Serialize to JSON
                        string jsonPayload = JsonSerializer.Serialize(payload);
                        ServiceBusMessage msg = new ServiceBusMessage()
                        {
                            ContentType = "application/json",
                            Body = BinaryData.FromString(jsonPayload)
                        };

                        // Send to topic
                        await outputTopic.AddAsync(msg);
                    } 
                    else
                    {
                        log.LogInformation($"ProcessResourceEvent: {restype}/{resid} did not meet criteria for Subscription/{id}");
                    }
                }
            }
        }
        public static async Task ProcessSubscription(string id,string action, ILogger log)
        {

            //Load Subscription resource from FHIR Server
               
                switch (action)
                {
                    case "Created":
                    case "Updated":
                        var fhirresp = await FHIRUtils.CallFHIRServer($"Subscription/{id}", null, HttpMethod.Get, log);
                        if (!fhirresp.Success)
                        {
                            log.LogError($"ProcessSubscription: Subscription {id} does not exist on FHIR Server");
                            return;
                        }
                        JToken t = JObject.Parse(fhirresp.Content);
                        string status = (t["status"].IsNullOrEmpty() ? "" : t["status"].ToString());
                        string criteria = isR5Backport ? t["_criteria"]["extension"][0]["valueString"].ToString() : t["criteria"].ToString();
                        //For Status off or error remove the cache subscription and thats it no changes to resource on Server
                        if (status.Equals("off") || status.Equals("error"))
                        {
                            removeSubscriptionCache(id, log);
                            return;
                        }
                        //Status should be requested for new/updated Subscriptions server marks them active
                        else if (status.Equals("active"))
                        {
                            return;
                        }
                        else
                        {
                            //Validate Subscription Resource
                            if (t["channel"].IsNullOrEmpty() || t["channel"]["type"].IsNullOrEmpty() || !t["channel"]["type"].ToString().Equals("rest-hook"))
                            {
                                t["status"] = "error";
                                t["error"] = $"ProcessSubscription: Channel Type Invalid Only rest-hook is supported Subscription/{id}";
                            }
                            else
                            {
                                //Find resource type trigger in Criteria
                                var cs = extractCriteriaResouce(t, log);
                                if (string.IsNullOrEmpty(cs))
                                {
                                    t["status"] = "error";
                                    t["error"] = $"ProcessSubscription: Criteria definition is invalid or non-existant for Subscription/{id}...Check logs";
                                }
                                else
                                {
                                    //Try criteria query see if it's valid
                                    var result = await FHIRUtils.CallFHIRServer(criteria, "", HttpMethod.Get, log);
                                    if (!result.Success)
                                    {
                                        t["status"] = "error";
                                        t["error"] = $"ProcessSubscription: Criteria is invalid on the FHIR Server Subscription/{id}: {result.Status}-{result.Content}";
                                    }
                                    else
                                    {
                                    //Cache Criteria and Activate it on FHIR Server
                                        t["status"] = "active";
                                        t["error"] = "";

                                        if (isR5Backport)
                                        {
                                            //Create the handshake bundle and send to the endpoint
                                            string subscriptionUrl = $"{fsurl}/Subscription/{id}";
                                            var handshakeBundle = createR4Bundle("handshake", subscriptionUrl, t["criteria"].ToString());
                                            try
                                            {
                                                HttpResponseMessage hbresult = await postToEndpoint(handshakeBundle, t);
                                                if (!hbresult.IsSuccessStatusCode)
                                                {
                                                    t["status"] = "error";
                                                    t["error"] = $"ProcessSubscription: Handshake with the endpoint failed for the Subscription/{id}: {hbresult.StatusCode}-{hbresult.Content}";
                                                }
                                            }
                                            catch (Exception e) {
                                                //Unhandled Exception
                                                log.LogError($"Http Client unhandled exception:{e.Message}");
                                                t["status"] = "error";
                                                t["error"] = $"ProcessSubscription: Handshake with the endpoint failed for the Subscription/{id}: {e.Message}";
                                            }
                                        }
                                        cacheSubscription(t, log);
                                        log.LogInformation($"ProcessSubscription: Subscription/{id} is now active in cache...");
                                    }
                                }
                            }
                        }
                        //update fhir server subscription with cache status
                        if (!string.IsNullOrEmpty(t["error"].ToString()))
                        {
                            log.LogError($"ProcessSubscription: {t["error"]}");
                        }
                        var saveresult = await updateFHIRSubscription(id, t.ToString(), log);
                        break;
                    case "Deleted":
                        log.LogInformation($"Deleting subscription from cache {id}...");
                        removeSubscriptionCache(id, log);
                        break;
                    default:
                        log.LogError($"ProcessSubscription: Invalid action {action} for subscription {id}");
                        break;
                }
            
          
        }
        public static async Task<FHIRResponse> updateFHIRSubscription(string id,string body,ILogger log)
        {
            log.LogInformation($"updateFHIRSubscription: Updating Subscription/{id} on FHIR server");
            var saveresult = await FHIRUtils.CallFHIRServer($"Subscription/{id}",body, HttpMethod.Put, log);
            if (!saveresult.Success)
            {
                log.LogError($"ProcessSubscription:Error updating resource Subscription/{id} on FHIR Server: {saveresult.Status}-{saveresult.Content}");
                removeSubscriptionCache(id, log);
            }
            return saveresult;
        }
        public static JObject loadCachedSubscription(string resid,ILogger log)
        {
            var cache = Utils.RedisConnection.GetDatabase();
            var s = cache.StringGet($"{RESCACHEPREFIX}{resid}");
            if (!string.IsNullOrEmpty(s))
            {
                return JObject.Parse(s);
            }
            return null;
        }
        public static void cacheSubscription(JToken token,ILogger log)
        {
            var resid = (string)token["id"];
            var restype = extractCriteriaResouce(token,log);
            if (string.IsNullOrEmpty(resid) || string.IsNullOrEmpty(restype))
            {
                throw new Exception($"saveCachedSubscription:Subscription Resource id or criteria type is empty");
            }
            removeSubscriptionCache(resid,log);
            var cache = Utils.RedisConnection.GetDatabase();
            cache.StringSet($"{RESCACHEPREFIX}{resid}", token.ToString());
            var idsbytype = loadSubscriptionIdsByType(restype,log);
            if (!idsbytype.Exists(x => x == resid))
            {
                idsbytype.Add(resid);
                cache.StringSet($"{TYPECACHEPREFIX}{restype}", idsbytype.SerializeList<string>());
            }
            log.LogInformation($"cacheSubscription:Subscription {resid} has been added to active monitor for {restype} resources");
            return;
        }
        public static void removeSubscriptionCache(string id, ILogger log)
        {
            var cache = Utils.RedisConnection.GetDatabase();
            var cs = loadCachedSubscription(id, log);
            if (cs != null)
            {
                var restype = extractCriteriaResouce(cs, log);
                var idsbytype = loadSubscriptionIdsByType(restype, log);
                idsbytype.Remove(id);
                cache.StringSet($"{TYPECACHEPREFIX}{restype}", idsbytype.SerializeList<string>());
                cache.KeyDelete($"{RESCACHEPREFIX}{id}");
                log.LogInformation($"Subscription {id} has been removed from the active processing cache.");
            }
            
        }
        public static List<string> loadSubscriptionIdsByType(string restype,ILogger log)
        {
            if (!string.IsNullOrEmpty(restype))
            {
                List<string> idsbytype = ((string)Utils.RedisConnection.GetDatabase().StringGet($"{TYPECACHEPREFIX}{restype}")).DeSerializeList<string>();
                if (idsbytype == null) idsbytype = new List<string>();
                return idsbytype;

            }
            log.LogWarning($"loadSubscriptionIdsByType: Resourcetype is null");
            return null;
        }
        public static string extractCriteriaResouce(JToken token,ILogger log)
        {
            string s = isR5Backport ? token["_criteria"]["extension"][0]["valueString"].ToString() : token["criteria"].ToString();
            if (string.IsNullOrEmpty(s)) {
                log.LogWarning($"extractCriteriaResouce:Criteria is null or empty string");
                return null;
            };
            var cs = s.Split("?");
            if (cs.Count() < 2 )
            {
                log.LogWarning($"extractCriteriaResouce:Criteria does not contain a query string");
                return null;
            }
            if (!cs[0].All(Char.IsLetter))
            {
                log.LogWarning($"extractCriteriaResouce:Criteria resource is not valid");
                return null;
            }
            return cs[0];
        }

        public static string createR4Bundle(string type, string subscriptionUrl, string topic, string focusReference = null, FHIRResponse fullresource = null)
        {
            string profileBundle = "http://hl7.org/fhir/uv/subscriptions-backport/StructureDefinition/backport-subscription-notification-r4";
            string profileStatus = "http://hl7.org/fhir/uv/subscriptions-backport/StructureDefinition/backport-subscription-status-r4";
            string timestamp = DateTime.UtcNow.ToString("o");

            JArray parameters = new JArray
            {
                new JObject { ["name"] = "subscription", ["valueReference"] = new JObject { ["reference"] = subscriptionUrl } },
                new JObject { ["name"] = "topic", ["valueCanonical"] = topic },
                new JObject { ["name"] = "status", ["valueCode"] = "active" },
                new JObject { ["name"] = "type", ["valueCode"] = "event-notification" },
                new JObject { ["name"] = "events-since-subscription-start", ["valueString"] = "2" }
            };

            switch (type.ToLower())
            {
                case "handshake":
                    parameters[2] = new JObject { ["name"] = "status", ["valueCode"] = "requested" };
                    parameters[3] = new JObject { ["name"] = "type", ["valueCode"] = "handshake" };
                    parameters[4] = new JObject { ["name"] = "events-since-subscription-start", ["valueString"] = "0" };
                    break;
                case "empty":
                    parameters.Add(
                        new JObject
                        {
                            ["name"] = "notification-event",
                            ["part"] = new JArray
                            {
                                new JObject { ["name"] = "event-number", ["valueString"] = "2" },
                                new JObject { ["name"] = "timestamp", ["valueInstant"] = timestamp }
                            }
                        }
                    );
                    break;
                case "id-only":
                case "full-resource":
                    parameters.Add(
                        new JObject
                        {
                            ["name"] = "notification-event",
                            ["part"] = new JArray
                            {
                                new JObject { ["name"] = "event-number", ["valueString"] = "2" },
                                new JObject { ["name"] = "timestamp", ["valueInstant"] = timestamp },
                                new JObject { ["name"] = "focus", ["valueReference"] = new JObject { ["reference"] = focusReference } }
                            }
                        }
                    );
                    break;
            }

            JArray entryList = new JArray();

            string paramId = Guid.NewGuid().ToString();
            JObject parametersEntry = new JObject
            {
                ["fullUrl"] = $"urn:uuid:{paramId}",
                ["resource"] = new JObject
                {
                    ["resourceType"] = "Parameters",
                    ["id"] = paramId,
                    ["meta"] = new JObject { ["profile"] = new JArray(profileStatus) },
                    ["parameter"] = parameters
                }
            };

            entryList.Add(parametersEntry);

            if (type.Equals("full-resource", StringComparison.OrdinalIgnoreCase))
            {
                // clean & unescape JSON
                string raw = fullresource.Content;
                raw = Regex.Unescape(raw);

                if (raw.StartsWith("\"") && raw.EndsWith("\""))
                    raw = raw.Substring(1, raw.Length - 2);

                JObject fullResourceObj = JObject.Parse(raw);

                JObject resourceEntry = new JObject
                {
                    ["fullUrl"] = focusReference,
                    ["resource"] = fullResourceObj
                };

                entryList.Add(resourceEntry);
            }

            JObject bundle = new JObject
            {
                ["resourceType"] = "Bundle",
                ["id"] = Guid.NewGuid().ToString(),
                ["meta"] = new JObject { ["profile"] = new JArray(profileBundle) },
                ["type"] = "history",
                ["timestamp"] = timestamp,
                ["entry"] = entryList
            };

            return bundle.ToString(Newtonsoft.Json.Formatting.Indented);
        }

        public static (string ResourceType, string CurrentValue) GetResourceTriggerAsync(JToken root, ILogger log)
        {
            var extensions = root["extension"] as JArray;
            if (extensions == null)
            {
                log.LogError($"No extensions found in the subscription topic");
                return (null, null);
            }

            foreach (var ext in extensions)
            {
                // Find resourceTrigger extension
                string urlValue = ext["url"]?.ToString();
                if (urlValue != null && urlValue.Contains("extension-SubscriptionTopic.resourceTrigger", StringComparison.OrdinalIgnoreCase))
                {
                    var innerExts = ext["extension"] as JArray;
                    if (innerExts == null) continue;

                    string resourceType = null;
                    string currentValue = null;

                    foreach (var inner in innerExts)
                    {
                        string innerUrl = inner["url"]?.ToString();
                        if (innerUrl == "resource")
                            resourceType = inner["valueUri"]?.ToString();
                        else if (innerUrl == "queryCriteria")
                        {
                            var qExts = inner["extension"] as JArray;
                            if (qExts != null)
                            {
                                foreach (var q in qExts)
                                {
                                    if (q["url"]?.ToString() == "current")
                                    {
                                        currentValue = q["valueString"]?.ToString();
                                        break;
                                    }
                                }
                            }
                        }
                    }

                    if (resourceType != null && currentValue != null)
                    {
                        log.LogError($"Found ResourceTrigger for the Resource :{resourceType} and filter criteria: {currentValue} ");
                        return (resourceType, currentValue);
                    }
                }
            }

            log.LogError($"No resourceTrigger found in the subscription topic");
            return (null, null);

        }

        public static async Task<HttpResponseMessage> postToEndpoint(string b, JToken t)
        {
            // Extract channel.endpoint
            string cep = t["channel"]?["endpoint"]?.ToString();
            if (string.IsNullOrEmpty(cep))
            {
                Console.WriteLine("No channel endpoint defined.");
                return await Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }

            // Prepare HTTP request
            using var request = new HttpRequestMessage(HttpMethod.Post, cep);
            request.Content = new StringContent(b, Encoding.UTF8, "application/fhir+json");

            // Optional: Add headers from channel.header[]
            var headers = t["channel"]?["header"] as JArray;
            if (headers != null)
            {
                foreach (var head in headers)
                {
                    var headerLine = head.ToString();
                    var split = headerLine.Split(':', 2);
                    if (split.Length == 2)
                    {
                        string headerName = split[0].Trim();
                        string headerValue = split[1].Trim();
                        if (headerName.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                            continue;
                        request.Headers.TryAddWithoutValidation(headerName, headerValue);
                    }
                }
            }

            // Send notification
            using var response = await httpClient.SendAsync(request);
            return response;
        }

    }
}
