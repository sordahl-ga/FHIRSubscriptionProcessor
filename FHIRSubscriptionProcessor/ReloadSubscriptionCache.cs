using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Net.Http;

namespace FHIRSubscriptionProcessor
{
    public static class ReloadSubscriptionCache
    {
        private static Object lockObj = new object();
        private static Task reloadtask = null;
        [FunctionName("ReloadSubscriptionCache")]
        public static IActionResult Run(
            [HttpTrigger(AuthorizationLevel.Function, "get", Route = null)] HttpRequest req,
            ILogger log)
        {
            if (reloadtask == null || reloadtask.IsCompleted)
            {
                reloadtask = Task.Run(() =>
                {

                    log.LogInformation("Clearing subscription cache....");
                    string[] rc = Utils.GetEnvironmentVariable("FSP-REDISCONNECTION", "").Split(",");
                    if (rc.Length > 0) {
                        var server = Utils.RedisConnection.GetServer(rc[0]);
                        foreach (var key in server.Keys())
                        {
                            string k = key.ToString();
                            if (k.StartsWith(EventHubProcessor.RESCACHEPREFIX))
                            {
                                string id = k.Substring(EventHubProcessor.RESCACHEPREFIX.Length);
                                EventHubProcessor.removeSubscriptionCache(id, log);
                            }
                        }
                        log.LogInformation("Reloading subscription cache....");
                        var fhirresp = FHIRUtils.CallFHIRServer("Subscription?status=active&_count=1000", null, HttpMethod.Get, log).GetAwaiter().GetResult();
                        if (fhirresp.Success)
                        {
                            JToken t = JObject.Parse(fhirresp.Content);
                            if (!t["entry"].IsNullOrEmpty())
                            {
                                JArray subs = (JArray)t["entry"];
                                foreach (JToken r in subs)
                                {
                                    EventHubProcessor.cacheSubscription(r["resource"], log);
                                }
                            }
                            log.LogInformation("Subscription Cache has been successfully reloaded");
                        } else
                        {
                            log.LogError($"Subscription Cache reload exception calling FHIR Server: {fhirresp}");
                        }

                    } else {
                        log.LogError($"Subscription reload invalida redis connection string...");
                    }
                });
                return new OkObjectResult("Reload Subscription Cache has started...");
            }
            return new ConflictObjectResult("Subcription cache reload is currently running....");

        }
    }
}
