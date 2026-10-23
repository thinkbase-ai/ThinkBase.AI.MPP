using ThinkBase.AI.MPP.Models;

namespace ThinkBase.AI.MPP.Interfaces
{
    /// <summary>
    /// Supplies the price of a single MCP tool call. Prices live in MongoDB and change
    /// infrequently, so the whole (small) collection is cached in memory and refreshed lazily.
    /// </summary>
    public interface IToolPricingService
    {
        /// <summary>The price of the named tool, falling back to the configured default price.</summary>
        Task<ToolPrice> GetPriceAsync(string toolName, CancellationToken cancellationToken = default);

        /// <summary>
        /// The fixed price one call of the named tool costs a subscribed user calling with a valid
        /// API key. The returned <see cref="ToolPrice.AmountCents"/> is that fixed price, so a caller
        /// that bills a subscriber never has to look at the pay per call price.
        /// </summary>
        Task<ToolPrice> GetSubscriberPriceAsync(string toolName, CancellationToken cancellationToken = default);

        /// <summary>Every price held in MongoDB, from the cache when it is still fresh.</summary>
        Task<IReadOnlyCollection<ToolPrice>> GetPricesAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// One off: writes a default price document for every MCP tool this server declares that has
        /// no document in the prices collection yet. Existing documents are left untouched, so the
        /// hand made prices survive. Returns the number of documents written.
        /// </summary>
        Task<int> SeedPricesAsync(CancellationToken cancellationToken = default);
    }
}
