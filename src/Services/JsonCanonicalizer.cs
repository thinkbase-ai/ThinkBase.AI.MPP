using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ThinkBase.AI.MPP.Services
{
    /// <summary>
    /// Serialises JSON the way the Machine Payments Protocol requires: RFC 8785 canonical JSON
    /// (object members sorted by their UTF-16 code units, no insignificant whitespace) encoded with
    /// base64url without padding. The challenge identifier is a hash of this form, so both the
    /// server and the paying client must produce exactly the same bytes.
    /// </summary>
    public static class JsonCanonicalizer
    {
        /// <summary>The canonical JSON text of the supplied node.</summary>
        public static string Canonicalize(JsonNode? node)
        {
            var builder = new StringBuilder();
            Write(node, builder);
            return builder.ToString();
        }

        /// <summary>Base64url, without padding, of the canonical JSON text of the supplied node.</summary>
        public static string ToBase64Url(JsonNode? node) => Base64UrlEncode(Encoding.UTF8.GetBytes(Canonicalize(node)));

        public static string Base64UrlEncode(byte[] bytes) =>
            Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        /// <summary>Decodes base64url, with or without padding; null when the text is not valid base64url.</summary>
        public static byte[]? Base64UrlDecode(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            var padded = text.Replace('-', '+').Replace('_', '/');
            padded = (padded.Length % 4) switch
            {
                2 => padded + "==",
                3 => padded + "=",
                0 => padded,
                _ => string.Empty
            };

            if (padded.Length == 0)
            {
                return null;
            }

            try
            {
                return Convert.FromBase64String(padded);
            }
            catch (FormatException)
            {
                return null;
            }
        }

        private static void Write(JsonNode? node, StringBuilder builder)
        {
            switch (node)
            {
                case null:
                    builder.Append("null");
                    break;
                case JsonObject obj:
                    builder.Append('{');
                    var first = true;
                    foreach (var property in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                    {
                        if (!first)
                        {
                            builder.Append(',');
                        }

                        first = false;
                        WriteString(property.Key, builder);
                        builder.Append(':');
                        Write(property.Value, builder);
                    }

                    builder.Append('}');
                    break;
                case JsonArray array:
                    builder.Append('[');
                    for (var i = 0; i < array.Count; i++)
                    {
                        if (i > 0)
                        {
                            builder.Append(',');
                        }

                        Write(array[i], builder);
                    }

                    builder.Append(']');
                    break;
                default:
                    if (node is JsonValue value && value.TryGetValue<string>(out var text))
                    {
                        WriteString(text, builder);
                        break;
                    }

                    builder.Append(node.ToJsonString());
                    break;
            }
        }

        /// <summary>
        /// Writes a JSON string the way RFC 8785 — and so every client that canonicalises the
        /// challenge for itself — requires it: only the quotation mark, the backslash and the
        /// control characters are escaped, everything else, an apostrophe or an em dash alike, is
        /// written as it stands. The default escaping of <see cref="JsonSerializer"/> is far more
        /// aggressive (it escapes <c>'</c>, <c>&amp;</c>, <c>&lt;</c>, <c>&gt;</c>, <c>+</c> and
        /// everything above ASCII), which would give a different hash for the same JSON.
        /// </summary>
        private static void WriteString(string value, StringBuilder builder)
        {
            builder.Append('"');
            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                switch (character)
                {
                    case '"':
                        builder.Append("\\\"");
                        break;
                    case '\\':
                        builder.Append("\\\\");
                        break;
                    case '\b':
                        builder.Append("\\b");
                        break;
                    case '\f':
                        builder.Append("\\f");
                        break;
                    case '\n':
                        builder.Append("\\n");
                        break;
                    case '\r':
                        builder.Append("\\r");
                        break;
                    case '\t':
                        builder.Append("\\t");
                        break;
                    default:
                        if (character < ' ' || IsUnpairedSurrogate(value, index))
                        {
                            // A control character, or half of a surrogate pair with nothing to pair
                            // it with, is written as the escape the canonical form asks for.
                            builder.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            builder.Append(character);
                        }

                        break;
                }
            }

            builder.Append('"');
        }

        /// <summary>True when the character at <paramref name="index"/> is a surrogate without its pair.</summary>
        private static bool IsUnpairedSurrogate(string value, int index)
        {
            var character = value[index];
            if (char.IsHighSurrogate(character))
            {
                return index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1]);
            }

            return char.IsLowSurrogate(character) && (index == 0 || !char.IsHighSurrogate(value[index - 1]));
        }
    }
}
