namespace ThinkBase.AI.MPP.Interfaces
{
    /// <summary>
    /// The MCP tools of the server the prices are for. The library prices and advertises the tools
    /// of the application that hosts it, not its own, so the catalogue is supplied rather than
    /// discovered: <see cref="Services.AssemblyToolCatalog"/> reads it from the assemblies the MCP
    /// server takes its tools from.
    /// </summary>
    public interface IMppToolCatalog
    {
        /// <summary>The name of every declared tool, with its description, keyed without case.</summary>
        IReadOnlyDictionary<string, string> GetTools();
    }
}
