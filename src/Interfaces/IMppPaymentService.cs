using ThinkBase.AI.MPP.Models;
using System.Text.Json.Nodes;

namespace ThinkBase.AI.MPP.Interfaces
{
    /// <summary>
    /// Implements one payment method of the server half of the Machine Payments Protocol: it
    /// issues the challenge that accompanies a 402 response and verifies, then settles — through
    /// Stripe or as a stablecoin transfer — the credential the caller presents when it retries the
    /// call. One implementation is registered per payment method that is offered.
    /// </summary>
    public interface IMppPaymentService
    {
        /// <summary>Payment method identifier, lower case, the challenges are issued for.</summary>
        string Method { get; }

        /// <summary>Builds a challenge for one call of the named tool at the supplied price.</summary>
        PaymentChallenge CreateChallenge(string realm, string toolName, ToolPrice price);

        /// <summary>The <c>WWW-Authenticate</c> field value carrying the challenge.</summary>
        string FormatChallengeHeader(PaymentChallenge challenge);

        /// <summary>The challenge as the MCP JSON-RPC transport carries it, with a native request object.</summary>
        JsonObject ToJsonRpcChallenge(PaymentChallenge challenge);

        /// <summary>
        /// Verifies that the credential answers a challenge this server issued for this tool with
        /// this payment method and, when it does, takes the payment.
        /// </summary>
        Task<PaymentVerificationResult> VerifyAndSettleAsync(JsonObject credential, string realm, string toolName, CancellationToken cancellationToken = default);
    }
}
