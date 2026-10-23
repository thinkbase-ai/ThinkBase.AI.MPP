# ThinkBase.AI.MPP

The server side of the [Machine Payments Protocol](https://github.com/tempoxyz/mpp-specs) (MPP) for
ASP.NET Core [Model Context Protocol](https://modelcontextprotocol.io/) servers.

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
`MongoConnectionString`). The full list of settings, and a description of the protocol as it is
implemented here, is in the [README of MCP.Scientio.Com](../README.md#machine-payments-mpp).

## Tests

`ThinkBase.AI.MPP.Tests` covers the library on its own:

```bash
dotnet test ThinkBase.AI.MPP.Tests/ThinkBase.AI.MPP.Tests.csproj
```
