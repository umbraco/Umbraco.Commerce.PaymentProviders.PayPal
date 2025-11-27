using Umbraco.Commerce.Core.PaymentProviders;

namespace Umbraco.Commerce.PaymentProviders.PayPal
{
    public class PayPalCheckoutOneTimeSettings : PayPalSettingsBase
    {
        [PaymentProviderSetting(
            SortOrder = 1000)]
        public bool Capture { get; set; }
    }
}
