using System;

namespace Umbraco.Commerce.PaymentProviders.PayPal.Api.Exceptions
{
    public class PaymentDeclinedException : Exception
    {
        public PaymentDeclinedException() : base("Payment is declined by Paypal")
        {
        }

        public PaymentDeclinedException(string message) : base(message)
        {
        }

        public PaymentDeclinedException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }
}
