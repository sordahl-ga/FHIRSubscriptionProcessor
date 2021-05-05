using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FHIRSubscriptionProcessor
{
    public static class ReloadSubscriptionCache
    {
        [FunctionName("ReloadSubscriptionCache")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Function, "get", Route = null)] HttpRequest req,
            ILogger log)
        {
            log.LogInformation("Reloading subscription cache....");
            var fhirresp = await FHIRClient.CallFHIRServer("Subscription?status=active&_count=1000", null, "GET", log);
            if (fhirresp.IsSuccess())
            {
                JToken t = fhirresp.toJToken();
                if (!t["entry"].IsNullOrEmpty())
                {
                    JArray subs = (JArray)t["entry"];
                    foreach (JToken r in subs)
                    {
                        FHIRSubscriptionProcessor.cacheSubscription(r, log);
                    }
                }
                return new OkObjectResult("Subscription Cache Reloaded");
            } else
            {
                return new BadRequestObjectResult(fhirresp.ToString());
            }

            
        }
    }
}
