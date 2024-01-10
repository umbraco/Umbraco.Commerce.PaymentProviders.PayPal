using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.Caching;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Flurl.Http;
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
            return await RequestAsync("/v2/checkout/orders", async (req, ct) => await req
                .WithHeader("Prefer", "return=representation")
                .PostJsonAsync(request, cancellationToken: ct)
                .ReceiveJson<PayPalOrder>().ConfigureAwait(false),
                cancellationToken)
                .ConfigureAwait(false);
        }

        public async Task<PayPalOrder> GetOrderAsync(string orderId, CancellationToken cancellationToken = default)
        {
            return await RequestAsync($"/v2/checkout/orders/{orderId}", async (req, ct) => await req
                .WithHeader("Prefer", "return=representation")
                .GetAsync(cancellationToken: ct)
                .ReceiveJson<PayPalOrder>().ConfigureAwait(false),
                cancellationToken)
                .ConfigureAwait(false);
        }

        public async Task<PayPalOrder> AuthorizeOrderAsync(string orderId, CancellationToken cancellationToken = default)
        {
            return await RequestAsync($"/v2/checkout/orders/{orderId}/authorize", async (req, ct) => await req
                .WithHeader("Prefer", "return=representation")
                .PostJsonAsync(null, cancellationToken: ct)
                .ReceiveJson<PayPalOrder>().ConfigureAwait(false),
                cancellationToken)
                .ConfigureAwait(false);
        }

        public async Task<PayPalOrder> CaptureOrderAsync(string orderId, CancellationToken cancellationToken = default)
        {
            return await RequestAsync($"/v2/checkout/orders/{orderId}/capture", async (req, ct) => await req
                .WithHeader("Prefer", "return=representation")
                .PostJsonAsync(null, cancellationToken: ct)
                .ReceiveJson<PayPalOrder>().ConfigureAwait(false),
                cancellationToken)
                .ConfigureAwait(false);
        }

        public async Task<PayPalCapturePayment> CapturePaymentAsync(string paymentId, CancellationToken cancellationToken = default)
        {
            return await RequestAsync($"/v2/payments/authorizations/{paymentId}/capture", async (req, ct) => await req
                .WithHeader("Prefer", "return=representation")
                .PostJsonAsync(new { final_capture = true }, cancellationToken: ct)
                .ReceiveJson<PayPalCapturePayment>().ConfigureAwait(false),
                cancellationToken)
                .ConfigureAwait(false);
        }

        public async Task<PayPalRefundPayment> RefundPaymentAsync(string paymentId, CancellationToken cancellationToken = default)
        {
            return await RequestAsync($"/v2/payments/captures/{paymentId}/refund", async (req, ct) => await req
                .PostJsonAsync(null, cancellationToken: ct)
                .ReceiveJson<PayPalRefundPayment>().ConfigureAwait(false),
                cancellationToken)
                .ConfigureAwait(false);
        }

        public async Task CancelPaymentAsync(string paymentId, CancellationToken cancellationToken = default)
        {
            await RequestAsync($"/v2/payments/authorizations/{paymentId}/void", async (req, ct) => await req
                .WithHeader("Prefer", "return=representation")
                .PostJsonAsync(null, cancellationToken: ct).ConfigureAwait(false),
                cancellationToken)
                .ConfigureAwait(false);
        }

        public async Task<PayPalWebhookEvent> ParseWebhookEventAsync(HttpRequestMessage request, CancellationToken cancellationToken = default)
        {
            var payPalWebhookEvent = default(PayPalWebhookEvent);

            var headers = request.Headers;

            using (var stream = await request.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
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
                        AuthAlgorithm = headers.GetValues("paypal-auth-algo").FirstOrDefault(),
                        CertUrl = headers.GetValues("paypal-cert-url").FirstOrDefault(),
                        TransmissionId = headers.GetValues("paypal-transmission-id").FirstOrDefault(),
                        TransmissionSignature = headers.GetValues("paypal-transmission-sig").FirstOrDefault(),
                        TransmissionTime = headers.GetValues("paypal-transmission-time").FirstOrDefault(),
                        WebhookId = _config.WebhookId,
                        WebhookEvent = new { }
                    };

                    string webhookSignatureRequestStr = JsonSerializer.Serialize(webhookSignatureRequest).Replace("{}", json, StringComparison.InvariantCulture);

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

            return payPalWebhookEvent;
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
