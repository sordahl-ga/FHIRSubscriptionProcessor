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
            var t = FHIRSubscriptionProcessor.loadCachedSubscription(subid, log);
            if (t["channel"].IsNullOrEmpty() || t["channel"]["type"].IsNullOrEmpty() || !t["channel"]["type"].ToString().Equals("rest-hook"))
            {
                string err = "NotifyProcessor: Channel type is not supported. Must be rest-hook";
                log.LogError(err);
                await messageReceiver.DeadLetterAsync(msg.SystemProperties.LockToken, err);
            }
            if (t["channel"].IsNullOrEmpty() || t["channel"]["endpoint"].IsNullOrEmpty())
            {
                string err = "NotifyProcessor: Channel endpoint is not defined";
                log.LogError(err);
                await messageReceiver.DeadLetterAsync(msg.SystemProperties.LockToken, err);
            }
            //POST to Channel Endpoint to notify of criteria met.
            try
            {
                string cep = t["channel"]["endpoint"].ToString();
                using (System.Net.WebClient client = new System.Net.WebClient())
                {

                    byte[] response =
                        client.UploadValues(cep, new NameValueCollection());
                    string result = System.Text.Encoding.UTF8.GetString(response);
                    
                }
                t["status"] = "active";
                t["error"] = "";
                await FHIRSubscriptionProcessor.updateFHIRSubscription(subid, t.ToString(), log);
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
                          await RetryMessageAsync(msg, messageReceiver, outputTopic, log);
                    }
                    else
                    {
                        log.LogError($"Web Client permanent failure:{we.Message}...Notify Subscription/{subid} Message is dead lettered!");
                        await messageReceiver.DeadLetterAsync(msg.SystemProperties.LockToken, response.StatusDescription);
                    }
                } else
                {
                    await messageReceiver.DeadLetterAsync(msg.SystemProperties.LockToken, we.Message);
                    log.LogError($"Web Client error:{we.Message}...Notify Subscription/{subid} Message is dead lettered!");
                }
                                   
            }
            catch (Exception e)
            {
                //Unhandled Exception Deadletter the message
                await messageReceiver.DeadLetterAsync(msg.SystemProperties.LockToken, e.Message);

            }
        }
        private static async Task RetryMessageAsync(Message msg, MessageReceiver messageReceiver, IAsyncCollector<Message> outputTopic,ILogger log)
        {
            int rtc = Utils.GetIntEnvironmentVariable("FSP-MAX-NOTIFY-RETRIES", "5");
            const string retryCountString = "RetryCount";
            // get our custom RetryCount property from the received message if it exists
            // if not, initiate it to 0
            var retryCount = msg.UserProperties.ContainsKey(retryCountString)
                ? (int)msg.UserProperties[retryCountString]
                : 0;
            // if we've tried 10 times or more, deadletter this message
            if (retryCount >= 10)
            {
                await messageReceiver.DeadLetterAsync(msg.SystemProperties.LockToken, $"Retry count > {rtc}");
                return;
            }
            // create a copy of the received message
            var clonedMessage = msg.Clone();
            // set the ScheduledEnqueueTimeUtc to 30 seconds from now
            clonedMessage.ScheduledEnqueueTimeUtc = DateTime.UtcNow.AddSeconds(30);
            clonedMessage.UserProperties[retryCountString] = retryCount + 1;
            await outputTopic.AddAsync(clonedMessage);

            // IMPORTANT- Complete the original Message!
            await messageReceiver.CompleteAsync(msg.SystemProperties.LockToken);
        }
    }
}
