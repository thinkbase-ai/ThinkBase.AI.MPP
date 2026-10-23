using ThinkBase.AI.MPP.Models;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using Stripe;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;

namespace ThinkBase.AI.MPP.Services
{
    /// <summary>
    /// The Machine Payments Protocol (MPP) <c>tempo</c>/<c>charge</c> method: the agent pays in
    /// stablecoin rather than with a card.
    ///
    /// The challenge names the price in base units of a TIP-20 stablecoin and the deposit address
    /// of this server. The agent transfers that amount on the Tempo blockchain itself — the
    /// <c>push</c> submission mode of the method, the only one offered, so that this server never
    /// holds or broadcasts a signed transaction — and presents the hash of the transfer as its
    /// credential. The transfer is then verified against the Tempo JSON-RPC endpoint: the
    /// transaction must have succeeded and must carry <c>Transfer</c> logs of the token named in
    /// the challenge that pay the deposit address at least the amount charged. The hash of a
    /// transfer that has paid for a call is remembered so that it cannot pay for a second one.
    /// </summary>
    public class TempoPaymentService : MppChallengeService
    {
        /// <summary>Name of the <see cref="HttpClient"/> the Tempo JSON-RPC endpoint is called with.</summary>
        public const string HttpClientName = "Tempo";

        /// <summary>Cache key prefix under which the hash of a spent transfer is remembered.</summary>
        internal const string TransferCacheKeyPrefix = "Mpp:Tempo:Transfer:";

        /// <summary>How often the node is asked again for a receipt it does not have yet.</summary>
        private static readonly TimeSpan ReceiptPollInterval = TimeSpan.FromMilliseconds(500);

        /// <summary>Topic of the ERC-20 style <c>Transfer(address,address,uint256)</c> event.</summary>
        private const string TransferTopic = "0xddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef";

        /// <summary>
        /// Topic of the TIP-20 <c>TransferWithMemo(address,address,uint256,bytes32)</c> event, which
        /// a transfer that carries a memo emits beside the plain <c>Transfer</c> one. An MPP payer
        /// attaches an attribution memo to the transfer, so both are always present.
        /// </summary>
        private const string TransferWithMemoTopic = "0x57bc7354aa85aed339e000bccffabbc529466af35f0772c8f8ee1145927de7f0";

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly StripeClient _stripeClient;
        private readonly TempoOptions _tempo;
        private readonly ILogger<TempoPaymentService> _logger;

        public TempoPaymentService(
            IHttpClientFactory httpClientFactory,
            StripeClient stripeClient,
            IDistributedCache distributedCache,
            IOptions<MppOptions> options,
            ILogger<TempoPaymentService> logger)
            : base(distributedCache, options.Value)
        {
            _httpClientFactory = httpClientFactory;
            _stripeClient = stripeClient;
            _tempo = options.Value.Tempo;
            _logger = logger;
        }

        public override string Method => TempoOptions.MethodName;

        /// <summary>The price of one call in base units of the stablecoin, rounded up.</summary>
        internal static long BaseUnits(decimal amountCents)
        {
            var units = (long)Math.Ceiling(amountCents * TempoOptions.BaseUnitsPerCent);
            return Math.Max(units, TempoOptions.MinimumBaseUnits);
        }

        protected override JsonObject BuildRequest(ToolPrice price, string description) => new()
        {
            ["amount"] = BaseUnits(price.AmountCents).ToString(CultureInfo.InvariantCulture),
            ["currency"] = _tempo.TokenAddress ?? string.Empty,
            ["recipient"] = _tempo.DepositAddress ?? string.Empty,
            ["methodDetails"] = new JsonObject
            {
                ["chainId"] = _tempo.ChainId,
                ["feePayer"] = false,
                ["supportedModes"] = new JsonArray("push")
            }
        };

        protected override async Task<PaymentVerificationResult> SettleAsync(
            JsonObject credential,
            JsonObject request,
            string challengeId,
            string toolName,
            DateTimeOffset expiresAt,
            CancellationToken cancellationToken)
        {
            var payload = credential["payload"] as JsonObject;
            var type = payload is null ? null : Value(payload, "type");
            var hash = payload is null ? null : Value(payload, "hash");
            if (!string.Equals(type, "hash", StringComparison.Ordinal) || !IsTransactionHash(hash))
            {
                return PaymentVerificationResult.Failure(
                    "invalid-payload",
                    "The credential does not carry the hash of a Tempo transfer; this server settles the charge in push mode only.");
            }

            var transferHash = hash!;
            var chainId = (request["methodDetails"] as JsonObject)?["chainId"] is JsonValue chain && chain.TryGetValue<long>(out var declaredChain)
                ? declaredChain
                : (long?)null;
            if (chainId != _tempo.ChainId)
            {
                return PaymentVerificationResult.Failure("invalid-challenge", "The challenge was issued for another chain.");
            }

            var amount = MinorUnits(request);
            var token = Value(request, "currency");
            var recipient = Value(request, "recipient");
            if (amount is null || string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(recipient))
            {
                return PaymentVerificationResult.Failure("invalid-challenge", "The challenge does not name a transfer to verify.");
            }

            if (await IsSettledAsync(challengeId, cancellationToken))
            {
                return PaymentVerificationResult.Failure("verification-failed", "The challenge has already been paid; pay the new one.");
            }

            var transferKey = TransferCacheKeyPrefix + transferHash.ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(await DistributedCache.GetStringAsync(transferKey, cancellationToken)))
            {
                return PaymentVerificationResult.Failure("verification-failed", "That transfer has already paid for a call; make a new one.");
            }

            JsonObject? receiptJson;
            try
            {
                receiptJson = await WaitForTransactionReceiptAsync(transferHash, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "The Tempo transfer {Hash} for tool {Tool} could not be verified.", transferHash, toolName);
                return PaymentVerificationResult.Failure("internal-payment-error", "The transfer could not be verified.");
            }

            if (receiptJson is null)
            {
                return PaymentVerificationResult.Failure("payment-action-required", "The transfer is not on chain yet; retry when it has been included.");
            }

            if (!string.Equals(Value(receiptJson, "status"), "0x1", StringComparison.OrdinalIgnoreCase))
            {
                return PaymentVerificationResult.Failure("verification-failed", "The transfer failed on chain.");
            }

            var paid = TransferredTo(receiptJson, token, recipient);
            if (paid < amount.Value)
            {
                return PaymentVerificationResult.Failure(
                    "verification-failed",
                    $"The transfer pays {paid} base units of the token, less than the {amount.Value} the challenge asks for.");
            }

            await DistributedCache.SetStringAsync(transferKey, challengeId, new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = _tempo.TransactionMemory
            }, cancellationToken);
            await MarkSettledAsync(challengeId, transferHash, expiresAt, cancellationToken);

            // Stripe holds what is paid to the deposit address, so the transfer is recorded with it
            // as a payment of the call, the way the card payment of the other rail already is.
            await StripeMachinePayments.RecordCryptoPaymentAsync(
                _stripeClient,
                TempoOptions.MethodName,
                transferHash,
                amount.Value,
                toolName,
                challengeId,
                _logger,
                cancellationToken);

            var receipt = new JsonObject
            {
                ["status"] = "success",
                ["method"] = Method,
                ["timestamp"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                ["reference"] = transferHash,
                ["challengeId"] = challengeId
            };

            return PaymentVerificationResult.Success(receipt, transferHash);
        }

        /// <summary>
        /// The transaction receipt the Tempo node holds, waited for until
        /// <see cref="TempoOptions.ReceiptWait"/> has passed, or null when the transfer is still not
        /// known by then. The payer presents the hash as soon as its own node has the transfer, so
        /// the node this server asks may not have caught up with it yet; and a payer that is told to
        /// retry pays again rather than presenting the same transfer once more, so waiting here is
        /// what keeps a transfer that has been made from being paid for twice.
        /// </summary>
        private async Task<JsonObject?> WaitForTransactionReceiptAsync(string hash, CancellationToken cancellationToken)
        {
            var deadline = DateTimeOffset.UtcNow + _tempo.ReceiptWait;
            while (true)
            {
                var receipt = await GetTransactionReceiptAsync(hash, cancellationToken);
                if (receipt is not null || DateTimeOffset.UtcNow + ReceiptPollInterval >= deadline)
                {
                    return receipt;
                }

                await Task.Delay(ReceiptPollInterval, cancellationToken);
            }
        }

        /// <summary>The transaction receipt the Tempo node holds, or null when the transfer is not mined.</summary>
        private async Task<JsonObject?> GetTransactionReceiptAsync(string hash, CancellationToken cancellationToken)
        {
            var body = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "eth_getTransactionReceipt",
                ["params"] = new JsonArray(hash)
            };

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_tempo.RpcTimeout);

            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(_tempo.RpcUrl, content, timeout.Token);
            response.EnsureSuccessStatusCode();

            var text = await response.Content.ReadAsStringAsync(timeout.Token);
            var answer = JsonNode.Parse(text) as JsonObject;
            if (answer?["error"] is JsonObject error)
            {
                throw new InvalidOperationException($"The Tempo node refused the request: {error.ToJsonString()}");
            }

            return answer?["result"] as JsonObject;
        }

        /// <summary>
        /// The base units the receipt transfers to <paramref name="recipient"/> in the token at
        /// <paramref name="token"/>, summed over every transfer log of that token.
        ///
        /// A TIP-20 transfer that carries a memo — the kind an MPP payer makes, so that the payment
        /// can be attributed to the challenge — emits a <c>Transfer</c> and a
        /// <c>TransferWithMemo</c> log for the one movement of funds. Both are recognised, and an
        /// adjacent pair that describes the same movement is counted once.
        /// </summary>
        private static BigInteger TransferredTo(JsonObject receipt, string token, string recipient)
        {
            var total = BigInteger.Zero;
            if (receipt["logs"] is not JsonArray logs)
            {
                return total;
            }

            (bool WithMemo, string? From, BigInteger Amount)? previous = null;
            var previousIndex = -1;
            for (var index = 0; index < logs.Count; index++)
            {
                if (logs[index] is not JsonObject log
                    || !SameAddress(Value(log, "address"), token)
                    || log["topics"] is not JsonArray topics
                    || topics.Count < 3
                    || !SameAddress(AsString(topics[2]), recipient))
                {
                    continue;
                }

                var topic = AsString(topics[0]);
                var withMemo = string.Equals(topic, TransferWithMemoTopic, StringComparison.OrdinalIgnoreCase);
                if (!withMemo && !string.Equals(topic, TransferTopic, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Both events carry the amount in the first word of the data; the memo of the
                // second is an indexed parameter and so is a topic rather than part of it.
                var transfer = (WithMemo: withMemo, From: AsString(topics[1]), Amount: FirstWord(Value(log, "data")));

                if (previous is { } earlier
                    && index == previousIndex + 1
                    && earlier.WithMemo != transfer.WithMemo
                    && SameAddress(earlier.From, transfer.From)
                    && earlier.Amount == transfer.Amount)
                {
                    // The pair of logs one memo transfer emits; the funds moved once.
                    previous = null;
                    previousIndex = -1;
                    continue;
                }

                total += transfer.Amount;
                previous = transfer;
                previousIndex = index;
            }

            return total;
        }

        /// <summary>True when both hold the same twenty byte address, whatever their case or padding.</summary>
        private static bool SameAddress(string? left, string? right)
        {
            static string? Normalise(string? address)
            {
                var text = address?.Trim();
                if (string.IsNullOrEmpty(text))
                {
                    return null;
                }

                if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    text = text[2..];
                }

                return text.Length < 40 ? null : text[^40..].ToLowerInvariant();
            }

            var a = Normalise(left);
            var b = Normalise(right);
            return a is not null && b is not null && string.Equals(a, b, StringComparison.Ordinal);
        }

        /// <summary>The unsigned integer the first thirty-two byte word of a hex data field holds.</summary>
        private static BigInteger FirstWord(string? hex)
        {
            var text = hex?.Trim();
            if (text is null)
            {
                return BigInteger.Zero;
            }

            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                text = text[2..];
            }

            return Quantity(text.Length > 64 ? text[..64] : text);
        }

        /// <summary>The unsigned integer a hex quantity holds; zero when it is not one.</summary>
        private static BigInteger Quantity(string? hex)
        {
            if (string.IsNullOrWhiteSpace(hex))
            {
                return BigInteger.Zero;
            }

            var text = hex.Trim();
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                text = text[2..];
            }

            if (text.Length == 0 || !text.All(Uri.IsHexDigit))
            {
                return BigInteger.Zero;
            }

            // The leading zero keeps the value unsigned whatever its most significant bit is.
            return BigInteger.Parse("0" + text, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        private static bool IsTransactionHash(string? hash) =>
            hash is not null
            && hash.Length == 66
            && hash.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && hash[2..].All(Uri.IsHexDigit);
    }
}
