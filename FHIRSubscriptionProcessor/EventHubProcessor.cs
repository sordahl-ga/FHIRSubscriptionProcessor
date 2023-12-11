using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Azure.Messaging.EventHubs;
using Azure.Messaging.ServiceBus;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.ServiceBus;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using StackExchange.Redis;

namespace FHIRSubscriptionProcessor
{
    public static class EventHubProcessor
    {
        public static readonly string RESCACHEPREFIX = "sx-resources-";
        public static readonly string TYPECACHEPREFIX = "sx-types-";
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
                var criteria = sr["criteria"].ToString();
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
                log.LogInformation($"ProcessResourceEvent: Evalutating Subscription/{id} criteria:{criteria}");
                var result = await FHIRUtils.CallFHIRServer(criteria, "", HttpMethod.Get, log);
                if (result.Success)
                {
                    JToken fhirresp = JObject.Parse(result.Content);
                    if (!fhirresp["entry"].IsNullOrEmpty())
                    {
                        //Resource met criteria so lets queue it to the notify processor
                        log.LogInformation($"ProcessResourceEvent: {restype}/{resid} met criteria for Subscription/{id} Adding to notify queue...");
                        ServiceBusMessage msg = new ServiceBusMessage();
                        msg.Body = BinaryData.FromString(id);
                        await outputTopic.AddAsync(msg);
                    } else
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
                        string criteria = t["criteria"].ToString();
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
            var s = (string)token["criteria"];
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
        
    }
}
