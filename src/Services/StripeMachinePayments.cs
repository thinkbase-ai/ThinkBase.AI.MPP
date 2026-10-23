using ThinkBase.AI.MPP.Models;
using Stripe;
using System.Text.Json.Nodes;

namespace ThinkBase.AI.MPP.Services
{
    /// <summary>
    /// The Stripe calls the two Machine Payments Protocol rails share. Whichever way the agent
    /// pays — a shared payment token or a stablecoin transfer to the Stripe deposit address — the
    /// payment ends up as a Stripe PaymentIntent, so that both are reported in one ledger, exactly
    /// as the Stripe machine payments sample does it.
    ///
    /// Both calls are made as raw requests because they need the preview API version, which the
    /// Stripe SDK does not let a typed call ask for, and parameters the SDK does not carry yet.
    /// </summary>
    internal static class StripeMachinePayments
    {
        /// <summary>
        /// Stripe API version the machine payment calls are made with: shared payment tokens and
        /// crypto transaction verification are both in preview and are refused without it.
        /// </summary>
        public const string PreviewApiVersion = "2026-07-29.preview";

        private const string PaymentIntentsPath = "/v1/payment_intents";

        /// <summary>Metadata marking a PaymentIntent as the payment for one MCP tool call.</summary>
        public static Dictionary<string, string> Metadata(string toolName, string challengeId) => new()
        {
            ["machine_payment"] = "true",
            ["tool"] = toolName,
            ["challenge_id"] = challengeId
        };

        /// <summary>
        /// Creates a PaymentIntent from the form parameters supplied, in the preview API version,
        /// and answers the JSON object Stripe replied with.
        /// </summary>
        public static async Task<JsonObject> CreatePaymentIntentAsync(
            StripeClient client,
            IEnumerable<KeyValuePair<string, string>> parameters,
            string idempotencyKey,
            CancellationToken cancellationToken)
        {
            var options = new RawRequestOptions { IdempotencyKey = idempotencyKey };
            options.AdditionalHeaders["Stripe-Version"] = PreviewApiVersion;

            var content = string.Join(
                '&',
                parameters.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));

            var response = await client.RawRequestAsync(HttpMethod.Post, PaymentIntentsPath, content, options, cancellationToken);
            return JsonNode.Parse(response.Content) as JsonObject ?? [];
        }

        /// <summary>The <c>metadata[…]</c> form parameters of the supplied metadata.</summary>
        public static IEnumerable<KeyValuePair<string, string>> MetadataParameters(IDictionary<string, string> metadata) =>
            metadata.Select(entry => new KeyValuePair<string, string>($"metadata[{entry.Key}]", entry.Value));

        /// <summary>
        /// Records a settled stablecoin transfer in Stripe, as a crypto PaymentIntent in
        /// transaction verification mode. The hash of the transfer is the idempotency key, so the
        /// same transfer is never recorded twice. The transfer has already paid for the call, so a
        /// Stripe that cannot record it is logged and no more: it must not cost the agent its call.
        /// </summary>
        public static async Task RecordCryptoPaymentAsync(
            StripeClient client,
            string network,
            string transactionHash,
            long baseUnits,
            string toolName,
            string challengeId,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            var amountCents = baseUnits / TempoOptions.BaseUnitsPerCent;
            if (amountCents < 1)
            {
                logger.LogWarning(
                    "The {Network} transfer {Hash} of {Units} base units is less than the cent Stripe can record.",
                    network, transactionHash, baseUnits);
                return;
            }

            var parameters = new List<KeyValuePair<string, string>>
            {
                new("amount", amountCents.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new("currency", "usd"),
                new("confirm", "true"),
                new("payment_method_data[type]", "crypto"),
                new("payment_method_types[0]", "crypto"),
                new("payment_method_options[crypto][mode]", "transaction_verification"),
                new("payment_method_options[crypto][transaction_verification_options][network]", network),
                new("payment_method_options[crypto][transaction_verification_options][transaction_hash]", transactionHash)
            };
            parameters.AddRange(MetadataParameters(Metadata(toolName, challengeId)));

            try
            {
                await CreatePaymentIntentAsync(client, parameters, transactionHash, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "The {Network} transfer {Hash} could not be recorded in Stripe.", network, transactionHash);
            }
        }
    }
}
