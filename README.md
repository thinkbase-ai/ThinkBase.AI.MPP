# MCP.Scientio.Com

An **ASP.NET Core 10** server that exposes a [Model Context Protocol (MCP)](https://modelcontextprotocol.io/) interface over HTTP. It provides LLM agents with tools for managing knowledge bases, indexing content, retrieving context, and querying knowledge graphs backed by a MongoDB RAG pipeline and Redis cache.

---

## Table of Contents

- [Overview](#overview)
- [Tech Stack](#tech-stack)
- [Configuration](#configuration)
- [Registration and API keys](#registration-and-api-keys)
- [Tool documentation and evaluation](#tool-documentation-and-evaluation)
- [KBase viewer](#kbase-viewer)
- [MCP Endpoint](#mcp-endpoint)
- [Usage Based Pricing](#usage-based-pricing)
- [Machine Payments (MPP)](#machine-payments-mpp)
- [KBase Autosave](#kbase-autosave)
- [Controllers and Tools](#controllers-and-tools)
  - [KBase Controller](#kbase-controller)
  - [Content Controller](#content-controller)
  - [Context Controller](#context-controller)
  - [Graph Controller](#graph-controller)
  - [Time Controller](#time-controller)
  - [Chat Controller](#chat-controller)
  - [Lineage Controller](#lineage-controller)
  - [Location Controller](#location-controller)

---

## Overview

MCP.Scientio.Com acts as an MCP server that LLM clients (e.g., Claude Desktop, custom agents) can connect to over HTTP. All tools are discovered automatically from the controller assemblies at startup and are protected by authorization.

Each controller groups a set of related **MCP tools**. A tool is the unit of capability that an LLM can invoke; each tool corresponds to a controller action decorated with `[McpServerTool]`.

---

## Tech Stack

| Component | Technology |
|-----------|-----------|
| Framework | ASP.NET Core 10 (.NET 10) |
| MCP library | `ModelContextProtocol` / `ModelContextProtocol.AspNetCore` v1.1 |
| Machine payments | `ThinkBase.AI.MPP` (this repository) |
| RAG backend | `ThinkBase.AI.Common` → `SKRagServiceMongo` (MongoDB) |
| Document loader | `ThinkBase.AI.Common` → `DocumentLoader` |
| Distributed cache | Redis (`Microsoft.Extensions.Caching.StackExchangeRedis`) |
| Transport | HTTP (Streamable HTTP) |

---

## Configuration

The following settings must be provided (e.g., via environment variables, user secrets, or `appsettings.json`):

| Key | Description |
|-----|-------------|
| `RedisCacheConnectionString` | Connection string for the Redis cache instance |
| `MongoConnectionString` | Connection string for the MongoDB instance holding the ASP.NET Identity collections |
| `MongoAuthDatabase` | Name of the authentication database (defaults to `scientioauth`) |
| `MongoGeonamesDatabase` | Name of the GeoNames gazetteer database used by the location tools (defaults to `geonames`) |
| `BlobStorageConnectionString` | Connection string, holding an account key, for the Azure Storage account that large uploads are written to by `GetUploadUrl` |
| `Upload:ContainerName` | Blob container the uploads are placed in (defaults to `uploads`) |
| `Upload:ExpiryMinutes` | How long an issued upload URL remains usable (defaults to `60`, capped at `1440`) |
| `Email:Host`, `Email:Port`, `Email:UserName`, `Email:Password`, `Email:From`, `Email:EnableSsl` | SMTP settings used to send registration confirmation emails |
| `Usage:TrialCreditDollars` | Usage credit granted to a trial user at sign up (default `50`) |
| `Usage:FlushIntervalSeconds` | How often buffered usage is written to MongoDB (default `60`) |
| `Usage:FlushBatchSize` | Buffered records that trigger an early write (default `200`) |
| `Usage:MaxBufferedRecords` | Upper bound on the in-memory usage buffer (default `20000`) |
| `Usage:StripeReportIntervalHours` | How often usage is reported to Stripe (default `24`) |
| `Usage:StripeMeterEventName` | Name of the Stripe billing meter events, in cents, are sent to (default `mcp_tool_cents`) |
| `Usage:AccountUrl` | Page the trial expiry email links to so the user can set up payment |
| `Stripe:secret_key`, `Stripe:webhook_secret` | Stripe API secret and the signing secret of the webhook endpoint |
| `Stripe:public_key` | Stripe publishable key used by the pricing table on `subscribe.html` |
| `Stripe:pricing_table_id` | Identifier of the Stripe pricing table shown on `subscribe.html` |
| `Stripe:billing_portal_url` | Stripe billing portal a subscriber is sent to in order to change or cancel their subscription |
| `Stripe:usage_product_id` | Stripe product holding the pay as you go price shown in the pricing section of `index.html` |
| `Mpp:Enabled` | Whether a tool call without an API key is answered with a payment challenge (default `true`) |
| `Mpp:Realm` | Protection space the challenges are issued for; defaults to the host of the request |
| `Mpp:ChallengeSecret` | Secret the stateless challenge identifiers are derived from; derived from `Stripe:secret_key` when it is not set, so every instance signs its challenges the same way |
| `Mpp:ChallengeLifetimeSeconds` | How long an issued challenge may be paid for (default `300`) |
| `Mpp:NetworkId` | Stripe profile (`STRIPE_PROFILE_ID`) the shared payment tokens are issued to; **required** for the card method |
| `Mpp:DefaultAmountCents` | Price charged to a paying agent for a tool that has no price document (default `5`) |
| `Mpp:DefaultSubscriberAmountCents` | Fixed price charged to a subscriber for a tool whose price document names no subscriber price (default `0.1`) |
| `Mpp:Currency` | Currency the tool prices are expressed in (default `usd`) |
| `Mpp:PricesCollectionName` | MongoDB collection the per tool prices are read from (default `ToolPrices`) |
| `Mpp:SeedPrices` | One off: when `true`, writes a default price document at startup for every tool that has none (default `false`) |
| `Mpp:PriceCacheMinutes` | How long the prices are cached in memory (default `60`) |
| `Mpp:DatabaseName` | Database holding the prices; defaults to `MongoAuthDatabase` (set to `scientioauth` in `appsettings.json`) |
| `Mpp:ServiceTitle` | Title of this server in the discovery document; defaults to the name of the application |
| `Mpp:ServiceDescription` | Description of this server in the discovery document |
| `Mpp:ServiceCategories` | Categories this server is advertised under in `x-service-info` |
| `Mpp:McpPaths` | Paths the MCP transport is served on, and so the ones the payment middleware inspects (default `/` and `/mcp`) |
| `Mpp:MaxRequestBodyBytes` | Largest JSON-RPC request body the middleware reads in order to find the tool being called (default `1048576`) |
| `Mpp:Tempo:DepositAddress` | Stripe crypto deposit address the stablecoin is to be transferred to; setting it is what offers the stablecoin challenge |
| `Mpp:Tempo:TokenAddress` | Address of the accepted TIP-20 stablecoin; defaults to USDC on mainnet and pathUSD on Moderato |
| `Mpp:Tempo:ChainId` | Tempo chain the transfer must be made on; defaults to mainnet (`4217`) for a live Stripe key and Moderato (`42431`) for a test one |
| `Mpp:Tempo:RpcUrl` | Tempo JSON-RPC endpoint the transfer is verified against; defaults to the public node of the chain |
| `Mpp:Tempo:RpcTimeoutSeconds` | How long that endpoint is waited on for an answer (default `10`) |
| `Mpp:Tempo:ReceiptWaitSeconds` | How long a node that has not caught up with a presented transfer is waited for (default `5`) |
| `Mpp:Tempo:TransactionMemoryHours` | How long a spent transfer hash is remembered (default `168`) |

Machine payments therefore need three settings only, as the [Stripe machine payments
sample](https://github.com/stripe-samples/machine-payments/tree/main/mpp/server/node-typescript)
does: `Stripe:secret_key`, `Mpp:NetworkId` and `Mpp:Tempo:DepositAddress`. Every other `Mpp` and
`Mpp:Tempo` key above is optional, and what the protocol fixes — the `stripe` and `tempo` method
names, the `charge` intent, the `card` and `link` payment method types, the six decimal places of a
TIP-20 token and the cent that is the smallest transfer asked for — is a constant of `MppOptions`
or `TempoOptions` rather than a setting.

Additional secrets required by `ThinkBase.AI.Common` (MongoDB connection, OpenAI/Azure OpenAI keys, etc.) should be configured according to that library's documentation.

---

## Registration and API keys

An MCP tool call either supplies an API key in the `X-Api-Key` header or pays for the call with the
[Machine Payments Protocol](#machine-payments-mpp). Users obtain an API key by registering:

1. `register.html` posts an email address and password to `POST /api/account/register`. A user document is created in the `Users` collection of the `scientioauth` database, in the standard ASP.NET Identity shape used by [Scientio.Com](https://github.com/thinkbase-ai/Scientio.Com), together with a randomly generated `APIKey` (a GUID) and a 30-day trial window (`TrialStartUtc` / `TrialEndsUtc`). Stripe processing beyond the trial is handled by Scientio.Com.
2. A confirmation email is sent containing a link to `confirm.html`, which calls `GET /api/account/confirm`. This marks the email as confirmed and signs the user in, returning a session token.
3. `apikey.html` shows the API key (via `GET /api/account/apikey`) and can replace it with a freshly generated GUID (via `POST /api/account/apikey/regenerate`). Signing in again is possible through `POST /api/account/login`.
4. `subscribe.html` shows the Stripe pricing table so the user can take out a subscription, and links a subscriber to the Stripe billing portal to change or cancel it. The page is configured from `GET /api/account/subscription`, which returns the pricing table, the publishable key, the billing portal and the current subscription of the signed-in user.

Confirmation and session tokens are stored in the `UserTokens` collection under the `MCP.Scientio.Com` login provider.

---

## Tool documentation and evaluation

The static documentation under `wwwroot/docs` describes every published tool, grouped by controller.

`docs/try.html` lets a user evaluate the tools from the browser. It reads the tool list and input
schemas from the running server (`tools/list` over the streamable HTTP transport), renders an input
field for each parameter, and calls `tools/call` with the user's API key in the `X-Api-Key` header,
showing the JSON that comes back. The key is filled in automatically for a signed-in visitor and is
never stored by the browser. Only non-destructive tools are offered: the page holds an explicit
allow-list of tool names and additionally skips any tool the server annotates with
`destructiveHint`. Every non-destructive tool section in the documentation carries a "Try it" link to
this page.

---

## KBase viewer

`wwwroot/kbaseviewer.html` is a browser page that shows a knowledge base and lets a user interact
with it. Like `docs/try.html` it talks to the server only through MCP tool calls (`tools/call` over
the streamable HTTP transport, with the API key in the `X-Api-Key` header), so it needs no
server-side code of its own.

A dialog is shown before the viewer is reachable. It takes the API key — filled in automatically for
a signed-in visitor — and then lists the knowledge bases returned by `ListKBases` and
`ListOpenKBases`. Choosing one and closing the dialog reveals two columns:

- **Left: the knowledge graph.** `GetGraph` supplies the nodes and edges, which are drawn with
  [cytoscape.js](https://js.cytoscape.org/). Each node is coloured by its display type — code,
  category, document, page, website, note and reference each have their own colour, and anything
  else is drawn in the implied colour — matching the other viewers of the same graph. Clicking a
  node asks `GetContentUrl` for that node's content and opens it in another page; a node with no
  content says so instead.
- **Right: the chat.** `Chat` is called with the message and the conversation ID, a spinner is shown
  while the server answers, and the Markdown that comes back is rendered. **New conversation**
  starts a fresh conversation ID and clears the log.

The Markdown is rendered into DOM nodes rather than HTML, and only `http`/`https` links are
followed, so nothing returned by a tool can be interpreted as markup.

---

## MCP Endpoint

```
POST /mcp
```

All MCP traffic flows through this single endpoint. Clients must supply a valid authorization token (Bearer or cookie, depending on your auth middleware configuration). The endpoint is registered in `Program.cs` via:

```csharp
app.MapMcp().RequireAuthorization();
```

---

## Usage Based Pricing

Customers are billed for the tool calls they make, rather than a monthly fee. Every call costs the
fixed price of the tool it invokes, so a bill is predictable and does not depend on how long a call
happened to take. A subscriber calling with a valid API key pays the (much lower) subscriber price of
the tool, accumulated and billed monthly, while an agent paying per call through the Machine Payments
Protocol pays the full price of the tool immediately.

**Recording.** `UsageTrackingFilter` is installed as a filter (middleware) on the MCP `tools/call`
pipeline in `Program.cs`. It resolves the caller from the `X-Api-Key` header, times the tool call and
hands the interaction to `IUsageService`. Recording never touches the database on the request path:
interactions are buffered in memory and written in batches by `UsageHostedService` (every
`Usage:FlushIntervalSeconds`, or earlier once `Usage:FlushBatchSize` interactions are pending, and
again on shutdown).

**Storage.** Each interaction becomes a document in the `UsageRecords` collection holding the user,
the tool name, the interaction type, the price of the call in cents, the duration in milliseconds,
the timestamp and the `yyyy-MM` billing period. The raw records are indexed by tool and time so that
a "most popular tools" dashboard can be built on top of them later. A rolled up `UsageSummaries`
document per user and month (`TotalCents`, `TotalMilliseconds`, `CallCount`, `ReportedCents`) is
maintained for billing; the total price of the calls made in the month is the billable metric, and
the duration is kept for reporting only.

**Stripe.** Once every `Usage:StripeReportIntervalHours` (24 by default) the outstanding usage of
each summary is sent to Stripe as a billing meter event against the customer's `StripeId`, which is
looked up from the user the API key belongs to. Only whole cents are reported and `ReportedCents` is
advanced accordingly, so the fractions of a cent a single call costs stay outstanding and rounding
never loses or double bills usage. The meter named by `Usage:StripeMeterEventName` therefore counts
cents of tool usage.

**Published rate.** The rate charged per unit of metered usage lives in Stripe, on the single price
of the product identified by `Stripe:usage_product_id`. `GET /api/pricing` reads that price through
`IStripeHandler`, caches it in Redis for six hours and returns it, so the pricing section of
`index.html` always shows the live rate without calling Stripe on every page view.

**Subscriptions.** `POST /api/stripe/webhook` receives Stripe webhooks. The payload signature is
verified against `Stripe:webhook_secret`, and `checkout.session.completed` plus the
`customer.subscription.*` events maintain `MarketplaceSubscriptionId` on the user document: the user
is matched on the `StripeId` of the customer, falling back to the client reference the pricing table
carries (the user id). Taking out a subscription lifts any trial suspension; a subscription that
Stripe no longer considers live clears the field again.

**Trials.** Registration assigns a usage credit (`TrialCreditCents`, $50 by default) alongside the
30-day trial window. When either the window expires or the recorded usage costs more than the
credit, the trial is suspended (`TrialSuspended` on the user document), an email inviting the user to
set up payment through Stripe is sent, and further tool calls are rejected until payment
arrangements are in place. Users with a subscription are never suspended.

---

## Machine Payments (MPP)

An agent that holds no API key is not turned away: it is invited to pay for the call it is making,
following the [Machine Payments Protocol](https://github.com/tempoxyz/mpp-specs) (MPP). The flow is
the familiar challenge, credential, receipt of the protocol.

An agent can pay with a card, through Stripe, or in stablecoin, by transferring TIP-20 tokens to
this server's deposit address on the [Tempo](https://docs.tempo.xyz) blockchain. Both are offered at
once and the agent picks the one it can settle.

**Where the code lives.** The protocol itself is not part of this application: it is the
[`ThinkBase.AI.MPP`](ThinkBase.AI.MPP/README.md) class library of this repository, so that the same
facility can be given to any ThinkBase MCP server. This server adds it in `Program.cs` with

```csharp
builder.Services.AddMachinePayments(builder.Configuration, typeof(Program).Assembly);
builder.Services.AddScoped<IMppSubscriberResolver, ApiKeySubscriberResolver>();
…
app.UseMachinePayments();
```

and supplies the two things that are its own: the Stripe client (`AddStripe`) and
`ApiKeySubscriberResolver`, which tells the library that the holder of a known `X-Api-Key` is a
subscriber and is billed rather than challenged. Everything described below — the middleware, the
challenges, the prices and the discovery document — is the library's.

**Challenge.** `MppPaymentMiddleware` sits in front of the MCP endpoint. A `tools/call` that carries
a usable API key passes straight through and is billed as [usage](#usage-based-pricing) as before. A
call without one has the price of its tool looked up and is answered with `402 Payment Required`:

```http
HTTP/1.1 402 Payment Required
WWW-Authenticate: Payment id="…", realm="mcp.scientio.com", method="stripe", intent="charge",
  request="eyJhbW91bnQiOiIyNSIsImN1cnJlbmN5IjoidXNkIiwi…", expires="2026-01-15T12:05:00Z", opaque="…"
Cache-Control: no-store
Content-Type: application/json

{"jsonrpc":"2.0","id":7,"error":{"code":-32042,"message":"Payment Required",
 "data":{"httpStatus":402,"challenges":[{"id":"…","realm":"mcp.scientio.com","method":"stripe",
 "intent":"charge","request":{"amount":"25","currency":"usd","description":"…",
 "methodDetails":{"networkId":"…","paymentMethodTypes":["card","link"]}},"expires":"2026-01-15T12:05:00Z"}],
 "problem":{"type":"https://paymentauth.org/problems/payment-required","status":402,…}}},
 "type":"https://paymentauth.org/problems/payment-required","title":"Payment Required","status":402,
 "detail":"Payment is required for the FindLocation tool, …"}
```

The same challenge is therefore offered twice: in the `WWW-Authenticate` header for an HTTP client
and in the JSON-RPC error (code `-32042`, as the MCP transport binding of the protocol requires) for
an MCP client. The problem details are likewise carried both inside the JSON-RPC error and at the top
of the body, as RFC 9457 asks, so a plain HTTP payer — an MPP agent or validator, which does not read
JSON-RPC — is told why a payment was refused and not merely that it was.

Challenges are stateless: the identifier is an HMAC-SHA256 of the challenge fields
over `Mpp:ChallengeSecret`, so the price cannot be altered by the caller, and the tool name travels
in the `opaque` parameter so that a challenge bought for one tool cannot pay for another.

A post to the endpoint that names no tool — the probe an MPP agent, directory or validator makes to
find out how a paid endpoint is paid for, and anything else that is not a JSON-RPC request — is
answered with a 402 as well, carrying the challenge of a call of the endpoint itself rather than of
any one tool. A probe that comes back with a credential for that challenge is settled like any
other payment: it names no tool to call, so what it buys is the catalogue of the tools of this
server and of their prices, returned with `200` as the result of the request, with the receipt both
in the `Payment-Receipt` header and in the `_meta` of the result. The other JSON-RPC methods
(`initialize`, `tools/list` and the rest) are free and are served by MCP untouched.

One challenge is issued per payment method that is configured, each in its own `WWW-Authenticate`
header and all together in the `challenges` array, so when `Mpp:Tempo` is configured the stablecoin
challenge accompanies the card one:

```json
{"id":"…","realm":"mcp.scientio.com","method":"tempo","intent":"charge",
 "request":{"amount":"250000","currency":"0x20c0…","recipient":"0x742d…",
 "description":"…","methodDetails":{"chainId":42431,"feePayer":false,"supportedModes":["push"]}},
 "expires":"2026-01-15T12:05:00Z","opaque":"…"}
```

The stablecoin amount is the price of the call converted to base units of the token (every TIP-20
token has six decimal places, so 25 cents is 250,000 base units), never less than the 10,000 base
units of the cent that is the smallest payment Stripe can record.

**Credential.** For the card method the agent authorises the payment with Stripe, which gives it a shared payment token
(`spt_…`), and retries the identical call presenting the credential — in `_meta` under
`org.paymentauth/credential` (checked both in `params` and at the root of the request) or, for a
plain HTTP client, in `Authorization: Payment <base64url>`:

```json
{"jsonrpc":"2.0","id":8,"method":"tools/call","params":{"name":"FindLocation","arguments":{…},
 "_meta":{"org.paymentauth/credential":{"challenge":{…echoed unchanged…},
 "payload":{"spt":"spt_1N4Zv32eZvKYlo2CPhVPkJlW"}}}}}
```

For the stablecoin method the agent makes the transfer itself — only the `push` submission mode of
the Tempo charge method is offered, so this server never holds or broadcasts a signed transaction —
and presents the hash of it:

```json
{"challenge":{…echoed unchanged…},"payload":{"type":"hash","hash":"0x1a2b…"}}
```

**Receipt.** `MppChallengeService` checks that the challenge was issued by this server, that it has
not expired, that it was issued for the tool being called and that it has not already been paid
(settled identifiers are held in the Redis cache), and the method named in the credential then takes
the payment. `MppPaymentService` charges it: a Stripe PaymentIntent for the amount of the challenge,
with `shared_payment_granted_token` and the challenge identifier as the idempotency key. Only a
PaymentIntent that reaches `succeeded` lets the call run. `TempoPaymentService` instead asks the
Tempo node named by `Mpp:Tempo:RpcUrl` for the receipt of the transfer, waiting up to
`Mpp:Tempo:ReceiptWaitSeconds` for a node that has not caught up with it yet: the transaction must
have succeeded and must carry transfer logs of the token named in the challenge that pay the deposit
address at least the amount charged. A transfer that carries a memo — the attribution memo an MPP
payer attaches, naming the challenge — emits a `Transfer` and a `TransferWithMemo` log for the one
movement of funds; both are recognised and the pair is counted once. The hash of a transfer that has
paid for a call is remembered
for `Mpp:Tempo:TransactionMemoryHours`, so one transfer cannot pay for a second call, and a transfer
that is still not on chain is answered with `payment-action-required` so the agent can retry. A
settled transfer is then recorded with Stripe as a crypto PaymentIntent in transaction verification
mode, the hash being the idempotency key, so that a stablecoin payment is reported beside a card
one; a Stripe that cannot record it is logged and no more, as the call has already been paid for.
Both PaymentIntents are created as raw requests in the `2026-07-29.preview` API version, which
shared payment tokens and crypto transaction verification are only accepted in. The call then
executes as the payment that bought it (`mpp:<payment intent id>`, which `APIAccess` returns in
place of a user) and the receipt comes back both in the `Payment-Receipt` header and, through
`MppReceiptFilter`, in the `_meta` of the tool result under `org.paymentauth/receipt`.

**Prices.** Every tool has its own price, held as one document per tool in the `ToolPrices`
collection of the same MongoDB instance the rest of the application uses:

```json
{ "ToolName": "FindLocation", "AmountCents": 25, "SubscriberAmountCents": 0.1, "Currency": "usd", "Description": "One location lookup." }
```

`AmountCents` is what an agent paying per call with MPP is charged; `SubscriberAmountCents` is the
fixed price a subscribed user calling with a valid API key is billed for the call, and falls back to
`Mpp:DefaultSubscriberAmountCents` when the document leaves it out. `IToolPricingService` serves the
two through `GetPriceAsync` and `GetSubscriberPriceAsync`.

Set `Mpp:SeedPrices` to `true` once to populate the collection: at startup
`ToolPricingService.SeedPricesAsync` finds every MCP tool this server declares and writes a document
for each tool that has none yet, with `AmountCents` 50, `SubscriberAmountCents` 0.1, `Currency`
`usd` and the tool's own description. Prices already in the collection are never overwritten, so the
seed can be run again after new tools are added.

Prices change infrequently, so `ToolPricingService` reads the whole (small) collection once and
keeps it in memory for `Mpp:PriceCacheMinutes`; a tool call never waits for the database, and a
pricing outage falls back to `Mpp:DefaultAmountCents` rather than failing. A tool priced at `0` is
not payable: it makes no sense without the account an API key comes with, so it is not advertised
for payment and a call that carries no API key is refused with `403 Forbidden` (JSON-RPC error
`-32044`) instead of being challenged. `GET /api/pricing/tools` publishes the cached prices, both per call
and per subscribed call, for clients that want to know the cost before they call, although the
challenge of a 402 is always authoritative for a paid call. The answer carries a one hour
`Cache-Control`, and the documentation pages annotate each tool's card with its two prices from it
(`wwwroot/docs/prices.js`), keeping the reply in session storage so that moving between pages does
not call the server again. A tool whose `AmountCents` is `0` is shown as *no agent access* rather
than as a price.

**Discovery.** An MPP agent, directory or validator does not have to provoke a 402 to learn what
this server offers and what it charges: `DiscoveryController` serves the discovery document the
protocol asks for at `GET /openapi.json`, an OpenAPI 3.1 description built by `MppDiscoveryService`
and cacheable for five minutes.

```json
{"openapi":"3.1.0","info":{"title":"MCP.Scientio.Com","version":"1.0.0","description":"…"},
 "servers":[{"url":"https://mcp.scientio.com"}],
 "x-service-info":{"categories":["data","search","developer-tools"],
  "docs":{"homepage":"https://mcp.scientio.com/","apiReference":"https://mcp.scientio.com/docs/",
  "llms":"https://mcp.scientio.com/llms.txt"}},
 "paths":{"/":{"post":{"operationId":"mcp","responses":{"200":{…},"402":{…},"403":{…}},
  "x-payment-info":{"offers":[
   {"intent":"charge","method":"stripe","amount":"25","currency":"usd","description":"…","tool":"FindLocation"},
   {"intent":"charge","method":"tempo","amount":"250000","currency":"0x20c0…","description":"…","tool":"FindLocation"}]}}},
  "/api/tools":{"get":{…}},"/api/pricing/tools":{"get":{…}}}}
```

The paid operation is the MCP endpoint itself — the path `Mpp:McpPaths` names first — since that is
where a `tools/call` is posted. The price, though, belongs to the tool rather than to the endpoint,
so the operation carries one offer per payment method per payable tool, each naming its `tool`. The
offers are made from the very challenges `IMppPaymentService` would issue, so the amount and
currency advertised are the ones a 402 asks for, in base units of the currency as the protocol
requires; the challenge of the 402 remains authoritative. The operation also carries an `example`
request — a `tools/call` of one of the tools that is offered — so that a client or validator which
builds its probe from this document calls something that has a price. A tool priced at `0` is left
out, as it cannot be paid for, and when no payment method is configured, or `Mpp:Enabled` is
`false`, the document simply describes the endpoint with no `x-payment-info` and no `402`.

**Plain http.** The document and the MCP endpoint have to answer a plain `http://` call. MPP
agents, directories and validators fetch `/openapi.json` and post their probe over http and most do
not follow redirects, so a `301` to `https` reads to them as a missing document or a failed call.
The server therefore never redirects a scheme of its own — there is no HTTPS redirection or HSTS
middleware in `Program.cs` — and the root `web.config` clears the rewrite rules IIS inherits from
its parent configuration, which is where a host writes its http-to-https redirect. On Azure App
Service that redirect is made by the platform in front of the site when **HTTPS Only** is on,
before any rewrite rule is read, so that setting has to be off for the site as well; https remains
available, and the `servers` URL of the document is simply the scheme the document was fetched
with.

### Next steps

- Run the `Mpp:SeedPrices` startup seed once to fill the `ToolPrices` collection, then tune the
  prices; until a tool has a document it is charged `Mpp:DefaultAmountCents` per paid call and
  `Mpp:DefaultSubscriberAmountCents` per subscribed call. An admin page for maintaining the prices
  would save hand editing the collection.
- Create the Stripe billing meter named by `Usage:StripeMeterEventName`, which counts cents of tool
  usage, and point the subscription price at it.
- Set `Mpp:NetworkId` to the Stripe profile the shared payment tokens must be issued to; without it
  the card challenge cannot be paid. `Mpp:ChallengeSecret` need only be set to sign the challenges
  with something other than the Stripe secret key they are otherwise derived from.
- Make the Tempo deposit address with
  `stripe post /v1/crypto/deposit_addresses --live --stripe-version 2026-05-27.preview -d network=tempo`
  and set it as `Mpp:Tempo:DepositAddress`; the stablecoin method is registered, and offered in a
  challenge, as soon as it is set. The chain, the node and the token follow the mode of the Stripe
  key and need only be configured to depart from the Tempo defaults.
- Enable the Machine Payments Protocol on the Stripe account and confirm the
  `shared_payment_granted_token` parameter of the PaymentIntent is accepted by the account; it, and
  the crypto transaction verification the stablecoin payments are recorded with, are sent as raw
  requests because the current Stripe.NET release models neither them nor the preview API version
  they need. Move them to the typed calls once it does.
- Advertise the payment capability in the MCP `initialize` result
  (`capabilities.experimental.payment`) so that clients can discover the supported method and intent
  before they call, and annotate `tools/list` with the price once the protocol settles on a way of
  doing so.
- Paid calls are already recorded in `UsageRecords` under their `mpp:<payment intent id>` identifier;
  build the reporting that reconciles those records against the Stripe payments so the revenue of
  each tool can be seen.
- Decide what a paid, user-less call may do with the knowledge bases it creates: such calls
  currently own their data under the payment identifier and cannot reach it again.

---

## KBase Autosave

`DocumentLoader` keeps the `IndexRecord` of a KBase in the Redis cache for 30 minutes, so a tool that
changes the graph updates the cached record without writing it back to the database. To make sure no
change is lost when the cache entry expires, the tools that change a KBase report its id to
`IIndexRecordAutosaveService`, which buffers the ids in memory.

`IndexRecordAutosaveHostedService` flushes the buffer every 10 minutes, comfortably inside the cache
lifetime, and again immediately on shutdown so the server can be restarted without losing work. A
flush reads each changed record back out of the cache and writes it to the database with
`IDocumentLoader.ForceUpdateAsync`. A KBase whose save fails stays pending and is retried on the next
flush, and deleting a KBase discards it from the buffer so a deleted KBase is never written back.

---

## Controllers and Tools

### KBase Controller

**Route prefix**: `/kbase`

Manages top-level **knowledge bases** (indexes). Each knowledge base is identified by a unique ID and belongs to a user.

#### `CreateKBase`

Create a new knowledge base.

| Parameter | Type | Source | Description |
|-----------|------|--------|-------------|
| `kbaseName` | `string` | query | Display name for the new knowledge base |
| `userId` | `string` | query | ID of the owning user |

**Returns**: `string` — the ID of the newly created knowledge base.

---

#### `UpdateKBaseMetadata`

Update the metadata of an existing knowledge base. Only the values that are supplied are changed; omitted values are left as they are.

| Parameter | Type | Source | Description |
|-----------|------|--------|-------------|
| `indexId` | `string` | query | ID of the knowledge base to modify |
| `kbaseName` | `string?` | query | Name of the knowledge base |
| `author` | `string?` | query | Author of the knowledge base |
| `copyright` | `string?` | query | Copyright of the knowledge base |
| `description` | `string?` | query | Description of the knowledge base |
| `isPublicWebsite` | `bool?` | query | `true` to publish the knowledge base as a public website |
| `showOnList` | `bool?` | query | `true` to allow the knowledge base to appear on the public list |

**Returns**: `bool` — `true` if the metadata was updated. A knowledge base that does not exist, or that the calling account does not own, fails with *KBase not found*.

---

#### `DeleteKBase`

Delete an existing knowledge base.

| Parameter | Type | Source | Description |
|-----------|------|--------|-------------|
| `kbaseId` | `string` | query | ID of the knowledge base to delete |
| `userId` | `string` | query | ID of the owning user |

**Returns**: `bool` — `true` if the knowledge base was found and deleted, `false` otherwise.

---

#### `ListKBases`

List all knowledge bases that belong to a user.

| Parameter | Type | Source | Description |
|-----------|------|--------|-------------|
| `userId` | `string` | query | ID of the user whose knowledge bases to list |

**Returns**: `List<(string Key, string Value)>` — a list of `(name, id)` pairs for each knowledge base.

---

### Content Controller

**Route prefix**: `/content`

Manages **content items** (documents, text snippets) within a knowledge base index. Content is stored, chunked, and embedded for RAG retrieval.

#### `GetContent`

Retrieve a content item from an index by its content ID.

| Parameter | Type | Source | Description |
|-----------|------|--------|-------------|
| `userId` | `string` | query | ID of the owning user |
| `contentId` | `string` | query | ID of the content item to retrieve |
| `indexName` | `string` | query | Name of the knowledge base index |

**Returns**: `(Stream?, string)` — a tuple of the raw content stream and its MIME content type. Both values are empty/null if the content or index does not exist.

---

#### `GetContentUrl`

Get a direct URL for a content item held in an index.

| Parameter | Type | Source | Description |
|-----------|------|--------|-------------|
| `contentId` | `string` | query | ID of the content item |
| `indexId` | `string` | query | ID of the knowledge base index |

**Returns**: `string?` — the URL of the content item. An error is returned if the knowledge base is not found for the calling account.

---

#### `GetUploadUrl`

Get a temporary URL, carrying its own shared access signature, that a single file may be uploaded to with an HTTP `PUT`. The signature grants read as well as write, so the same URL is then passed as the `blobUrl` of `FillFromDocument` or `AnnotateNodeFromFile`. This is how a client supplies a large document without ever holding the storage connection string. Each call names a fresh, private blob under the calling account.

| Parameter | Type | Source | Description |
|-----------|------|--------|-------------|
| `fileName` | `string` | query | Name of the file to be uploaded, without any path |

**Returns**: `UploadUrl` — the `Url` to write the file to and read it back from, the `FileName` it was issued for, and `ExpiresOnUtc`, the moment the URL stops working.

---

#### `AddContent`

Add a new content item to an index. The content is incrementally indexed (chunked and embedded) for later retrieval.

| Parameter | Type | Source | Description |
|-----------|------|--------|-------------|
| `userId` | `string` | query | ID of the owning user |
| `indexName` | `string` | query | Name of the knowledge base index |
| `contentName` | `string` | query | Display name / identifier for the content item |
| `content` | `string` | query | The raw text content to index |

**Returns**: `string` — the content ID of the newly added item, or an empty string if the index was not found.

---

#### `DeleteContent`

Remove a content item from an index.

| Parameter | Type | Source | Description |
|-----------|------|--------|-------------|
| `userId` | `string` | query | ID of the owning user |
| `indexName` | `string` | query | Name of the knowledge base index |
| `contentName` | `string` | query | Name of the content item to delete |

**Returns**: `bool` — `true` if the item was found and deleted, `false` otherwise.

---

### Context Controller

**Route prefix**: `/context`

Retrieves **LLM-friendly summaries** of a knowledge base index — either its full context (all embedded content) or a short description.

#### `GetIndexContext`

Get a formatted, LLM-ready summary of the full context stored in an index.

| Parameter | Type | Source | Description |
|-----------|------|--------|-------------|
| `userId` | `string` | query | ID of the owning user |
| `indexId` | `string` | query | ID of the knowledge base index |

**Returns**: `string?` — an LLM-friendly representation of the index context, or `null` if the index does not exist.

---

#### `GetIndexDescription`

Get the human-readable description of an index.

| Parameter | Type | Source | Description |
|-----------|------|--------|-------------|
| `userId` | `string` | query | ID of the owning user |
| `indexId` | `string` | query | ID of the knowledge base index |

**Returns**: `string?` — an LLM-friendly version of the index description, or `null` if the index does not exist.

---

### Graph Controller

**Route prefix**: `/graph`

Exposes the **knowledge graph** structure of an index. The graph represents entities and their relationships as extracted during indexing, or assembled node by node in a *node-first* knowledge base.

#### `GetGraph`

Get the graph structure for a given user and knowledge base index.

| Parameter | Type | Source | Description |
|-----------|------|--------|-------------|
| `userId` | `string` | query | ID of the owning user |
| `indexName` | `string` | query | Name of the knowledge base index |

**Returns**: `string?` — a JSON-serialized graph model, or `null` if the index does not exist.

The remaining graph tools read and curate the graph; the full parameter lists are published at
[`/docs/graph.html`](https://mcp.scientio.com/docs/graph.html).

| Tool | Description |
|------|-------------|
| `GetFilteredGraph`, `GetLocations` | The graph, or the locations of its nodes, as they stood at an ISO 8601 Extended date. |
| `CreateNode`, `UpdateNode`, `DeleteNode` | Create, rename or re-type, and delete nodes. |
| `GetNodeCore`, `GetGraphObject`, `GetNodeContext` | Read a node, a complete graph object, or a node together with its immediate neighbours. |
| `GetNodeAttributes`, `UpsertNodeAttribute`, `DeleteNodeAttribute` | Read and edit the attributes of a node. |
| `GetNodeLocations`, `UpdateNodeLocations` | Read and set the locations of a node. |
| `CreateEdge`, `GetEdgeCore`, `UpdateEdge`, `DeleteEdge` | Create, read, change and delete edges. |
| `AnnotateNode`, `AnnotateNodeFromUrl`, `AnnotateNodeFromFile` | Enrich a node from text, a web page or a PDF, optionally creating and linking related nodes. |
| `IsNodeFirst` | Whether the KBase is node-first (assembled node by node) or built from documents. |
| `IndexNodeKBase` | Indexes the nodes of a node-first KBase into the RAG store so they can be searched and chatted with. |
| `SetNodeAsKBaseReference` | Turns a node into a reference to another KBase, taking that KBase's name and description and recording its ID as the node's dynamic source. |

---

### Time Controller

**Route prefix**: `/time`

Reads and writes the **temporal existence** of the things a knowledge base knows about. An existence is a
*fuzzy time*, built from one to four ISO 8601 Extended dates (a negative year is a BCE date), each with an
optional precision in seconds. It is what the time-aware graph tools `GetFilteredGraph` and `GetLocations`
filter on.

| Tool | Description |
|------|-------------|
| `GetTimeLine` | The set of existences recorded for the nodes of a KBase. |
| `GetNodeExistence` | The existence recorded for a single node. |
| `UpdateExistence` | Sets a node's existence from one to four dates and their precisions. |
| `UpdateExistenceFromFuzzyTime` | Sets a node's existence from a ready-made fuzzy time. |
| `ExtractExistenceFromText` | Reads the dates out of a piece of text and returns them as a fuzzy time, without storing anything. |

---

### Chat Controller

**Route prefix**: `/chat`

Enables **conversational chat** with the content of a knowledge base. The chat client is chosen per call from the form of the KBase: a KBase built from documents is answered by `ThinkBase.AI.Common.ChatClients.SiteChatClient` from its indexed content, while a *node-first* KBase (see `IsNodeFirst` on the [Graph Controller](#graph-controller)) is answered by `GraphChatClient` from its knowledge graph. Both implement `IChatClient` from `Microsoft.Extensions.AI`.

Conversation history is maintained server-side in Redis, keyed by `userId`, `indexId`, and an optional `sessionId`, so multi-turn dialogues are preserved across successive tool calls.

#### `Chat`

Send a chat message and receive the assistant's reply.

| Parameter | Type | Source | Description |
|-----------|------|--------|-------------|
| `indexId` | `string` | argument | ID of the knowledge base to chat with |
| `message` | `string` | argument | The user's message |
| `sessionId` | `string?` | argument | Optional session ID to maintain conversation history across calls; a new one is generated when omitted |
| `iso8601extendedTime` | `string?` | argument | Optional time in ISO 8601 Extended format (negative years are BCE) restricting the answer to knowledge in existence at that time |

**Returns**: `string?` — the assistant's text response. Throws if the API key, `indexId` or `message` is missing, or if the chat request fails.

---

### Lineage Controller

**Route prefix**: `/lineage`

Looks up the shared **taxonomy of concepts** ("lineages") used to type the nodes, edges and attributes of a
knowledge graph. A lineage places a thing in a hierarchy of increasingly general concepts (its *hypernymy
path*), which is what allows content from different sources to be related consistently.

| Tool | Description |
|------|-------------|
| `GetKeywords` | The keywords starting with the given initial text that map to a lineage. |
| `FindLineage` | The nearest lineage for a word or phrase, given some surrounding context. |
| `FindEdgeLineage` | The nearest lineage for an edge, given a word or phrase, some surrounding context and the lineages of the nodes the edge joins. |
| `LookupLineage` | The full lineage record for a lineage identifier. |
| `LookUpLineages` | The full lineage records for a set of lineage identifiers, one per identifier given. |
| `GetLineagePath` | The hypernymy path of a lineage record. |

---

### Location Controller

**Route prefix**: `/location`

Turns a **place name** into coordinates. The names are looked up in the GeoNames gazetteer held in
MongoDB (database `geonames`, container `geoname`, mapped by `MCP.Scientio.Com.Models.Geoname`) using
the same `MongoConnectionString` as the rest of the application.

The collection holds millions of places, so the lookup relies on the collection's text index over `name`
(`name_text`): the name is searched for as a phrase, which the index answers, and only the few documents
the index yields are then held to the whole name, ignoring case. The index is required: MongoDB rejects a
text search on a collection that has no text index. One consequence of searching the text index is that a
place whose name is made up only of words the index treats as too common to record, such as `The`, cannot
be found.

| Tool | Description |
|------|-------------|
| `FindLocation` | Every place whose name matches the one supplied, with the details that tell places of the same name apart. |

#### `FindLocation`

Find a list of possible locations given a place name.

| Parameter | Type | Source | Description |
|-----------|------|--------|-------------|
| `locationName` | `string` | query | The name of the location. The whole name must match, but any capitalization will do |

**Returns**: `List<ExtendedLocation>` — the matching places, most populous first (at most 100), or an
empty list if none match. Each entry holds a `FuzzyLocation` carrying a single longitude, latitude and
altitude (`0.0` where the gazetteer records no elevation), the ISO `CountryCode`, the `CountryName` that
code stands for (empty where the code is not one of ISO 3166-1), the `TopLevelRegionName`, the
first-order administrative division the country code and the GeoNames admin1 code name together, such as
`England` for `GB.ENG` (empty where the pair is not one GeoNames knows), the `SecondaryRegionName`, the
second-order administrative division the country code and the GeoNames admin1 and admin2 codes name
together, such as `Greater London` for `GB.ENG.GLA` (empty where the three are not ones GeoNames knows),
and the `Type` of the place, the
kind of place its GeoNames feature code stands for, such as `forest station` for the code `STNF` (empty
where the code is not one GeoNames knows).
