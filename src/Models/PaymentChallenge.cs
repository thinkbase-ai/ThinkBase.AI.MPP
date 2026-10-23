using System.Text.Json.Nodes;

namespace ThinkBase.AI.MPP.Models
{
    /// <summary>
    /// A payment challenge as defined by the Machine Payments Protocol (MPP). It is handed to the
    /// caller in a 402 response, both as a <c>WWW-Authenticate: Payment</c> header and, for the MCP
    /// JSON-RPC transport, as a challenge object in the error data.
    /// </summary>
    public class PaymentChallenge
    {
        /// <summary>Identifier binding the challenge to its other fields; see <see cref="Services.MppPaymentService"/>.</summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>Protection space the challenge belongs to.</summary>
        public string Realm { get; set; } = string.Empty;

        /// <summary>Payment method identifier, lower case, for example <c>stripe</c>.</summary>
        public string Method { get; set; } = "stripe";

        /// <summary>Payment intent, for example <c>charge</c>.</summary>
        public string Intent { get; set; } = "charge";

        /// <summary>The payment request: amount, currency and method details.</summary>
        public JsonObject Request { get; set; } = new();

        /// <summary>When the challenge stops being payable, in RFC 3339 form.</summary>
        public string Expires { get; set; } = string.Empty;

        /// <summary>Server correlation data the client echoes back unchanged; holds the tool name.</summary>
        public string? Opaque { get; set; }

        /// <summary>Display only description of what is being paid for.</summary>
        public string? Description { get; set; }
    }

    /// <summary>The outcome of verifying and settling a payment credential.</summary>
    public class PaymentVerificationResult
    {
        public bool Succeeded { get; private init; }

        /// <summary>The receipt handed back to the caller when the payment succeeded.</summary>
        public JsonObject? Receipt { get; private init; }

        /// <summary>Identifier of the settled payment, used to attribute the usage of the call.</summary>
        public string? Reference { get; private init; }

        /// <summary>Problem type of a failure, for example <c>verification-failed</c>.</summary>
        public string? Reason { get; private init; }

        /// <summary>Human readable explanation of a failure.</summary>
        public string? Detail { get; private init; }

        public static PaymentVerificationResult Success(JsonObject receipt, string reference) =>
            new() { Succeeded = true, Receipt = receipt, Reference = reference };

        public static PaymentVerificationResult Failure(string reason, string detail) =>
            new() { Succeeded = false, Reason = reason, Detail = detail };
    }
}
