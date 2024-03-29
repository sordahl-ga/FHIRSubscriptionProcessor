﻿
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Client;
using System;
using System.Collections.Specialized;
using System.IdentityModel.Tokens.Jwt;
using System.Text;
using System.Threading.Tasks;

namespace FHIRSubscriptionProcessor
{
    public static class ADUtils
    {
        public static bool isTokenExpired(string bearerToken)
        {
            if (bearerToken == null) return true;
            var handler = new JwtSecurityTokenHandler();
            var token = handler.ReadToken(bearerToken) as JwtSecurityToken;
            var tokenExpiryDate = token.ValidTo;

            // If there is no valid `exp` claim then `ValidTo` returns DateTime.MinValue
            if (tokenExpiryDate == DateTime.MinValue) return true;

            // If the token is in the past then you can't use it
            if (tokenExpiryDate < DateTime.UtcNow) return true;
            return false;

        }
        public static async Task<string> GetAADAccessToken(string authority, string clientId, string clientSecret, string audience, bool msi, ILogger log)
        {
            try
            {
                if (msi)
                {
                    var tokenCredential = new DefaultAzureCredential();
                    var accessToken = await tokenCredential.GetTokenAsync(
                        new TokenRequestContext(scopes: new string[] { audience + "/.default" }) { }
                    );
                    return accessToken.Token;

                }
                else
                {
                    var clientApplication = ConfidentialClientApplicationBuilder.Create(clientId)
                      .WithClientSecret(clientSecret)
                      .WithAuthority(authority)
                      .Build();
                    var scopes = new string[] { audience + "/.default" };
                    var authenticationResult = await clientApplication.AcquireTokenForClient(scopes)
                      .ExecuteAsync()
                      .ConfigureAwait(false);
                    var accesstoken = authenticationResult.AccessToken;
                    return accesstoken;
                }

            }
            catch (Exception e)
            {
                log.LogError($"GetAADAccessToken: Exception getting access token: {e.Message}");
                return null;
            }

        }
    }
}
