﻿/* 
* 2020 Microsoft Corp
* 
* THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS “AS IS”
* AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO,
* THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE
* ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE
* FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
* (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
* LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION)
* HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
* OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
* OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
*/

using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text;
using Newtonsoft.Json;
using Microsoft.AspNetCore.Http;

namespace FHIRSubscriptionProcessor
{
    public class FHIRParsedPath
    {
        public string ResourceType { get; set; }
        public string ResourceId { get; set; }
        public string Operation { get; set; }
        public string VersionId { get; set; }
        public IEnumerable<string> PathElements { get; set; }
    }
    public static class Extensions
    {
        
        public static bool SafeEquals(this string source, string compare)
        {
            if (compare == null) return false;
            return source.Equals(compare);
        }
        public static string SerializeList<T>(this List<T> thelist)
        {
            if (thelist == null) return null;
            return JsonConvert.SerializeObject(thelist);
        }
        public static List<T> DeSerializeList<T>(this string str)
        {
            if (string.IsNullOrEmpty(str)) return null;
            return JsonConvert.DeserializeObject<List<T>>(str);
        }
        public static string FHIRResourceId(this JToken token)
        {
            if (!token.IsNullOrEmpty()) return (string)token["id"];
            return "";
        }
        public static string FHIRResourceType(this JToken token)
        {
            if (!token.IsNullOrEmpty()) return (string)token["resourceType"];
            return "";
        }
        public static string FHIRVersionId(this JToken token)
        {
            if (!token.IsNullOrEmpty() && !token["meta"].IsNullOrEmpty())
            {
                return (string)token["meta"]?["versionId"];
            }
            return "";
        }
        public static string FHIRLastUpdated(this JToken token)
        {
            if (!token.IsNullOrEmpty() && !token["meta"].IsNullOrEmpty() && !token["meta"]["lastUpdated"].IsNullOrEmpty())
            {
                return JsonConvert.SerializeObject(token["meta"]?["lastUpdated"]).Replace("\"", "");
            }
            return "";
        }
        public static string FHIRReferenceId(this JToken token)
        {
            if (!token.IsNullOrEmpty() && !token["resourceType"].IsNullOrEmpty() && !token["id"].IsNullOrEmpty())
            {
                return (string)token["resourceType"] + "/" + (string)token["id"];
            }
            return "";
        }
        public static bool IsNullOrEmpty(this JToken token)
        {
            return (token == null) ||
                   (token.Type == JTokenType.Array && !token.HasValues) ||
                   (token.Type == JTokenType.Object && !token.HasValues) ||
                   (token.Type == JTokenType.String && token.ToString() == String.Empty) ||
                   (token.Type == JTokenType.Null);
        }
        public static JToken getFirstField(this JToken o)
        {
            if (o == null) return null;
            if (o.Type == JTokenType.Array)
            {
                if (o.HasValues) return ((JArray)o)[0];
                return null;
            }
            return o;
        }
        public static string ToAzureKeyString(this string str)
        {
            var sb = new StringBuilder();
            foreach (var c in str
                .Where(c => c != '/'
                            && c != '\\'
                            && c != '#'
                            && c != '/'
                            && c != '?'
                            && !char.IsControl(c)))
                sb.Append(c);
            return sb.ToString();
        }
        public static IEnumerable<T> All<T>(
        this IQueryCollection collection,
        string key)
        {
            var values = new List<T>();

            if (collection.TryGetValue(key, out var results))
            {
                foreach (var s in results)
                {
                    try
                    {
                        var result = (T)Convert.ChangeType(s, typeof(T));
                        values.Add(result);
                    }
                    catch (Exception)
                    {
                        // conversion failed
                        // skip value
                    }
                }
            }

            // return an array with at least one
            return values;
        }

        public static T Get<T>(
            this IQueryCollection collection,
            string key,
            T @default = default,
            ParameterPick option = ParameterPick.First)
        {
            var values = All<T>(collection, key);
            var value = @default;

            if (values.Any())
            {
                value = option switch
                {
                    ParameterPick.First => values.FirstOrDefault(),
                    ParameterPick.Last => values.LastOrDefault(),
                    _ => value
                };
            }

            return value ?? @default;
        }
    }    
    public enum ParameterPick
    {
        First,
        Last
    }
}

