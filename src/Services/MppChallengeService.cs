using ThinkBase.AI.MPP.Interfaces;
using ThinkBase.AI.MPP.Models;
using Microsoft.Extensions.Caching.Distributed;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace ThinkBase.AI.MPP.Services
{
    /// <summary>
    /// The part of the Machine Payments Protocol (MPP) every payment method shares: issuing the
    /// stateless challenge and checking, when the credential comes back, that the challenge it
    /// answers is one this server issued, for this protection space and this tool, and that it has
    /// neither expired nor already been paid. Settling the payment itself is left to the method:
    /// <see cref="MppPaymentService"/> takes it through Stripe, <see cref="TempoPaymentService"/>
    /// verifies a stablecoin transfer on the Tempo blockchain.
    ///
    /// Challenges are stateless: the identifier is an HMAC-SHA256 of the challenge fields, so a
    /// credential can be checked without having stored anything, and nothing in the challenge —
    /// the price above all — can be altered by the caller.
    /// </summary>
    public abstract class MppChallengeService : IMppPaymentService
    {
        internal const string SettledCacheKeyPrefix = "Mpp:Settled:";

        private static readonly string ProcessSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        protected MppChallengeService(IDistributedCache distributedCache, MppOptions options)
        {
            DistributedCache = distributedCache;
            Options = options;
        }

        protected IDistributedCache DistributedCache { get; }

        protected MppOptions Options { get; }

        /// <inheritdoc />
        public abstract string Method { get; }

        /// <summary>Payment intent the challenges of this method are issued for.</summary>
        protected virtual string Intent => MppOptions.IntentName;

        /// <summary>The payment request of a challenge for one call of the tool at the given price.</summary>
        protected abstract JsonObject BuildRequest(ToolPrice price, string description);

        /// <summary>Takes the payment the verified credential authorises.</summary>
        protected abstract Task<PaymentVerificationResult> SettleAsync(
            JsonObject credential,
            JsonObject request,
            string challengeId,
            string toolName,
            DateTimeOffset expiresAt,
            CancellationToken cancellationToken);

        public PaymentChallenge CreateChallenge(string realm, string toolName, ToolPrice price)
        {
            var description = string.IsNullOrWhiteSpace(price.Description)
                ? $"One call of the {toolName} tool of ThinkBase.AI.MPP."
                : price.Description;

            var request = BuildRequest(price, description);
            request["description"] = description;

            // The tool name travels in the opaque parameter so that a challenge issued for one tool
            // cannot be used to pay for another, as the MCP transport binding requires.
            var opaque = JsonCanonicalizer.ToBase64Url(new JsonObject { ["tool"] = toolName });

            var challenge = new PaymentChallenge
            {
                Realm = realm,
                Method = Method,
                Intent = Intent,
                Request = request,
                Expires = DateTimeOffset.UtcNow.Add(Options.ChallengeLifetime).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                Opaque = opaque,
                Description = description
            };

            challenge.Id = ComputeId(challenge.Realm, challenge.Method, challenge.Intent, JsonCanonicalizer.ToBase64Url(request), challenge.Expires, null, null, opaque);
            return challenge;
        }

        public string FormatChallengeHeader(PaymentChallenge challenge)
        {
            var parameters = new List<string>
            {
                $"id=\"{Escape(challenge.Id)}\"",
                $"realm=\"{Escape(challenge.Realm)}\"",
                $"method=\"{Escape(challenge.Method)}\"",
                $"intent=\"{Escape(challenge.Intent)}\"",
                $"request=\"{Escape(JsonCanonicalizer.ToBase64Url(challenge.Request))}\""
            };

            if (!string.IsNullOrWhiteSpace(challenge.Expires))
            {
                parameters.Add($"expires=\"{Escape(challenge.Expires)}\"");
            }

            if (!string.IsNullOrWhiteSpace(challenge.Opaque))
            {
                parameters.Add($"opaque=\"{Escape(challenge.Opaque)}\"");
            }

            if (!string.IsNullOrWhiteSpace(challenge.Description))
            {
                parameters.Add($"description=\"{Escape(challenge.Description)}\"");
            }

            return "Payment " + string.Join(", ", parameters);
        }

        public JsonObject ToJsonRpcChallenge(PaymentChallenge challenge)
        {
            // Over JSON-RPC the request travels as a native JSON object rather than base64url.
            var json = new JsonObject
            {
                ["id"] = challenge.Id,
                ["realm"] = challenge.Realm,
                ["method"] = challenge.Method,
                ["intent"] = challenge.Intent,
                ["request"] = JsonNode.Parse(challenge.Request.ToJsonString()),
                ["expires"] = challenge.Expires
            };

            if (!string.IsNullOrWhiteSpace(challenge.Opaque))
            {
                json["opaque"] = challenge.Opaque;
            }

            if (!string.IsNullOrWhiteSpace(challenge.Description))
            {
                json["description"] = challenge.Description;
            }

            return json;
        }

        public async Task<PaymentVerificationResult> VerifyAndSettleAsync(JsonObject credential, string realm, string toolName, CancellationToken cancellationToken = default)
        {
            if (credential["challenge"] is not JsonObject echoed)
            {
                return PaymentVerificationResult.Failure("malformed-credential", "The credential does not carry the challenge it answers.");
            }

            var id = Value(echoed, "id");
            var challengeRealm = Value(echoed, "realm");
            var method = Value(echoed, "method");
            var intent = Value(echoed, "intent");
            var expires = Value(echoed, "expires");
            var opaque = NormalizeOpaque(echoed["opaque"]);
            var digest = Value(echoed, "digest");
            var header = Value(echoed, "header");

            // The request is base64url over HTTP and a native object over JSON-RPC. Either way it is
            // bound to the identifier through its canonical form, which is computed here from the
            // request itself rather than taken from the encoding the payer sent: a client that
            // decodes the request to read the price and encodes it again — as every MPP client
            // library does — may order or escape its JSON differently without the payment ceasing
            // to answer the challenge it was made for.
            var requestNode = echoed["request"];
            var request = requestNode as JsonObject ?? ParseObject(JsonCanonicalizer.Base64UrlDecode(AsString(requestNode)));
            var encodedRequest = request is null ? null : JsonCanonicalizer.ToBase64Url(request);

            if (string.IsNullOrWhiteSpace(id) || request is null || string.IsNullOrWhiteSpace(encodedRequest))
            {
                return PaymentVerificationResult.Failure("malformed-credential", "The challenge in the credential is incomplete.");
            }

            var expected = ComputeId(challengeRealm, method, intent, encodedRequest, expires, digest, header, opaque);
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(id)))
            {
                return PaymentVerificationResult.Failure("invalid-challenge", "The challenge was not issued by this server.");
            }

            if (!string.Equals(challengeRealm, realm, StringComparison.OrdinalIgnoreCase))
            {
                return PaymentVerificationResult.Failure("invalid-challenge", "The challenge was issued for another protection space.");
            }

            if (!string.Equals(method, Method, StringComparison.Ordinal) || !string.Equals(intent, Intent, StringComparison.Ordinal))
            {
                return PaymentVerificationResult.Failure("method-unsupported", $"This challenge is not one of the {Method} {Intent} payment method.");
            }

            if (!DateTimeOffset.TryParse(expires, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var expiresAt)
                || expiresAt <= DateTimeOffset.UtcNow)
            {
                return PaymentVerificationResult.Failure("payment-expired", "The challenge has expired; pay the new one.");
            }

            if (!PaysForTool(opaque, toolName))
            {
                return PaymentVerificationResult.Failure("invalid-challenge", "The challenge was issued for another tool.");
            }

            return await SettleAsync(credential, request, id, toolName, expiresAt, cancellationToken);
        }

        /// <summary>True when this challenge has already been paid for.</summary>
        protected async Task<bool> IsSettledAsync(string challengeId, CancellationToken cancellationToken) =>
            !string.IsNullOrWhiteSpace(await DistributedCache.GetStringAsync(SettledCacheKeyPrefix + challengeId, cancellationToken));

        /// <summary>Remembers, until it expires, that this challenge has been paid, so it cannot be replayed.</summary>
        protected Task MarkSettledAsync(string challengeId, string reference, DateTimeOffset expiresAt, CancellationToken cancellationToken) =>
            DistributedCache.SetStringAsync(SettledCacheKeyPrefix + challengeId, reference, new DistributedCacheEntryOptions
            {
                AbsoluteExpiration = expiresAt.AddMinutes(5)
            }, cancellationToken);

        /// <summary>The amount, in the minor units the challenge names it in, or null when it names none.</summary>
        protected static long? MinorUnits(JsonObject request) =>
            long.TryParse(Value(request, "amount"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var units) && units > 0
                ? units
                : null;

        protected static bool PaysForTool(string? opaque, string toolName)
        {
            var json = ParseObject(JsonCanonicalizer.Base64UrlDecode(opaque));
            return json is not null && string.Equals(Value(json, "tool"), toolName, StringComparison.Ordinal);
        }

        /// <summary>
        /// The opaque parameter of an echoed challenge in the form it was issued in: the base64url
        /// string of the protocol, whether the payer returned it as it stands, decoded it and
        /// encoded it again, or sent the correlation data itself as an object, as some clients do.
        /// </summary>
        private static string? NormalizeOpaque(JsonNode? opaque)
        {
            if (opaque is JsonObject expanded)
            {
                return JsonCanonicalizer.ToBase64Url(expanded);
            }

            var text = AsString(opaque);
            var decoded = ParseObject(JsonCanonicalizer.Base64UrlDecode(text));
            return decoded is null ? text : JsonCanonicalizer.ToBase64Url(decoded);
        }

        /// <summary>
        /// The challenge identifier, an HMAC of the fields the challenge binds, exactly as the
        /// stateless construction of the protocol describes it.
        /// </summary>
        private string ComputeId(string? realm, string? method, string? intent, string? encodedRequest, string? expires, string? digest, string? header, string? opaque)
        {
            var parts = new List<string> { realm ?? string.Empty, method ?? string.Empty, intent ?? string.Empty, encodedRequest ?? string.Empty, expires ?? string.Empty, digest ?? string.Empty };
            if (!string.IsNullOrEmpty(header))
            {
                parts.Add(header);
            }

            parts.Add(opaque ?? string.Empty);

            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(Secret));
            return JsonCanonicalizer.Base64UrlEncode(hmac.ComputeHash(Encoding.UTF8.GetBytes(string.Join('|', parts))));
        }

        /// <summary>
        /// The secret the challenge identifiers are derived from. A per process secret is used when
        /// none is configured: challenges then stop being payable when the server restarts, which
        /// is safe but means <c>Mpp:ChallengeSecret</c> should be set on a farm.
        /// </summary>
        private string Secret => string.IsNullOrWhiteSpace(Options.ChallengeSecret) ? ProcessSecret : Options.ChallengeSecret;

        /// <summary>The JSON object the supplied UTF-8 bytes hold, or null when they are not one.</summary>
        protected static JsonObject? ParseObject(byte[]? utf8)
        {
            if (utf8 is null)
            {
                return null;
            }

            try
            {
                return JsonNode.Parse(Encoding.UTF8.GetString(utf8)) as JsonObject;
            }
            catch (System.Text.Json.JsonException)
            {
                return null;
            }
        }

        /// <summary>The string held under <paramref name="name"/>, or null when it is absent or is not a string.</summary>
        protected static string? Value(JsonObject json, string name) => AsString(json[name]);

        protected static string? AsString(JsonNode? node) =>
            node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        /// <summary>
        /// A challenge parameter as the quoted string of a <c>WWW-Authenticate</c> header: the
        /// backslash and the quotation mark are escaped, and so is anything an HTTP header may not
        /// carry — a control character or a character above ASCII, which a description taken from
        /// the price of a tool may well hold. A payer unescapes them again, and none of these
        /// parameters is bound to the challenge identifier in its escaped form.
        /// </summary>
        private static string Escape(string? value)
        {
            var builder = new StringBuilder();
            foreach (var character in value ?? string.Empty)
            {
                if (character is '\\' or '"')
                {
                    builder.Append('\\').Append(character);
                }
                else if (character < ' ' || character > '~')
                {
                    builder.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                }
                else
                {
                    builder.Append(character);
                }
            }

            return builder.ToString();
        }
    }
}
