using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Umbraco.Commerce.Common.Logging;
using Umbraco.Commerce.Core.Api;
using Umbraco.Commerce.Core.Models;
using Umbraco.Commerce.Core.PaymentProviders;
using Umbraco.Commerce.Extensions;
using Umbraco.Commerce.PaymentProviders.PayPal.Api;
using Umbraco.Commerce.PaymentProviders.PayPal.Api.Exceptions;
using Umbraco.Commerce.PaymentProviders.PayPal.Api.Models;

namespace Umbraco.Commerce.PaymentProviders.PayPal
{
    [PaymentProvider("paypal-checkout-onetime")]
    public class PayPalCheckoutOneTimePaymentProvider : PayPalPaymentProviderBase<PayPalCheckoutOneTimeSettings>
    {
        private readonly ILogger<PayPalCheckoutOneTimePaymentProvider> _logger;

        public PayPalCheckoutOneTimePaymentProvider(
            UmbracoCommerceContext ctx,
            ILogger<PayPalCheckoutOneTimePaymentProvider> logger)
            : base(ctx)
        {
            _logger = logger;
        }

        public override bool CanFetchPaymentStatus => true;
        public override bool CanCapturePayments => true;
        public override bool CanCancelPayments => true;
        public override bool CanRefundPayments => true;
        public override bool CanPartiallyRefundPayments => true;

        // Don't finalize at continue as we will finalize async via webhook
        public override bool FinalizeAtContinueUrl => false;

        public override IEnumerable<TransactionMetaDataDefinition> TransactionMetaDataDefinitions => new[]
        {
            new TransactionMetaDataDefinition("PayPalOrderId"),
        };

        public override async Task<OrderReference> GetOrderReferenceAsync(PaymentProviderContext<PayPalCheckoutOneTimeSettings> ctx, CancellationToken cancellationToken = default)
        {
            try
            {
                var clientConfig = GetPayPalClientConfig(ctx.Settings);
                var client = new PayPalClient(clientConfig);
                var payPalWebhookEvent = await GetPayPalWebhookEventAsync(client, ctx, cancellationToken).ConfigureAwait(false);

                if (payPalWebhookEvent != null)
                {
                    if (payPalWebhookEvent.EventType.StartsWith("CHECKOUT.ORDER.", StringComparison.InvariantCultureIgnoreCase))
                    {
                        var payPalOrder = payPalWebhookEvent.Resource.Deserialize<PayPalOrder>();
                        if (payPalOrder?.PurchaseUnits != null && payPalOrder.PurchaseUnits.Length == 1)
                        {
                            return OrderReference.Parse(payPalOrder.PurchaseUnits[0].CustomId);
                        }
                    }
                    else if (payPalWebhookEvent.EventType.StartsWith("PAYMENT.", StringComparison.InvariantCultureIgnoreCase))
                    {
                        var payPalPayment = payPalWebhookEvent.Resource.Deserialize<PayPalPayment>();
                        if (payPalPayment != null)
                        {
                            return OrderReference.Parse(payPalPayment.CustomId);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "PayPal - GetOrderReference");
            }

            return await base.GetOrderReferenceAsync(ctx, cancellationToken).ConfigureAwait(false);
        }

        public override async Task<PaymentFormResult> GenerateFormAsync(PaymentProviderContext<PayPalCheckoutOneTimeSettings> ctx, CancellationToken cancellationToken = default)
        {
            // Get currency information
            var currency = await Context.Services.CurrencyService.GetCurrencyAsync(ctx.Order.CurrencyId);
            var currencyCode = currency.Code.ToUpperInvariant();

            // Ensure currency has valid ISO 4217 code
            if (!Iso4217.CurrencyCodes.ContainsKey(currencyCode))
            {
                throw new PayPalPaymentProviderGeneralException("Currency must be a valid ISO 4217 currency code: " + currency.Name);
            }

            // Create the order
            var clientConfig = GetPayPalClientConfig(ctx.Settings);
            var client = new PayPalClient(clientConfig);
            var payPalOrder = await client.CreateOrderAsync(
                new PayPalCreateOrderRequest
                {
                    Intent = ctx.Settings.Capture
                        ? PayPalOrder.Intents.CAPTURE
                        : PayPalOrder.Intents.AUTHORIZE,
                    PurchaseUnits = new[]
                    {
                        new PayPalPurchaseUnitRequest
                        {
                            CustomId = ctx.Order.GenerateOrderReference(),
                            Description = !string.IsNullOrWhiteSpace(ctx.Settings.OrderDescription)
                                ? string.Format(ctx.Settings.OrderDescription, $"#{ctx.Order.OrderNumber}")
                                : $"#{ctx.Order.OrderNumber}",
                            Amount = new PayPalAmount
                            {
                                CurrencyCode = currencyCode,
                                Value = ctx.Order.TransactionAmount.Value.Value.ToString("0.00", CultureInfo.InvariantCulture)
                            }
                        }
                    },
                    AplicationContext = new PayPalOrderApplicationContext
                    {
                        BrandName = ctx.Settings.BrandName,
                        UserAction = "PAY_NOW",
                        ReturnUrl = ctx.Urls.ContinueUrl,
                        CancelUrl = ctx.Urls.CancelUrl
                    }
                },
                cancellationToken).ConfigureAwait(false);

            // Setup the payment form to redirect to approval link
            var approveLink = payPalOrder.Links.FirstOrDefault(x => x.Rel == "approve");
            var approveLinkMethod = (PaymentFormMethod)Enum.Parse(typeof(PaymentFormMethod), approveLink.Method, true);

            return new PaymentFormResult()
            {
                Form = new PaymentForm(approveLink.Href, approveLinkMethod)
            };
        }

        public override async Task<CallbackResult> ProcessCallbackAsync(PaymentProviderContext<PayPalCheckoutOneTimeSettings> ctx, CancellationToken cancellationToken = default)
        {
            try
            {
                var clientConfig = GetPayPalClientConfig(ctx.Settings);
                var client = new PayPalClient(clientConfig);
                var payPalWebhookEvent = await GetPayPalWebhookEventAsync(client, ctx, cancellationToken).ConfigureAwait(false);

                if (payPalWebhookEvent != null)
                {
                    var metaData = new Dictionary<string, string>();

                    PayPalOrder? payPalOrder = null;
                    PayPalPayment? payPalPayment = null;

                    if (payPalWebhookEvent.EventType.StartsWith("CHECKOUT.ORDER.", StringComparison.InvariantCultureIgnoreCase))
                    {
                        var webhookPayPalOrder = payPalWebhookEvent.Resource.Deserialize<PayPalOrder>();

                        // Fetch persisted order as it may have changed since the webhook
                        // was initially sent (it could be a webhook resend)
                        var persistedPayPalOrder = await client.GetOrderAsync(webhookPayPalOrder.Id, cancellationToken).ConfigureAwait(false);

                        if (!IsForOrder(persistedPayPalOrder.PurchaseUnits?.Length == 1 ? persistedPayPalOrder.PurchaseUnits[0].CustomId : null, ctx.Order))
                        {
                            _logger.Warn($"PayPal webhook for order '{ctx.Order.OrderNumber}' ignored: PayPal order '{persistedPayPalOrder.Id}' doesn't refer to this order.");
                            return CallbackResult.Empty;
                        }

                        if (persistedPayPalOrder.Intent == PayPalOrder.Intents.AUTHORIZE)
                        {
                            try
                            {
                                // Authorize
                                payPalOrder = persistedPayPalOrder.Status != PayPalOrder.Statuses.APPROVED
                                ? persistedPayPalOrder
                                : await client.AuthorizeOrderAsync(persistedPayPalOrder.Id, cancellationToken).ConfigureAwait(false);
                            }
                            catch (PaymentDeclinedException)
                            {
                                return CallbackResult.Ok(new TransactionInfo
                                {
                                    PaymentStatus = PaymentStatus.Error,
                                    TransactionId = persistedPayPalOrder.Id,
                                });

                                throw;
                            }

                            payPalPayment = payPalOrder.PurchaseUnits[0].Payments?.Authorizations?.FirstOrDefault();
                        }
                        else
                        {
                            // Capture
                            try
                            {
                                payPalOrder = persistedPayPalOrder.Status != PayPalOrder.Statuses.APPROVED
                                    ? persistedPayPalOrder
                                    : await client.CaptureOrderAsync(persistedPayPalOrder.Id, cancellationToken).ConfigureAwait(false);
                            }
                            catch (PaymentDeclinedException)
                            {
                                return CallbackResult.Ok(new TransactionInfo
                                {
                                    PaymentStatus = PaymentStatus.Error,
                                    TransactionId = persistedPayPalOrder.Id,
                                });

                                throw;
                            }

                            payPalPayment = payPalOrder.PurchaseUnits[0].Payments?.Captures?.FirstOrDefault();
                        }

                        // Store the paypal order ID
                        metaData.Add("PayPalOrderId", payPalOrder.Id);
                    }
                    else if (payPalWebhookEvent.EventType.StartsWith("PAYMENT.", StringComparison.InvariantCultureIgnoreCase))
                    {
                        // Listen for payment changes and update the status accordingly
                        // NB: These tend to be pretty delayed so shouldn't cause a huge issue but it's worth knowing
                        // that these will be notified after clicking the cancel / capture / refund buttons too so
                        // effectively the order will get updated twice. It's important to know as it could cause
                        // issues if they were to overlap and cause concurrency issues?
                        if (!IsHandledPaymentResourceType(payPalWebhookEvent.ResourceType))
                        {
                            _logger.Warn($"PayPal webhook for order '{ctx.Order.OrderNumber}' ignored: '{payPalWebhookEvent.EventType}' events for '{payPalWebhookEvent.ResourceType}' resources aren't handled.");
                            return CallbackResult.Empty;
                        }

                        if (payPalWebhookEvent.ResourceType == PayPalWebhookEvent.ResourceTypes.Payment.AUTHORIZATION
                            || payPalWebhookEvent.ResourceType == PayPalWebhookEvent.ResourceTypes.Payment.CAPTURE)
                        {
                            // Fetch the persisted payment as it may have changed since the webhook
                            // was initially sent (it could be a webhook resend)
                            payPalPayment = await FetchPaymentAsync(client, payPalWebhookEvent, cancellationToken).ConfigureAwait(false);

                            if (payPalPayment == null)
                            {
                                _logger.Warn($"PayPal webhook for order '{ctx.Order.OrderNumber}' ignored: the payment it refers to couldn't be found.");
                                return CallbackResult.Empty;
                            }

                            if (!IsForOrder(payPalPayment.CustomId, ctx.Order))
                            {
                                _logger.Warn($"PayPal webhook for order '{ctx.Order.OrderNumber}' ignored: PayPal payment '{payPalPayment.Id}' doesn't refer to this order.");
                                return CallbackResult.Empty;
                            }
                        }
                        else if (payPalWebhookEvent.ResourceType == PayPalWebhookEvent.ResourceTypes.Payment.REFUND)
                        {
                            payPalPayment = payPalWebhookEvent.Resource.Deserialize<PayPalRefundPayment>();
                            switch (payPalPayment?.Status)
                            {
                                case PayPalRefundPayment.Statuses.COMPLETED:
                                    return CallbackResult.Empty;

                                case PayPalRefundPayment.Statuses.PENDING:
                                case PayPalRefundPayment.Statuses.CANCELLED:
                                    _logger.Warn($"Refund request for order '{ctx.Order.TransactionInfo.TransactionId}' has been issued but the status is '{payPalPayment.Status}'. PayPal resource id: '{payPalPayment.Id}'.");
                                    return CallbackResult.Empty;

                                default:
                                    throw new PayPalPaymentProviderGeneralException($"Refund request for order '{ctx.Order.TransactionInfo.TransactionId}' failed. PayPal resource id: '{payPalPayment?.Id}'.");
                            }
                        }
                    }
                    else
                    {
                        _logger.Warn($"PayPal webhook for order '{ctx.Order.OrderNumber}' ignored: '{payPalWebhookEvent.EventType}' events aren't handled.");
                        return CallbackResult.Empty;
                    }

                    return CallbackResult.Ok(
                        new TransactionInfo
                        {
                            AmountAuthorized = decimal.Parse(payPalPayment?.Amount.Value ?? "0.00", CultureInfo.InvariantCulture),
                            TransactionId = payPalPayment?.Id ?? ctx.Order.TransactionInfo.TransactionId ?? string.Empty,
                            PaymentStatus = payPalOrder != null
                                ? GetPaymentStatus(payPalOrder)
                                : GetPaymentStatus(payPalPayment)
                        },
                        metaData);
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "PayPal - ProcessCallback");
            }

            return CallbackResult.BadRequest();
        }

        /// <summary>
        /// Checks a <c>PAYMENT.*</c> webhook event is for a resource the callback handles:
        /// an authorization, a capture or a refund.
        /// </summary>
        internal static bool IsHandledPaymentResourceType(string? resourceType)
            => resourceType == PayPalWebhookEvent.ResourceTypes.Payment.AUTHORIZATION
                || resourceType == PayPalWebhookEvent.ResourceTypes.Payment.CAPTURE
                || resourceType == PayPalWebhookEvent.ResourceTypes.Payment.REFUND;

        /// <summary>
        /// Fetches the authorization or capture a <c>PAYMENT.*</c> webhook event refers to from PayPal.
        /// </summary>
        internal static async Task<PayPalPayment?> FetchPaymentAsync(PayPalClient client, PayPalWebhookEvent payPalWebhookEvent, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(client);
            ArgumentNullException.ThrowIfNull(payPalWebhookEvent);

            var paymentId = payPalWebhookEvent.Resource?.Deserialize<PayPalPayment>()?.Id;
            if (string.IsNullOrWhiteSpace(paymentId))
            {
                return null;
            }

            return payPalWebhookEvent.ResourceType switch
            {
                PayPalWebhookEvent.ResourceTypes.Payment.AUTHORIZATION => await client.GetAuthorizationPaymentAsync(paymentId, cancellationToken).ConfigureAwait(false),
                PayPalWebhookEvent.ResourceTypes.Payment.CAPTURE => await client.GetCapturePaymentAsync(paymentId, cancellationToken).ConfigureAwait(false),
                _ => null,
            };
        }

        /// <summary>
        /// Checks a PayPal <c>custom_id</c> refers to the given order.
        /// </summary>
        internal static bool IsForOrder(string? customId, OrderReadOnly order)
            => order != null && IsForOrder(customId, order.GenerateOrderReference());

        internal static bool IsForOrder(string? customId, OrderReference orderReference)
            => orderReference != null
                && !string.IsNullOrEmpty(customId)
                && OrderReference.TryParse(customId, out OrderReference? parsed)
                && orderReference.Equals(parsed);

        public override async Task<ApiResult> FetchPaymentStatusAsync(PaymentProviderContext<PayPalCheckoutOneTimeSettings> ctx, CancellationToken cancellationToken = default)
        {
            try
            {
                if (ctx.Order.Properties.ContainsKey("PayPalOrderId"))
                {
                    var payPalOrderId = ctx.Order.Properties["PayPalOrderId"].Value;

                    var clientConfig = GetPayPalClientConfig(ctx.Settings);
                    var client = new PayPalClient(clientConfig);
                    var payPalOrder = await client.GetOrderAsync(payPalOrderId, cancellationToken).ConfigureAwait(false);

                    var paymentStatus = GetPaymentStatus(payPalOrder, out PayPalPayment payPalPayment);

                    return new ApiResult()
                    {
                        TransactionInfo = new TransactionInfoUpdate()
                        {
                            // The payment is the capture (or authorization) so the id stays valid for further refunds
                            TransactionId = payPalPayment.Id,
                            PaymentStatus = paymentStatus
                        }
                    };
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "PayPal - FetchPaymentStatus");
            }

            return ApiResult.Empty;
        }

        public override async Task<ApiResult> CapturePaymentAsync(PaymentProviderContext<PayPalCheckoutOneTimeSettings> ctx, CancellationToken cancellationToken = default)
        {
            try
            {
                if (ctx.Order.TransactionInfo.PaymentStatus == PaymentStatus.Authorized)
                {
                    var clientConfig = GetPayPalClientConfig(ctx.Settings);
                    var client = new PayPalClient(clientConfig);

                    var payPalPayment = await client.CapturePaymentAsync(ctx.Order.TransactionInfo.TransactionId, cancellationToken).ConfigureAwait(false);

                    return new ApiResult()
                    {
                        TransactionInfo = new TransactionInfoUpdate()
                        {
                            TransactionId = payPalPayment.Id,
                            PaymentStatus = GetPaymentStatus(payPalPayment)
                        }
                    };
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "PayPal - CapturePayment");
            }

            return ApiResult.Empty;
        }

        public override async Task<ApiResult?> RefundPaymentAsync(PaymentProviderContext<PayPalCheckoutOneTimeSettings> context, PaymentProviderOrderRefundRequest refundRequest, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(refundRequest);

            try
            {
                if (context.Order.TransactionInfo.PaymentStatus
                    is PaymentStatus.Captured or PaymentStatus.PartiallyRefunded)
                {
                    // Get currency information
                    CurrencyReadOnly currency = await Context.Services.CurrencyService.GetCurrencyAsync(context.Order.CurrencyId);
                    string currencyCode = currency.Code.ToUpperInvariant();

                    PayPalClientConfig clientConfig = GetPayPalClientConfig(context.Settings);
                    PayPalClient client = new(clientConfig);

                    // Commerce records the refund after this call, so the refundable amount is still the pre-refund balance
                    decimal refundableAmount = await context.Order.CalculateRefundableAmountAsync();

                    PayPalRefundPayment payPalPayment = await client.RefundPaymentAsync(
                        new PaypalClientRefundRequest
                        {
                            PaymentId = context.Order.TransactionInfo.TransactionId,
                            Amount = new PayPalAmount
                            {
                                Value = refundRequest.RefundAmount.ToString("0.00", CultureInfo.InvariantCulture),
                                CurrencyCode = currencyCode,
                            }
                        },
                        cancellationToken).ConfigureAwait(false);

                    // A completed refund is only a full refund if it covers the remaining refundable amount
                    PaymentStatus refundStatus = GetPaymentStatus(payPalPayment);
                    if (refundStatus == PaymentStatus.Refunded
                        && refundRequest.RefundAmount < refundableAmount)
                    {
                        refundStatus = PaymentStatus.PartiallyRefunded;
                    }

                    return new ApiResult()
                    {
                        TransactionInfo = new TransactionInfoUpdate()
                        {
                            // Need to keep the paypal capture resource id after a partial refund in order to do more refunds later on
                            TransactionId = context.Order.TransactionInfo.TransactionId,
                            PaymentStatus = refundStatus
                        },
                    };
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "PayPal - RefundPayment");
            }

            return ApiResult.Empty;
        }

        public override async Task<ApiResult> CancelPaymentAsync(PaymentProviderContext<PayPalCheckoutOneTimeSettings> ctx, CancellationToken cancellationToken = default)
        {
            try
            {
                if (ctx.Order.TransactionInfo.PaymentStatus == PaymentStatus.Authorized)
                {
                    var clientConfig = GetPayPalClientConfig(ctx.Settings);
                    var client = new PayPalClient(clientConfig);

                    await client.CancelPaymentAsync(ctx.Order.TransactionInfo.TransactionId, cancellationToken).ConfigureAwait(false);

                    // Cancel payment enpoint doesn't return a result so if the request is successfull
                    // then we'll deem it as successfull and directly set the payment status to Cancelled
                    return new ApiResult()
                    {
                        TransactionInfo = new TransactionInfoUpdate()
                        {
                            TransactionId = ctx.Order.TransactionInfo.TransactionId,
                            PaymentStatus = PaymentStatus.Cancelled
                        }
                    };
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "PayPal - CancelPayment");
            }

            return ApiResult.Empty;
        }
    }
}
