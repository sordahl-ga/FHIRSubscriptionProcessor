using System;
using Microsoft.Azure.WebJobs;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs.ServiceBus;

using Azure.Messaging.ServiceBus;

namespace FHIRSubscriptionProcessor
{
    public class SubscriptionQueueProcessor
    {
        [FunctionName("SubscriptionQueueProcessor")]
        public async Task Run([QueueTrigger("%FSP-STORAGEQUEUENAME%", Connection = "FSP-STORAGEACCOUNT")] JObject fhirevent,
                              [ServiceBus("%FSP-NOTIFYSB-TOPIC%", Connection = "FSP-NOTIFYSB-CONNECTION", EntityType = ServiceBusEntityType.Topic)] IAsyncCollector<ServiceBusMessage> outputTopic,
                                     ILogger log)
        {
            string eventtype = fhirevent["eventType"].ToString();
            log.LogInformation($"SubscriptionEventGridProcessor: Processing event type:{fhirevent["eventType"].ToString()} data:{fhirevent["data"].ToString(Newtonsoft.Json.Formatting.None)}");
            try
            {
                string action = "";
                switch (eventtype)
                {
                    case "Microsoft.HealthcareApis.FhirResourceCreated":
                        action = "Created";
                        break;
                    case "Microsoft.HealthcareApis.FhirResourceUpdated":
                        action = "Updated";
                        break;
                    case "Microsoft.HealthcareApis.FhirResourceDeleted":
                        action = "Deleted";
                        break;

                }
                JToken m = fhirevent["data"];
                if (!m["resourceType"].IsNullOrEmpty() && m["resourceType"].ToString().Equals("Subscription"))
                {
                    await EventHubProcessor.ProcessSubscription(m["resourceFhirId"].ToString(),action, log);
                }
                else
                {
                    await EventHubProcessor.ProcessResourceEvent(m["resourceType"].ToString(), m["resourceFhirId"].ToString(),action, outputTopic, log);
                }
            }
            catch (Exception e)
            {
                log.LogError($"SubscriptionEventGridProcessor: Error receiving event:{e.StackTrace}");
            }

        }
    }
}
