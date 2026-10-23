using ThinkBase.AI.MPP.Services;
using System.Text.Json.Nodes;

namespace ThinkBase.AI.MPP.Tests.Services
{
    [TestClass]
    public class JsonCanonicalizerTests
    {
        [TestMethod]
        public void Canonicalize_SortsMembersAndDropsWhitespace()
        {
            var json = JsonNode.Parse("""{ "currency": "usd", "amount": "25", "methodDetails": { "networkId": "profile_1", "metadata": {} } }""");

            Assert.AreEqual("{\"amount\":\"25\",\"currency\":\"usd\",\"methodDetails\":{\"metadata\":{},\"networkId\":\"profile_1\"}}",
                JsonCanonicalizer.Canonicalize(json));
        }

        [TestMethod]
        public void ToBase64Url_MatchesTheEncodingOfTheProtocol()
        {
            // The worked example of the specification: {"amount":"1000000"}
            Assert.AreEqual("eyJhbW91bnQiOiIxMDAwMDAwIn0",
                JsonCanonicalizer.ToBase64Url(JsonNode.Parse("""{"amount":"1000000"}""")));
        }

        [TestMethod]
        public void Canonicalize_EscapesOnlyWhatTheCanonicalFormRequires()
        {
            // RFC 8785 escapes the quotation mark, the backslash and the control characters and
            // nothing else: an apostrophe, an ampersand or an em dash is written as it stands, as
            // every client that canonicalises the challenge for itself writes it.
            var json = JsonNode.Parse("""{ "description": "the assistant\u0027s response & more <b> \u2014 \u0001\n", "a\u0027b": "x" }""");

            Assert.AreEqual("{\"a'b\":\"x\",\"description\":\"the assistant's response & more <b> \u2014 \\u0001\\n\"}",
                JsonCanonicalizer.Canonicalize(json));
        }

        [TestMethod]
        public void ToBase64Url_IsTheSameWhateverEscapingTheSameJsonWasWrittenWith()
        {
            // A payer decodes the request of a challenge to read the price and encodes it again
            // before echoing it; the bytes it hashes must be the ones this server hashed.
            var issued = JsonNode.Parse("""{"amount":"25","description":"the assistant's response"}""");
            var echoed = JsonNode.Parse("""{ "description": "the assistant\u0027s response", "amount": "25" }""");

            Assert.AreEqual(JsonCanonicalizer.ToBase64Url(issued), JsonCanonicalizer.ToBase64Url(echoed));
        }

        [TestMethod]
        public void Base64UrlDecode_ReadsValuesWithAndWithoutPadding()
        {
            Assert.AreEqual("{\"pi\":\"pi_123\"}", System.Text.Encoding.UTF8.GetString(JsonCanonicalizer.Base64UrlDecode("eyJwaSI6InBpXzEyMyJ9")!));
            Assert.AreEqual("{\"amount\":\"1000000\"}", System.Text.Encoding.UTF8.GetString(JsonCanonicalizer.Base64UrlDecode("eyJhbW91bnQiOiIxMDAwMDAwIn0")!));
            Assert.AreEqual("{\"amount\":\"1000000\"}", System.Text.Encoding.UTF8.GetString(JsonCanonicalizer.Base64UrlDecode("eyJhbW91bnQiOiIxMDAwMDAwIn0=")!));
        }

        [TestMethod]
        public void Base64UrlDecode_ReturnsNullForTextThatIsNotBase64Url()
        {
            Assert.IsNull(JsonCanonicalizer.Base64UrlDecode("not base64!"));
            Assert.IsNull(JsonCanonicalizer.Base64UrlDecode(null));
        }
    }
}
