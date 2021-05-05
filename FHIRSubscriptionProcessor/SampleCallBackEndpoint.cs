using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace FHIRSubscriptionProcessor
{
    public static class SampleCallBackEndpoint
    {
        [FunctionName("SampleCallBackEndpoint")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = null)] HttpRequest req,
            ILogger log)
        {
            log.LogInformation("SampleCallBackEndpoint processed a request.");

            string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
           
            return new OkObjectResult("");
        }
    }
}
