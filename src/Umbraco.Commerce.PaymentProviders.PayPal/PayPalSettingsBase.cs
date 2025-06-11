using Umbraco.Commerce.Core.PaymentProviders;

namespace Umbraco.Commerce.PaymentProviders.PayPal
{
    public class PayPalSettingsBase
    {
        [PaymentProviderSetting(
            SortOrder = 100)]
        public string ContinueUrl { get; set; }

        [PaymentProviderSetting(
            SortOrder = 200)]
        public string CancelUrl { get; set; }

        [PaymentProviderSetting(
            SortOrder = 300)]
        public string ErrorUrl { get; set; }

        [PaymentProviderSetting(
            SortOrder = 400)]
        public string SandboxClientId { get; set; }

        [PaymentProviderSetting(
            SortOrder = 500)]
        public string SandboxSecret { get; set; }

        [PaymentProviderSetting(
            SortOrder = 600)]
        public string SandboxWebhookId { get; set; }

        [PaymentProviderSetting(
            SortOrder = 700)]
        public string LiveClientId { get; set; }

        [PaymentProviderSetting(
            SortOrder = 800)]
        public string LiveSecret { get; set; }

        [PaymentProviderSetting(
            SortOrder = 900)]
        public string LiveWebhookId { get; set; }

        [PaymentProviderSetting(
            SortOrder = 1000000)]
        public bool SandboxMode { get; set; }

        // Advanced settings
        [PaymentProviderSetting(
            SortOrder = 100,
            IsAdvanced = true)]
        public string BrandName { get; set; }

        [PaymentProviderSetting(
            SortOrder = 110,
            IsAdvanced = true)]
        public string OrderDescription { get; set; }

    }
}
