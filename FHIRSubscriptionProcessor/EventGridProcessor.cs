// Default URL for triggering event grid function in the local environment.
// http://localhost:7071/runtime/webhooks/EventGrid?functionName={functionname}
using System;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Azure.EventGrid.Models;
using Microsoft.Azure.WebJobs.Extensions.EventGrid;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using System.Threading.Tasks;
using Microsoft.Azure.ServiceBus;
using Microsoft.Azure.WebJobs.ServiceBus;

namespace FHIRSubscriptionProcessor
{
    public static class EventGridProcessor
    {
        [FunctionName("SubscriptionEventGridProcessor")]
        public static async Task Run([EventGridTrigger]EventGridEvent eventGridEvent,
                                     [ServiceBus("%FSP-NOTIFYSB-TOPIC%", Connection = "FSP-NOTIFYSB-CONNECTION", EntityType = EntityType.Topic)] IAsyncCollector<Message> outputTopic,
                                     ILogger log)
        {
            log.LogInformation($"SubscriptionEventGridProcessor: Processing event type:{eventGridEvent.EventType} data:{eventGridEvent.Data.ToString()}");
            try
            {
                string action = "";
                switch (eventGridEvent.EventType)
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
                JObject m = JObject.Parse(eventGridEvent.Data.ToString());
                if (!m["ResourceType"].IsNullOrEmpty() && m["ResourceType"].ToString().Equals("Subscription"))
                {
                    await EventHubProcessor.ProcessSubscription(m["ResourceFhirId"].ToString(),action, log);
                }
                else
                {
                    await EventHubProcessor.ProcessResourceEvent(m["ResourceType"].ToString(), m["ResourceFhirId"].ToString(),action, outputTopic, log);
                }
            }
            catch (Exception e)
            {
                log.LogError($"SubscriptionEventGridProcessor: Error receiving event:{e.StackTrace}");
            }

        }
    }
}
