# ThinkBase.AI.MPP

The server side of the [Machine Payments Protocol](https://github.com/tempoxyz/mpp-specs) (MPP) for
ASP.NET Core [Model Context Protocol](https://modelcontextprotocol.io/) servers.

## Reasons for existence

The MPP protocol, created by Stripe and Tempo and documented at [mpp.dev](https://mpp.dev) has not yet got a .Net implementation. 
At [ThinkBase](https://thinkbase.llc) we wanted to add MPP support to several websites that had MCP servers. These are implemented in ASP.Net. This would have been a long task in the days before AI coding, 
but we managed to create this within a couple of days with the help of GitHub Copilot and Claude. It passes the validation process on [mpp.dev](https://mpp.dev).
This implementation assumes you have a Stripe account, and that you will use Stripe as your payment mechanism.
The purpose is to monetize some of our services for ad-hoc payments where this makes sense.

Our implementation is probably what most people will want. You can subscribe to our services, so MCP tool calls with an API key are passed through to the existing MCP code, where API keys are checked and the user is inferred.
MPP requires that you present a list of your chargeable tools in a root file called openapi.json. This is created, in our case, from a MongoDB container which contains all the tool descriptions and pricing.
This is defined in the class ToolPrice. Since prices don't change often, the code caches this locally, as it is used frequently by the MPP code to apply charges.

```c#
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
```
Some of our tools are only relevant if you have a subscription. The AmountCents is set to zero for these tools, and the user is bounced if they call them without an API key.
We also use the above class for our subscription services, which are usage-based. 
Obviously, the subscription per-use prices are much lower than the one-time charge, but they also reflect two different circumstances: development, where many calls will be made to construct and test a knowledge Base, and usage, where the knowledge base is used by agents and humans.

This is an interim implementation until [mpp.dev](https://mpp.dev) creates a .net SDK, if they ever do. We'll update it as changes are required through our experience with usage. 
If you want to become a maintainer, that would be great.


An agent that holds no API key is not turned away: the call it is making is priced, answered with a
`402 Payment Required` challenge, and run once the agent comes back with a payment credential. An
agent may pay with a card, settled through Stripe, or in stablecoin, by transferring TIP-20 tokens
to the server's deposit address on the [Tempo](https://docs.tempo.xyz) blockchain.

| Piece | What it does |
|-------|--------------|
| `MppPaymentMiddleware` | Sits in front of the MCP endpoint: challenges an unpaid `tools/call`, settles a credential, answers a payment probe |
| `MppChallengeService` | The stateless challenge every payment method shares, its HMAC identifier and its replay protection |
| `MppPaymentService` | The `stripe`/`charge` method: a shared payment token settled as a Stripe PaymentIntent |
| `TempoPaymentService` | The `tempo`/`charge` method: a TIP-20 transfer verified on the Tempo blockchain |
| `ToolPricingService` | The per tool prices, one document per tool in a MongoDB collection, cached in memory |
| `MppDiscoveryService` | The `/openapi.json` discovery document, with an `x-payment-info` offer per payable tool |
| `MppReceiptFilter` | Returns the receipt of a settled payment in the `_meta` of the tool result |

## Using it

```csharp
// The prices, the payment methods and the discovery document, bound from the Mpp configuration
// section. The tools that are priced are those declared by the assemblies named here.
builder.Services.AddMachinePayments(builder.Configuration, typeof(Program).Assembly);

// How this application recognises a subscriber, who is billed rather than challenged.
builder.Services.AddScoped<IMppSubscriberResolver, ApiKeySubscriberResolver>();

var app = builder.Build();

// Before the MCP endpoint is mapped.
app.UseMachinePayments();
app.MapMcp();
```

The host still owns three things, because they are its own:

* the Stripe client the payments are settled with (`AddStripe`, from
  `Stripe.Extensions.DependencyInjection`);
* an `IMppSubscriberResolver`, which says whether a request already belongs to a subscriber —
  without one every call is paid for;
* an `IDistributedCache`, in which settled challenges and spent transfers are remembered.

Serve the discovery document from a controller of the application, which knows the URL it is
reached at:

```csharp
[HttpGet("/openapi.json")]
public async Task<IActionResult> GetDiscoveryDocument(CancellationToken cancellationToken)
{
    var realm = string.IsNullOrWhiteSpace(_options.Realm) ? Request.Host.Host : _options.Realm;
    var document = await _discoveryService.BuildDocumentAsync($"{Request.Scheme}://{Request.Host}", realm, cancellationToken);

    Response.Headers.CacheControl = $"public, max-age={MppDiscoveryService.CacheSeconds}";
    return Content(document.ToJsonString(), "application/json");
}
```

## Configuration

Everything is bound from the `Mpp` configuration section and, as in the Stripe machine payments
sample, three settings are enough: `Stripe:secret_key`, from which the challenge signing secret and
the Tempo network are derived, `Mpp:NetworkId` and `Mpp:Tempo:DepositAddress`. The prices are read
from `Mpp:DatabaseName` (or `MongoAuthDatabase`) of `Mpp:ConnectionString` (or
`MongoConnectionString`). 

#### Adding ThinkBase.AI.MPP to an ASP.NET Core site with MCP

The changes made here are the whole of the work, and are the same for any ASP.NET Core site that already maps an MCP endpoint.

1. **Reference the package.** `dotnet add package ThinkBase.AI.MPP`. It brings `MongoDB.Driver`, `Stripe.net`, `ModelContextProtocol`, and `Microsoft.Extensions.Http.Resilience` with it; a site that already references `MongoDB.Driver` may have to raise its version to the one the package asks for, or the restore fails with `NU1605`.
2. **Supply the three things the package leaves to the host**, because they belong to the application rather than to the protocol:
   - a `StripeClient`, which settles the card payments. Register it from the Stripe secret key, or use `AddStripe` from `Stripe.Extensions.DependencyInjection`.
   - an `IDistributedCache`, in which the settled challenges and the spent stablecoin transfers are remembered so that neither can be presented twice. This site uses `AddStackExchangeRedisCache` when `Redis:ConnectionString` is configured and `AddDistributedMemoryCache` otherwise.
   - an `IMppSubscriberResolver`, which tells the middleware that a request already belongs to a subscriber and is to be billed rather than challenged. A site that sells no subscriptions, as this one does, registers none and every call is then paid for.
3. **Register the protocol and put the middleware in front of the MCP endpoint:**

   ```csharp
   builder.Services.AddHttpContextAccessor();
   builder.Services.AddSingleton(new StripeClient(stripeSecretKey));
   builder.Services.AddMachinePayments(builder.Configuration, typeof(Program).Assembly);

   builder.Services
       .AddMcpServer()
       .WithHttpTransport()
       .WithTools<JobsMcpTools>()
       // Returns the receipt of a settled payment in the _meta of the tool result.
       .WithRequestFilters(filters => filters.AddCallToolFilter(MppReceiptFilter.Create()));

   var app = builder.Build();

   app.UseMachinePayments();   // before the MCP endpoint is mapped
   app.MapMcp("/mcp");
   ```

   The assembly passed to `AddMachinePayments` is the one whose `[McpServerToolType]` classes are priced; it is also what `Mpp:SeedPrices` writes the price documents from. `AddHttpContextAccessor` is what lets the receipt filter find the receipt of the call.
4. **Name the MCP path.** `Mpp:McpPaths` defaults to `["/", "/mcp"]`, which would also challenge the `POST` of a Razor page served from the site root. Set it to the path the MCP transport is actually mapped on; this site does so in `MachinePaymentsDefaults`, which also fills in the realm, the MongoDB instance the prices are read from, and the title of the discovery document from the settings the site already has:

   ```csharp
   builder.Services.PostConfigure<MppOptions>(options => MachinePaymentsDefaults.Apply(options, mcpOptions, mongoOptions));
   ```
5. **Serve the discovery document** from the application, which is the only part that knows the URL it is reached at:

   ```csharp
   app.MapGet("/openapi.json", async (HttpContext context, IMppDiscoveryService discoveryService, IOptions<MppOptions> mppOptions, CancellationToken cancellationToken) =>
   {
       var realm = string.IsNullOrWhiteSpace(mppOptions.Value.Realm) ? context.Request.Host.Host : mppOptions.Value.Realm;
       var document = await discoveryService.BuildDocumentAsync($"{context.Request.Scheme}://{context.Request.Host}", realm, cancellationToken);

       context.Response.Headers.CacheControl = $"public, max-age={MppDiscoveryService.CacheSeconds}";

       return Results.Content(document.ToJsonString(), "application/json");
   });
   ```
6. **Configure it.** Three settings are enough: `Stripe:secret_key`, from which the challenge signing secret and the Tempo network are derived, `Mpp:NetworkId`, the Stripe Business Network Profile the shared payment tokens are issued to, and, for stablecoin payments, `Mpp:Tempo:DepositAddress`. Supply the Stripe secret key through environment variables, user secrets, or Azure App Service configuration, never through `appsettings.json`. Guard the registration on the key being present, as this site does, so that the site still runs without a Stripe account.
7. **Price the tools.** Set `Mpp:SeedPrices` to `true` for one run to write a `ToolPrice` document per tool, then set the prices by hand in the `ToolPrices` collection. `AmountCents` of zero means the tool is not for sale at all: a call without a subscription is refused with `403` rather than challenged.

Check the result with an unpaid call, which must come back as a 402 carrying a challenge, and with `GET /openapi.json`, which must list an `x-payment-info` offer per tool:

```bash
curl -i -X POST https://aicomply.ai/mcp -H 'Content-Type: application/json' \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search_jobs","arguments":{"query":"ai"}}}'
```


## Tests

`ThinkBase.AI.MPP.Tests` covers the library on its own:

```bash
dotnet test ThinkBase.AI.MPP.Tests/ThinkBase.AI.MPP.Tests.csproj
```
