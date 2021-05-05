using Microsoft.AspNetCore.Http;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Table;
using StackExchange.Redis;
namespace FHIRSubscriptionProcessor
{
    public class Utils
    {
   
        private static Lazy<ConnectionMultiplexer> lazyConnection = new Lazy<ConnectionMultiplexer>(() =>
        {
            string cacheConnection = GetEnvironmentVariable("FSP-REDISCONNECTION");
            return ConnectionMultiplexer.Connect(cacheConnection);
        });
        public static ConnectionMultiplexer RedisConnection
        {
            get
            {
                return lazyConnection.Value;
            }
        }
        
        public static string GetEnvironmentVariable(string varname, string defval = null)
        {
            if (string.IsNullOrEmpty(varname)) return null;
            string retVal = System.Environment.GetEnvironmentVariable(varname);
            if (defval != null && retVal == null) return defval;
            return retVal;
        }
        public static bool GetBoolEnvironmentVariable(string varname, bool defval = false)
        {
            var s = GetEnvironmentVariable(varname);
            if (string.IsNullOrEmpty(s)) return defval;
            if (s.Equals("1") || s.Equals("yes", System.StringComparison.InvariantCultureIgnoreCase) || s.Equals("true", System.StringComparison.InvariantCultureIgnoreCase))
            {
                return true;
            }
            if (s.Equals("0") || s.Equals("no", System.StringComparison.InvariantCultureIgnoreCase) || s.Equals("false", System.StringComparison.InvariantCultureIgnoreCase))
            {
                return false;
            }
            throw new Exception($"GetBoolEnvironmentVariable: Unparsable boolean environment variable for {varname} : {s}");
        }
        public static int GetIntEnvironmentVariable(string varname, string defval = null)
        {


            string retVal = System.Environment.GetEnvironmentVariable(varname);
            if (defval != null && retVal == null) retVal = defval;
            return int.Parse(retVal);
        }

    }
    
}
