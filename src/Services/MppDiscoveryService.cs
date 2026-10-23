using ThinkBase.AI.MPP.Interfaces;
using ThinkBase.AI.MPP.Models;
using Microsoft.Extensions.Options;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ThinkBase.AI.MPP.Services
{
    /// <summary>
    /// Builds the discovery document of the Machine Payments Protocol: the OpenAPI 3.1 description
    /// an MPP agent, directory or validator expects to find at <c>/openapi.json</c>, in which every
    /// paid operation carries an <c>x-payment-info</c> extension naming what a call costs and how it
    /// may be paid for.
    ///
    /// This server is an MCP server, so it has one paid endpoint: the streamable HTTP transport a
    /// <c>tools/call</c> is posted to. The price, however, belongs to the tool rather than to the
    /// endpoint, so the operation carries one offer per payment method per payable tool, each
    /// naming its tool. The offers are built from the very challenges
    /// <see cref="IMppPaymentService"/> would issue, so what is advertised here is what a 402
    /// actually asks for; the challenge of the 402 remains the authoritative one.
    /// </summary>
    public class MppDiscoveryService : IMppDiscoveryService
    {
        /// <summary>How long a caller may cache the discovery document for, in seconds.</summary>
        public const int CacheSeconds = 300;

        /// <summary>An MPP amount is an integer number of the smallest units of its currency.</summary>
        private static readonly Regex BaseUnits = new("^(0|[1-9][0-9]*)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private readonly IToolPricingService _toolPricingService;
        private readonly IMppToolCatalog _tools;
        private readonly IEnumerable<IMppPaymentService> _payments;
        private readonly MppOptions _options;
        private readonly ILogger<MppDiscoveryService> _logger;

        public MppDiscoveryService(
            IToolPricingService toolPricingService,
            IMppToolCatalog tools,
            IEnumerable<IMppPaymentService> payments,
            IOptions<MppOptions> options,
            ILogger<MppDiscoveryService> logger)
        {
            _toolPricingService = toolPricingService;
            _tools = tools;
            _payments = payments;
            _options = options.Value;
            _logger = logger;
        }

        public async Task<JsonObject> BuildDocumentAsync(string serverUrl, string realm, CancellationToken cancellationToken = default)
        {
            serverUrl = (serverUrl ?? string.Empty).TrimEnd('/');
            var offers = await BuildOffersAsync(realm, cancellationToken);

            var paths = new JsonObject
            {
                [McpPath] = new JsonObject { ["post"] = BuildMcpOperation(offers) },
                ["/api/tools"] = new JsonObject { ["get"] = BuildToolListOperation() },
                ["/api/pricing/tools"] = new JsonObject { ["get"] = BuildToolPricingOperation() }
            };

            return new JsonObject
            {
                ["openapi"] = "3.1.0",
                ["info"] = new JsonObject
                {
                    ["title"] = Title,
                    ["version"] = Version,
                    ["description"] = Description
                },
                ["servers"] = new JsonArray(new JsonObject { ["url"] = serverUrl }),
                ["x-service-info"] = new JsonObject
                {
                    ["categories"] = new JsonArray(_options.ServiceCategories.Select(c => (JsonNode?)c).ToArray()),
                    ["docs"] = new JsonObject
                    {
                        ["homepage"] = serverUrl + "/",
                        ["apiReference"] = serverUrl + "/docs/",
                        ["llms"] = serverUrl + "/llms.txt"
                    }
                },
                ["paths"] = paths
            };
        }

        /// <summary>The path the MCP transport is served on and so the one the offers are made for.</summary>
        private string McpPath
        {
            get
            {
                var path = _options.McpPaths.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p)) ?? "/";
                return path.StartsWith('/') ? path : "/" + path;
            }
        }

        /// <summary>The title of this server: the configured one, or the name of the application.</summary>
        private string Title => string.IsNullOrWhiteSpace(_options.ServiceTitle)
            ? Assembly.GetEntryAssembly()?.GetName().Name ?? "MCP server"
            : _options.ServiceTitle;

        /// <summary>What this server does, for an agent choosing between servers.</summary>
        private string Description => string.IsNullOrWhiteSpace(_options.ServiceDescription)
            ? "A Model Context Protocol server. A tool call made with an API key is billed to its subscription; a call made "
                + "without one is answered with a Machine Payments Protocol challenge and runs once it has been paid for."
            : _options.ServiceDescription;

        /// <summary>The version of this server, as the assembly of the application carries it.</summary>
        private static string Version => Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "1.0.0";

        /// <summary>
        /// One offer per payment method per payable tool. A tool priced at zero is left out: it
        /// needs the account an API key comes with and is not offered for payment at all.
        /// </summary>
        private async Task<JsonArray> BuildOffersAsync(string realm, CancellationToken cancellationToken)
        {
            var offers = new JsonArray();
            if (!_options.Enabled)
            {
                return offers;
            }

            var payments = _payments.ToList();
            if (payments.Count == 0)
            {
                return offers;
            }

            foreach (var tool in _tools.GetTools().Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
            {
                var price = await _toolPricingService.GetPriceAsync(tool, cancellationToken);
                if (price.AmountCents <= 0)
                {
                    continue;
                }

                foreach (var payment in payments)
                {
                    var offer = BuildOffer(payment, realm, tool, price, _logger);
                    if (offer is not null)
                    {
                        offers.Add(offer);
                    }
                }
            }

            return offers;
        }

        /// <summary>
        /// The offer for one tool paid for with one method, taken from the challenge that method
        /// would issue, so that the advertised price is the one that is actually charged.
        /// </summary>
        private static JsonObject? BuildOffer(IMppPaymentService payment, string realm, string tool, ToolPrice price, ILogger logger)
        {
            PaymentChallenge challenge;
            try
            {
                challenge = payment.CreateChallenge(realm, tool, price);
            }
            catch (Exception ex)
            {
                // A method that cannot price this tool is simply not advertised for it; the other
                // methods, and the 402 itself, are unaffected.
                logger.LogWarning(ex, "The {Method} payment method could not price the {Tool} tool for discovery.", payment.Method, tool);
                return null;
            }

            var amount = AsString(challenge.Request["amount"]);
            var offer = new JsonObject
            {
                ["intent"] = challenge.Intent,
                ["method"] = challenge.Method,
                // A price this server cannot express in whole base units is advertised as dynamic,
                // which tells the agent to read the amount from the challenge of the 402.
                ["amount"] = amount is not null && BaseUnits.IsMatch(amount) ? amount : null
            };

            var currency = AsString(challenge.Request["currency"]);
            if (!string.IsNullOrWhiteSpace(currency))
            {
                offer["currency"] = currency;
            }

            if (!string.IsNullOrWhiteSpace(challenge.Description))
            {
                offer["description"] = challenge.Description;
            }

            // Not a field of the protocol, but the one thing an MCP endpoint has to say that an
            // HTTP one does not: which tool of the endpoint this offer prices.
            offer["tool"] = tool;
            return offer;
        }

        /// <summary>The MCP endpoint, described as the one paid operation of this server.</summary>
        private JsonObject BuildMcpOperation(JsonArray offers)
        {
            var responses = new JsonObject
            {
                ["200"] = new JsonObject
                {
                    ["description"] = "The JSON-RPC response, as a JSON object or as a Server-Sent Events stream of them.",
                    ["content"] = new JsonObject
                    {
                        ["application/json"] = new JsonObject { ["schema"] = ResponseSchema() },
                        ["text/event-stream"] = new JsonObject { ["schema"] = new JsonObject { ["type"] = "string" } }
                    }
                }
            };

            if (offers.Count > 0)
            {
                responses["402"] = new JsonObject
                {
                    ["description"] = "Payment Required: the call carried neither an API key nor a payment credential. "
                        + "The payment challenges are returned in the WWW-Authenticate headers and in the data of the "
                        + "JSON-RPC error -32042.",
                    ["content"] = new JsonObject
                    {
                        ["application/json"] = new JsonObject { ["schema"] = ResponseSchema() }
                    }
                };
            }

            responses["403"] = new JsonObject
            {
                ["description"] = "The tool is only available to a subscriber and cannot be paid for (JSON-RPC error -32044).",
                ["content"] = new JsonObject
                {
                    ["application/json"] = new JsonObject { ["schema"] = ResponseSchema() }
                }
            };

            var operation = new JsonObject
            {
                ["operationId"] = "mcp",
                ["summary"] = "The Model Context Protocol endpoint (JSON-RPC 2.0 over streamable HTTP).",
                ["description"] = "Lists and calls the tools of this server. A subscriber authenticates the call with its API key in "
                    + "the X-Api-Key header; an agent without one pays for it, presenting its credential in the _meta of the request "
                    + "under org.paymentauth/credential or in an Authorization: Payment header. The price of each tool is given in the "
                    + "x-payment-info offers below, each of which names the tool it prices.",
                ["security"] = new JsonArray(),
                ["requestBody"] = new JsonObject
                {
                    ["required"] = true,
                    ["content"] = new JsonObject
                    {
                        ["application/json"] = new JsonObject
                        {
                            ["schema"] = RequestSchema(),
                            // The example is a call of a tool that is paid for, so that a client or
                            // validator building its request from this document is answered with the
                            // 402 challenge of that tool rather than with something unpriced.
                            ["example"] = ExampleRequest(offers)
                        }
                    }
                },
                ["responses"] = responses
            };

            if (offers.Count > 0)
            {
                operation["x-payment-info"] = new JsonObject { ["offers"] = offers };
            }

            return operation;
        }

        private static JsonObject BuildToolListOperation() => new()
        {
            ["operationId"] = "listTools",
            ["summary"] = "The tools this server offers, with their descriptions.",
            ["security"] = new JsonArray(),
            ["responses"] = new JsonObject
            {
                ["200"] = new JsonObject
                {
                    ["description"] = "The tools of this server.",
                    ["content"] = new JsonObject
                    {
                        ["application/json"] = new JsonObject
                        {
                            ["schema"] = new JsonObject
                            {
                                ["type"] = "array",
                                ["items"] = new JsonObject
                                {
                                    ["type"] = "object",
                                    ["properties"] = new JsonObject
                                    {
                                        ["name"] = new JsonObject { ["type"] = "string" },
                                        ["description"] = new JsonObject { ["type"] = "string" }
                                    },
                                    ["required"] = new JsonArray("name")
                                }
                            }
                        }
                    }
                }
            }
        };

        private static JsonObject BuildToolPricingOperation() => new()
        {
            ["operationId"] = "listToolPrices",
            ["summary"] = "The price of one call of each tool, for an agent paying per call and for a subscriber.",
            ["security"] = new JsonArray(),
            ["responses"] = new JsonObject
            {
                ["200"] = new JsonObject
                {
                    ["description"] = "The per tool prices, in cents.",
                    ["content"] = new JsonObject
                    {
                        ["application/json"] = new JsonObject
                        {
                            ["schema"] = new JsonObject
                            {
                                ["type"] = "array",
                                ["items"] = new JsonObject
                                {
                                    ["type"] = "object",
                                    ["properties"] = new JsonObject
                                    {
                                        ["tool"] = new JsonObject { ["type"] = "string" },
                                        ["amountCents"] = new JsonObject { ["type"] = "number" },
                                        ["subscriberAmountCents"] = new JsonObject { ["type"] = "number" },
                                        ["currency"] = new JsonObject { ["type"] = "string" },
                                        ["description"] = new JsonObject { ["type"] = new JsonArray("string", "null") }
                                    },
                                    ["required"] = new JsonArray("tool", "amountCents")
                                }
                            }
                        }
                    }
                }
            }
        };

        /// <summary>
        /// An example request for the endpoint: a call of the first tool that is paid for, so that
        /// a caller which builds its request from this document calls something that has a price.
        /// </summary>
        private JsonObject ExampleRequest(JsonArray offers)
        {
            var tool = offers.Select(offer => AsString(offer?["tool"])).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name))
                ?? _tools.GetTools().Keys.FirstOrDefault();

            return new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "tools/call",
                ["params"] = new JsonObject
                {
                    ["name"] = tool ?? "tools/call",
                    ["arguments"] = new JsonObject()
                }
            };
        }

        private static JsonObject RequestSchema() => new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["jsonrpc"] = new JsonObject { ["type"] = "string", ["const"] = "2.0" },
                ["id"] = new JsonObject { ["type"] = new JsonArray("string", "number", "null") },
                ["method"] = new JsonObject { ["type"] = "string", ["description"] = "The JSON-RPC method, for example tools/list or tools/call." },
                ["params"] = new JsonObject { ["type"] = "object" }
            },
            ["required"] = new JsonArray("jsonrpc", "method")
        };

        private static JsonObject ResponseSchema() => new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["jsonrpc"] = new JsonObject { ["type"] = "string", ["const"] = "2.0" },
                ["id"] = new JsonObject { ["type"] = new JsonArray("string", "number", "null") },
                ["result"] = new JsonObject { ["type"] = "object" },
                ["error"] = new JsonObject { ["type"] = "object" }
            },
            ["required"] = new JsonArray("jsonrpc")
        };

        private static string? AsString(JsonNode? node) =>
            node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    }
}
