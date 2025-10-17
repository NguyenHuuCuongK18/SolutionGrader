using System.Text.Json;
using System.Xml.Linq;

namespace SolutionGrader.Legacy.Service

{
    public class DataCompare
    {
        public static bool CompareJson(string json1, string json2)
        {
            if (string.IsNullOrWhiteSpace(json1) && string.IsNullOrWhiteSpace(json2))
                return true;

            try
            {
                using var doc1 = JsonDocument.Parse(json1);
                using var doc2 = JsonDocument.Parse(json2);
                return CompareJsonElements(doc1.RootElement, doc2.RootElement);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CompareJson ERROR] {ex.Message}");
                return false;
            }
        }

        private static bool CompareJsonElements(JsonElement e1, JsonElement e2)
        {
            if (e1.ValueKind != e2.ValueKind)
                return false;

            switch (e1.ValueKind)
            {
                case JsonValueKind.Object:
                    var props1 = e1.EnumerateObject().OrderBy(p => p.Name).ToList();
                    var props2 = e2.EnumerateObject().OrderBy(p => p.Name).ToList();
                    if (props1.Count != props2.Count)
                        return false;
                    for (int i = 0; i < props1.Count; i++)
                    {
                        if (props1[i].Name != props2[i].Name)
                            return false;
                        if (!CompareJsonElements(props1[i].Value, props2[i].Value))
                            return false;
                    }
                    return true;

                case JsonValueKind.Array:
                    var arr1 = e1.EnumerateArray().ToList();
                    var arr2 = e2.EnumerateArray().ToList();
                    if (arr1.Count != arr2.Count)
                        return false;
                    for (int i = 0; i < arr1.Count; i++)
                        if (!CompareJsonElements(arr1[i], arr2[i]))
                            return false;
                    return true;

                case JsonValueKind.String:
                    return NormalizeData(e1.GetString()) == NormalizeData(e2.GetString());
                case JsonValueKind.Number:
                    return e1.GetDouble() == e2.GetDouble();
                case JsonValueKind.True:
                case JsonValueKind.False:
                    return e1.GetBoolean() == e2.GetBoolean();
                case JsonValueKind.Null:
                    return true;
                default:
                    return NormalizeData(e1.ToString()) == NormalizeData(e2.ToString());
            }
        }

        // So sánh XML với XML
        public static bool CompareXml(string xml1, string xml2)
        {
            if (xml1 == null || xml2 == null) { return false; }
            try
            {
                var dict1 = XmlToDictionary(XDocument.Parse(xml1).Root);
                var dict2 = XmlToDictionary(XDocument.Parse(xml2).Root);

                return CompareDictionaries(dict1, dict2);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[ERROR XML] " + ex.Message);
                return false;
            }
        }

        // Chuyển XML -> Dictionary
        private static Dictionary<string, object> XmlToDictionary(XElement element)
        {
            var dict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

            foreach (var child in element.Elements())
            {
                if (!child.HasElements)
                    dict[child.Name.LocalName] = child.Value.Trim();
                else
                    dict[child.Name.LocalName] = XmlToDictionary(child);
            }

            return dict;
        }

        // So sánh dictionary
        private static bool CompareDictionaries(Dictionary<string, object> d1, Dictionary<string, object> d2)
        {
            if (d1.Count != d2.Count) return false;

            foreach (var kv in d1)
            {
                if (!d2.ContainsKey(kv.Key)) return false;

                var v1 = kv.Value?.ToString()?.Trim();
                var v2 = d2[kv.Key]?.ToString()?.Trim();

                if (!string.Equals(v1, v2, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            return true;
        }
        public static string NormalizeData(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return string.Empty;

            // 1️⃣ Unescape Unicode (\u0027 -> ')
            string unescaped = System.Text.RegularExpressions.Regex.Unescape(input);

            // 2️⃣ Normalize smart quotes and dashes
            unescaped = unescaped
                .Replace("’", "'")
                .Replace("‘", "'")
                .Replace("“", "\"")
                .Replace("”", "\"")
                .Replace("–", "-")
                .Replace("—", "-");

            // 3️⃣ Try JSON canonicalization
            try
            {
                using var doc = JsonDocument.Parse(unescaped);
                return JsonSerializer.Serialize(doc, new JsonSerializerOptions
                {
                    WriteIndented = false,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                });
            }
            catch
            {
                // Not JSON, continue
            }

            // 4️⃣ Collapse whitespace
            unescaped = System.Text.RegularExpressions.Regex.Replace(unescaped, @"\s+", " ");

            return unescaped.Trim();
        }

    }

}
