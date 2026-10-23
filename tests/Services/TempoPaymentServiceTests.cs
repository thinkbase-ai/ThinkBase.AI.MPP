using ThinkBase.AI.MPP.Models;
using ThinkBase.AI.MPP.Services;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Stripe;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace ThinkBase.AI.MPP.Tests.Services
{
    [TestClass]
    public class TempoPaymentServiceTests
    {
        private const string Realm = "mcp.scientio.com";
        private const string Tool = "FindLocation";
        private const string Deposit = "0x742d35Cc6634C0532925a3b844Bc9e7595f8fE00";
        private const string Token = "0x20c0000000000000000000000000000000000000";
        private const string Hash = "0x1a2b3c4d5e6f7890abcdef1234567890abcdef1234567890abcdef1234567890";
        private const string TransferTopic = "0xddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef";
        private const string TransferWithMemoTopic = "0x57bc7354aa85aed339e000bccffabbc529466af35f0772c8f8ee1145927de7f0";
        private const string Memo = "0xef1ed712011ece072f76bd8b82350e00000000000000000000eb18f2d4402269";

        private IDistributedCache _cache = null!;
        private List<string> _stripeRequests = null!;

        [TestInitialize]
        public void Initialize()
        {
            _cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
            _stripeRequests = [];
        }

        private static MppOptions Configured() => new()
        {
            ChallengeSecret = "test-secret",
            Tempo = new TempoOptions
            {
                DepositAddress = Deposit,
                TokenAddress = Token,
                ChainId = 42431,
                RpcUrl = "https://rpc.example/tempo",
                ReceiptWaitSeconds = 1
            }
        };

        private TempoPaymentService CreateService(string? rpcAnswer = null, MppOptions? options = null) =>
            CreateService([rpcAnswer ?? """{"jsonrpc":"2.0","id":1,"result":null}"""], options);

        /// <summary>A service whose node answers with each of <paramref name="rpcAnswers"/> in turn, then repeats the last.</summary>
        private TempoPaymentService CreateService(string[] rpcAnswers, MppOptions? options = null)
        {
            var handler = new StubHandler(rpcAnswers);
            var factory = new Mock<IHttpClientFactory>();
            factory.Setup(f => f.CreateClient(TempoPaymentService.HttpClientName)).Returns(() => new HttpClient(handler));

            return new TempoPaymentService(
                factory.Object,
                CreateStripeClient(),
                _cache,
                Options.Create(options ?? Configured()),
                new Mock<ILogger<TempoPaymentService>>().Object);
        }

        /// <summary>A Stripe client answered by the stub rather than by the network.</summary>
        private StripeClient CreateStripeClient() =>
            new("sk_test_0000000000", httpClient: new SystemNetHttpClient(new HttpClient(new StripeStubHandler(_stripeRequests))));

        private static ToolPrice Price(decimal cents = 25m) => new()
        {
            ToolName = Tool,
            AmountCents = cents,
            Currency = "usd",
            Description = "One location lookup."
        };

        private static JsonObject Credential(PaymentChallenge challenge, string type = "hash", string hash = Hash)
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
                ["payload"] = new JsonObject { ["type"] = type, ["hash"] = hash }
            };
        }

        /// <summary>A JSON-RPC answer holding a receipt that transfers <paramref name="units"/> to the deposit address.</summary>
        private static string Receipt(string units = "0x3d090", string status = "0x1", string? recipient = null, string? token = null) =>
            ReceiptOf(status, TransferLog(units, recipient, token));

        /// <summary>A JSON-RPC answer holding a receipt carrying exactly the supplied logs.</summary>
        private static string ReceiptOf(string status, params JsonObject[] logs) =>
            new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["result"] = new JsonObject
                {
                    ["status"] = status,
                    ["transactionHash"] = Hash,
                    ["logs"] = new JsonArray(logs.Cast<JsonNode>().ToArray())
                }
            }.ToJsonString();

        /// <summary>
        /// One transfer log of the token: the plain <c>Transfer</c> event, or the
        /// <c>TransferWithMemo</c> one a transfer that carries a memo emits beside it.
        /// </summary>
        private static JsonObject TransferLog(string units = "0x3d090", string? recipient = null, string? token = null, bool withMemo = false)
        {
            var topics = new JsonArray(
                withMemo ? TransferWithMemoTopic : TransferTopic,
                "0x000000000000000000000000a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2",
                "0x000000000000000000000000" + (recipient ?? Deposit)[2..].ToLowerInvariant());

            if (withMemo)
            {
                topics.Add(Memo);
            }

            return new JsonObject
            {
                ["address"] = token ?? Token,
                ["topics"] = topics,
                ["data"] = units
            };
        }

        [TestMethod]
        public void CreateChallenge_AsksForTheStablecoinPriceAtTheDepositAddress()
        {
            var challenge = CreateService().CreateChallenge(Realm, Tool, Price());

            Assert.AreEqual("tempo", challenge.Method);
            Assert.AreEqual("charge", challenge.Intent);
            // 25 cents of a six decimal TIP-20 token is 250,000 base units.
            Assert.AreEqual("250000", challenge.Request["amount"]!.GetValue<string>());
            Assert.AreEqual(Token, challenge.Request["currency"]!.GetValue<string>());
            Assert.AreEqual(Deposit, challenge.Request["recipient"]!.GetValue<string>());

            var details = (challenge.Request["methodDetails"] as JsonObject)!;
            Assert.AreEqual(42431L, details["chainId"]!.GetValue<long>());
            Assert.AreEqual("push", details["supportedModes"]![0]!.GetValue<string>());
        }

        [TestMethod]
        public void CreateChallenge_NeverAsksForLessThanTheSmallestTransferThatIsWorthMaking()
        {
            var challenge = CreateService().CreateChallenge(Realm, Tool, Price(0.1m));

            Assert.AreEqual("10000", challenge.Request["amount"]!.GetValue<string>());
        }

        [TestMethod]
        public async Task VerifyAndSettle_AcceptsATransferThatPaysTheDepositAddress()
        {
            var service = CreateService(Receipt());
            var challenge = service.CreateChallenge(Realm, Tool, Price());

            var result = await service.VerifyAndSettleAsync(Credential(challenge), Realm, Tool);

            Assert.IsTrue(result.Succeeded, result.Detail);
            Assert.AreEqual(Hash, result.Reference);
            Assert.AreEqual("tempo", result.Receipt!["method"]!.GetValue<string>());
            Assert.AreEqual("success", result.Receipt!["status"]!.GetValue<string>());
        }

        [TestMethod]
        public async Task VerifyAndSettle_AcceptsATransferThatCarriesAnAttributionMemo()
        {
            // A payer attaches a memo naming the challenge, so the token emits the plain Transfer
            // event and the TransferWithMemo one for the single movement of funds.
            var service = CreateService(ReceiptOf("0x1", TransferLog(), TransferLog(withMemo: true)));
            var challenge = service.CreateChallenge(Realm, Tool, Price());

            var result = await service.VerifyAndSettleAsync(Credential(challenge), Realm, Tool);

            Assert.IsTrue(result.Succeeded, result.Detail);
        }

        [TestMethod]
        public async Task VerifyAndSettle_AcceptsATransferKnownOnlyByItsMemoEvent()
        {
            var service = CreateService(ReceiptOf("0x1", TransferLog(withMemo: true)));
            var challenge = service.CreateChallenge(Realm, Tool, Price());

            var result = await service.VerifyAndSettleAsync(Credential(challenge), Realm, Tool);

            Assert.IsTrue(result.Succeeded, result.Detail);
        }

        [TestMethod]
        public async Task VerifyAndSettle_CountsThePairOfEventsOfOneMemoTransferOnce()
        {
            // Half the price twice over, as the two events of one transfer of half the price: the
            // funds moved once, so the challenge has not been paid for.
            var service = CreateService(ReceiptOf("0x1", TransferLog("0x3d090"), TransferLog("0x3d090", withMemo: true)));
            var challenge = service.CreateChallenge(Realm, Tool, Price(50m));

            var result = await service.VerifyAndSettleAsync(Credential(challenge), Realm, Tool);

            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual("verification-failed", result.Reason);
        }

        [TestMethod]
        public async Task VerifyAndSettle_WaitsForANodeThatHasNotCaughtUpWithTheTransfer()
        {
            // The payer presents the hash as soon as its own node has the transfer; a payer told to
            // retry pays again rather than presenting the same transfer, so the wait is what keeps
            // one call from being paid for twice.
            var service = CreateService(["""{"jsonrpc":"2.0","id":1,"result":null}""", Receipt()]);
            var challenge = service.CreateChallenge(Realm, Tool, Price());

            var result = await service.VerifyAndSettleAsync(Credential(challenge), Realm, Tool);

            Assert.IsTrue(result.Succeeded, result.Detail);
        }

        [TestMethod]
        public async Task VerifyAndSettle_RecordsTheSettledTransferInStripe()
        {
            var service = CreateService(Receipt());
            var challenge = service.CreateChallenge(Realm, Tool, Price());

            Assert.IsTrue((await service.VerifyAndSettleAsync(Credential(challenge), Realm, Tool)).Succeeded);

            Assert.AreEqual(1, _stripeRequests.Count);
            var recorded = Uri.UnescapeDataString(_stripeRequests[0]).Replace('+', ' ');
            // 250,000 base units of a six decimal token are the 25 cents Stripe records.
            StringAssert.Contains(recorded, "amount=25");
            StringAssert.Contains(recorded, "payment_method_options[crypto][mode]=transaction_verification");
            StringAssert.Contains(recorded, "[transaction_verification_options][network]=tempo");
            StringAssert.Contains(recorded, "[transaction_verification_options][transaction_hash]=" + Hash);
        }

        [TestMethod]
        public async Task VerifyAndSettle_RefusesATransferThatHasAlreadyPaidForACall()
        {
            var service = CreateService(Receipt());
            var first = service.CreateChallenge(Realm, Tool, Price());
            Assert.IsTrue((await service.VerifyAndSettleAsync(Credential(first), Realm, Tool)).Succeeded);

            // A second challenge, paid for with the same transfer.
            await Task.Delay(1100);
            var second = service.CreateChallenge(Realm, Tool, Price());
            var result = await service.VerifyAndSettleAsync(Credential(second), Realm, Tool);

            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual("verification-failed", result.Reason);
        }

        [TestMethod]
        public async Task VerifyAndSettle_RefusesATransferThatPaysTooLittle()
        {
            var service = CreateService(Receipt("0x1388"));
            var challenge = service.CreateChallenge(Realm, Tool, Price());

            var result = await service.VerifyAndSettleAsync(Credential(challenge), Realm, Tool);

            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual("verification-failed", result.Reason);
        }

        [TestMethod]
        public async Task VerifyAndSettle_RefusesATransferToAnotherAddress()
        {
            var service = CreateService(Receipt(recipient: "0x00000000000000000000000000000000deadbeef"));
            var challenge = service.CreateChallenge(Realm, Tool, Price());

            var result = await service.VerifyAndSettleAsync(Credential(challenge), Realm, Tool);

            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual("verification-failed", result.Reason);
        }

        [TestMethod]
        public async Task VerifyAndSettle_RefusesATransferOfAnotherToken()
        {
            var service = CreateService(Receipt(token: "0x00000000000000000000000000000000deadbeef"));
            var challenge = service.CreateChallenge(Realm, Tool, Price());

            var result = await service.VerifyAndSettleAsync(Credential(challenge), Realm, Tool);

            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual("verification-failed", result.Reason);
        }

        [TestMethod]
        public async Task VerifyAndSettle_RefusesATransactionThatFailedOnChain()
        {
            var service = CreateService(Receipt(status: "0x0"));
            var challenge = service.CreateChallenge(Realm, Tool, Price());

            var result = await service.VerifyAndSettleAsync(Credential(challenge), Realm, Tool);

            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual("verification-failed", result.Reason);
        }

        [TestMethod]
        public async Task VerifyAndSettle_AsksTheClientToRetryWhileTheTransferIsNotOnChain()
        {
            var service = CreateService();
            var challenge = service.CreateChallenge(Realm, Tool, Price());

            var result = await service.VerifyAndSettleAsync(Credential(challenge), Realm, Tool);

            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual("payment-action-required", result.Reason);
        }

        [TestMethod]
        public async Task VerifyAndSettle_RejectsACredentialThatCarriesNoTransferHash()
        {
            var service = CreateService(Receipt());
            var challenge = service.CreateChallenge(Realm, Tool, Price());

            var result = await service.VerifyAndSettleAsync(Credential(challenge, type: "transaction", hash: "0x76f901"), Realm, Tool);

            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual("invalid-payload", result.Reason);
        }

        [TestMethod]
        public async Task VerifyAndSettle_RejectsAChallengeIssuedForAnotherTool()
        {
            var service = CreateService(Receipt());
            var challenge = service.CreateChallenge(Realm, Tool, Price());

            var result = await service.VerifyAndSettleAsync(Credential(challenge), Realm, "GetGraph");

            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual("invalid-challenge", result.Reason);
        }

        [TestMethod]
        public async Task VerifyAndSettle_RejectsAChallengeThisServerDidNotIssue()
        {
            var service = CreateService(Receipt());
            var challenge = service.CreateChallenge(Realm, Tool, Price());
            var credential = Credential(challenge);
            // Halve the price and keep the identifier of the original challenge.
            (credential["challenge"]!["request"] as JsonObject)!["amount"] = "125000";

            var result = await service.VerifyAndSettleAsync(credential, Realm, Tool);

            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual("invalid-challenge", result.Reason);
        }

        /// <summary>Answers every Stripe call with a succeeded PaymentIntent, keeping what was asked.</summary>
        private sealed class StripeStubHandler(List<string> requests) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content is null
                    ? string.Empty
                    : await request.Content.ReadAsStringAsync(cancellationToken);

                lock (requests)
                {
                    requests.Add(body);
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"id":"pi_123","object":"payment_intent","status":"succeeded"}""",
                        Encoding.UTF8,
                        "application/json")
                };
            }
        }

        /// <summary>Answers each request with the next JSON-RPC body, as a Tempo node would, repeating the last.</summary>
        private sealed class StubHandler(string[] answers) : HttpMessageHandler
        {
            private int _asked;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var answer = answers[Math.Min(Interlocked.Increment(ref _asked) - 1, answers.Length - 1)];
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(answer, Encoding.UTF8, "application/json")
                });
            }
        }
    }
}
