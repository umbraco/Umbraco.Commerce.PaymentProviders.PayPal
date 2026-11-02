using System;
using System.IO;
using System.Linq;
using System.Runtime.Caching;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Flurl.Http;
using Microsoft.AspNetCore.Http;
using Umbraco.Commerce.PaymentProviders.PayPal.Api.Exceptions;
using Umbraco.Commerce.PaymentProviders.PayPal.Api.Models;

namespace Umbraco.Commerce.PaymentProviders.PayPal.Api
{
    public class PayPalClient
    {
        private static readonly MemoryCache _accessTokenCache = new MemoryCache("PayPalClient_AccessTokenCache");

        public const string SandboxApiUrl = "https://api.sandbox.paypal.com";

        public const string LiveApiUrl = "https://api.paypal.com";

        private readonly PayPalClientConfig _config;

        public PayPalClient(PayPalClientConfig config)
        {
            _config = config;
        }

        public async Task<PayPalOrder> CreateOrderAsync(PayPalCreateOrderRequest request, CancellationToken cancellationToken = default)
        {
            return await RequestAsync(
                "/v2/checkout/orders",
                async (req, ct) => await req
                .WithHeader("Prefer", "return=representation")
                .PostJsonAsync(request, cancellationToken: ct)
                .ReceiveJson<PayPalOrder>().ConfigureAwait(false),
                cancellationToken)
                .ConfigureAwait(false);
        }

        public async Task<PayPalOrder> GetOrderAsync(string orderId, CancellationToken cancellationToken = default)
        {
            return await RequestAsync(
                $"/v2/checkout/orders/{orderId}",
                async (req, ct) => await req
                .WithHeader("Prefer", "return=representation")
                .GetAsync(cancellationToken: ct)
                .ReceiveJson<PayPalOrder>().ConfigureAwait(false),
                cancellationToken)
                .ConfigureAwait(false);
        }

        public async Task<PayPalOrder> AuthorizeOrderAsync(string orderId, CancellationToken cancellationToken = default)
        {
            try
            {
                PayPalOrder paypalOrder = await RequestAsync(
                $"/v2/checkout/orders/{orderId}/authorize",
                async (req, ct) => await req
                    .WithHeader("Prefer", "return=representation")
                    .PostJsonAsync(null, cancellationToken: ct)
                    .ReceiveJson<PayPalOrder>().ConfigureAwait(false),
                cancellationToken)
                .ConfigureAwait(false);

                return paypalOrder;
            }
            catch (FlurlHttpException ex)
            {
                if (ex.Call.Response.StatusCode == 422)
                {
                    throw new PaymentDeclinedException();
                }

                throw;
            }
        }

        public async Task<PayPalOrder> CaptureOrderAsync(string orderId, CancellationToken cancellationToken = default)
        {
            try
            {
                PayPalOrder paypalOrder = await RequestAsync(
                    $"/v2/checkout/orders/{orderId}/capture",
                    async (req, ct) => await req
                        .WithHeader("Prefer", "return=representation")
                        .PostJsonAsync(null, cancellationToken: ct)
                        .ReceiveJson<PayPalOrder>().ConfigureAwait(false),
                    cancellationToken)
                    .ConfigureAwait(false);

                return paypalOrder;
            }
            catch (FlurlHttpException ex)
            {
                if (ex.Call.Response.StatusCode == 422)
                {
                    throw new PaymentDeclinedException();
                }

                throw;
            }
        }

        public async Task<PayPalCapturePayment> CapturePaymentAsync(string paymentId, CancellationToken cancellationToken = default)
        {
            return await RequestAsync(
                $"/v2/payments/authorizations/{paymentId}/capture",
                async (req, ct) => await req
                .WithHeader("Prefer", "return=representation")
                .PostJsonAsync(new { final_capture = true }, cancellationToken: ct)
                .ReceiveJson<PayPalCapturePayment>().ConfigureAwait(false),
                cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Send a full refund request.
        /// </summary>
        /// <param name="paymentId"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async Task<PayPalRefundPayment> RefundPaymentAsync(string paymentId, CancellationToken cancellationToken = default)
        {
            return await RequestAsync(
                $"/v2/payments/captures/{paymentId}/refund",
                async (req, ct) => await req
                .PostJsonAsync(null, cancellationToken: ct)
                .ReceiveJson<PayPalRefundPayment>().ConfigureAwait(false),
                cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Send a refund request with additional information, such as an amount object for a partial refund.
        /// </summary>
        /// <param name="refundRequest"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async Task<PayPalRefundPayment> RefundPaymentAsync(PaypalClientRefundRequest refundRequest, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(refundRequest);

            return await RequestAsync(
                $"/v2/payments/captures/{refundRequest.PaymentId}/refund",
                async (req, ct) => await req
                .PostJsonAsync(
                    new
                    {
                        amount = refundRequest.Amount,
                    },
                    cancellationToken: ct)
                .ReceiveJson<PayPalRefundPayment>().ConfigureAwait(false),
                cancellationToken)
                .ConfigureAwait(false);
        }

        public async Task CancelPaymentAsync(string paymentId, CancellationToken cancellationToken = default)
        {
            await RequestAsync(
                $"/v2/payments/authorizations/{paymentId}/void",
                async (req, ct) => await req
                .WithHeader("Prefer", "return=representation")
                .PostJsonAsync(null, cancellationToken: ct).ConfigureAwait(false),
                cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Gets a captured payment.
        /// </summary>
        internal async Task<PayPalCapturePayment> GetCapturePaymentAsync(string captureId, CancellationToken cancellationToken = default)
        {
            return await RequestAsync(
                $"/v2/payments/captures/{Uri.EscapeDataString(captureId)}",
                async (req, ct) => await req
                .GetAsync(cancellationToken: ct)
                .ReceiveJson<PayPalCapturePayment>().ConfigureAwait(false),
                cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Gets an authorized payment.
        /// </summary>
        internal async Task<PayPalAuthorizationPayment> GetAuthorizationPaymentAsync(string authorizationId, CancellationToken cancellationToken = default)
        {
            return await RequestAsync(
                $"/v2/payments/authorizations/{Uri.EscapeDataString(authorizationId)}",
                async (req, ct) => await req
                .GetAsync(cancellationToken: ct)
                .ReceiveJson<PayPalAuthorizationPayment>().ConfigureAwait(false),
                cancellationToken)
                .ConfigureAwait(false);
        }

        public async Task<PayPalWebhookEvent> ParseWebhookEventAsync(HttpRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            IHeaderDictionary headers = request.Headers;

            PayPalWebhookEvent payPalWebhookEvent = default;
            using (Stream stream = request.Body)
            {
                if (stream.CanSeek)
                {
                    stream.Seek(0, SeekOrigin.Begin);
                }

                using (var reader = new StreamReader(stream))
                {
                    var json = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

                    var webhookSignatureRequest = new PayPalVerifyWebhookSignatureRequest
                    {
                        AuthAlgorithm = headers["paypal-auth-algo"].FirstOrDefault(),
                        CertUrl = headers["paypal-cert-url"].FirstOrDefault(),
                        TransmissionId = headers["paypal-transmission-id"].FirstOrDefault(),
                        TransmissionSignature = headers["paypal-transmission-sig"].FirstOrDefault(),
                        TransmissionTime = headers["paypal-transmission-time"].FirstOrDefault(),
                        WebhookId = _config.WebhookId,
                    };

                    string? webhookSignatureRequestStr = CreateVerifyWebhookSignatureRequestBody(webhookSignatureRequest, json);
                    if (webhookSignatureRequestStr != null)
                    {
                        PayPalVerifyWebhookSignatureResult result = await RequestAsync(
                                "/v1/notifications/verify-webhook-signature",
                                async (req, ct) => await req
                                    .WithHeader("Content-Type", "application/json")
                                    .PostStringAsync(webhookSignatureRequestStr, cancellationToken: ct)
                                    .ReceiveJson<PayPalVerifyWebhookSignatureResult>()
                                    .ConfigureAwait(false),
                                cancellationToken)
                            .ConfigureAwait(false);

                        if (result != null && result.VerificationStatus == "SUCCESS")
                        {
                            payPalWebhookEvent = JsonSerializer.Deserialize<PayPalWebhookEvent>(json);
                        }
                    }
                }
            }

            return payPalWebhookEvent;
        }

        /// <summary>
        /// Builds the verify-webhook-signature request body. The webhook event is embedded exactly as
        /// received, and must be a JSON object; returns <c>null</c> if it isn't.
        /// </summary>
        internal static string? CreateVerifyWebhookSignatureRequestBody(PayPalVerifyWebhookSignatureRequest request, string webhookEventJson)
        {
            ArgumentNullException.ThrowIfNull(request);

            if (string.IsNullOrWhiteSpace(webhookEventJson))
            {
                return null;
            }

            try
            {
                using (JsonDocument webhookEvent = JsonDocument.Parse(webhookEventJson))
                {
                    if (webhookEvent.RootElement.ValueKind != JsonValueKind.Object)
                    {
                        return null;
                    }
                }
            }
            catch (JsonException)
            {
                return null;
            }

            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                // Property names match the [JsonPropertyName] attributes on PayPalVerifyWebhookSignatureRequest
                writer.WriteStartObject();
                writer.WriteString("auth_algo", request.AuthAlgorithm);
                writer.WriteString("cert_url", request.CertUrl);
                writer.WriteString("transmission_id", request.TransmissionId);
                writer.WriteString("transmission_sig", request.TransmissionSignature);
                writer.WriteString("transmission_time", request.TransmissionTime);
                writer.WriteString("webhook_id", request.WebhookId);
                writer.WritePropertyName("webhook_event");
                writer.WriteRawValue(webhookEventJson);
                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(buffer.ToArray());
        }

        private async Task<TResult> RequestAsync<TResult>(string url, Func<IFlurlRequest, CancellationToken, Task<TResult>> func, CancellationToken cancellationToken = default)
        {
            var result = default(TResult);

            try
            {
                var accessToken = await GetAccessTokenAsync(false, cancellationToken).ConfigureAwait(false);
                FlurlRequest req = new FlurlRequest(_config.BaseUrl + url)
                    .WithSettings(x => x.JsonSerializer = new CustomFlurlJsonSerializer(new JsonSerializerOptions
                    {
                        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                    }))
                    .WithOAuthBearerToken(accessToken);

                result = await func.Invoke(req, cancellationToken).ConfigureAwait(false);
            }
            catch (FlurlHttpException ex)
            {
                if (ex.Call.Response.StatusCode == 401)
                {
                    var accessToken = await GetAccessTokenAsync(true, cancellationToken).ConfigureAwait(false);
                    FlurlRequest req = new FlurlRequest(_config.BaseUrl + url)
                        .WithSettings(x => x.JsonSerializer = new CustomFlurlJsonSerializer(new JsonSerializerOptions
                        {
                            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                        }))
                        .WithOAuthBearerToken(accessToken);

                    result = await func.Invoke(req, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    throw;
                }
            }

            return result;
        }

        private async Task<string> GetAccessTokenAsync(bool forceReAuthentication = false, CancellationToken cancellationToken = default)
        {
            var cacheKey = $"{_config.BaseUrl}__{_config.ClientId}__{_config.Secret}";

            if (!_accessTokenCache.Contains(cacheKey) || forceReAuthentication)
            {
                PayPalAccessTokenResult result = await AuthenticateAsync(cancellationToken).ConfigureAwait(false);

                _accessTokenCache.Set(cacheKey, result.AccessToken, new CacheItemPolicy
                {
                    AbsoluteExpiration = DateTimeOffset.UtcNow.AddSeconds(result.ExpiresIn - 5)
                });
            }

            return _accessTokenCache.Get(cacheKey).ToString();
        }

        private async Task<PayPalAccessTokenResult> AuthenticateAsync(CancellationToken cancellationToken = default)
        {
            return await new FlurlRequest(_config.BaseUrl + "/v1/oauth2/token")
                .WithSettings(x => x.JsonSerializer = new CustomFlurlJsonSerializer(new JsonSerializerOptions
                {
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                }))
                .WithBasicAuth(_config.ClientId, _config.Secret)
                .PostUrlEncodedAsync(new { grant_type = "client_credentials" }, cancellationToken: cancellationToken)
                .ReceiveJson<PayPalAccessTokenResult>()
                .ConfigureAwait(false);
        }
    }
}
