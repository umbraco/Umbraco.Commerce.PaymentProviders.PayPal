namespace Umbraco.Commerce.PaymentProviders.PayPal.Api.Models
{
    public class PaypalClientRefundRequest
    {
        public required string PaymentId { get; set; }
        public required PayPalAmount Amount { get; set; }
    }
}
