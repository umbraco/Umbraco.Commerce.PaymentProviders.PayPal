using Newtonsoft.Json;

namespace Umbraco.Commerce.PaymentProviders.PayPal.Api.Models
{
    public class PayPalOrderMinimal
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("status")]
        public string Status { get; set; }

        [JsonProperty("links")]
        public PayPalHateoasLink[] Links { get; set; }
    }
}
