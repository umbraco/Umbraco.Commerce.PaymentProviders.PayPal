using FluentAssertions;
using Umbraco.Commerce.Core.Models;
using Umbraco.Commerce.PaymentProviders.PayPal.Api.Models;

namespace Umbraco.Commerce.PaymentProviders.PayPal.UnitTests
{
    public class PayPalPaymentStatusTests
    {
        private readonly TestablePayPalProvider _provider = new();

        [Fact]
        public void GetPaymentStatus_Should_Return_Captured_For_Completed_Capture_Without_Refunds()
        {
            PayPalOrder order = CreateOrder(new PayPalPaymentCollection
            {
                Captures = [Capture("CAP-1", PayPalCapturePayment.Statuses.COMPLETED)],
            });

            PaymentStatus status = _provider.GetStatus(order, out PayPalPayment payment);

            status.Should().Be(PaymentStatus.Captured);
            payment.Id.Should().Be("CAP-1");
        }

        [Fact]
        public void GetPaymentStatus_Should_Return_PartiallyRefunded_And_Capture_For_Partially_Refunded_Capture()
        {
            PayPalOrder order = CreateOrder(new PayPalPaymentCollection
            {
                Captures = [Capture("CAP-1", PayPalCapturePayment.Statuses.PARTIALLY_REFUNDED)],
                Refunds = [Refund("REF-1", PayPalRefundPayment.Statuses.COMPLETED)],
            });

            PaymentStatus status = _provider.GetStatus(order, out PayPalPayment payment);

            status.Should().Be(PaymentStatus.PartiallyRefunded);
            payment.Should().BeOfType<PayPalCapturePayment>();
            payment.Id.Should().Be("CAP-1");
        }

        [Fact]
        public void GetPaymentStatus_Should_Return_Refunded_For_Refunded_Capture()
        {
            PayPalOrder order = CreateOrder(new PayPalPaymentCollection
            {
                Captures = [Capture("CAP-1", PayPalCapturePayment.Statuses.REFUNDED)],
                Refunds = [Refund("REF-1", PayPalRefundPayment.Statuses.COMPLETED)],
            });

            PaymentStatus status = _provider.GetStatus(order, out PayPalPayment payment);

            status.Should().Be(PaymentStatus.Refunded);
            payment.Id.Should().Be("CAP-1");
        }

        [Fact]
        public void GetPaymentStatus_Should_Return_Authorized_For_Authorization_Only()
        {
            PayPalOrder order = CreateOrder(new PayPalPaymentCollection
            {
                Authorizations = [new PayPalAuthorizationPayment { Id = "AUTH-1", Status = PayPalAuthorizationPayment.Statuses.CREATED }],
            });

            PaymentStatus status = _provider.GetStatus(order, out PayPalPayment payment);

            status.Should().Be(PaymentStatus.Authorized);
            payment.Id.Should().Be("AUTH-1");
        }

        [Fact]
        public void GetPaymentStatus_Should_Return_Initialized_When_There_Are_No_Payments()
        {
            PayPalOrder order = CreateOrder(null);

            PaymentStatus status = _provider.GetStatus(order, out PayPalPayment payment);

            status.Should().Be(PaymentStatus.Initialized);
            payment.Should().BeNull();
        }

        private static PayPalOrder CreateOrder(PayPalPaymentCollection? payments)
            => new() { PurchaseUnits = [new PayPalPurchaseUnit { Payments = payments }] };

        private static PayPalCapturePayment Capture(string id, string status)
            => new() { Id = id, Status = status };

        private static PayPalRefundPayment Refund(string id, string status)
            => new() { Id = id, Status = status };

        // GetPaymentStatus is protected, so expose it through a thin subclass
        private sealed class TestablePayPalProvider : PayPalCheckoutOneTimePaymentProvider
        {
            public TestablePayPalProvider()
                : base(null!, null!)
            { }

            public PaymentStatus GetStatus(PayPalOrder order, out PayPalPayment payment)
                => GetPaymentStatus(order, out payment);
        }
    }
}
