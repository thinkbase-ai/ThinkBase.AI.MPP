using ThinkBase.AI.MPP.Interfaces;
using ThinkBase.AI.MPP.Models;
using ThinkBase.AI.MPP.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace ThinkBase.AI.MPP.Tests.Services
{
    [TestClass]
    public class ToolPricingServiceTests
    {
        private static ToolPricingService CreateService(MppOptions? options = null)
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
            return new ToolPricingService(
                configuration,
                new AssemblyToolCatalog(typeof(TestTools).Assembly),
                Options.Create(options ?? new MppOptions()),
                new Mock<ILogger<ToolPricingService>>().Object);
        }

        [TestMethod]
        public async Task GetPrice_FallsBackToTheDefaultPriceWhenMongoIsUnavailable()
        {
            var service = CreateService(new MppOptions { DefaultAmountCents = 7m, Currency = "gbp" });

            var price = await service.GetPriceAsync("FindLocation");

            Assert.AreEqual(7m, price.AmountCents);
            Assert.AreEqual("gbp", price.Currency);
            Assert.AreEqual("FindLocation", price.ToolName);
        }

        [TestMethod]
        public async Task GetSubscriberPrice_FallsBackToTheFixedSubscriberPrice()
        {
            var service = CreateService(new MppOptions { DefaultAmountCents = 25m, DefaultSubscriberAmountCents = 0.1m, Currency = "gbp" });

            var price = await service.GetSubscriberPriceAsync("FindLocation");

            Assert.AreEqual(0.1m, price.AmountCents);
            Assert.AreEqual(0.1m, price.SubscriberAmountCents);
            Assert.AreEqual("gbp", price.Currency);
            Assert.AreEqual("FindLocation", price.ToolName);
        }

        [TestMethod]
        public async Task GetSubscriberPrice_IsNeverNegative()
        {
            var service = CreateService(new MppOptions { DefaultSubscriberAmountCents = -1m });

            var price = await service.GetSubscriberPriceAsync("FindLocation");

            Assert.AreEqual(0m, price.AmountCents);
        }

        [TestMethod]
        public async Task GetPrices_IsEmptyWhenNoPricesCanBeRead()
        {
            var prices = await CreateService().GetPricesAsync();

            Assert.AreEqual(0, prices.Count);
        }

        [TestMethod]
        public void SeedDefaults_AreTheAgreedPrices()
        {
            Assert.AreEqual(50m, ToolPricingService.SeedAmountCents);
            Assert.AreEqual(0.1m, ToolPricingService.SeedSubscriberAmountCents);
            Assert.AreEqual("usd", ToolPricingService.SeedCurrency);
        }

        [TestMethod]
        public async Task SeedPrices_FailsWhenMongoIsNotConfigured()
        {
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => CreateService().SeedPricesAsync());
        }
    }
}
