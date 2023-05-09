namespace Umbraco.Commerce.PaymentProviders.PayPal.Api.Models
{
    public class SandboxPayPalClientConfig : PayPalClientConfig
    {
        public override string BaseUrl => PayPalClient.SandboxApiUrl;
    }
}
