using System.Text.Json.Nodes;

namespace ThinkBase.AI.MPP.Interfaces
{
    /// <summary>
    /// Builds the discovery document the Machine Payments Protocol asks a paid server to serve at
    /// <c>/openapi.json</c>: an OpenAPI description of the endpoints, with the price of each paid
    /// one carried in the <c>x-payment-info</c> extension.
    /// </summary>
    public interface IMppDiscoveryService
    {
        /// <summary>
        /// The discovery document for a server reached at <paramref name="serverUrl"/>, whose
        /// payment challenges are issued for the <paramref name="realm"/> protection space.
        /// </summary>
        Task<JsonObject> BuildDocumentAsync(string serverUrl, string realm, CancellationToken cancellationToken = default);
    }
}
