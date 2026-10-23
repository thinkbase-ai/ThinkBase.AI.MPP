using ThinkBase.AI.MPP.Models;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using Stripe;
using System.Globalization;
using System.Text.Json.Nodes;

namespace ThinkBase.AI.MPP.Services
{
    /// <summary>
    /// The Machine Payments Protocol (MPP) <c>stripe</c>/<c>charge</c> method: the agent authorises
    /// the payment with Stripe and presents the shared payment token it is given.
    ///
    /// The challenge is the stateless one <see cref="MppChallengeService"/> builds. A settled
    /// challenge identifier is kept in the distributed cache until it expires so that it cannot be
    /// replayed, and the Stripe idempotency key makes a retry of the same payment safe.
    /// </summary>
    public class MppPaymentService : MppChallengeService
    {
        private readonly StripeClient _client;
        private readonly ILogger<MppPaymentService> _logger;

        public MppPaymentService(
            StripeClient client,
            IDistributedCache distributedCache,
            IOptions<MppOptions> options,
            ILogger<MppPaymentService> logger)
            : base(distributedCache, options.Value)
        {
            _client = client;
            _logger = logger;
        }

        public override string Method => MppOptions.MethodName;

        protected override JsonObject BuildRequest(ToolPrice price, string description)
        {
            var request = new JsonObject
            {
                ["amount"] = Math.Round(price.AmountCents, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture),
                ["currency"] = (string.IsNullOrWhiteSpace(price.Currency) ? Options.Currency : price.Currency).ToLowerInvariant()
            };

            var methodDetails = new JsonObject
            {
                // The Stripe charge method asks for the profile the token is to be issued to; a
                // challenge without it cannot be paid, so Mpp:NetworkId has to be configured.
                ["networkId"] = Options.NetworkId ?? string.Empty,
                ["paymentMethodTypes"] = new JsonArray(MppOptions.PaymentMethodTypes.Select(t => (JsonNode)JsonValue.Create(t)!).ToArray())
            };

            request["methodDetails"] = methodDetails;
            return request;
        }

        protected override async Task<PaymentVerificationResult> SettleAsync(
            JsonObject credential,
            JsonObject request,
            string challengeId,
            string toolName,
            DateTimeOffset expiresAt,
            CancellationToken cancellationToken)
        {
            var payload = credential["payload"] as JsonObject;
            var token = payload is null ? null : Value(payload, "spt");
            if (string.IsNullOrWhiteSpace(token) || !token.StartsWith("spt_", StringComparison.Ordinal))
            {
                return PaymentVerificationResult.Failure("invalid-payload", "The credential does not carry a Stripe shared payment token.");
            }

            if (await IsSettledAsync(challengeId, cancellationToken))
            {
                return PaymentVerificationResult.Failure("verification-failed", "The challenge has already been paid; pay the new one.");
            }

            var minorUnits = MinorUnits(request);
            var currency = Value(request, "currency");
            if (minorUnits is null || string.IsNullOrWhiteSpace(currency))
            {
                return PaymentVerificationResult.Failure("invalid-challenge", "The challenge does not name an amount to charge.");
            }

            try
            {
                // The shared payment token is only taken in the preview API version, so the
                // PaymentIntent is created as a raw request that can ask for that version.
                var parameters = new List<KeyValuePair<string, string>>
                {
                    new("amount", minorUnits.Value.ToString(CultureInfo.InvariantCulture)),
                    new("currency", currency),
                    new("confirm", "true"),
                    new("automatic_payment_methods[enabled]", "true"),
                    new("automatic_payment_methods[allow_redirects]", "never"),
                    new("shared_payment_granted_token", token)
                };
                parameters.AddRange(StripeMachinePayments.MetadataParameters(StripeMachinePayments.Metadata(toolName, challengeId)));

                var paymentIntent = await StripeMachinePayments.CreatePaymentIntentAsync(
                    _client,
                    parameters,
                    $"{challengeId}_{token}",
                    cancellationToken);

                var status = Value(paymentIntent, "status");
                var reference = Value(paymentIntent, "id");
                if (!string.Equals(status, "succeeded", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(reference))
                {
                    return PaymentVerificationResult.Failure("payment-action-required", $"Stripe did not settle the payment ({status}).");
                }

                await MarkSettledAsync(challengeId, reference, expiresAt, cancellationToken);

                var receipt = new JsonObject
                {
                    ["status"] = "success",
                    ["method"] = Method,
                    ["timestamp"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                    ["reference"] = reference,
                    ["challengeId"] = challengeId
                };

                return PaymentVerificationResult.Success(receipt, reference);
            }
            catch (StripeException ex)
            {
                _logger.LogWarning(ex, "Stripe refused the payment for tool {Tool}.", toolName);
                return PaymentVerificationResult.Failure("verification-failed", "The payment was refused.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "The payment for tool {Tool} could not be settled.", toolName);
                return PaymentVerificationResult.Failure("internal-payment-error", "The payment could not be settled.");
            }
        }
    }
}
