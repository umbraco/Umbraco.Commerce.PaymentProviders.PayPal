using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Flurl.Http;
using Flurl.Http.Testing;
using Microsoft.AspNetCore.Http;
using Umbraco.Commerce.Core.Models;
using Umbraco.Commerce.PaymentProviders.PayPal.Api;
using Umbraco.Commerce.PaymentProviders.PayPal.Api.Exceptions;
using Umbraco.Commerce.PaymentProviders.PayPal.Api.Models;

namespace Umbraco.Commerce.PaymentProviders.PayPal.UnitTests
{
    /// <summary>
    /// Runs <see cref="PayPalClient"/> against Flurl's HttpTest instead of the PayPal API.
    /// </summary>
    public class PayPalClientTests
    {
        private const string CapturedOrderJson = """
            {
              "id": "5O190127TN364715T",
              "intent": "CAPTURE",
              "status": "COMPLETED",
              "purchase_units": [
                {
                  "reference_id": "default",
                  "custom_id": "store-1:order-1",
                  "amount": { "currency_code": "EUR", "value": "100.00" },
                  "payments": {
                    "captures": [
                      { "id": "3C679366HH908993F", "status": "PARTIALLY_REFUNDED", "custom_id": "store-1:order-1", "amount": { "currency_code": "EUR", "value": "100.00" } }
                    ],
                    "refunds": [
                      { "id": "1JU08902781691411", "status": "COMPLETED", "amount": { "currency_code": "EUR", "value": "25.00" } }
                    ]
                  }
                }
              ],
              "links": [
                { "href": "https://api.sandbox.paypal.com/v2/checkout/orders/5O190127TN364715T", "rel": "self", "method": "GET" }
              ]
            }
            """;

        private const string WebhookJson = """
            {
              "id": "WH-58D329510W468432D-8HN650336L201105X",
              "event_version": "1.0",
              "create_time": "2026-11-02T10:00:00.000Z",
              "resource_type": "checkout-order",
              "event_type": "CHECKOUT.ORDER.APPROVED",
              "summary": "An order has been approved by buyer",
              "resource": { "id": "5O190127TN364715T", "intent": "CAPTURE", "status": "APPROVED" }
            }
            """;

        // Each test uses its own client id: the access token cache is static and keyed on it.
        private static PayPalClient CreateClient(out string clientId)
        {
            clientId = "client-" + Guid.NewGuid().ToString("N");
            return new PayPalClient(new SandboxPayPalClientConfig(clientId, "secret", "WEBHOOK-ID-1"));
        }

        private static object Token(string accessToken) => new { access_token = accessToken, token_type = "Bearer", expires_in = 32400 };

        [Fact]
        public async Task GetOrder_Reads_The_Order_And_Its_Payments()
        {
            using var httpTest = new HttpTest();
            httpTest.RespondWithJson(Token("TOKEN-1")).RespondWith(CapturedOrderJson);
            PayPalClient client = CreateClient(out _);

            PayPalOrder order = await client.GetOrderAsync("5O190127TN364715T");

            order.Intent.Should().Be(PayPalOrder.Intents.CAPTURE);
            order.PurchaseUnits.Should().ContainSingle();
            order.PurchaseUnits[0].Payments.Captures[0].Amount.Value.Should().Be("100.00");
            new StatusProvider().GetStatus(order, out PayPalPayment payment).Should().Be(PaymentStatus.PartiallyRefunded);
            payment.Id.Should().Be("3C679366HH908993F");
        }

        [Fact]
        public async Task Access_Token_Is_Requested_Once_And_Sent_As_Bearer()
        {
            using var httpTest = new HttpTest();
            httpTest.RespondWithJson(Token("TOKEN-1")).RespondWith(CapturedOrderJson).RespondWith(CapturedOrderJson);
            PayPalClient client = CreateClient(out string clientId);

            await client.GetOrderAsync("5O190127TN364715T");
            await client.GetOrderAsync("5O190127TN364715T");

            string basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:secret"));
            httpTest.ShouldHaveCalled("https://api.sandbox.paypal.com/v1/oauth2/token")
                .WithVerb(HttpMethod.Post)
                .WithHeader("Authorization", $"Basic {basic}")
                .WithRequestBody("grant_type=client_credentials")
                .Times(1);
            httpTest.ShouldHaveCalled("https://api.sandbox.paypal.com/v2/checkout/orders/5O190127TN364715T")
                .WithOAuthBearerToken("TOKEN-1")
                .Times(2);
        }

        [Fact]
        public async Task Unauthorized_Response_Refreshes_The_Token_And_Retries_Once()
        {
            using var httpTest = new HttpTest();
            httpTest
                .RespondWithJson(Token("STALE"))
                .RespondWith(status: 401)
                .RespondWithJson(Token("FRESH"))
                .RespondWith(CapturedOrderJson);
            PayPalClient client = CreateClient(out _);

            PayPalOrder order = await client.GetOrderAsync("5O190127TN364715T");

            order.Id.Should().Be("5O190127TN364715T");
            httpTest.ShouldHaveCalled("*/v1/oauth2/token").Times(2);
            httpTest.ShouldHaveCalled("*/v2/checkout/orders/*").WithOAuthBearerToken("FRESH").Times(1);
        }

        [Fact]
        public async Task Unprocessable_Capture_Is_A_Declined_Payment()
        {
            using var httpTest = new HttpTest();
            httpTest.RespondWithJson(Token("TOKEN-1")).RespondWith("""{ "name": "UNPROCESSABLE_ENTITY" }""", 422);
            PayPalClient client = CreateClient(out _);

            Func<Task> act = () => client.CaptureOrderAsync("5O190127TN364715T");

            await act.Should().ThrowAsync<PaymentDeclinedException>();
            httpTest.ShouldHaveCalled("*/v2/checkout/orders/5O190127TN364715T/capture").WithVerb(HttpMethod.Post);
        }

        [Fact]
        public async Task Unprocessable_Authorize_Is_A_Declined_Payment()
        {
            using var httpTest = new HttpTest();
            httpTest.RespondWithJson(Token("TOKEN-1")).RespondWith("""{ "name": "UNPROCESSABLE_ENTITY" }""", 422);
            PayPalClient client = CreateClient(out _);

            Func<Task> act = () => client.AuthorizeOrderAsync("5O190127TN364715T");

            await act.Should().ThrowAsync<PaymentDeclinedException>();
        }

        [Fact]
        public async Task Partial_Refund_Sends_The_Amount()
        {
            using var httpTest = new HttpTest();
            httpTest.RespondWithJson(Token("TOKEN-1")).RespondWith("""{ "id": "REF-1", "status": "COMPLETED" }""");
            PayPalClient client = CreateClient(out _);

            PayPalRefundPayment refund = await client.RefundPaymentAsync(new PaypalClientRefundRequest
            {
                PaymentId = "3C679366HH908993F",
                Amount = new PayPalAmount { CurrencyCode = "EUR", Value = "25.00" },
            });

            refund.Status.Should().Be(PayPalRefundPayment.Statuses.COMPLETED);
            httpTest.ShouldHaveCalled("*/v2/payments/captures/3C679366HH908993F/refund")
                .WithRequestJson(new { amount = new { currency_code = "EUR", value = "25.00" } });
        }

        [Fact]
        public async Task Webhook_Is_Verified_With_PayPal_Before_It_Is_Parsed()
        {
            using var httpTest = new HttpTest();
            httpTest.RespondWithJson(Token("TOKEN-1")).RespondWithJson(new { verification_status = "SUCCESS" });
            PayPalClient client = CreateClient(out _);

            PayPalWebhookEvent evt = await client.ParseWebhookEventAsync(WebhookRequest());

            evt.Should().NotBeNull();
            evt.EventType.Should().Be("CHECKOUT.ORDER.APPROVED");
            evt.Resource["id"]!.GetValue<string>().Should().Be("5O190127TN364715T");

            FlurlCall verify = httpTest.CallLog.Single(c => c.Request.Url.Path.EndsWith("/v1/notifications/verify-webhook-signature", StringComparison.Ordinal));
            JsonNode body = JsonNode.Parse(verify.RequestBody)!;
            body["auth_algo"]!.GetValue<string>().Should().Be("SHA256withRSA");
            body["cert_url"]!.GetValue<string>().Should().Be("https://api.sandbox.paypal.com/v1/notifications/certs/CERT-1");
            body["transmission_id"]!.GetValue<string>().Should().Be("TX-1");
            body["transmission_sig"]!.GetValue<string>().Should().Be("SIG==");
            body["transmission_time"]!.GetValue<string>().Should().Be("2026-11-02T10:00:01Z");
            body["webhook_id"]!.GetValue<string>().Should().Be("WEBHOOK-ID-1");
            body["webhook_event"]!["id"]!.GetValue<string>().Should().Be("WH-58D329510W468432D-8HN650336L201105X");
        }

        [Fact]
        public async Task Webhook_That_Fails_Verification_Is_Ignored()
        {
            using var httpTest = new HttpTest();
            httpTest.RespondWithJson(Token("TOKEN-1")).RespondWithJson(new { verification_status = "FAILURE" });
            PayPalClient client = CreateClient(out _);

            PayPalWebhookEvent evt = await client.ParseWebhookEventAsync(WebhookRequest());

            evt.Should().BeNull();
        }

        [Fact]
        public async Task Webhook_Header_Values_Are_Sent_As_Plain_Strings()
        {
            const string transmissionId = "TX-{}\"1";
            using var httpTest = new HttpTest();
            httpTest.RespondWithJson(Token("TOKEN-1")).RespondWithJson(new { verification_status = "SUCCESS" });
            PayPalClient client = CreateClient(out _);

            await client.ParseWebhookEventAsync(WebhookRequest(transmissionId: transmissionId));

            FlurlCall verify = httpTest.CallLog.Single(c => c.Request.Url.Path.EndsWith("/v1/notifications/verify-webhook-signature", StringComparison.Ordinal));
            JsonObject body = JsonNode.Parse(verify.RequestBody)!.AsObject();
            body.Select(x => x.Key).Should().BeEquivalentTo(
                "auth_algo", "cert_url", "transmission_id", "transmission_sig", "transmission_time", "webhook_id", "webhook_event");
            body["transmission_id"]!.GetValue<string>().Should().Be(transmissionId);
            body["webhook_id"]!.GetValue<string>().Should().Be("WEBHOOK-ID-1");
            body["webhook_event"]!["id"]!.GetValue<string>().Should().Be("WH-58D329510W468432D-8HN650336L201105X");
        }

        [Fact]
        public async Task Webhook_Event_Is_Sent_For_Verification_Exactly_As_Received()
        {
            using var httpTest = new HttpTest();
            httpTest.RespondWithJson(Token("TOKEN-1")).RespondWithJson(new { verification_status = "SUCCESS" });
            PayPalClient client = CreateClient(out _);

            await client.ParseWebhookEventAsync(WebhookRequest());

            FlurlCall verify = httpTest.CallLog.Single(c => c.Request.Url.Path.EndsWith("/v1/notifications/verify-webhook-signature", StringComparison.Ordinal));
            verify.RequestBody.Should().Contain(WebhookJson);
        }

        [Theory]
        [InlineData("")]
        [InlineData("not json")]
        [InlineData("[1,2]")]
        [InlineData("{\"id\": \"WH-1\"} {}")]
        public async Task Webhook_Body_That_Is_Not_A_Json_Object_Is_Ignored_Without_Calling_PayPal(string webhookBody)
        {
            using var httpTest = new HttpTest();
            PayPalClient client = CreateClient(out _);

            PayPalWebhookEvent evt = await client.ParseWebhookEventAsync(WebhookRequest(body: webhookBody));

            evt.Should().BeNull();
            httpTest.CallLog.Should().BeEmpty();
        }

        [Fact]
        public async Task Payment_Webhook_Uses_The_Capture_Fetched_From_PayPal()
        {
            using var httpTest = new HttpTest();
            httpTest.RespondWithJson(Token("TOKEN-1")).RespondWith("""
                { "id": "3C679366HH908993F", "status": "REFUNDED", "custom_id": "store-1:order-1", "amount": { "currency_code": "EUR", "value": "100.00" } }
                """);
            PayPalClient client = CreateClient(out _);
            var evt = new PayPalWebhookEvent
            {
                EventType = "PAYMENT.CAPTURE.COMPLETED",
                ResourceType = PayPalWebhookEvent.ResourceTypes.Payment.CAPTURE,
                Resource = JsonNode.Parse("""{ "id": "3C679366HH908993F", "status": "COMPLETED", "custom_id": "store-1:order-2", "amount": { "currency_code": "EUR", "value": "1.00" } }""")!.AsObject(),
            };

            PayPalPayment? payment = await PayPalCheckoutOneTimePaymentProvider.FetchPaymentAsync(client, evt);

            httpTest.ShouldHaveCalled("*/v2/payments/captures/3C679366HH908993F").WithVerb(HttpMethod.Get);
            payment.Should().BeOfType<PayPalCapturePayment>();
            payment!.Status.Should().Be(PayPalCapturePayment.Statuses.REFUNDED);
            payment.CustomId.Should().Be("store-1:order-1");
            payment.Amount.Value.Should().Be("100.00");
        }

        [Fact]
        public async Task Authorization_Webhook_Uses_The_Authorization_Fetched_From_PayPal()
        {
            using var httpTest = new HttpTest();
            httpTest.RespondWithJson(Token("TOKEN-1")).RespondWith("""
                { "id": "0VF52814937998046", "status": "VOIDED", "custom_id": "store-1:order-1", "amount": { "currency_code": "EUR", "value": "100.00" } }
                """);
            PayPalClient client = CreateClient(out _);
            var evt = new PayPalWebhookEvent
            {
                EventType = "PAYMENT.AUTHORIZATION.CREATED",
                ResourceType = PayPalWebhookEvent.ResourceTypes.Payment.AUTHORIZATION,
                Resource = JsonNode.Parse("""{ "id": "0VF52814937998046", "status": "CREATED" }""")!.AsObject(),
            };

            PayPalPayment? payment = await PayPalCheckoutOneTimePaymentProvider.FetchPaymentAsync(client, evt);

            httpTest.ShouldHaveCalled("*/v2/payments/authorizations/0VF52814937998046").WithVerb(HttpMethod.Get);
            payment.Should().BeOfType<PayPalAuthorizationPayment>();
            payment!.Status.Should().Be(PayPalAuthorizationPayment.Statuses.VOIDED);
        }

        [Fact]
        public void Custom_Id_Must_Refer_To_The_Order_Being_Processed()
        {
            var order = new OrderReference(Guid.NewGuid(), "ORDER-01");
            var otherOrder = new OrderReference(Guid.NewGuid(), "ORDER-02");

            PayPalCheckoutOneTimePaymentProvider.IsForOrder(order, order).Should().BeTrue();
            PayPalCheckoutOneTimePaymentProvider.IsForOrder(otherOrder, order).Should().BeFalse();
            PayPalCheckoutOneTimePaymentProvider.IsForOrder(null, order).Should().BeFalse();
            PayPalCheckoutOneTimePaymentProvider.IsForOrder("not-a-reference", order).Should().BeFalse();
        }

        private static HttpRequest WebhookRequest(string body = WebhookJson, string transmissionId = "TX-1")
        {
            var context = new DefaultHttpContext();
            context.Request.Method = "POST";
            context.Request.ContentType = "application/json";
            context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
            context.Request.Headers["paypal-auth-algo"] = "SHA256withRSA";
            context.Request.Headers["paypal-cert-url"] = "https://api.sandbox.paypal.com/v1/notifications/certs/CERT-1";
            context.Request.Headers["paypal-transmission-id"] = transmissionId;
            context.Request.Headers["paypal-transmission-sig"] = "SIG==";
            context.Request.Headers["paypal-transmission-time"] = "2026-11-02T10:00:01Z";
            return context.Request;
        }

        private sealed class StatusProvider : PayPalCheckoutOneTimePaymentProvider
        {
            public StatusProvider()
                : base(null!, null!)
            { }

            public PaymentStatus GetStatus(PayPalOrder order, out PayPalPayment payment)
                => GetPaymentStatus(order, out payment);
        }
    }
}
