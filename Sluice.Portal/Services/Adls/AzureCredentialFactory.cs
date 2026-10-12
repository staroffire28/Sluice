using System.Reflection;
using Azure.Core;

namespace Sluice.Portal.Services.Adls;

internal static class AzureCredentialFactory
{
    public static TokenCredential CreateClientSecret(string tenantId, string clientId, string clientSecret)
    {
        var type = Assembly.Load("Azure.Identity").GetType("Azure.Identity.ClientSecretCredential", throwOnError: true)!;
        return (TokenCredential)Activator.CreateInstance(type, tenantId, clientId, clientSecret)!;
    }
}