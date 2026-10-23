using ThinkBase.AI.MPP.Interfaces;
using ThinkBase.AI.MPP.Models;
using ThinkBase.AI.MPP.Services;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Stripe;
using System.Text.Json.Nodes;

namespace ThinkBase.AI.MPP.Tests.Services
{
    [TestClass]
    public class MppDiscoveryServiceTests
    {
        private const string Realm = "mcp.example.com";
        private const string ServerUrl = "https://mcp.example.com";
        private const string Title = "A paid MCP server";

        /// <summary>A tool that is only available to a subscriber, so it is priced at zero.</summary>
        private const string SubscriberOnlyTool = "GetTimeLine";

        private static MppDiscoveryService CreateService(MppOptions? options = null, bool withPayments = true)
        {
            var mppOptions = options ?? new MppOptions { ChallengeSecret = "test-secret", NetworkId = "acct_test", ServiceTitle = Title };

            var pricing = new Mock<IToolPricingService>();
            pricing
                .Setup(p => p.GetPriceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string tool, CancellationToken _) => new ToolPrice
                {
                    ToolName = tool,
                    AmountCents = string.Equals(tool, SubscriberOnlyTool, StringComparison.OrdinalIgnoreCase) ? 0m : 25m,
                    SubscriberAmountCents = 0.1m,
                    Currency = "usd"
                });

            var payments = new List<IMppPaymentService>();
            if (withPayments)
            {
                IDistributedCache cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
                payments.Add(new MppPaymentService(
                    new StripeClient("sk_test_0000000000"),
                    cache,
                    Options.Create(mppOptions),
                    new Mock<ILogger<MppPaymentService>>().Object));
            }

            return new MppDiscoveryService(
                pricing.Object,
                new AssemblyToolCatalog(typeof(TestTools).Assembly),
                payments,
                Options.Create(mppOptions),
                new Mock<ILogger<MppDiscoveryService>>().Object);
        }

        private static JsonArray Offers(JsonObject document, string path = "/") =>
            (JsonArray)((JsonObject)document["paths"]![path]!["post"]!["x-payment-info"]!)["offers"]!;

        [TestMethod]
        public async Task BuildDocument_IsAnOpenApiDocumentNamingTheServer()
        {
            var document = await CreateService().BuildDocumentAsync(ServerUrl + "/", Realm);

            Assert.AreEqual("3.1.0", document["openapi"]!.GetValue<string>());
            Assert.AreEqual(Title, document["info"]!["title"]!.GetValue<string>());
            Assert.IsFalse(string.IsNullOrWhiteSpace(document["info"]!["version"]!.GetValue<string>()));
            Assert.AreEqual(ServerUrl, document["servers"]![0]!["url"]!.GetValue<string>());
            Assert.AreEqual(ServerUrl + "/", document["x-service-info"]!["docs"]!["homepage"]!.GetValue<string>());
        }

        [TestMethod]
        public async Task BuildDocument_DescribesTheMcpEndpointAsAPaidOperation()
        {
            var document = await CreateService().BuildDocumentAsync(ServerUrl, Realm);

            var operation = (JsonObject)document["paths"]!["/"]!["post"]!;
            Assert.IsNotNull(operation["requestBody"]);
            Assert.IsNotNull(operation["responses"]!["200"]);
            Assert.IsNotNull(operation["responses"]!["402"], "A payable operation must declare its 402 response.");
            Assert.IsNotNull(operation["x-payment-info"]);
        }

        [TestMethod]
        public async Task BuildDocument_ExamplesTheMcpEndpointWithACallOfAPaidTool()
        {
            var document = await CreateService().BuildDocumentAsync(ServerUrl, Realm);

            var example = document["paths"]!["/"]!["post"]!["requestBody"]!["content"]!["application/json"]!["example"]!;
            Assert.AreEqual("tools/call", example["method"]!.GetValue<string>());

            var tool = example["params"]!["name"]!.GetValue<string>();
            Assert.IsFalse(string.IsNullOrWhiteSpace(tool));
            Assert.AreNotEqual(SubscriberOnlyTool, tool, "The example must call a tool that can be paid for.");
            Assert.IsTrue(
                Offers(document).Any(offer => string.Equals(offer!["tool"]!.GetValue<string>(), tool, StringComparison.Ordinal)),
                "The example must call a tool the endpoint offers a price for.");
        }

        [TestMethod]
        public async Task BuildDocument_OffersEveryPayableToolInBaseUnitsOfItsCurrency()
        {
            var document = await CreateService().BuildDocumentAsync(ServerUrl, Realm);

            var offers = Offers(document);
            Assert.IsTrue(offers.Count > 0);

            var offer = offers.OfType<JsonObject>().Single(o => o["tool"]!.GetValue<string>() == "FindLocation");
            Assert.AreEqual(MppOptions.IntentName, offer["intent"]!.GetValue<string>());
            Assert.AreEqual(MppOptions.MethodName, offer["method"]!.GetValue<string>());
            Assert.AreEqual("25", offer["amount"]!.GetValue<string>());
            Assert.AreEqual("usd", offer["currency"]!.GetValue<string>());
            Assert.IsFalse(string.IsNullOrWhiteSpace(offer["description"]!.GetValue<string>()));
        }

        [TestMethod]
        public async Task BuildDocument_LeavesOutAToolThatCannotBePaidFor()
        {
            var document = await CreateService().BuildDocumentAsync(ServerUrl, Realm);

            var tools = Offers(document).OfType<JsonObject>().Select(o => o["tool"]!.GetValue<string>()).ToList();

            Assert.IsFalse(tools.Contains(SubscriberOnlyTool), "A tool priced at zero is not offered for payment.");
        }

        [TestMethod]
        public async Task BuildDocument_FollowsTheConfiguredMcpPath()
        {
            var options = new MppOptions { ChallengeSecret = "test-secret", NetworkId = "acct_test", ServiceTitle = Title, McpPaths = ["/mcp"] };

            var document = await CreateService(options).BuildDocumentAsync(ServerUrl, Realm);

            Assert.IsNotNull(document["paths"]!["/mcp"]);
            Assert.IsNull(document["paths"]!["/"]);
        }

        [TestMethod]
        public async Task BuildDocument_AdvertisesNoPaymentWhenNoneIsOffered()
        {
            var document = await CreateService(withPayments: false).BuildDocumentAsync(ServerUrl, Realm);

            var operation = (JsonObject)document["paths"]!["/"]!["post"]!;
            Assert.IsNull(operation["x-payment-info"]);
            Assert.IsNull(operation["responses"]!["402"]);
        }

        [TestMethod]
        public async Task BuildDocument_AdvertisesNoPaymentWhenMppIsDisabled()
        {
            var options = new MppOptions { Enabled = false, ChallengeSecret = "test-secret", ServiceTitle = Title };

            var document = await CreateService(options).BuildDocumentAsync(ServerUrl, Realm);

            Assert.IsNull(((JsonObject)document["paths"]!["/"]!["post"]!)["x-payment-info"]);
        }

        [TestMethod]
        public async Task BuildDocument_DescribesTheFreeInformationalEndpoints()
        {
            var document = await CreateService().BuildDocumentAsync(ServerUrl, Realm);

            Assert.IsNotNull(document["paths"]!["/api/tools"]!["get"]);
            Assert.IsNotNull(document["paths"]!["/api/pricing/tools"]!["get"]);
        }
    }
}
