using Newtonsoft.Json;

namespace Umbraco.Commerce.PaymentProviders.PayPal.Api.Models
{
    public class PayPalOrderBase<TPaymentUnit> : PayPalOrderMinimal
        where TPaymentUnit : PayPalPurchaseUnitBase
    {
        [JsonProperty("intent")]
        public string Intent { get; set; }

        [JsonProperty("purchase_units")]
        public TPaymentUnit[] PurchaseUnits { get; set; }

        [JsonProperty("application_context")]
        public PayPalOrderApplicationContext AplicationContext { get; set; }
    }
}
