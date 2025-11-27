using System;

namespace Umbraco.Commerce.PaymentProviders.PayPal.Api.Exceptions
{
    public class PayPalPaymentProviderGeneralException : Exception
    {
        public PayPalPaymentProviderGeneralException(string message) : base(message)
        {
        }

        public PayPalPaymentProviderGeneralException(string message, Exception innerException) : base(message, innerException)
        {
        }

        public PayPalPaymentProviderGeneralException()
        {
        }
    }
}
