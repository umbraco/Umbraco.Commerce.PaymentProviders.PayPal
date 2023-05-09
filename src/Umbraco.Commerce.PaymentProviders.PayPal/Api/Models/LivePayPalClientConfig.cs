namespace Umbraco.Commerce.PaymentProviders.PayPal.Api.Models
{
    public class LivePayPalClientConfig : PayPalClientConfig
    {
        public override string BaseUrl => PayPalClient.LiveApiUrl;
    }
}
