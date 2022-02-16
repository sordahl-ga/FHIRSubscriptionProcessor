using System;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using Microsoft.Azure.ServiceBus;
using Microsoft.Azure.WebJobs.ServiceBus;
using Microsoft.Azure.ServiceBus.Core;
using System.Collections.Specialized;
using System.Net;
using Newtonsoft.Json.Linq;

namespace FHIRSubscriptionProcessor
{
    public static class NotifyProcessor
    {
        [FunctionName("NotifyProcessor")]
        public static async Task Run([ServiceBusTrigger("%FSP-NOTIFYSB-TOPIC%", "%FSP-NOTIFYSB-SUBSCRIPTION%", Connection = "FSP-NOTIFYSB-CONNECTION")] Message msg,
                                     [ServiceBus("%FSP-NOTIFYSB-TOPIC%", Connection = "FSP-NOTIFYSB-CONNECTION", EntityType = EntityType.Topic)] IAsyncCollector<Message> outputTopic,
                                      MessageReceiver messageReceiver,
                                      ILogger log)
        {
            var subid = System.Text.Encoding.UTF8.GetString(msg.Body);
            var t = EventHubProcessor.loadCachedSubscription(subid, log);
            if (t==null)
            {
                string err = $"NotifyProcessor: No Cached Subscription found id: {subid}";
                log.LogError(err);
                await messageReceiver.DeadLetterAsync(msg.SystemProperties.LockToken, err);
                return;
            }
            if (t["channel"].IsNullOrEmpty() || t["channel"]["type"].IsNullOrEmpty() || !t["channel"]["type"].ToString().Equals("rest-hook"))
            {
                string err = "NotifyProcessor: Channel type is not supported. Must be rest-hook";
                log.LogError(err);
                await messageReceiver.DeadLetterAsync(msg.SystemProperties.LockToken, err);
                return;
            }
            if (t["channel"].IsNullOrEmpty() || t["channel"]["endpoint"].IsNullOrEmpty())
            {
                string err = "NotifyProcessor: Channel endpoint is not defined";
                log.LogError(err);
                await messageReceiver.DeadLetterAsync(msg.SystemProperties.LockToken, err);
                return;
            }
            //POST to Channel Endpoint to notify of criteria met.
            try
            {
                string cep = t["channel"]["endpoint"].ToString();
                using (System.Net.WebClient client = new System.Net.WebClient())
                {
                    if (!t["channel"]["header"].IsNullOrEmpty())
                    {
                        JArray arr = (JArray)t["channel"]["header"];
                        foreach (JToken head in arr)
                        {
                            client.Headers.Add(head.ToString());
                        }
                    }
                    byte[] response =
                        client.UploadValues(cep, new NameValueCollection());
                    string result = System.Text.Encoding.UTF8.GetString(response);
                    
                }
                await messageReceiver.CompleteAsync(msg.SystemProperties.LockToken);
            }
            catch (System.Net.WebException we)
            {
                HttpWebResponse response = (System.Net.HttpWebResponse)we.Response;
                if (response != null)
                {
                    //Retry on Transient Errors otherwise deadlettter for permanent
                    if (response.StatusCode == HttpStatusCode.TooManyRequests || response.StatusCode == HttpStatusCode.ServiceUnavailable)
                    {
                          log.LogWarning($"Web Client Transient Error:{we.Message}...Notify Subscription/{subid} is requeing for retry!");
                          await RetryMessageAsync(msg, messageReceiver, outputTopic, t, log);
                    }
                    else
                    {
                        await messageReceiver.DeadLetterAsync(msg.SystemProperties.LockToken, response.StatusDescription);
                        await ProcessSubscriptionInError(t, we.Message, log);
                    }
                } else
                {
                    await messageReceiver.DeadLetterAsync(msg.SystemProperties.LockToken, we.Message);
                    await ProcessSubscriptionInError(t, we.Message, log);
                }
                                   
            }
            catch (Exception e)
            {
                //Unhandled Exception Deadletter the message
                log.LogError($"Web Client unhandled exception:{e.Message}");
                await messageReceiver.DeadLetterAsync(msg.SystemProperties.LockToken, e.Message);
                await ProcessSubscriptionInError(t, e.Message, log);
            }
        }
        private static async Task RetryMessageAsync(Message msg, MessageReceiver messageReceiver, IAsyncCollector<Message> outputTopic,JToken t, ILogger log)
        {
            int rtc = Utils.GetIntEnvironmentVariable("FSP-NOTIFY-MAXRETRIES", "5");
            const string retryCountString = "RetryCount";
            // get our custom RetryCount property from the received message if it exists
            // if not, initiate it to 0
            var retryCount = msg.UserProperties.ContainsKey(retryCountString)
                ? (int)msg.UserProperties[retryCountString]
                : 0;
            // if we've exceeded retry count linit, deadletter this message, mark subscription in error
            if (retryCount > rtc)
            {
                await messageReceiver.DeadLetterAsync(msg.SystemProperties.LockToken, $"Retry count > {rtc}");
                await ProcessSubscriptionInError(t, "Notification channel exceeded max retry count", log);
                return;
            }
            // create a copy of the received message
            var clonedMessage = msg.Clone();
            // set the ScheduledEnqueueTimeUtc to configured seconds from now default is 30
            int rsecs = Utils.GetIntEnvironmentVariable("FSP-NOTIFY-RETRYAFTER-SECONDS", "30");
            clonedMessage.ScheduledEnqueueTimeUtc = DateTime.UtcNow.AddSeconds(rsecs);
            clonedMessage.UserProperties[retryCountString] = retryCount + 1;
            await outputTopic.AddAsync(clonedMessage);

            // IMPORTANT- Complete the original Message!
            await messageReceiver.CompleteAsync(msg.SystemProperties.LockToken);
        }
        private static async Task ProcessSubscriptionInError(JToken t, string errmsg, ILogger log)
        {
            string id = t["id"].ToString();
            t["status"] = "error";
            t["error"] = errmsg;
            EventHubProcessor.removeSubscriptionCache(id, log);
            var fr = await EventHubProcessor.updateFHIRSubscription(id, t.ToString(), log);
            if (fr.IsSuccess())
            {
                log.LogError($"NotifyProcessor: Channel notify permanent error:{errmsg}...Notify Subscription/{id} status is now error!");
            } else
            {
                log.LogError($"NotifyProcessor: Error updating Subscription/{id} on FHIR Server:{fr.ToString()}");
            }
        }
    }
  
}
