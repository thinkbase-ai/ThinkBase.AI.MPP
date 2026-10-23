using ThinkBase.AI.MPP.Interfaces;
using ThinkBase.AI.MPP.Models;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

namespace ThinkBase.AI.MPP.Services
{
    /// <summary>
    /// Reads the per tool prices from the <c>ToolPrices</c> collection of the same MongoDB
    /// instance the rest of the application uses. Prices are changed by hand every now and then,
    /// so the whole collection is held in memory for <c>Mpp:PriceCacheMinutes</c> and a tool call
    /// never waits for the database. A tool without a price document is charged
    /// <c>Mpp:DefaultAmountCents</c> per paid call, and a price document without a subscriber price
    /// charges a subscriber <c>Mpp:DefaultSubscriberAmountCents</c> for the call.
    /// </summary>
    public class ToolPricingService : IToolPricingService
    {
        /// <summary>Pay per call price, in cents, written by <see cref="SeedPricesAsync"/>.</summary>
        internal const decimal SeedAmountCents = 50m;

        /// <summary>Subscriber price, in cents, written by <see cref="SeedPricesAsync"/>.</summary>
        internal const decimal SeedSubscriberAmountCents = 0.1m;

        /// <summary>Currency written by <see cref="SeedPricesAsync"/>.</summary>
        internal const string SeedCurrency = "usd";

        private readonly IConfiguration _configuration;
        private readonly IMppToolCatalog _tools;
        private readonly MppOptions _options;
        private readonly ILogger<ToolPricingService> _logger;
        private readonly Lazy<IMongoCollection<ToolPrice>> _prices;
        private readonly SemaphoreSlim _refreshLock = new(1, 1);
        private IReadOnlyDictionary<string, ToolPrice> _cache = new Dictionary<string, ToolPrice>(StringComparer.OrdinalIgnoreCase);
        private DateTime _cacheExpiresUtc = DateTime.MinValue;

        public ToolPricingService(
            IConfiguration configuration,
            IMppToolCatalog tools,
            IOptions<MppOptions> options,
            ILogger<ToolPricingService> logger)
        {
            _configuration = configuration;
            _tools = tools;
            _options = options.Value;
            _logger = logger;
            _prices = new Lazy<IMongoCollection<ToolPrice>>(CreateCollection, LazyThreadSafetyMode.ExecutionAndPublication);
        }

        private IMongoCollection<ToolPrice> CreateCollection()
        {
            var connectionString = _options.ConnectionString ?? _configuration["MongoConnectionString"];
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException("MongoConnectionString is not configured.");
            }

            var databaseName = _options.DatabaseName ?? _configuration["MongoAuthDatabase"];
            if (string.IsNullOrWhiteSpace(databaseName))
            {
                throw new InvalidOperationException("Mpp:DatabaseName is not configured.");
            }

            return new MongoClient(connectionString).GetDatabase(databaseName).GetCollection<ToolPrice>(_options.PricesCollectionName);
        }

        public async Task<ToolPrice> GetPriceAsync(string toolName, CancellationToken cancellationToken = default)
        {
            var prices = await LoadAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(toolName) && prices.TryGetValue(toolName, out var price))
            {
                return price;
            }

            return new ToolPrice
            {
                ToolName = toolName,
                AmountCents = _options.DefaultAmountCents,
                SubscriberAmountCents = _options.DefaultSubscriberAmountCents,
                Currency = _options.Currency
            };
        }

        public async Task<ToolPrice> GetSubscriberPriceAsync(string toolName, CancellationToken cancellationToken = default)
        {
            var price = await GetPriceAsync(toolName, cancellationToken);
            var amount = price.SubscriberAmountCents ?? _options.DefaultSubscriberAmountCents;
            if (amount < 0)
            {
                amount = 0;
            }

            // A copy, so that the cached document keeps the pay per call price it was read with.
            return new ToolPrice
            {
                Id = price.Id,
                ToolName = price.ToolName,
                AmountCents = amount,
                SubscriberAmountCents = amount,
                Currency = price.Currency,
                Description = price.Description,
                UpdatedUtc = price.UpdatedUtc
            };
        }

        public async Task<IReadOnlyCollection<ToolPrice>> GetPricesAsync(CancellationToken cancellationToken = default)
        {
            var prices = await LoadAsync(cancellationToken);
            return prices.Values.OrderBy(p => p.ToolName, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>
        /// One off: writes a default price document for every MCP tool this server declares that has
        /// no document in the prices collection yet, taking the description from the tool's
        /// <see cref="DescriptionAttribute"/>. Existing documents are left untouched, so prices that
        /// were set by hand survive a second run. Returns the number of documents written.
        /// </summary>
        public async Task<int> SeedPricesAsync(CancellationToken cancellationToken = default)
        {
            var tools = _tools.GetTools();
            if (tools.Count == 0)
            {
                return 0;
            }

            var collection = _prices.Value;
            var existing = await collection.Find(FilterDefinition<ToolPrice>.Empty).ToListAsync(cancellationToken);
            var known = new HashSet<string>(
                existing.Where(p => !string.IsNullOrWhiteSpace(p.ToolName)).Select(p => p.ToolName),
                StringComparer.OrdinalIgnoreCase);

            var missing = tools
                .Where(tool => !known.Contains(tool.Key))
                .Select(tool => new ToolPrice
                {
                    ToolName = tool.Key,
                    AmountCents = SeedAmountCents,
                    SubscriberAmountCents = SeedSubscriberAmountCents,
                    Currency = SeedCurrency,
                    Description = string.IsNullOrWhiteSpace(tool.Value) ? null : tool.Value,
                    UpdatedUtc = DateTime.UtcNow
                })
                .ToList();

            if (missing.Count == 0)
            {
                _logger.LogInformation("The MCP tool prices are already populated; no price was written.");
                return 0;
            }

            await collection.InsertManyAsync(missing, cancellationToken: cancellationToken);

            // The cache no longer matches the collection, so the next call reads the prices again.
            _cacheExpiresUtc = DateTime.MinValue;
            _logger.LogInformation("Wrote {Count} default MCP tool prices.", missing.Count);
            return missing.Count;
        }

        private async Task<IReadOnlyDictionary<string, ToolPrice>> LoadAsync(CancellationToken cancellationToken)
        {
            if (DateTime.UtcNow < _cacheExpiresUtc)
            {
                return _cache;
            }

            await _refreshLock.WaitAsync(cancellationToken);
            try
            {
                if (DateTime.UtcNow < _cacheExpiresUtc)
                {
                    return _cache;
                }

                try
                {
                    var documents = await _prices.Value.Find(FilterDefinition<ToolPrice>.Empty).ToListAsync(cancellationToken);
                    var loaded = new Dictionary<string, ToolPrice>(StringComparer.OrdinalIgnoreCase);
                    foreach (var document in documents.Where(d => !string.IsNullOrWhiteSpace(d.ToolName)))
                    {
                        if (string.IsNullOrWhiteSpace(document.Currency))
                        {
                            document.Currency = _options.Currency;
                        }

                        loaded[document.ToolName] = document;
                    }

                    _cache = loaded;
                    _cacheExpiresUtc = DateTime.UtcNow.Add(_options.PriceCacheLifetime);
                }
                catch (Exception ex)
                {
                    // A pricing outage must not take the server down: the default price is charged
                    // instead and the prices are read again shortly.
                    _logger.LogError(ex, "Unable to read the MCP tool prices; the default price is used.");
                    _cacheExpiresUtc = DateTime.UtcNow.AddMinutes(1);
                }

                return _cache;
            }
            finally
            {
                _refreshLock.Release();
            }
        }
    }
}
