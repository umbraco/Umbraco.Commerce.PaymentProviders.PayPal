using Newtonsoft.Json;

namespace Umbraco.Commerce.PaymentProviders.PayPal.Api.Models
{
    public class PayPalPurchaseUnitBase
    {
        [JsonProperty("custom_id")]
        public string CustomId { get; set; }

        [JsonProperty("description")]
        public string Description { get; set; }

        [JsonProperty("amount")]
        public PayPalAmount Amount { get; set; }
    }
}
