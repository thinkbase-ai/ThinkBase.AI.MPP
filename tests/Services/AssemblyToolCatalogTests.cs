using ThinkBase.AI.MPP.Services;

namespace ThinkBase.AI.MPP.Tests.Services
{
    [TestClass]
    public class AssemblyToolCatalogTests
    {
        [TestMethod]
        public void GetTools_FindsEveryMcpToolOfTheNamedAssemblyWithItsDescription()
        {
            var tools = new AssemblyToolCatalog(typeof(TestTools).Assembly).GetTools();

            Assert.IsTrue(tools.ContainsKey("GetTimeLine"));
            Assert.IsFalse(string.IsNullOrWhiteSpace(tools["GetTimeLine"]));
            Assert.IsTrue(tools.ContainsKey("FindLocation"));
            Assert.IsFalse(tools.ContainsKey("FindLocationAsync"), "The tool name, not the method name, is used.");
            Assert.IsFalse(tools.ContainsKey("NotATool"), "A method that declares no tool is not priced.");
        }

        [TestMethod]
        public void GetTools_IsEmptyForAnAssemblyThatDeclaresNoTool()
        {
            var tools = new AssemblyToolCatalog(typeof(AssemblyToolCatalog).Assembly).GetTools();

            Assert.AreEqual(0, tools.Count);
        }
    }
}
