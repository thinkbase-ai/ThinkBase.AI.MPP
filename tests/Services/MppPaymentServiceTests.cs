using ThinkBase.AI.MPP.Models;
using ThinkBase.AI.MPP.Services;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Stripe;
using System.Text;
using System.Text.Json.Nodes;

namespace ThinkBase.AI.MPP.Tests.Services
{
    [TestClass]
    public class MppPaymentServiceTests
    {
        private const string Realm = "mcp.scientio.com";
        private const string Tool = "FindLocation";

        private static MppPaymentService CreateService(MppOptions? options = null)
        {
            IDistributedCache cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
            var logger = new Mock<ILogger<MppPaymentService>>();
            return new MppPaymentService(
                new StripeClient("sk_test_0000000000"),
                cache,
                Options.Create(options ?? new MppOptions { ChallengeSecret = "test-secret" }),
                logger.Object);
        }

        private static ToolPrice Price(decimal cents = 25m) => new()
        {
            ToolName = Tool,
            AmountCents = cents,
            Currency = "usd",
            Description = "One location lookup."
        };

        private static JsonObject Credential(PaymentChallenge challenge, string token = "spt_1N4Zv32eZvKYlo2CPhVPkJlW")
        {
            return new JsonObject
            {
                ["challenge"] = new JsonObject
                {
                    ["id"] = challenge.Id,
                    ["realm"] = challenge.Realm,
                    ["method"] = challenge.Method,
                    ["intent"] = challenge.Intent,
                    ["request"] = JsonNode.Parse(challenge.Request.ToJsonString()),
                    ["expires"] = challenge.Expires,
                    ["opaque"] = challenge.Opaque
                },
                ["payload"] = new JsonObject { ["spt"] = token }
            };
        }

        [TestMethod]
        public void CreateChallenge_PricesTheToolAndBindsTheToolName()
        {
            var challenge = CreateService().CreateChallenge(Realm, Tool, Price());

            Assert.AreEqual("25", challenge.Request["amount"]!.GetValue<string>());
            Assert.AreEqual("usd", challenge.Request["currency"]!.GetValue<string>());
            Assert.AreEqual("stripe", challenge.Method);
            Assert.AreEqual("charge", challenge.Intent);
            Assert.IsFalse(string.IsNullOrWhiteSpace(challenge.Id));

            var opaque = JsonCanonicalizer.Base64UrlDecode(challenge.Opaque);
            Assert.IsNotNull(opaque);
            Assert.AreEqual(Tool, (JsonNode.Parse(Encoding.UTF8.GetString(opaque!)) as JsonObject)!["tool"]!.GetValue<string>());
        }

        [TestMethod]
        public void CreateChallenge_NamesTheStripeProfileAndTheMethodsItAccepts()
        {
            var service = CreateService(new MppOptions { ChallengeSecret = "test-secret", NetworkId = "bnp_123" });

            var details = (service.CreateChallenge(Realm, Tool, Price()).Request["methodDetails"] as JsonObject)!;

            Assert.AreEqual("bnp_123", details["networkId"]!.GetValue<string>());
            Assert.AreEqual("card", details["paymentMethodTypes"]![0]!.GetValue<string>());
            Assert.AreEqual("link", details["paymentMethodTypes"]![1]!.GetValue<string>());
        }

        [TestMethod]
        public void FormatChallengeHeader_CarriesEveryRequiredParameter()
        {
            var service = CreateService();
            var challenge = service.CreateChallenge(Realm, Tool, Price());

            var header = service.FormatChallengeHeader(challenge);

            StringAssert.StartsWith(header, "Payment ");
            StringAssert.Contains(header, $"id=\"{challenge.Id}\"");
            StringAssert.Contains(header, $"realm=\"{Realm}\"");
            StringAssert.Contains(header, "method=\"stripe\"");
            StringAssert.Contains(header, "intent=\"charge\"");
            StringAssert.Contains(header, $"request=\"{JsonCanonicalizer.ToBase64Url(challenge.Request)}\"");
            StringAssert.Contains(header, $"opaque=\"{challenge.Opaque}\"");
        }

        [TestMethod]
        public void FormatChallengeHeader_EscapesWhatAnHttpHeaderCannotCarry()
        {
            // A description edited into the prices collection may hold an em dash or a curly
            // apostrophe, which no HTTP header may carry as it stands; a payer unescapes them
            // again, and the description is not part of what the identifier binds.
            var service = CreateService();
            var price = Price();
            price.Description = "Hold a chat with a KBase \u2014 the assistant\u2019s response.";

            var header = service.FormatChallengeHeader(service.CreateChallenge(Realm, Tool, price));

            StringAssert.Contains(header, "description=\"Hold a chat with a KBase \\u2014 the assistant\\u2019s response.\"");
            Assert.IsTrue(header.All(character => character >= ' ' && character <= '~'));
        }

        [TestMethod]
        public async Task VerifyAndSettle_RejectsAChallengeThisServerDidNotIssue()
        {
            var service = CreateService();
            var challenge = service.CreateChallenge(Realm, Tool, Price());
            var credential = Credential(challenge);
            // Halve the price and keep the identifier of the original challenge.
            (credential["challenge"]!["request"] as JsonObject)!["amount"] = "1";

            var result = await service.VerifyAndSettleAsync(credential, Realm, Tool);

            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual("invalid-challenge", result.Reason);
        }

        [TestMethod]
        public async Task VerifyAndSettle_RejectsAChallengeIssuedForAnotherTool()
        {
            var service = CreateService();
            var challenge = service.CreateChallenge(Realm, Tool, Price());

            var result = await service.VerifyAndSettleAsync(Credential(challenge), Realm, "GetGraph");

            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual("invalid-challenge", result.Reason);
        }

        [TestMethod]
        public async Task VerifyAndSettle_RejectsAnExpiredChallenge()
        {
            var service = CreateService(new MppOptions { ChallengeSecret = "test-secret", ChallengeLifetimeSeconds = 1 });
            var challenge = service.CreateChallenge(Realm, Tool, Price());
            await Task.Delay(1500);

            var result = await service.VerifyAndSettleAsync(Credential(challenge), Realm, Tool);

            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual("payment-expired", result.Reason);
        }

        [TestMethod]
        public async Task VerifyAndSettle_RejectsACredentialWithoutASharedPaymentToken()
        {
            var service = CreateService();
            var challenge = service.CreateChallenge(Realm, Tool, Price());

            var result = await service.VerifyAndSettleAsync(Credential(challenge, "pm_123"), Realm, Tool);

            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual("invalid-payload", result.Reason);
        }

        [TestMethod]
        public async Task VerifyAndSettle_RejectsACredentialThatCarriesNoChallenge()
        {
            var result = await CreateService().VerifyAndSettleAsync(new JsonObject(), Realm, Tool);

            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual("malformed-credential", result.Reason);
        }

        [TestMethod]
        public async Task VerifyAndSettle_AcceptsTheChallengeOfAnHttpClientAsBase64Url()
        {
            // An HTTP client echoes the request exactly as the WWW-Authenticate header carried it.
            var service = CreateService();
            var challenge = service.CreateChallenge(Realm, Tool, Price());
            var credential = Credential(challenge);
            credential["challenge"]!["request"] = JsonCanonicalizer.ToBase64Url(challenge.Request);

            var result = await service.VerifyAndSettleAsync(credential, Realm, "GetGraph");

            // The challenge itself is accepted; it is the tool that does not match.
            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual("invalid-challenge", result.Reason);
            Assert.AreEqual("The challenge was issued for another tool.", result.Detail);
        }

        [TestMethod]
        public async Task VerifyAndSettle_AcceptsAChallengeWhoseDescriptionCarriesAnApostrophe()
        {
            // The payer canonicalises the request itself before echoing it, so the challenge of a
            // tool whose description holds an apostrophe, an ampersand or a character above ASCII
            // has to be bound to the same bytes the payer writes.
            var service = CreateService();
            var price = Price();
            price.Description = "Sends a message and returns the assistant's response — & more.";
            var challenge = service.CreateChallenge(Realm, Tool, price);
            var credential = Credential(challenge);
            credential["challenge"]!["request"] = JsonCanonicalizer.ToBase64Url(challenge.Request);

            var result = await service.VerifyAndSettleAsync(credential, Realm, "GetGraph");

            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual("The challenge was issued for another tool.", result.Detail);
        }

        [TestMethod]
        public async Task VerifyAndSettle_AcceptsARequestThatWasEncodedAgainByThePayer()
        {
            // The same request, written with the member order and the escaping of another JSON
            // writer: it is the same challenge and still answers it.
            var service = CreateService();
            var price = Price();
            price.Description = "One location lookup; the caller's own.";
            var challenge = service.CreateChallenge(Realm, Tool, price);
            var credential = Credential(challenge);
            var reordered = new JsonObject();
            foreach (var member in challenge.Request.Reverse().ToList())
            {
                reordered[member.Key] = member.Value is null ? null : JsonNode.Parse(member.Value.ToJsonString());
            }

            credential["challenge"]!["request"] = JsonCanonicalizer.Base64UrlEncode(
                Encoding.UTF8.GetBytes(reordered.ToJsonString()));

            var result = await service.VerifyAndSettleAsync(credential, Realm, "GetGraph");

            Assert.AreEqual("The challenge was issued for another tool.", result.Detail);
        }
    }
}
