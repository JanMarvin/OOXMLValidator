using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;

namespace OOXMLValidator;

sealed class LocalOnlyResolver : XmlUrlResolver {
    readonly Dictionary<string, string> byName;
    readonly ConcurrentDictionary<string, byte> issues;

    public LocalOnlyResolver(Dictionary<string, string> byName, ConcurrentDictionary<string, byte> issues) {
        this.byName = byName;
        this.issues = issues;
    }

    public override object? GetEntity(Uri absoluteUri, string? role, Type? ofObjectToReturn) {
        if (absoluteUri.IsFile && File.Exists(absoluteUri.LocalPath))
            return base.GetEntity(absoluteUri, role, ofObjectToReturn);
        string name = Path.GetFileName(absoluteUri.AbsolutePath);
        if (byName.TryGetValue(name, out string? local))
            return base.GetEntity(new Uri(local), role, ofObjectToReturn);
        issues.TryAdd($"'{absoluteUri}' is imported but not available locally, add '{name}' to the schema folder", 0);
        throw new FileNotFoundException($"schema '{absoluteUri}' is not available locally");
    }
}

sealed class SchemaStore {
    public const string McNs = "http://schemas.openxmlformats.org/markup-compatibility/2006";
    const string XmlNs = "http://www.w3.org/XML/1998/namespace";

    readonly Dictionary<string, List<string>> files = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> byName = new(StringComparer.OrdinalIgnoreCase);
    public readonly ConcurrentDictionary<string, byte> Issues = new();

    [ThreadStatic]
    static Dictionary<string, XmlSchemaSet?>? cache;

    public SchemaStore(string dir) {
        HashSet<string> seen = new(StringComparer.Ordinal);
        List<string> all = new(Directory.EnumerateFiles(dir, "*.xsd", SearchOption.AllDirectories));
        all.Sort(StringComparer.Ordinal);
        foreach (string f in all) {
            string full = Path.GetFullPath(f);
            if (Path.GetRelativePath(dir, full).Split(Path.DirectorySeparatorChar).Any(seg => seg.StartsWith('.'))) continue;
            if (!seen.Add(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(full))))) continue;
            byName.TryAdd(Path.GetFileName(full), full);
            try {
                using XmlReader r = XmlReader.Create(full, PackageChecks.ReaderSettings);
                r.MoveToContent();
                if (r.LocalName != "schema" || r.NamespaceURI != XmlSchema.Namespace) continue;
                string ns = r.GetAttribute("targetNamespace") ?? "";
                if (!files.TryGetValue(ns, out List<string>? list)) {
                    list = new List<string>();
                    files[ns] = list;
                }
                list.Add(full);
            } catch (XmlException ex) {
                Issues.TryAdd($"{full}: {ex.Message}", 0);
            }
        }
    }

    public int NamespaceCount => files.Count;

    public bool Has(string ns) => files.ContainsKey(ns);

    public bool Understood(string ns) => ns == McNs || ns == XmlNs || files.ContainsKey(ns);

    public XmlSchemaSet? Get(string ns) {
        cache ??= new Dictionary<string, XmlSchemaSet?>(StringComparer.Ordinal);
        if (cache.TryGetValue(ns, out XmlSchemaSet? cached)) return cached;

        XmlSchemaSet? set = null;
        if (files.ContainsKey(ns)) {
            set = new XmlSchemaSet { XmlResolver = new LocalOnlyResolver(byName, Issues) };
            bool failed = false;
            set.ValidationEventHandler += (_, e) => {
                Issues.TryAdd($"schema set for '{ns}': {e.Message}", 0);
                if (e.Severity == XmlSeverityType.Error) failed = true;
            };
            HashSet<string> added = new(StringComparer.Ordinal);
            Queue<string> pending = new();
            pending.Enqueue(ns);
            try {
                while (pending.Count > 0) {
                    string n = pending.Dequeue();
                    if (!added.Add(n) || set.Contains(n) || !files.TryGetValue(n, out List<string>? list)) continue;
                    foreach (string f in list) {
                        HashSet<string> loaded = new(StringComparer.Ordinal);
                        Stack<XmlSchema> walk = new();
                        foreach (XmlSchema s in set.Schemas()) walk.Push(s);
                        while (walk.Count > 0) {
                            XmlSchema s = walk.Pop();
                            if (s.SourceUri == null || !loaded.Add(s.SourceUri)) continue;
                            foreach (XmlSchemaObject inc in s.Includes) {
                                if (inc is XmlSchemaExternal ext && ext.Schema != null) walk.Push(ext.Schema);
                            }
                        }
                        string uri = new Uri(f).AbsoluteUri;
                        if (!loaded.Contains(uri)) set.Add(n.Length == 0 ? null : n, uri);
                    }
                    foreach (XmlSchema s in set.Schemas()) {
                        foreach (XmlSchemaObject inc in s.Includes) {
                            if (inc is XmlSchemaImport imp && !added.Contains(imp.Namespace ?? "")) pending.Enqueue(imp.Namespace ?? "");
                        }
                    }
                }
                set.Compile();
                if (failed) set = null;
            } catch (Exception ex) when (ex is XmlSchemaException || ex is XmlException || ex is IOException) {
                Issues.TryAdd($"schema set for '{ns}': {ex.Message}", 0);
                set = null;
            }
        }
        cache[ns] = set;
        return set;
    }

    public static void ApplyMarkupCompatibility(XElement el, HashSet<string> ignorable, Func<string, bool> understood) {
        HashSet<string> ign = ignorable;
        XAttribute? ia = el.Attribute(XName.Get("Ignorable", McNs));
        if (ia != null) {
            ign = new HashSet<string>(ignorable, StringComparer.Ordinal);
            foreach (string prefix in ia.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)) {
                XNamespace? ns = el.GetNamespaceOfPrefix(prefix);
                if (ns != null) ign.Add(ns.NamespaceName);
            }
        }

        foreach (XAttribute a in el.Attributes().ToList()) {
            if (a.IsNamespaceDeclaration) continue;
            string ns = a.Name.NamespaceName;
            if (ns == McNs || (ns.Length > 0 && ign.Contains(ns) && !understood(ns))) a.Remove();
        }

        foreach (XElement child in el.Elements().ToList()) {
            string ns = child.Name.NamespaceName;
            if (ns == McNs && child.Name.LocalName == "AlternateContent") {
                XElement? pick = null;
                foreach (XElement choice in child.Elements()) {
                    if (choice.Name.NamespaceName != McNs) continue;
                    if (choice.Name.LocalName == "Fallback") {
                        pick ??= choice;
                        continue;
                    }
                    if (choice.Name.LocalName != "Choice") continue;
                    bool ok = true;
                    foreach (string prefix in (choice.Attribute("Requires")?.Value ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)) {
                        XNamespace? rns = choice.GetNamespaceOfPrefix(prefix);
                        if (rns == null || !understood(rns.NamespaceName)) ok = false;
                    }
                    if (ok) {
                        pick = choice;
                        break;
                    }
                }
                List<XNode> nodes = pick?.Nodes().ToList() ?? new List<XNode>();
                foreach (XNode n in nodes) n.Remove();
                child.ReplaceWith(nodes);
                foreach (XNode n in nodes) {
                    if (n is XElement e) ApplyMarkupCompatibility(e, ign, understood);
                }
            } else if (ns.Length > 0 && ign.Contains(ns) && !understood(ns)) {
                child.Remove();
            } else {
                ApplyMarkupCompatibility(child, ign, understood);
            }
        }
    }

    static string PathOf(XElement el) {
        List<string> steps = new();
        for (XElement? e = el; e != null; e = e.Parent) {
            string? prefix = e.GetPrefixOfNamespace(e.Name.Namespace);
            string name = string.IsNullOrEmpty(prefix) ? e.Name.LocalName : prefix + ":" + e.Name.LocalName;
            int index = 1;
            foreach (XElement _ in e.ElementsBeforeSelf(e.Name)) index++;
            steps.Add($"/{name}[{index}]");
        }
        steps.Reverse();
        return string.Concat(steps);
    }

    static string StartTag(XElement el) {
        StringBuilder sb = new();
        string? prefix = el.GetPrefixOfNamespace(el.Name.Namespace);
        sb.Append('<').Append(string.IsNullOrEmpty(prefix) ? "" : prefix + ":").Append(el.Name.LocalName);
        foreach (XAttribute a in el.Attributes()) {
            if (a.IsNamespaceDeclaration) continue;
            string? ap = a.Name.Namespace == XNamespace.None ? null : el.GetPrefixOfNamespace(a.Name.Namespace);
            sb.Append(' ').Append(string.IsNullOrEmpty(ap) ? "" : ap + ":").Append(a.Name.LocalName)
              .Append("=\"").Append(a.Value).Append('"');
            if (sb.Length > 200) break;
        }
        sb.Append(el.IsEmpty ? "/>" : ">");
        return sb.Length > 200 ? string.Concat(sb.ToString(0, 197), "...") : sb.ToString();
    }

    public HashSet<string> ValidatePart(ZipArchiveEntry entry, string part, bool whole, HashSet<string> fragmentNs, List<Finding> fs) {
        HashSet<string> validated = new(StringComparer.Ordinal);
        XDocument doc;
        try {
            using XmlReader r = XmlReader.Create(entry.Open(), PackageChecks.ReaderSettings);
            doc = XDocument.Load(r, LoadOptions.SetLineInfo);
        } catch (XmlException ex) {
            fs.Add(PackageChecks.NotWellFormed(part, ex, Severity.Error));
            return validated;
        } catch (InvalidDataException ex) {
            fs.Add(PackageChecks.CorruptEntry(part, ex));
            return validated;
        }
        if (doc.Root == null) return validated;

        ApplyMarkupCompatibility(doc.Root, new HashSet<string>(StringComparer.Ordinal), Understood);

        // a root without a schema, like the <xml> container of legacy VML drawings, is not
        // validated itself, but every subtree in a namespace with a schema is
        if (whole && Get(doc.Root.Name.NamespaceName) == null) {
            whole = false;
            fragmentNs = new HashSet<string>(StringComparer.Ordinal);
            foreach (XElement e in doc.Root.Descendants()) {
                if (Has(e.Name.NamespaceName)) fragmentNs.Add(e.Name.NamespaceName);
            }
        }

        List<XElement> todo = new();
        if (whole) {
            todo.Add(doc.Root);
        } else {
            foreach (XElement e in doc.Root.DescendantsAndSelf()) {
                string ns = e.Name.NamespaceName;
                if (fragmentNs.Contains(ns) && !e.Ancestors().Any(a => fragmentNs.Contains(a.Name.NamespaceName))) todo.Add(e);
            }
        }

        foreach (XElement el in todo) {
            XmlSchemaSet? set = Get(el.Name.NamespaceName);
            if (set == null) continue;
            if (set.GlobalElements[new XmlQualifiedName(el.Name.LocalName, el.Name.NamespaceName)] is not XmlSchemaElement decl) {
                IXmlLineInfo li = el;
                fs.Add(new Finding {
                    Severity = Severity.Warning, Category = "XSD", Id = "Xsd_NoDeclaration", Part = part,
                    Line = li.LineNumber, Column = li.LinePosition, XPath = PathOf(el), Node = StartTag(el),
                    Description = $"the supplied schemas for '{el.Name.NamespaceName}' declare no global element '{el.Name.LocalName}'."
                });
                continue;
            }
            validated.Add(el.Name.NamespaceName);
            el.Validate(decl, set, (sender, e) => {
                XElement? at = sender as XElement ?? (sender as XAttribute)?.Parent;
                IXmlLineInfo? li = sender as IXmlLineInfo;
                fs.Add(new Finding {
                    Severity = e.Severity == XmlSeverityType.Error ? Severity.Error : Severity.Warning,
                    Category = "XSD", Id = e.Severity == XmlSeverityType.Error ? "Xsd_Invalid" : "Xsd_Warning", Part = part,
                    Line = li != null && li.HasLineInfo() ? li.LineNumber : 0,
                    Column = li != null && li.HasLineInfo() ? li.LinePosition : 0,
                    XPath = at != null ? PathOf(at) : "", Node = at != null ? StartTag(at) : "",
                    Description = e.Message
                });
            });
        }
        return validated;
    }
}
