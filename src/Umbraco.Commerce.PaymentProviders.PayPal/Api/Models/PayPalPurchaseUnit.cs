using Newtonsoft.Json;

namespace Umbraco.Commerce.PaymentProviders.PayPal.Api.Models
{
    public class PayPalPurchaseUnit : PayPalPurchaseUnitBase
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("payments")]
        public PayPalPaymentCollection Payments { get; set; }
    }
}
