using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace ThinkBase.AI.MPP.Models
{
    /// <summary>
    /// The price of a single MCP tool call. One document per tool is held in the
    /// <c>ToolPrices</c> collection; prices change infrequently and are cached in memory by
    /// <see cref="Interfaces.IToolPricingService"/>.
    /// </summary>
    public class ToolPrice
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        [BsonIgnoreIfNull]
        public string? Id { get; set; }

        /// <summary>Name of the MCP tool the price applies to, for example <c>FindLocation</c>.</summary>
        public string ToolName { get; set; } = string.Empty;

        /// <summary>
        /// Amount charged for one call, in cents of <see cref="Currency"/>, to an agent paying for
        /// the call with the Machine Payments Protocol. Zero means the tool is not payable at all:
        /// it needs the account an API key comes with, and a call without one is refused with 403.
        /// </summary>
        public decimal AmountCents { get; set; }

        /// <summary>
        /// Fixed amount charged for one call, in cents of <see cref="Currency"/>, to a subscribed
        /// user calling with a valid API key. These are generally fractions of a cent and are billed
        /// monthly rather than per call. <c>null</c> falls back to <c>Mpp:DefaultSubscriberAmountCents</c>.
        /// </summary>
        [BsonIgnoreIfNull]
        public decimal? SubscriberAmountCents { get; set; }

        /// <summary>Currency the amount is expressed in, for example <c>usd</c>.</summary>
        public string Currency { get; set; } = "usd";

        /// <summary>Human readable description shown to the paying agent in the challenge.</summary>
        public string? Description { get; set; }

        [BsonIgnoreIfNull]
        public DateTime? UpdatedUtc { get; set; }
    }
}
