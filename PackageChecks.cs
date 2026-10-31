using System.IO.Compression;
using System.Xml;

namespace OOXMLValidator;

sealed class PackageInfo {
    public const string ContentTypesName = "[Content_Types].xml";
    public readonly Dictionary<string, ZipArchiveEntry> Entries = new(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, string> Defaults = new(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, string> Overrides = new(StringComparer.OrdinalIgnoreCase);
    public readonly HashSet<string> Targets = new(StringComparer.OrdinalIgnoreCase);
    public string? MainPart;

    public string? ContentType(string name) {
        if (Overrides.TryGetValue(name, out string? ct)) return ct;
        int slash = name.LastIndexOf('/');
        int dot = name.LastIndexOf('.');
        if (dot <= slash) return null;
        return Defaults.TryGetValue(name[(dot + 1)..], out ct) ? ct : null;
    }
}

static class PackageChecks {
    const string ContentTypesNs = "http://schemas.openxmlformats.org/package/2006/content-types";
    const string RelationshipsNs = "http://schemas.openxmlformats.org/package/2006/relationships";

    public static readonly XmlReaderSettings ReaderSettings = new() {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        CloseInput = true
    };

    public static PackageInfo Run(ZipArchive zip, List<Finding> fs) {
        PackageInfo info = new();

        foreach (ZipArchiveEntry e in zip.Entries) {
            string name = e.FullName;
            if (name.EndsWith('/')) continue;

            string? problem = PartNameProblem(name);
            if (problem != null) {
                fs.Add(new Finding {
                    Category = "OPC", Id = "Opc_InvalidPartName", Part = name,
                    Description = $"zip item '{name}' is not a valid part name: {problem}."
                });
            }
            if (!info.Entries.TryAdd(name, e)) {
                fs.Add(new Finding {
                    Category = "OPC", Id = "Opc_DuplicatePart", Part = name,
                    Description = $"zip item '{name}' occurs more than once (part names are compared case-insensitively)."
                });
            }
        }

        if (!info.Entries.TryGetValue(PackageInfo.ContentTypesName, out ZipArchiveEntry? ctEntry)) {
            fs.Add(new Finding {
                Category = "OPC", Id = "Opc_MissingContentTypes",
                Description = "the package has no [Content_Types].xml."
            });
        } else {
            ReadContentTypes(ctEntry, info, fs);
            foreach (string name in info.Entries.Keys) {
                if (name == PackageInfo.ContentTypesName) continue;
                if (info.ContentType(name) == null) {
                    fs.Add(new Finding {
                        Category = "OPC", Id = "Opc_MissingContentType", Part = name,
                        Description = $"no Default or Override in [Content_Types].xml provides a content type for '{name}'."
                    });
                }
            }
            foreach (string name in info.Overrides.Keys) {
                if (!info.Entries.ContainsKey(name)) {
                    fs.Add(new Finding {
                        Severity = Severity.Warning, Category = "OPC", Id = "Opc_OverrideWithoutPart",
                        Part = PackageInfo.ContentTypesName,
                        Description = $"Override for '/{name}' refers to a part that does not exist."
                    });
                }
            }
        }

        if (!info.Entries.ContainsKey("_rels/.rels")) {
            fs.Add(new Finding {
                Category = "OPC", Id = "Opc_MissingPackageRelationships",
                Description = "the package has no _rels/.rels, so no main document part can be found."
            });
        }

        foreach (KeyValuePair<string, ZipArchiveEntry> kv in info.Entries) {
            string name = kv.Key;
            if (!name.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)) continue;
            int slash = name.LastIndexOf('/');
            string dir = slash < 0 ? "" : name[..slash];
            if (!(dir == "_rels" || dir.EndsWith("/_rels", StringComparison.OrdinalIgnoreCase))) continue;

            string sourceDir = dir.Length == 5 ? "" : dir[..^5];
            string sourceName = name[(slash + 1)..^5];
            string source = sourceDir + sourceName;
            if (source.Length > 0 && !info.Entries.ContainsKey(source)) {
                fs.Add(new Finding {
                    Severity = Severity.Warning, Category = "OPC", Id = "Opc_OrphanRelationships", Part = name,
                    Description = $"relationship part belongs to '{source}', which does not exist."
                });
            }
            ReadRelationships(kv.Value, name, sourceDir, info, fs);
        }

        return info;
    }

    static string? PartNameProblem(string name) {
        if (name.Contains('\\')) return "it contains a backslash";
        if (name.StartsWith('/')) return "it starts with '/'";
        foreach (string seg in name.Split('/')) {
            if (seg.Length == 0) return "it contains an empty segment";
            if (seg.EndsWith('.')) return $"segment '{seg}' ends with '.'";
            if (seg.Contains("%2f", StringComparison.OrdinalIgnoreCase) || seg.Contains("%5c", StringComparison.OrdinalIgnoreCase))
                return $"segment '{seg}' contains an escaped '/' or '\\'";
        }
        return null;
    }

    static void ReadContentTypes(ZipArchiveEntry entry, PackageInfo info, List<Finding> fs) {
        string part = PackageInfo.ContentTypesName;
        try {
            using XmlReader r = XmlReader.Create(entry.Open(), ReaderSettings);
            IXmlLineInfo li = (IXmlLineInfo)r;
            bool root = true;
            while (r.Read()) {
                if (r.NodeType != XmlNodeType.Element) continue;
                if (root) {
                    root = false;
                    if (r.NamespaceURI != ContentTypesNs || r.LocalName != "Types") {
                        fs.Add(new Finding {
                            Category = "OPC", Id = "Opc_InvalidContentTypes", Part = part,
                            Line = li.LineNumber, Column = li.LinePosition,
                            Description = $"root element must be 'Types' in namespace '{ContentTypesNs}', found '{r.LocalName}' in '{r.NamespaceURI}'."
                        });
                    }
                    continue;
                }
                if (r.LocalName == "Default") {
                    string? ext = r.GetAttribute("Extension");
                    string? ct = r.GetAttribute("ContentType");
                    if (string.IsNullOrEmpty(ext) || string.IsNullOrEmpty(ct)) {
                        fs.Add(new Finding {
                            Category = "OPC", Id = "Opc_InvalidContentTypes", Part = part,
                            Line = li.LineNumber, Column = li.LinePosition,
                            Description = "Default requires non-empty Extension and ContentType attributes."
                        });
                    } else if (!info.Defaults.TryAdd(ext, ct)) {
                        fs.Add(new Finding {
                            Category = "OPC", Id = "Opc_DuplicateContentType", Part = part,
                            Line = li.LineNumber, Column = li.LinePosition,
                            Description = $"Default for extension '{ext}' is declared more than once."
                        });
                    }
                } else if (r.LocalName == "Override") {
                    string? pn = r.GetAttribute("PartName");
                    string? ct = r.GetAttribute("ContentType");
                    if (string.IsNullOrEmpty(pn) || string.IsNullOrEmpty(ct) || !pn.StartsWith('/')) {
                        fs.Add(new Finding {
                            Category = "OPC", Id = "Opc_InvalidContentTypes", Part = part,
                            Line = li.LineNumber, Column = li.LinePosition,
                            Description = "Override requires a ContentType and a PartName starting with '/'."
                        });
                    } else if (!info.Overrides.TryAdd(Uri.UnescapeDataString(pn[1..]), ct)) {
                        fs.Add(new Finding {
                            Category = "OPC", Id = "Opc_DuplicateContentType", Part = part,
                            Line = li.LineNumber, Column = li.LinePosition,
                            Description = $"Override for '{pn}' is declared more than once."
                        });
                    }
                }
            }
        } catch (XmlException ex) {
            fs.Add(NotWellFormed(part, ex, Severity.Error));
        } catch (InvalidDataException ex) {
            fs.Add(CorruptEntry(part, ex));
        }
    }

    static void ReadRelationships(ZipArchiveEntry entry, string part, string sourceDir, PackageInfo info, List<Finding> fs) {
        HashSet<string> ids = new(StringComparer.Ordinal);
        try {
            using XmlReader r = XmlReader.Create(entry.Open(), ReaderSettings);
            IXmlLineInfo li = (IXmlLineInfo)r;
            bool root = true;
            while (r.Read()) {
                if (r.NodeType != XmlNodeType.Element) continue;
                if (root) {
                    root = false;
                    if (r.NamespaceURI != RelationshipsNs || r.LocalName != "Relationships") {
                        fs.Add(new Finding {
                            Category = "OPC", Id = "Opc_InvalidRelationship", Part = part,
                            Line = li.LineNumber, Column = li.LinePosition,
                            Description = $"root element must be 'Relationships' in namespace '{RelationshipsNs}', found '{r.LocalName}' in '{r.NamespaceURI}'."
                        });
                    }
                    continue;
                }
                if (r.LocalName != "Relationship") continue;

                string? id = r.GetAttribute("Id");
                string? type = r.GetAttribute("Type");
                string? target = r.GetAttribute("Target");
                string? mode = r.GetAttribute("TargetMode");
                int line = li.LineNumber, col = li.LinePosition;

                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(type) || target == null) {
                    fs.Add(new Finding {
                        Category = "OPC", Id = "Opc_InvalidRelationship", Part = part, Line = line, Column = col,
                        Description = "Relationship requires Id, Type and Target attributes."
                    });
                    continue;
                }
                try {
                    XmlConvert.VerifyNCName(id);
                } catch (XmlException) {
                    fs.Add(new Finding {
                        Category = "OPC", Id = "Opc_InvalidRelationshipId", Part = part, Line = line, Column = col,
                        Description = $"relationship Id '{id}' is not a valid xsd:ID."
                    });
                }
                if (!ids.Add(id)) {
                    fs.Add(new Finding {
                        Category = "OPC", Id = "Opc_DuplicateRelationshipId", Part = part, Line = line, Column = col,
                        Description = $"relationship Id '{id}' is used more than once."
                    });
                }
                if (!Uri.TryCreate(target, UriKind.RelativeOrAbsolute, out Uri? uri)) {
                    fs.Add(new Finding {
                        Category = "OPC", Id = "Opc_MalformedUri", Part = part, Line = line, Column = col,
                        Description = $"relationship '{id}' has a malformed Target '{target}'."
                    });
                    continue;
                }
                if (mode == "External") continue;
                if (mode != null && mode != "Internal") {
                    fs.Add(new Finding {
                        Category = "OPC", Id = "Opc_InvalidRelationship", Part = part, Line = line, Column = col,
                        Description = $"relationship '{id}' has TargetMode '{mode}', expected 'Internal' or 'External'."
                    });
                    continue;
                }
                if (uri.IsAbsoluteUri) {
                    fs.Add(new Finding {
                        Category = "OPC", Id = "Opc_InvalidRelationship", Part = part, Line = line, Column = col,
                        Description = $"internal relationship '{id}' has an absolute URI Target '{target}'; external targets need TargetMode=\"External\"."
                    });
                    continue;
                }

                string path = target;
                int hash = path.IndexOf('#');
                if (hash >= 0) path = path[..hash];
                path = path.StartsWith('/') ? path[1..] : sourceDir + path;
                string? resolved = Normalize(Uri.UnescapeDataString(path));
                if (resolved != null) info.Targets.Add(resolved);
                if (resolved != null && part == "_rels/.rels" && type.EndsWith("/officeDocument", StringComparison.Ordinal)) info.MainPart = resolved;
                if (resolved == null || !info.Entries.ContainsKey(resolved)) {
                    fs.Add(new Finding {
                        Category = "OPC", Id = "Opc_MissingTarget", Part = part, Line = line, Column = col,
                        Description = $"relationship '{id}' ({type}) targets '{target}', but '{resolved ?? path}' does not exist in the package."
                    });
                }
            }
        } catch (XmlException ex) {
            fs.Add(NotWellFormed(part, ex, Severity.Error));
        } catch (InvalidDataException ex) {
            fs.Add(CorruptEntry(part, ex));
        }
    }

    static string? Normalize(string path) {
        List<string> segs = new();
        foreach (string s in path.Split('/')) {
            if (s.Length == 0 || s == ".") continue;
            if (s == "..") {
                if (segs.Count == 0) return null;
                segs.RemoveAt(segs.Count - 1);
            } else {
                segs.Add(s);
            }
        }
        return string.Join('/', segs);
    }

    public static Finding NotWellFormed(string part, XmlException ex, Severity severity) {
        return new Finding {
            Severity = severity, Category = "XML", Id = "Xml_NotWellFormed", Part = part,
            Line = ex.LineNumber, Column = ex.LinePosition,
            Description = System.Text.RegularExpressions.Regex.Replace(ex.Message, @"\s*Line \d+, position \d+\.?$", "")
        };
    }

    public static Finding CorruptEntry(string part, Exception ex) {
        return new Finding {
            Category = "OPC", Id = "Opc_CorruptEntry", Part = part,
            Description = $"zip item cannot be decompressed: {ex.Message}"
        };
    }

    public static void CheckWellFormed(ZipArchiveEntry entry, string part, Severity severity, List<Finding> fs) {
        try {
            using XmlReader r = XmlReader.Create(entry.Open(), ReaderSettings);
            while (r.Read()) { }
        } catch (XmlException ex) {
            fs.Add(NotWellFormed(part, ex, severity));
        } catch (InvalidDataException ex) {
            fs.Add(CorruptEntry(part, ex));
        }
    }

    public static bool LooksLikeXml(string? contentType, ZipArchiveEntry entry) {
        if (contentType != null && (contentType.EndsWith("+xml", StringComparison.OrdinalIgnoreCase) ||
                                    contentType.EndsWith("/xml", StringComparison.OrdinalIgnoreCase)))
            return true;
        try {
            using Stream s = entry.Open();
            Span<byte> buf = stackalloc byte[64];
            int n = s.ReadAtLeast(buf, buf.Length, false);
            int i = 0;
            if (n >= 3 && buf[0] == 0xEF && buf[1] == 0xBB && buf[2] == 0xBF) i = 3;
            while (i < n && (buf[i] == ' ' || buf[i] == '\t' || buf[i] == '\r' || buf[i] == '\n')) i++;
            return i < n && buf[i] == '<';
        } catch (InvalidDataException) {
            return false;
        }
    }
}
