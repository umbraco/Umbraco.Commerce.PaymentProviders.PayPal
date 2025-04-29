using Umbraco.Commerce.Extensions;

namespace Umbraco.Commerce.PaymentProviders.PayPal.Api.Models
{
    public class SandboxPayPalClientConfig : PayPalClientConfig
    {
        public SandboxPayPalClientConfig(string clientId, string secret, string webhookId)
        {
            clientId.MustNotBeNullOrWhiteSpace(nameof(clientId));
            secret.MustNotBeNullOrWhiteSpace(nameof(secret));
            webhookId.MustNotBeNullOrWhiteSpace(nameof(webhookId));

            ClientId = clientId;
            Secret = secret;
            WebhookId = webhookId;
        }

        public override string BaseUrl => PayPalClient.SandboxApiUrl;
    }
}
