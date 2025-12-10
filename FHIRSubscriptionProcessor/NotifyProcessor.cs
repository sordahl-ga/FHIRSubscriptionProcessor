using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs;
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.WebJobs.ServiceBus;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;


namespace FHIRSubscriptionProcessor
{
    public static class NotifyProcessor
    {
        private static string fsurl = Utils.GetEnvironmentVariable("FS-URL");
        private static bool isR5Backport = Utils.GetBoolEnvironmentVariable("FS-ISR5BACKPORT", false);

        [FunctionName("NotifyProcessor")]
        public static async Task Run([ServiceBusTrigger("%FSP-NOTIFYSB-TOPIC%", "%FSP-NOTIFYSB-SUBSCRIPTION%", Connection = "FSP-NOTIFYSB-CONNECTION")] ServiceBusReceivedMessage msg,
                                     [ServiceBus("%FSP-NOTIFYSB-TOPIC%", Connection = "FSP-NOTIFYSB-CONNECTION", EntityType = ServiceBusEntityType.Topic)] IAsyncCollector<ServiceBusMessage> outputTopic,
                                      ServiceBusMessageActions messageActions,
                                      ILogger log)
        {           
            // var subid = System.Text.Encoding.UTF8.GetString(msg.Body);
            string body = msg.Body.ToString();
            log.LogInformation($"Received message body: {body}");

            // Parse JSON
            using var doc = JsonDocument.Parse(body);
            JsonElement root = doc.RootElement;

            string subid = root.GetProperty("subscriptionId").GetString();
            string resource = root.GetProperty("resource").GetString();

            var t = EventHubProcessor.loadCachedSubscription(subid, log);
            if (t==null)
            {
                string err = $"NotifyProcessor: No Cached Subscription found id: {subid}";
                log.LogError(err);
                await messageActions.DeadLetterMessageAsync(msg);
                return;
            }
            if (t["channel"].IsNullOrEmpty() || t["channel"]["type"].IsNullOrEmpty() || !t["channel"]["type"].ToString().Equals("rest-hook"))
            {
                string err = "NotifyProcessor: Channel type is not supported. Must be rest-hook";
                log.LogError(err);
                await messageActions.DeadLetterMessageAsync(msg, err);
                return;
            }
            if (t["channel"].IsNullOrEmpty() || t["channel"]["endpoint"].IsNullOrEmpty())
            {
                string err = "NotifyProcessor: Channel endpoint is not defined";
                log.LogError(err);
                await messageActions.DeadLetterMessageAsync(msg, err);
                return;
            }
            //POST to Channel Endpoint to notify of criteria met.
            try
            {
                if (isR5Backport)
                {
                    string prc = t["channel"]["_payload"]["extension"][0]["valueCode"].ToString();
                    string subscriptionUrl = $"{fsurl}/Subscription/{subid}";
                    string eventBundle = string.Empty ;
                    if (!string.IsNullOrEmpty(prc) && prc.Equals("full-resource", StringComparison.OrdinalIgnoreCase))
                    {
                        var fhirresp = await FHIRUtils.CallFHIRServer(resource, null, HttpMethod.Get, log);
                        log.LogInformation($"Content : {fhirresp.Content}");
                        eventBundle = EventHubProcessor.createR4Bundle(prc, subscriptionUrl, t["criteria"].ToString(), $"{fsurl}/{resource}", fhirresp);
                    }
                    else
                    {
                        eventBundle = EventHubProcessor.createR4Bundle(prc, subscriptionUrl, t["criteria"].ToString(), $"{fsurl}/{resource}");
                    }
                    HttpResponseMessage hbresult = await EventHubProcessor.postToEndpoint(eventBundle, t);
                    if (!hbresult.IsSuccessStatusCode)
                    {
                        string econtent = $"ProcessSubscription: Event Notification failed for the Subscription/{subid}: {hbresult.StatusCode}-{hbresult.Content} while sending to endpoint";
                        await ProcessSubscriptionInError(t, econtent, log);
                    }
                }
                else
                {
                    string es = string.Empty;
                    HttpResponseMessage response = await EventHubProcessor.postToEndpoint(es, t);
                    if (response.IsSuccessStatusCode)
                    {
                        log.LogInformation($"Subscription/{subid} notification succeeded.");
                    }
                    else if (response.StatusCode == HttpStatusCode.TooManyRequests ||
                             response.StatusCode == HttpStatusCode.ServiceUnavailable)
                    {
                        // Retry transient failures
                        log.LogWarning($"Transient HTTP error ({(int)response.StatusCode} {response.ReasonPhrase})... Requeuing Subscription/{subid} for retry.");
                        await RetryMessageAsync(msg, messageActions, outputTopic, t, log);
                        
                    }
                    else
                    {
                        // Permanent failure — dead-letter
                        string econtent = $"ProcessSubscription: Event Notification failed for the Subscription/{subid}: {response.StatusCode}-{response.Content} while sending to endpoint";
                        log.LogError($"HTTP error ({(int)response.StatusCode} {response.ReasonPhrase}) for Subscription/{subid}");
                        await messageActions.DeadLetterMessageAsync(msg, $"HTTP {response.StatusCode}: {response.ReasonPhrase}");
                        await ProcessSubscriptionInError(t, econtent, log);
                        
                    }                
                }
                await messageActions.CompleteMessageAsync(msg);
            }
            catch (Exception e)
            {
                //Unhandled Exception Deadletter the message
                log.LogError($"Http Client unhandled exception:{e.Message}");
                await messageActions.DeadLetterMessageAsync(msg, e.Message);
                await ProcessSubscriptionInError(t, e.Message, log);
            }
        }
        private static async Task RetryMessageAsync(ServiceBusReceivedMessage msg, ServiceBusMessageActions messageActions, IAsyncCollector<ServiceBusMessage> outputTopic,JToken t, ILogger log)
        {
            int rtc = Utils.GetIntEnvironmentVariable("FSP-NOTIFY-MAXRETRIES", "5");
            const string retryCountString = "RetryCount";
            // get our custom RetryCount property from the received message if it exists
            // if not, initiate it to 0
            var retryCount = msg.ApplicationProperties.ContainsKey(retryCountString)
                ? (int)msg.ApplicationProperties[retryCountString]
                : 0;
            // if we've exceeded retry count linit, deadletter this message, mark subscription in error
            if (retryCount > rtc)
            {
                await messageActions.DeadLetterMessageAsync(msg, $"Retry count > {rtc}");
                await ProcessSubscriptionInError(t, "Notification channel exceeded max retry count", log);
                return;
            }
            // create a copy of the received message
            var clonedMessage = new ServiceBusMessage(msg);
            // set the ScheduledEnqueueTimeUtc to configured seconds from now default is 30
            int rsecs = Utils.GetIntEnvironmentVariable("FSP-NOTIFY-RETRYAFTER-SECONDS", "30");
            clonedMessage.ScheduledEnqueueTime = DateTimeOffset.Now.AddSeconds(rsecs);
            clonedMessage.ApplicationProperties[retryCountString] = retryCount + 1;
            await outputTopic.AddAsync(clonedMessage);

            // IMPORTANT- Complete the original Message!
            await messageActions.CompleteMessageAsync(msg);
        }
        private static async Task ProcessSubscriptionInError(JToken t, string errmsg, ILogger log)
        {
            string id = t["id"].ToString();
            t["status"] = "error";
            t["error"] = errmsg;
            EventHubProcessor.removeSubscriptionCache(id, log);
            var fr = await EventHubProcessor.updateFHIRSubscription(id, t.ToString(), log);
            if (fr.Success)
            {
                log.LogError($"NotifyProcessor: Channel notify permanent error:{errmsg}...Notify Subscription/{id} status is now error!");
            } else
            {
                log.LogError($"NotifyProcessor: Error updating Subscription/{id} on FHIR Server:{fr.Status}-{fr.Content}");
            }
        }
    }
  
}
