using ThinkBase.AI.MPP.Interfaces;
using ThinkBase.AI.MPP.Models;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ThinkBase.AI.MPP.Authorization
{
    /// <summary>
    /// Implements the Machine Payments Protocol (MPP) in front of the MCP endpoint.
    ///
    /// A <c>tools/call</c> that carries a usable API key is left alone and billed through
    /// <see cref="UsageTrackingFilter"/> as before. A call without one is no longer simply
    /// rejected: the price of the tool is looked up and a 402 Payment Required answer carrying a
    /// payment challenge is returned, both as a <c>WWW-Authenticate: Payment</c> header and as the
    /// JSON-RPC error (<c>-32042</c>) the MCP transport binding of the protocol defines. When the
    /// caller retries with a payment credential the payment is settled — through Stripe for a card
    /// or on the Tempo blockchain for a stablecoin transfer — and the call runs, attributed to the
    /// payment rather than to a user. A tool priced at zero cents is one
    /// that makes no sense without an account: it is not offered for payment and a call without an
    /// API key is refused with 403.
    ///
    /// A post to the endpoint that names no tool at all — the probe an MPP agent, directory or
    /// validator makes to discover how a paid endpoint is paid for — is answered with a challenge
    /// too, rather than handed to the MCP transport, which has no JSON-RPC request to reply to and
    /// would leave the prober waiting until it gave up. A probe that comes back with a credential
    /// for that challenge is settled like any other payment: it names no tool to call, so what it
    /// buys is the catalogue of the tools of this server and of their prices, returned with 200 and
    /// the receipt of the payment.
    /// </summary>
    public class MppPaymentMiddleware
    {
        /// <summary><c>_meta</c> key the caller presents its payment credential under.</summary>
        public const string CredentialMetaKey = "org.paymentauth/credential";

        /// <summary><c>_meta</c> key the receipt of a settled payment is returned under.</summary>
        public const string ReceiptMetaKey = "org.paymentauth/receipt";

        /// <summary><see cref="HttpContext.Items"/> key holding the receipt of the call being served.</summary>
        public const string ReceiptItemKey = "Mpp:Receipt";

        /// <summary><see cref="HttpContext.Items"/> key holding the identity a paid call runs as.</summary>
        public const string PayerItemKey = "Mpp:Payer";

        /// <summary>Prefix of the identity a call paid for with MPP, rather than a subscriber, runs as.</summary>
        public const string PayerIdentityPrefix = "mpp:";

        /// <summary>
        /// Name the challenge of a payment probe is issued for. A probe names no tool, so the
        /// challenge is bound to the call itself rather than to any tool; a credential presented
        /// for it therefore pays for no tool call but for the catalogue of the tools of this
        /// endpoint and of what each of them costs.
        /// </summary>
        public const string ProbeToolName = "tools/call";

        private const int PaymentRequiredCode = -32042;
        private const int PaymentVerificationFailedCode = -32043;

        /// <summary>JSON-RPC error of a tool that is only available to a subscriber.</summary>
        private const int SubscribersOnlyCode = -32044;

        private const string ProblemBaseUri = "https://paymentauth.org/problems/";

        private readonly RequestDelegate _next;
        private readonly MppOptions _options;
        private readonly ILogger<MppPaymentMiddleware> _logger;

        public MppPaymentMiddleware(RequestDelegate next, IOptions<MppOptions> options, ILogger<MppPaymentMiddleware> logger)
        {
            _next = next;
            _options = options.Value;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            if (!_options.Enabled || !HttpMethods.IsPost(context.Request.Method) || !IsMcpPath(context.Request.Path))
            {
                await _next(context);
                return;
            }

            // A call that belongs to a subscriber is billed to that subscription, so it is handed
            // on untouched. How a subscriber is recognised belongs to the application, which says
            // so with an IMppSubscriberResolver; without one every call is paid for.
            var subscribers = context.RequestServices.GetService<IMppSubscriberResolver>();
            if (subscribers is not null
                && !string.IsNullOrWhiteSpace(await subscribers.GetSubscriberIdAsync(context, context.RequestAborted)))
            {
                await _next(context);
                return;
            }

            var (body, tooLarge) = await ReadBodyAsync(context.Request);
            var parameters = body?["params"] as JsonObject;
            var method = body is null ? null : StringValue(body, "method");
            if (!string.IsNullOrWhiteSpace(method) && !string.Equals(method, "tools/call", StringComparison.Ordinal))
            {
                // Every other JSON-RPC method — initialize, tools/list and the rest — is free and
                // is served by MCP as it always was.
                await _next(context);
                return;
            }

            if (tooLarge)
            {
                // A request body beyond the cap is left to MCP to deal with rather than buffered here.
                await _next(context);
                return;
            }

            // A POST to the MCP endpoint that names no tool is not a tool call this server can run
            // but a payment probe: the request an MPP agent, directory or validator makes of a paid
            // endpoint to see the challenge it issues. It is answered with one, rather than handed
            // to the MCP transport, which has no JSON-RPC request to reply to and would leave the
            // prober waiting until it gave up.
            var toolName = parameters is null ? null : StringValue(parameters, "name");
            var probe = string.IsNullOrWhiteSpace(toolName);

            var price = await context.RequestServices.GetRequiredService<IToolPricingService>()
                .GetPriceAsync(probe ? ProbeToolName : toolName!, context.RequestAborted);
            if (probe)
            {
                price = ProbePrice(price);
                toolName = ProbeToolName;
            }

            if (price.AmountCents <= 0)
            {
                if (probe)
                {
                    await _next(context);
                    return;
                }

                // A tool priced at zero makes no sense without an account, so it is not offered for
                // payment at all: the call is refused rather than challenged or run for free.
                await WriteSubscribersOnlyAsync(context, toolName!, body!);
                return;
            }

            // One service per payment method that is offered: the card payment through Stripe and,
            // when it is configured, the stablecoin transfer on Tempo.
            var payments = context.RequestServices.GetServices<IMppPaymentService>().ToList();
            if (payments.Count == 0)
            {
                await _next(context);
                return;
            }

            var realm = string.IsNullOrWhiteSpace(_options.Realm) ? context.Request.Host.Host : _options.Realm;

            var credential = ReadCredential(context.Request, body, parameters);

            if (credential is not null)
            {
                var payment = SelectMethod(payments, credential);
                var result = await payment.VerifyAndSettleAsync(credential, realm, toolName!, context.RequestAborted);
                if (result.Succeeded && result.Receipt is not null)
                {
                    context.Items[PayerItemKey] = PayerIdentityPrefix + result.Reference;
                    context.Items[ReceiptItemKey] = result.Receipt;
                    context.Response.Headers["Payment-Receipt"] = Services.JsonCanonicalizer.ToBase64Url(result.Receipt);
                    context.Response.Headers.CacheControl = "private";

                    // A paid probe names no tool, so there is no call for the MCP transport to run:
                    // what the payment buys is the catalogue of the tools and of their prices.
                    if (probe)
                    {
                        await WritePaidProbeAsync(context, body, result.Receipt);
                        return;
                    }

                    await _next(context);
                    return;
                }

                _logger.LogWarning(
                    "The payment presented for tool {Tool} was not accepted: {Reason}. {Detail}",
                    toolName,
                    result.Reason,
                    result.Detail);

                await WriteChallengeAsync(context, payments, realm, toolName!, price, body, probe, result);
                return;
            }

            await WriteChallengeAsync(context, payments, realm, toolName!, price, body, probe, null);
        }

        /// <summary>
        /// The price a probe is challenged for: the price of a tool that has none of its own, named
        /// and described for the call itself rather than for any one tool.
        /// </summary>
        private static ToolPrice ProbePrice(ToolPrice price) => new()
        {
            ToolName = ProbeToolName,
            AmountCents = price.AmountCents,
            SubscriberAmountCents = price.SubscriberAmountCents,
            Currency = price.Currency,
            Description = "One tool call on the MCP endpoint of ThinkBase.AI.MPP. "
                + "Each tool is priced separately: name the tool in params.name to be challenged for its price. "
                + "A call that names none buys the catalogue of the tools and of their prices."
        };

        /// <summary>
        /// The payment method the credential answers. A credential that names no method this
        /// server offers is handed to the first one, which refuses it as unsupported.
        /// </summary>
        private static IMppPaymentService SelectMethod(List<IMppPaymentService> payments, JsonObject credential)
        {
            var method = credential["challenge"] is JsonObject challenge ? StringValue(challenge, "method") : null;
            if (string.IsNullOrWhiteSpace(method))
            {
                return payments[0];
            }

            return payments.FirstOrDefault(p => string.Equals(p.Method, method, StringComparison.OrdinalIgnoreCase)) ?? payments[0];
        }

        private bool IsMcpPath(PathString path)
        {
            foreach (var candidate in _options.McpPaths)
            {
                if (string.Equals(candidate, "/", StringComparison.Ordinal))
                {
                    if (!path.HasValue || path.Value == "/")
                    {
                        return true;
                    }

                    continue;
                }

                if (path.StartsWithSegments(candidate, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Reads the JSON-RPC request without consuming it, so that MCP can read it again, together
        /// with whether the body was too large to read at all.
        /// </summary>
        private async Task<(JsonObject? Body, bool TooLarge)> ReadBodyAsync(HttpRequest request)
        {
            if (request.ContentLength > _options.MaxRequestBodyBytes)
            {
                return (null, true);
            }

            request.EnableBuffering();
            try
            {
                // A request larger than the cap is left to MCP to deal with rather than buffered here.
                var buffer = new byte[_options.MaxRequestBodyBytes];
                var read = await request.Body.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, request.HttpContext.RequestAborted);
                if (read >= buffer.Length)
                {
                    return (null, true);
                }

                return (JsonNode.Parse(Encoding.UTF8.GetString(buffer, 0, read)) as JsonObject, false);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or IOException)
            {
                return (null, false);
            }
            finally
            {
                request.Body.Position = 0;
            }
        }

        /// <summary>
        /// The payment credential, taken from the <c>_meta</c> of the request as the MCP transport
        /// binding places it — in <c>params</c> as well as at the root, both of which have to be
        /// checked — or from the HTTP credential header when a plain HTTP client is calling.
        /// </summary>
        private static JsonObject? ReadCredential(HttpRequest request, JsonObject? body, JsonObject? parameters)
        {
            var credential = (parameters?["_meta"] as JsonObject)?[CredentialMetaKey] as JsonObject
                ?? (body?["_meta"] as JsonObject)?[CredentialMetaKey] as JsonObject;
            if (credential is not null)
            {
                return credential;
            }

            foreach (var header in new[] { "Payment-Authorization", "Authorization" })
            {
                var value = request.Headers[header].ToString();
                if (string.IsNullOrWhiteSpace(value) || !value.StartsWith("Payment ", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var decoded = Services.JsonCanonicalizer.Base64UrlDecode(value["Payment ".Length..].Trim());
                if (decoded is null)
                {
                    continue;
                }

                try
                {
                    if (JsonNode.Parse(Encoding.UTF8.GetString(decoded)) is JsonObject parsed)
                    {
                        return parsed;
                    }
                }
                catch (JsonException)
                {
                    // A credential that is not JSON is treated as if none had been presented.
                }
            }

            return null;
        }

        /// <summary>
        /// Writes the 402 answer: one challenge per payment method offered, in a
        /// <c>WWW-Authenticate</c> header each for HTTP clients and together in the JSON-RPC error
        /// data for MCP clients, with the problem details of a failed payment when there was one.
        /// </summary>
        private static async Task WriteChallengeAsync(
            HttpContext context,
            List<IMppPaymentService> payments,
            string realm,
            string toolName,
            ToolPrice price,
            JsonObject? body,
            bool probe,
            PaymentVerificationResult? failure)
        {
            var challenges = payments.Select(p => (Payment: p, Challenge: p.CreateChallenge(realm, toolName, price))).ToList();
            var challenge = challenges[0].Challenge;

            context.Response.StatusCode = StatusCodes.Status402PaymentRequired;
            context.Response.Headers.WWWAuthenticate = new StringValues(
                challenges.Select(c => c.Payment.FormatChallengeHeader(c.Challenge)).ToArray());
            context.Response.Headers.CacheControl = "no-store";
            context.Response.ContentType = "application/json";

            var problem = new JsonObject
            {
                ["type"] = ProblemBaseUri + (failure?.Reason ?? "payment-required"),
                ["title"] = failure is null ? "Payment Required" : "Payment Verification Failed",
                ["status"] = StatusCodes.Status402PaymentRequired,
                ["detail"] = failure?.Detail ?? (probe
                    ? "Payment is required for a tool call on this endpoint, or supply an API key in the X-Api-Key header. "
                        + "Each tool is priced separately: name the tool in params.name to be challenged for its price."
                    : $"Payment is required for the {toolName} tool, or supply an API key in the X-Api-Key header."),
                ["challengeId"] = challenge.Id
            };

            var data = new JsonObject
            {
                ["httpStatus"] = StatusCodes.Status402PaymentRequired,
                ["challenges"] = new JsonArray(challenges.Select(c => (JsonNode)c.Payment.ToJsonRpcChallenge(c.Challenge)).ToArray()),
                ["problem"] = problem
            };

            if (failure is not null)
            {
                data["failure"] = new JsonObject
                {
                    ["reason"] = failure.Reason,
                    ["detail"] = failure.Detail
                };
            }

            var response = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = body?["id"] is JsonNode id ? JsonNode.Parse(id.ToJsonString()) : null,
                ["error"] = new JsonObject
                {
                    ["code"] = failure is null ? PaymentRequiredCode : PaymentVerificationFailedCode,
                    ["message"] = failure is null ? "Payment Required" : "Payment Verification Failed",
                    ["data"] = data
                }
            };

            // The problem details are carried at the top of the body as well as inside the JSON-RPC
            // error, so that a plain HTTP payer — an MPP agent or validator, which reads the body as
            // RFC 9457 problem details and not as JSON-RPC — is told why its payment was refused
            // rather than only that it was.
            foreach (var member in problem)
            {
                response[member.Key] = member.Value is null ? null : JsonNode.Parse(member.Value.ToJsonString());
            }

            await context.Response.WriteAsync(response.ToJsonString(), context.RequestAborted);

        }

        /// <summary>
        /// Writes the answer to a probe that has been paid for. A probe names no tool, so there is
        /// no call for the MCP transport to run: what the payment buys is the catalogue of the
        /// tools of this server and of what each of them costs, returned as the result of the
        /// JSON-RPC request together with the receipt of the payment.
        /// </summary>
        private async Task WritePaidProbeAsync(HttpContext context, JsonObject? body, JsonObject receipt)
        {
            var catalogue = new JsonArray();
            try
            {
                var prices = await context.RequestServices.GetRequiredService<IToolPricingService>()
                    .GetPricesAsync(context.RequestAborted);
                foreach (var price in prices)
                {
                    catalogue.Add(new JsonObject
                    {
                        ["tool"] = price.ToolName,
                        ["amountCents"] = price.AmountCents,
                        ["currency"] = string.IsNullOrWhiteSpace(price.Currency) ? _options.Currency : price.Currency,
                        ["subscribersOnly"] = price.AmountCents <= 0,
                        ["description"] = price.Description
                    });
                }
            }
            catch (Exception ex)
            {
                // The payment has been taken, so the answer is written without the catalogue rather
                // than failed; the prices are served at /api/pricing/tools as well.
                _logger.LogWarning(ex, "The prices could not be read for a paid payment probe.");
            }

            var text = "This call named no tool, so it bought the catalogue of this endpoint: "
                + $"{catalogue.Count} priced tool(s), each with the price of one call in cents. "
                + "Name a tool in params.name to call it, paying the challenge that is then issued for that tool.";

            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "application/json";

            var response = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = body?["id"] is JsonNode id ? JsonNode.Parse(id.ToJsonString()) : null,
                ["result"] = new JsonObject
                {
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
                    ["structuredContent"] = new JsonObject { ["tools"] = catalogue },
                    ["_meta"] = new JsonObject { [ReceiptMetaKey] = JsonNode.Parse(receipt.ToJsonString()) }
                }
            };

            await context.Response.WriteAsync(response.ToJsonString(), context.RequestAborted);
        }

        /// <summary>
        /// Writes the 403 answer for a tool priced at zero: such a tool needs the account an API key
        /// comes with, so it is not advertised as payable and no payment is accepted for it.
        /// </summary>
        private static async Task WriteSubscribersOnlyAsync(HttpContext context, string toolName, JsonObject body)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.Headers.CacheControl = "no-store";
            context.Response.ContentType = "application/json";

            var response = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = body["id"] is JsonNode id ? JsonNode.Parse(id.ToJsonString()) : null,
                ["error"] = new JsonObject
                {
                    ["code"] = SubscribersOnlyCode,
                    ["message"] = "Forbidden",
                    ["data"] = new JsonObject
                    {
                        ["httpStatus"] = StatusCodes.Status403Forbidden,
                        ["problem"] = new JsonObject
                        {
                            ["type"] = ProblemBaseUri + "subscribers-only",
                            ["title"] = "Forbidden",
                            ["status"] = StatusCodes.Status403Forbidden,
                            ["detail"] = $"The {toolName} tool needs an account: supply an API key in the X-Api-Key header. It cannot be paid for."
                        }
                    }
                }
            };

            await context.Response.WriteAsync(response.ToJsonString(), context.RequestAborted);
        }

        private static string? StringValue(JsonObject json, string name) =>
            json[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    }
}
