using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Reflection;
using ThinkBase.AI.MPP.Interfaces;

namespace ThinkBase.AI.MPP.Services
{
    /// <summary>
    /// The tools an MCP server declares, found the way the server itself finds them with
    /// <c>WithToolsFromAssembly</c>: the methods marked with <see cref="McpServerToolAttribute"/>
    /// of the types marked with <see cref="McpServerToolTypeAttribute"/>. The assemblies are those
    /// the application names, defaulting to the one it was started from.
    /// </summary>
    public class AssemblyToolCatalog : IMppToolCatalog
    {
        private readonly Assembly[] _assemblies;

        public AssemblyToolCatalog(params Assembly[] assemblies)
        {
            _assemblies = assemblies.Length > 0
                ? assemblies
                : [Assembly.GetEntryAssembly() ?? typeof(AssemblyToolCatalog).Assembly];
        }

        public IReadOnlyDictionary<string, string> GetTools() => GetTools(_assemblies);

        /// <summary>The name and description of every MCP tool declared by the named assemblies.</summary>
        public static IReadOnlyDictionary<string, string> GetTools(params Assembly[] assemblies)
        {
            var tools = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var methods = assemblies
                .SelectMany(assembly => assembly.GetTypes())
                .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() != null)
                .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static));

            foreach (var method in methods)
            {
                var tool = method.GetCustomAttribute<McpServerToolAttribute>();
                if (tool == null)
                {
                    continue;
                }

                var name = tool.Name ?? method.Name;
                tools[name] = method.GetCustomAttribute<DescriptionAttribute>()?.Description ?? string.Empty;
            }

            return tools;
        }
    }
}
