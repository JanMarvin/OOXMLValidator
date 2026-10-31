using System.IO.Compression;
using System.Text;
using System.Xml;

namespace OOXMLValidator;

static class Locator {
    const char Sep = '\n';

    public static string? KeyFromXPath(string? xpath, IXmlNamespaceResolver? resolver) {
        if (string.IsNullOrEmpty(xpath) || xpath == "/") return null;
        StringBuilder sb = new();
        foreach (string step in xpath.Split('/', StringSplitOptions.RemoveEmptyEntries)) {
            string name = step;
            int index = 1;
            int br = step.IndexOf('[');
            if (br >= 0) {
                if (!step.EndsWith(']') || !int.TryParse(step.AsSpan(br + 1, step.Length - br - 2), out index)) return null;
                name = step[..br];
            }
            int colon = name.IndexOf(':');
            string prefix = colon < 0 ? "" : name[..colon];
            string local = colon < 0 ? name : name[(colon + 1)..];
            string? ns = resolver?.LookupNamespace(prefix);
            if (ns == null) {
                if (prefix.Length > 0) return null;
                ns = "";
            }
            sb.Append(Sep).Append(ns).Append('|').Append(local).Append('[').Append(index).Append(']');
        }
        return sb.ToString();
    }

    public static string StartTag(XmlReader r) {
        StringBuilder sb = new();
        sb.Append('<').Append(r.Name);
        if (r.MoveToFirstAttribute()) {
            do {
                if (r.Prefix == "xmlns" || r.Name == "xmlns") continue;
                sb.Append(' ').Append(r.Name).Append("=\"").Append(r.Value).Append('"');
                if (sb.Length > 200) break;
            } while (r.MoveToNextAttribute());
            r.MoveToElement();
        }
        sb.Append(r.IsEmptyElement ? "/>" : ">");
        return sb.Length > 200 ? string.Concat(sb.ToString(0, 197), "...") : sb.ToString();
    }

    sealed class Level {
        public string Key = "";
        public Dictionary<string, int> Counts = new(StringComparer.Ordinal);
        public string Cell = "";
    }

    public static void Locate(ZipArchiveEntry entry, List<Finding> findings, bool worksheet) {
        Dictionary<string, List<Finding>> targets = new(StringComparer.Ordinal);
        HashSet<string> prefixes = new(StringComparer.Ordinal);
        foreach (Finding f in findings) {
            if (f.Key == null || f.Line != 0) continue;
            if (!targets.TryGetValue(f.Key, out List<Finding>? list)) {
                list = new List<Finding>();
                targets[f.Key] = list;
                for (int i = f.Key.IndexOf(Sep, 1); i > 0; i = f.Key.IndexOf(Sep, i + 1)) prefixes.Add(f.Key[..i]);
                prefixes.Add(f.Key);
            }
            list.Add(f);
        }
        if (targets.Count == 0) return;

        try {
            using XmlReader r = XmlReader.Create(entry.Open(), PackageChecks.ReaderSettings);
            IXmlLineInfo li = (IXmlLineInfo)r;
            List<Level> stack = new() { new Level() };
            int found = 0;
            bool more = r.Read();
            while (more && found < targets.Count) {
                if (r.NodeType == XmlNodeType.Element) {
                    Level parent = stack[^1];
                    string name = r.NamespaceURI + "|" + r.LocalName;
                    parent.Counts.TryGetValue(name, out int n);
                    parent.Counts[name] = ++n;
                    string key = string.Concat(parent.Key, Sep.ToString(), name, "[", n.ToString(), "]");
                    if (!prefixes.Contains(key)) {
                        r.Skip();
                        more = !r.EOF;
                        continue;
                    }

                    string cell = parent.Cell;
                    if (worksheet && (r.LocalName == "c" || r.LocalName == "row")) {
                        string? rv = r.GetAttribute("r");
                        if (!string.IsNullOrEmpty(rv)) cell = r.LocalName == "row" ? "row " + rv : rv;
                    }
                    if (targets.TryGetValue(key, out List<Finding>? hits)) {
                        string tag = StartTag(r);
                        foreach (Finding f in hits) {
                            f.Line = li.LineNumber;
                            f.Column = li.LinePosition;
                            f.Node = tag;
                            if (f.Cell.Length == 0) f.Cell = cell;
                        }
                        found++;
                    }
                    if (!r.IsEmptyElement) stack.Add(new Level { Key = key, Cell = cell });
                } else if (r.NodeType == XmlNodeType.EndElement) {
                    stack.RemoveAt(stack.Count - 1);
                }
                more = r.Read();
            }
        } catch (XmlException) {
        } catch (InvalidDataException) {
        }
    }
}
