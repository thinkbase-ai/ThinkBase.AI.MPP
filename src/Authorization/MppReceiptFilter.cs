using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.Text.Json.Nodes;

namespace ThinkBase.AI.MPP.Authorization
{
    /// <summary>
    /// Returns the receipt of a payment settled by <see cref="MppPaymentMiddleware"/> in the
    /// <c>_meta</c> of the tool result, which is where the MCP transport binding of the Machine
    /// Payments Protocol expects it. Calls that were not paid for are left untouched.
    /// </summary>
    public static class MppReceiptFilter
    {
        public static McpRequestFilter<CallToolRequestParams, CallToolResult> Create()
        {
            return next => async (request, cancellationToken) =>
            {
                var result = await next(request, cancellationToken);

                var httpContext = request.Services?.GetService<IHttpContextAccessor>()?.HttpContext;
                if (httpContext?.Items.TryGetValue(MppPaymentMiddleware.ReceiptItemKey, out var receipt) == true
                    && receipt is JsonObject json)
                {
                    result.Meta ??= new JsonObject();
                    result.Meta[MppPaymentMiddleware.ReceiptMetaKey] = JsonNode.Parse(json.ToJsonString());
                }

                return result;
            };
        }
    }
}
