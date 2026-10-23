using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Reflection;
using ThinkBase.AI.MPP.Authorization;
using ThinkBase.AI.MPP.Interfaces;
using ThinkBase.AI.MPP.Models;
using ThinkBase.AI.MPP.Services;

namespace ThinkBase.AI.MPP
{
    /// <summary>
    /// Registers the server side of the Machine Payments Protocol in an ASP.NET Core MCP server.
    ///
    /// The application still owns three things, because they are its own: the Stripe client the
    /// card payments are settled with (<c>AddStripe</c>), the way a subscriber is recognised
    /// (<see cref="IMppSubscriberResolver"/>) and the distributed cache settled challenges are
    /// remembered in.
    /// </summary>
    public static class MppServiceCollectionExtensions
    {
        /// <summary>
        /// Adds the pricing, the payment methods and the discovery document of the protocol, bound
        /// from the <c>Mpp</c> configuration section. The tools that are priced are those declared
        /// by <paramref name="toolAssemblies"/>, which defaults to the assembly the application was
        /// started from, as <c>WithToolsFromAssembly</c> does.
        /// </summary>
        public static IServiceCollection AddMachinePayments(
            this IServiceCollection services,
            IConfiguration configuration,
            params Assembly[] toolAssemblies)
        {
            services.Configure<MppOptions>(configuration.GetSection(MppOptions.SectionName));

            // Stripe settles both payment rails, so the Stripe secret key is all they need: the
            // challenge signing secret is derived from it and its mode picks the Tempo network.
            var stripeSecretKey = configuration.GetValue<string>("Stripe:secret_key");
            services.PostConfigure<MppOptions>(options => options.ApplyStripeDefaults(stripeSecretKey));

            services.TryAddSingleton<IMppToolCatalog>(new AssemblyToolCatalog(toolAssemblies));
            services.TryAddSingleton<IToolPricingService, ToolPricingService>();
            services.AddScoped<IMppPaymentService, MppPaymentService>();

            // The discovery document, served at /openapi.json, is built from the same prices and
            // payment methods the 402 challenges are, so it is registered beside them.
            services.TryAddScoped<IMppDiscoveryService, MppDiscoveryService>();

            // Stablecoin payments are offered as a second payment method whenever a Tempo deposit
            // address has been configured; the chain, the node and the token have defaults.
            var tempo = configuration.GetSection($"{MppOptions.SectionName}:{nameof(MppOptions.Tempo)}").Get<TempoOptions>();
            if (tempo?.IsConfigured == true)
            {
                services.AddHttpClient(TempoPaymentService.HttpClientName).AddStandardResilienceHandler();
                services.AddScoped<IMppPaymentService, TempoPaymentService>();
            }

            return services;
        }

        /// <summary>
        /// Answers an MCP tool call that carries neither a subscriber's credentials nor a payment
        /// with a 402 payment challenge. It has to run before the MCP endpoint is mapped.
        /// </summary>
        public static IApplicationBuilder UseMachinePayments(this IApplicationBuilder app) =>
            app.UseMiddleware<MppPaymentMiddleware>();
    }
}
