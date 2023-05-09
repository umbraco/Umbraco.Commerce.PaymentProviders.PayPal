using Newtonsoft.Json;

namespace Umbraco.Commerce.PaymentProviders.PayPal.Api.Models
{
    // https://developer.paypal.com/docs/api/webhooks/v1/#verify-webhook-signature

    public class PayPalVerifyWebhookSignatureResult
    {
        [JsonProperty("verification_status")]
        public string VerificationStatus { get; set; }
    }
}
