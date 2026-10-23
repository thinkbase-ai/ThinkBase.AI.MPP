using ModelContextProtocol.Server;

namespace ThinkBase.AI.MPP.Tests
{
    /// <summary>
    /// A couple of MCP tools declared the way a server declares them, so that the catalogue, the
    /// prices and the discovery document can be tested without a server to host them.
    /// </summary>
    [McpServerToolType]
    public static class TestTools
    {
        [McpServerTool(Name = "FindLocation")]
        [System.ComponentModel.Description("Finds a place by name.")]
        public static string FindLocationAsync(string name) => name;

        [McpServerTool]
        [System.ComponentModel.Description("Returns the timeline of a knowledge base.")]
        public static string GetTimeLine() => string.Empty;

        /// <summary>Not a tool, so that the catalogue is seen to leave plain methods out.</summary>
        public static string NotATool() => string.Empty;
    }
}
