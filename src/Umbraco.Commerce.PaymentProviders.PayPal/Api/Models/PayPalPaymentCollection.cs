using Newtonsoft.Json;

namespace Umbraco.Commerce.PaymentProviders.PayPal.Api.Models
{
    public class PayPalPaymentCollection
    {
        [JsonProperty("authorizations")]
        public PayPalAuthorizationPayment[] Authorizations { get; set; }

        [JsonProperty("captures")]
        public PayPalCapturePayment[] Captures { get; set; }

        [JsonProperty("refunds")]
        public PayPalRefundPayment[] Refunds { get; set; }

    }
}
