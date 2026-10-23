using System.Security.Cryptography;
using System.Text;

namespace ThinkBase.AI.MPP.Models
{
    /// <summary>
    /// Configuration of the Machine Payments Protocol (MPP) support, bound from the <c>Mpp</c>
    /// configuration section. MPP lets an agent without an API key pay for a tool call: the server
    /// answers with a 402 challenge, the agent retries with a payment credential and the call runs.
    ///
    /// As in the Stripe machine payments sample, only three settings have to be given: the Stripe
    /// secret key (<c>Stripe:secret_key</c>), the Stripe profile the shared payment tokens are
    /// issued to (<see cref="NetworkId"/>) and the Tempo deposit address
    /// (<see cref="TempoOptions.DepositAddress"/>). Everything else is derived by
    /// <see cref="ApplyStripeDefaults"/> or fixed by the protocol.
    /// </summary>
    public class MppOptions
    {
        public const string SectionName = "Mpp";

        /// <summary>Payment method identifier of the card rail, settled through Stripe.</summary>
        public const string MethodName = "stripe";

        /// <summary>Payment intent every challenge is issued for.</summary>
        public const string IntentName = "charge";

        /// <summary>Payment method types the Stripe charge accepts.</summary>
        public static readonly string[] PaymentMethodTypes = ["card", "link"];

        /// <summary>Label the challenge signing secret is derived from, as the Stripe sample derives it.</summary>
        private const string ChallengeSigningLabel = "mpp-challenge-signing";

        /// <summary>When false the server keeps rejecting calls that carry no API key.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Protection space the challenge is issued for; defaults to the request host.</summary>
        public string? Realm { get; set; }

        /// <summary>
        /// Secret the stateless challenge identifier is derived from with HMAC-SHA256. It is
        /// derived from the Stripe secret key when it is not configured, so every instance of the
        /// server signs its challenges the same way without a secret of its own.
        /// </summary>
        public string? ChallengeSecret { get; set; }

        /// <summary>How long an issued challenge may be paid for.</summary>
        public int ChallengeLifetimeSeconds { get; set; } = 300;

        /// <summary>Stripe profile the shared payment token must have been issued to (<c>STRIPE_PROFILE_ID</c>).</summary>
        public string? NetworkId { get; set; }

        /// <summary>Currency the tool prices are expressed in when a price does not name one.</summary>
        public string Currency { get; set; } = "usd";

        /// <summary>Price, in cents, charged for a tool that has no price document.</summary>
        public decimal DefaultAmountCents { get; set; } = 5m;

        /// <summary>
        /// Fixed price, in cents, charged to a subscribed user for one call of a tool whose price
        /// document does not name a subscriber price. Subscribers are billed monthly, so this is
        /// generally a fraction of a cent.
        /// </summary>
        public decimal DefaultSubscriberAmountCents { get; set; } = 0.1m;

        /// <summary>Stablecoin payments over the Tempo blockchain, the <c>tempo</c> payment method.</summary>
        public TempoOptions Tempo { get; set; } = new();

        /// <summary>
        /// Connection string of the MongoDB instance the prices are held in; defaults to the
        /// <c>MongoConnectionString</c> setting of the application.
        /// </summary>
        public string? ConnectionString { get; set; }

        /// <summary>Database holding the prices; defaults to the <c>MongoAuthDatabase</c> setting.</summary>
        public string? DatabaseName { get; set; }

        /// <summary>Title of this server in the discovery document; defaults to the application name.</summary>
        public string? ServiceTitle { get; set; }

        /// <summary>Description of this server in the discovery document.</summary>
        public string? ServiceDescription { get; set; }

        /// <summary>Categories this server is advertised under in <c>x-service-info</c>.</summary>
        public string[] ServiceCategories { get; set; } = ["developer-tools"];

        /// <summary>Collection the per tool prices are read from.</summary>
        public string PricesCollectionName { get; set; } = "ToolPrices";

        /// <summary>
        /// One off: when true the server writes a default price document at startup for every MCP
        /// tool that has none yet. Left on it is harmless, as existing prices are never overwritten.
        /// </summary>
        public bool SeedPrices { get; set; }

        /// <summary>How long the prices are held in memory before they are read again.</summary>
        public int PriceCacheMinutes { get; set; } = 60;

        /// <summary>Paths the MCP streamable HTTP transport is served on.</summary>
        public string[] McpPaths { get; set; } = ["/", "/mcp"];

        /// <summary>Largest JSON-RPC request body, in bytes, the middleware inspects.</summary>
        public int MaxRequestBodyBytes { get; set; } = 1024 * 1024;

        /// <summary>The challenge lifetime as a <see cref="TimeSpan"/>.</summary>
        public TimeSpan ChallengeLifetime => TimeSpan.FromSeconds(ChallengeLifetimeSeconds <= 0 ? 300 : ChallengeLifetimeSeconds);

        /// <summary>The price cache lifetime as a <see cref="TimeSpan"/>.</summary>
        public TimeSpan PriceCacheLifetime => TimeSpan.FromMinutes(PriceCacheMinutes <= 0 ? 60 : PriceCacheMinutes);

        /// <summary>
        /// Derives from the Stripe secret key everything the Stripe machine payments sample derives
        /// from it: the secret the challenge identifiers are signed with, and whether the live or
        /// the test Tempo network is the one transfers are to be made on.
        /// </summary>
        public void ApplyStripeDefaults(string? stripeSecretKey)
        {
            var liveMode = !string.IsNullOrWhiteSpace(stripeSecretKey)
                && !stripeSecretKey.Contains("_test_", StringComparison.Ordinal);

            if (string.IsNullOrWhiteSpace(ChallengeSecret) && !string.IsNullOrWhiteSpace(stripeSecretKey))
            {
                using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(stripeSecretKey));
                ChallengeSecret = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(ChallengeSigningLabel)));
            }

            Tempo.ApplyNetworkDefaults(liveMode);
        }
    }
}
