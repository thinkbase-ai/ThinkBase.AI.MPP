using ThinkBase.AI.MPP.Authorization;
using ThinkBase.AI.MPP.Interfaces;
using ThinkBase.AI.MPP.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using System.Text;
using System.Text.Json.Nodes;

namespace ThinkBase.AI.MPP.Tests.Authorization
{
    /// <summary>
    /// A subscriber resolver of the kind an application supplies: the API key of the
    /// <c>X-Api-Key</c> header, looked up in the cache the application keeps its keys in.
    /// </summary>
    internal sealed class ApiKeySubscriberResolver : IMppSubscriberResolver
    {
        private readonly IDistributedCache _cache;

        public ApiKeySubscriberResolver(IDistributedCache cache) => _cache = cache;

        public async Task<string?> GetSubscriberIdAsync(HttpContext context, CancellationToken cancellationToken = default)
        {
            var apiKey = context.Request.Headers["X-Api-Key"].ToString();
            return string.IsNullOrWhiteSpace(apiKey)
                ? null
                : await _cache.GetStringAsync($"ApiKey:{apiKey}", cancellationToken);
        }
    }

    [TestClass]
    public class MppPaymentMiddlewareTests
    {
        private const string ApiKey = "11111111-2222-3333-4444-555555555555";
        private const string UserId = "6890f0d1c0ffee0000000001";
        private const string Tool = "FindLocation";

        private readonly Mock<IToolPricingService> _pricing = new();
        private readonly Mock<IMppPaymentService> _payment = new();
        private IDistributedCache _cache = null!;
        private IServiceProvider _services = null!;

        [TestInitialize]
        public void Initialize()
        {
            _cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
            _cache.SetString($"ApiKey:{ApiKey}", UserId);

            _pricing.Setup(p => p.GetPriceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ToolPrice { ToolName = Tool, AmountCents = 25m, Currency = "usd" });
            _payment.Setup(p => p.CreateChallenge(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ToolPrice>()))
                .Returns(new PaymentChallenge
                {
                    Id = "challenge-1",
                    Realm = "localhost",
                    Request = new JsonObject { ["amount"] = "25", ["currency"] = "usd" },
                    Expires = "2099-01-01T00:00:00Z"
                });
            _payment.Setup(p => p.FormatChallengeHeader(It.IsAny<PaymentChallenge>()))
                .Returns("Payment id=\"challenge-1\", realm=\"localhost\", method=\"stripe\", intent=\"charge\", request=\"eyJ9\"");
            _payment.Setup(p => p.ToJsonRpcChallenge(It.IsAny<PaymentChallenge>()))
                .Returns(new JsonObject { ["id"] = "challenge-1" });

            var services = new ServiceCollection();
            services.AddSingleton(_cache);
            services.AddSingleton<IMppSubscriberResolver>(new ApiKeySubscriberResolver(_cache));
            services.AddSingleton(_pricing.Object);
            services.AddSingleton(_payment.Object);
            _services = services.BuildServiceProvider();
        }

        private MppPaymentMiddleware CreateMiddleware(RequestDelegate next, MppOptions? options = null)
        {
            return new MppPaymentMiddleware(next, Options.Create(options ?? new MppOptions()), new Mock<ILogger<MppPaymentMiddleware>>().Object);
        }

        private HttpContext CreateContext(string body, string? apiKey = null, JsonObject? credential = null)
        {
            var request = ParseOrNull(body);
            if (credential is not null && request?["params"] is JsonObject parameters)
            {
                parameters["_meta"] = new JsonObject { [MppPaymentMiddleware.CredentialMetaKey] = credential };
            }

            var context = new DefaultHttpContext { RequestServices = _services };
            context.Request.Method = HttpMethods.Post;
            context.Request.Path = "/";
            context.Request.ContentType = "application/json";
            context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(request?.ToJsonString() ?? body));
            context.Response.Body = new MemoryStream();
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                context.Request.Headers["X-Api-Key"] = apiKey;
            }

            return context;
        }

        /// <summary>The request body, or null when it is not a JSON object, as a probe's body may not be.</summary>
        private static JsonObject? ParseOrNull(string body)
        {
            try
            {
                return JsonNode.Parse(body) as JsonObject;
            }
            catch (System.Text.Json.JsonException)
            {
                return null;
            }
        }

        /// <summary>A credential as a plain HTTP client presents it, base64url encoded.</summary>
        private static string Base64Url(string json) =>
            MPP.Services.JsonCanonicalizer.Base64UrlEncode(Encoding.UTF8.GetBytes(json));

        private static string ToolCall(string tool) =>
            "{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"tools/call\",\"params\":{\"name\":\"" + tool + "\",\"arguments\":{}}}";

        private static JsonObject ReadResponse(HttpContext context)
        {
            context.Response.Body.Position = 0;
            using var reader = new StreamReader(context.Response.Body);
            return (JsonNode.Parse(reader.ReadToEnd()) as JsonObject)!;
        }

        [TestMethod]
        public async Task Middleware_ChallengesAToolCallThatCarriesNoApiKey()
        {
            var called = false;
            var middleware = CreateMiddleware(_ => { called = true; return Task.CompletedTask; });
            var context = CreateContext(ToolCall(Tool));

            await middleware.InvokeAsync(context);

            Assert.IsFalse(called, "An unpaid call must not reach the MCP server.");
            Assert.AreEqual(StatusCodes.Status402PaymentRequired, context.Response.StatusCode);
            StringAssert.StartsWith(context.Response.Headers.WWWAuthenticate.ToString(), "Payment ");
            Assert.AreEqual("no-store", context.Response.Headers.CacheControl.ToString());

            var body = ReadResponse(context);
            Assert.AreEqual(7, body["id"]!.GetValue<int>());
            Assert.AreEqual(-32042, body["error"]!["code"]!.GetValue<int>());
            Assert.AreEqual(402, body["error"]!["data"]!["httpStatus"]!.GetValue<int>());
            Assert.AreEqual("challenge-1", body["error"]!["data"]!["challenges"]![0]!["id"]!.GetValue<string>());
        }

        [TestMethod]
        public async Task Middleware_LetsACallWithAnApiKeyThrough()
        {
            var called = false;
            var middleware = CreateMiddleware(_ => { called = true; return Task.CompletedTask; });
            var context = CreateContext(ToolCall(Tool), ApiKey);

            await middleware.InvokeAsync(context);

            Assert.IsTrue(called);
            Assert.AreNotEqual(StatusCodes.Status402PaymentRequired, context.Response.StatusCode);
            _payment.Verify(p => p.CreateChallenge(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ToolPrice>()), Times.Never);
        }

        [TestMethod]
        public async Task Middleware_RefusesAZeroPricedToolWithoutAnApiKey()
        {
            _pricing.Setup(p => p.GetPriceAsync(Tool, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ToolPrice { ToolName = Tool, AmountCents = 0m });
            var called = false;
            var middleware = CreateMiddleware(_ => { called = true; return Task.CompletedTask; });

            var context = CreateContext(ToolCall(Tool));
            await middleware.InvokeAsync(context);

            Assert.IsFalse(called, "A tool that needs an account must not run without one.");
            Assert.AreEqual(StatusCodes.Status403Forbidden, context.Response.StatusCode);
            Assert.IsTrue(string.IsNullOrEmpty(context.Response.Headers.WWWAuthenticate.ToString()));
            _payment.Verify(p => p.CreateChallenge(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ToolPrice>()), Times.Never);

            var body = ReadResponse(context);
            Assert.AreEqual(7, body["id"]!.GetValue<int>());
            Assert.AreEqual(-32044, body["error"]!["code"]!.GetValue<int>());
            Assert.AreEqual(403, body["error"]!["data"]!["httpStatus"]!.GetValue<int>());
            Assert.AreEqual(
                "https://paymentauth.org/problems/subscribers-only",
                body["error"]!["data"]!["problem"]!["type"]!.GetValue<string>());
        }

        [TestMethod]
        public async Task Middleware_LetsAZeroPricedToolThroughForASubscriber()
        {
            _pricing.Setup(p => p.GetPriceAsync(Tool, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ToolPrice { ToolName = Tool, AmountCents = 0m });
            var called = false;
            var middleware = CreateMiddleware(_ => { called = true; return Task.CompletedTask; });

            var context = CreateContext(ToolCall(Tool), ApiKey);
            await middleware.InvokeAsync(context);

            Assert.IsTrue(called);
            Assert.AreNotEqual(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        }

        [TestMethod]
        public async Task Middleware_IgnoresRequestsThatAreNotToolCalls()
        {
            var called = false;
            var middleware = CreateMiddleware(_ => { called = true; return Task.CompletedTask; });

            await middleware.InvokeAsync(CreateContext("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}"""));

            Assert.IsTrue(called);
            _pricing.Verify(p => p.GetPriceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [TestMethod]
        public async Task Middleware_ChallengesAPaymentProbeThatNamesNoTool()
        {
            var called = false;
            var middleware = CreateMiddleware(_ => { called = true; return Task.CompletedTask; });

            var context = CreateContext("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{}}""");
            await middleware.InvokeAsync(context);

            Assert.IsFalse(called, "A probe must be answered here, not left to the MCP transport.");
            Assert.AreEqual(StatusCodes.Status402PaymentRequired, context.Response.StatusCode);
            StringAssert.StartsWith(context.Response.Headers.WWWAuthenticate.ToString(), "Payment ");
            Assert.AreEqual(-32042, ReadResponse(context)["error"]!["code"]!.GetValue<int>());
            _payment.Verify(
                p => p.CreateChallenge(It.IsAny<string>(), MppPaymentMiddleware.ProbeToolName, It.IsAny<ToolPrice>()),
                Times.Once,
                "A probe is challenged for the call itself, since it names no tool.");
        }

        [TestMethod]
        public async Task Middleware_ChallengesAProbeThatIsNotAJsonRpcRequest()
        {
            var called = false;
            var middleware = CreateMiddleware(_ => { called = true; return Task.CompletedTask; });

            var context = CreateContext("{}");
            await middleware.InvokeAsync(context);

            Assert.IsFalse(called);
            Assert.AreEqual(StatusCodes.Status402PaymentRequired, context.Response.StatusCode);
            StringAssert.StartsWith(context.Response.Headers.WWWAuthenticate.ToString(), "Payment ");

            var body = ReadResponse(context);
            Assert.IsNull(body["id"]);
            Assert.AreEqual(-32042, body["error"]!["code"]!.GetValue<int>());
            Assert.AreEqual("challenge-1", body["error"]!["data"]!["challenges"]![0]!["id"]!.GetValue<string>());
        }

        [TestMethod]
        public async Task Middleware_LetsAProbeThroughForASubscriber()
        {
            var called = false;
            var middleware = CreateMiddleware(_ => { called = true; return Task.CompletedTask; });

            var context = CreateContext("{}", ApiKey);
            await middleware.InvokeAsync(context);

            Assert.IsTrue(called);
            Assert.AreNotEqual(StatusCodes.Status402PaymentRequired, context.Response.StatusCode);
        }

        [TestMethod]
        public async Task Middleware_SettlesAPaymentForAProbeAndReturnsTheCatalogue()
        {
            var receipt = new JsonObject { ["status"] = "success", ["reference"] = "pi_probe" };
            _payment.Setup(p => p.VerifyAndSettleAsync(
                    It.IsAny<JsonObject>(), It.IsAny<string>(), MppPaymentMiddleware.ProbeToolName, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PaymentVerificationResult.Success(receipt, "pi_probe"));
            _pricing.Setup(p => p.GetPricesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new[] { new ToolPrice { ToolName = Tool, AmountCents = 25m, Currency = "usd" } });
            var called = false;
            var middleware = CreateMiddleware(_ => { called = true; return Task.CompletedTask; });

            var context = CreateContext(
                """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{}}""",
                credential: new JsonObject { ["payload"] = new JsonObject { ["spt"] = "spt_1" } });
            await middleware.InvokeAsync(context);

            Assert.IsFalse(called, "A probe names no tool, so there is no call for the MCP transport to run.");
            Assert.AreEqual(StatusCodes.Status200OK, context.Response.StatusCode);
            Assert.IsFalse(string.IsNullOrWhiteSpace(context.Response.Headers["Payment-Receipt"].ToString()));

            var body = ReadResponse(context);
            Assert.AreEqual(1, body["id"]!.GetValue<int>());
            Assert.AreEqual(Tool, body["result"]!["structuredContent"]!["tools"]![0]!["tool"]!.GetValue<string>());
            Assert.AreEqual(
                "success",
                body["result"]!["_meta"]![MppPaymentMiddleware.ReceiptMetaKey]!["status"]!.GetValue<string>());
        }

        [TestMethod]
        public async Task Middleware_SettlesTheCredentialOfAProbeThatCarriesNoBody()
        {
            var receipt = new JsonObject { ["status"] = "success", ["reference"] = "0xabc" };
            _payment.Setup(p => p.VerifyAndSettleAsync(
                    It.IsAny<JsonObject>(), It.IsAny<string>(), MppPaymentMiddleware.ProbeToolName, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PaymentVerificationResult.Success(receipt, "0xabc"));
            _pricing.Setup(p => p.GetPricesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);
            var middleware = CreateMiddleware(_ => Task.CompletedTask);

            // The validator of the protocol probes with an empty body and presents its credential
            // in the HTTP header, which is the shape that has to be settled too.
            var context = CreateContext(string.Empty);
            context.Request.Headers.Authorization = "Payment " + Base64Url(
                """{"challenge":{"method":"stripe"},"payload":{"spt":"spt_1"}}""");

            await middleware.InvokeAsync(context);

            Assert.AreEqual(StatusCodes.Status200OK, context.Response.StatusCode);
            Assert.IsNull(ReadResponse(context)["id"]);
            _payment.Verify(
                p => p.VerifyAndSettleAsync(
                    It.IsAny<JsonObject>(), It.IsAny<string>(), MppPaymentMiddleware.ProbeToolName, It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [TestMethod]
        public async Task Middleware_ChallengesAProbeAgainWhenItsPaymentIsRefused()
        {
            _payment.Setup(p => p.VerifyAndSettleAsync(
                    It.IsAny<JsonObject>(), It.IsAny<string>(), MppPaymentMiddleware.ProbeToolName, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PaymentVerificationResult.Failure("verification-failed", "The payment was refused."));
            var middleware = CreateMiddleware(_ => Task.CompletedTask);

            var context = CreateContext(
                """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{}}""",
                credential: new JsonObject { ["payload"] = new JsonObject { ["spt"] = "spt_1" } });
            await middleware.InvokeAsync(context);

            Assert.AreEqual(StatusCodes.Status402PaymentRequired, context.Response.StatusCode);
            Assert.AreEqual(-32043, ReadResponse(context)["error"]!["code"]!.GetValue<int>());
        }

        [TestMethod]
        public async Task Middleware_RunsThePaidCallAndReturnsTheReceipt()
        {
            var receipt = new JsonObject { ["status"] = "success", ["reference"] = "pi_123" };
            _payment.Setup(p => p.VerifyAndSettleAsync(It.IsAny<JsonObject>(), It.IsAny<string>(), Tool, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PaymentVerificationResult.Success(receipt, "pi_123"));
            string? payer = null;
            var middleware = CreateMiddleware(context =>
            {
                payer = context.Items[MppPaymentMiddleware.PayerItemKey] as string;
                return Task.CompletedTask;
            });

            var context = CreateContext(ToolCall(Tool), credential: new JsonObject { ["payload"] = new JsonObject { ["spt"] = "spt_1" } });
            await middleware.InvokeAsync(context);

            Assert.AreEqual("mpp:pi_123", payer);
            Assert.AreNotEqual(StatusCodes.Status402PaymentRequired, context.Response.StatusCode);
            Assert.IsFalse(string.IsNullOrWhiteSpace(context.Response.Headers["Payment-Receipt"].ToString()));
            Assert.AreSame(receipt, context.Items[MppPaymentMiddleware.ReceiptItemKey]);
        }

        [TestMethod]
        public async Task Middleware_ChallengesAgainWhenThePaymentIsRefused()
        {
            _payment.Setup(p => p.VerifyAndSettleAsync(It.IsAny<JsonObject>(), It.IsAny<string>(), Tool, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PaymentVerificationResult.Failure("verification-failed", "The payment was refused."));
            var called = false;
            var middleware = CreateMiddleware(_ => { called = true; return Task.CompletedTask; });

            var context = CreateContext(ToolCall(Tool), credential: new JsonObject { ["payload"] = new JsonObject { ["spt"] = "spt_1" } });
            await middleware.InvokeAsync(context);

            Assert.IsFalse(called);
            Assert.AreEqual(StatusCodes.Status402PaymentRequired, context.Response.StatusCode);

            var body = ReadResponse(context);
            Assert.AreEqual(-32043, body["error"]!["code"]!.GetValue<int>());
            Assert.AreEqual("verification-failed", body["error"]!["data"]!["failure"]!["reason"]!.GetValue<string>());
            Assert.AreEqual("https://paymentauth.org/problems/verification-failed", body["error"]!["data"]!["problem"]!["type"]!.GetValue<string>());

            // A plain HTTP payer reads the body as problem details rather than as JSON-RPC, so the
            // reason the payment was refused is carried at the top of it as well.
            Assert.AreEqual("https://paymentauth.org/problems/verification-failed", body["type"]!.GetValue<string>());
            Assert.AreEqual("Payment Verification Failed", body["title"]!.GetValue<string>());
            Assert.AreEqual(402, body["status"]!.GetValue<int>());
            Assert.AreEqual("The payment was refused.", body["detail"]!.GetValue<string>());
        }

        [TestMethod]
        public async Task Middleware_OffersOneChallengePerPaymentMethod()
        {
            var stablecoin = AddStablecoinMethod();
            var middleware = CreateMiddleware(_ => Task.CompletedTask);

            var context = CreateContext(ToolCall(Tool));
            await middleware.InvokeAsync(context);

            Assert.AreEqual(StatusCodes.Status402PaymentRequired, context.Response.StatusCode);
            Assert.AreEqual(2, context.Response.Headers.WWWAuthenticate.Count);

            var challenges = (ReadResponse(context)["error"]!["data"]!["challenges"] as JsonArray)!;
            Assert.AreEqual(2, challenges.Count);
            Assert.AreEqual("challenge-1", challenges[0]!["id"]!.GetValue<string>());
            Assert.AreEqual("challenge-2", challenges[1]!["id"]!.GetValue<string>());
            stablecoin.Verify(p => p.CreateChallenge(It.IsAny<string>(), Tool, It.IsAny<ToolPrice>()), Times.Once);
        }

        [TestMethod]
        public async Task Middleware_SettlesACredentialWithTheMethodItNames()
        {
            var stablecoin = AddStablecoinMethod();
            stablecoin.Setup(p => p.VerifyAndSettleAsync(It.IsAny<JsonObject>(), It.IsAny<string>(), Tool, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PaymentVerificationResult.Success(new JsonObject { ["status"] = "success" }, "0xabc"));
            string? payer = null;
            var middleware = CreateMiddleware(context =>
            {
                payer = context.Items[MppPaymentMiddleware.PayerItemKey] as string;
                return Task.CompletedTask;
            });

            var credential = new JsonObject
            {
                ["challenge"] = new JsonObject { ["method"] = "tempo" },
                ["payload"] = new JsonObject { ["type"] = "hash", ["hash"] = "0xabc" }
            };
            await middleware.InvokeAsync(CreateContext(ToolCall(Tool), credential: credential));

            Assert.AreEqual("mpp:0xabc", payer);
            _payment.Verify(
                p => p.VerifyAndSettleAsync(It.IsAny<JsonObject>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Never,
                "A stablecoin credential must not be handed to the card payment method.");
        }

        /// <summary>Adds a second payment method, as configuring the stablecoin deposit address does.</summary>
        private Mock<IMppPaymentService> AddStablecoinMethod()
        {
            _payment.SetupGet(p => p.Method).Returns("stripe");

            var stablecoin = new Mock<IMppPaymentService>();
            stablecoin.SetupGet(p => p.Method).Returns("tempo");
            stablecoin.Setup(p => p.CreateChallenge(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ToolPrice>()))
                .Returns(new PaymentChallenge
                {
                    Id = "challenge-2",
                    Realm = "localhost",
                    Method = "tempo",
                    Request = new JsonObject { ["amount"] = "250000" },
                    Expires = "2099-01-01T00:00:00Z"
                });
            stablecoin.Setup(p => p.FormatChallengeHeader(It.IsAny<PaymentChallenge>()))
                .Returns("Payment id=\"challenge-2\", realm=\"localhost\", method=\"tempo\", intent=\"charge\", request=\"eyJ9\"");
            stablecoin.Setup(p => p.ToJsonRpcChallenge(It.IsAny<PaymentChallenge>()))
                .Returns(new JsonObject { ["id"] = "challenge-2", ["method"] = "tempo" });

            var services = new ServiceCollection();
            services.AddSingleton(_cache);
            services.AddSingleton<IMppSubscriberResolver>(new ApiKeySubscriberResolver(_cache));
            services.AddSingleton(_pricing.Object);
            services.AddSingleton(_payment.Object);
            services.AddSingleton(stablecoin.Object);
            _services = services.BuildServiceProvider();
            return stablecoin;
        }
    }
}
