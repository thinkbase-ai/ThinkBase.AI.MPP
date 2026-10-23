using ThinkBase.AI.MPP.Models;

namespace ThinkBase.AI.MPP.Tests.Models
{
    /// <summary>
    /// The Stripe machine payments sample asks for three settings only: the Stripe secret key, the
    /// Stripe profile and the Tempo deposit address. These cover what is derived from the first of
    /// them, so that the other two are all the configuration this server needs as well.
    /// </summary>
    [TestClass]
    public class MppOptionsTests
    {
        private const string Deposit = "0x742d35Cc6634C0532925a3b844Bc9e7595f8fE00";

        [TestMethod]
        public void ApplyStripeDefaults_DerivesTheChallengeSecretFromTheStripeKey()
        {
            var options = new MppOptions();

            options.ApplyStripeDefaults("sk_test_0000000000");

            Assert.IsFalse(string.IsNullOrWhiteSpace(options.ChallengeSecret));

            // Every instance of the server derives the same secret, so a challenge issued by one
            // can be paid at another and survives a restart.
            var other = new MppOptions();
            other.ApplyStripeDefaults("sk_test_0000000000");
            Assert.AreEqual(options.ChallengeSecret, other.ChallengeSecret);

            var another = new MppOptions();
            another.ApplyStripeDefaults("sk_test_1111111111");
            Assert.AreNotEqual(options.ChallengeSecret, another.ChallengeSecret);
        }

        [TestMethod]
        public void ApplyStripeDefaults_KeepsAConfiguredChallengeSecret()
        {
            var options = new MppOptions { ChallengeSecret = "configured" };

            options.ApplyStripeDefaults("sk_test_0000000000");

            Assert.AreEqual("configured", options.ChallengeSecret);
        }

        [TestMethod]
        public void ApplyStripeDefaults_PutsATestKeyOnTheTempoTestNetwork()
        {
            var options = new MppOptions { Tempo = { DepositAddress = Deposit } };

            options.ApplyStripeDefaults("sk_test_0000000000");

            Assert.IsTrue(options.Tempo.IsConfigured);
            Assert.AreEqual(TempoOptions.TestnetChainId, options.Tempo.ChainId);
            Assert.AreEqual(TempoOptions.TestnetRpcUrl, options.Tempo.RpcUrl);
            Assert.AreEqual(TempoOptions.TestnetTokenAddress, options.Tempo.TokenAddress);
        }

        [TestMethod]
        public void ApplyStripeDefaults_PutsALiveKeyOnTempoMainnet()
        {
            var options = new MppOptions { Tempo = { DepositAddress = Deposit } };

            options.ApplyStripeDefaults("sk_live_0000000000");

            Assert.AreEqual(TempoOptions.MainnetChainId, options.Tempo.ChainId);
            Assert.AreEqual(TempoOptions.MainnetRpcUrl, options.Tempo.RpcUrl);
            Assert.AreEqual(TempoOptions.MainnetTokenAddress, options.Tempo.TokenAddress);
        }

        [TestMethod]
        public void ApplyStripeDefaults_KeepsAConfiguredTempoNetwork()
        {
            var options = new MppOptions
            {
                Tempo =
                {
                    DepositAddress = Deposit,
                    ChainId = TempoOptions.MainnetChainId,
                    RpcUrl = "https://rpc.example/tempo",
                    TokenAddress = "0x20c0000000000000000000000000000000000001"
                }
            };

            options.ApplyStripeDefaults("sk_test_0000000000");

            Assert.AreEqual(TempoOptions.MainnetChainId, options.Tempo.ChainId);
            Assert.AreEqual("https://rpc.example/tempo", options.Tempo.RpcUrl);
            Assert.AreEqual("0x20c0000000000000000000000000000000000001", options.Tempo.TokenAddress);
        }

        [TestMethod]
        public void Tempo_IsNotConfiguredWithoutADepositAddress()
        {
            var options = new MppOptions();

            options.ApplyStripeDefaults("sk_test_0000000000");

            Assert.IsFalse(options.Tempo.IsConfigured);
        }
    }
}
