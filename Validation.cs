using System.IO.Compression;
using System.Xml;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Validation;

namespace OOXMLValidator;

static class Validation {
    static readonly HashSet<string> WordExtensions = new(StringComparer.OrdinalIgnoreCase) { ".docx", ".docm", ".dotx", ".dotm" };
    static readonly HashSet<string> SlideExtensions = new(StringComparer.OrdinalIgnoreCase) {
        ".pptx", ".pptm", ".potx", ".potm", ".ppsx", ".ppsm", ".ppam"
    };

    sealed class Unknown {
        public string Namespace = "";
        public string Name = "";
        public bool Attribute;
        public int Count;
        public OpenXmlElement First = null!;
    }

    public static FileReport Run(string path, Options opt, SchemaStore? schemas) {
        FileReport rep = new() { Path = path };
        List<Finding> fs = rep.Findings;

        byte[] bytes;
        try {
            bytes = File.ReadAllBytes(path);
        } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) {
            rep.Fatal = true;
            fs.Add(new Finding { Category = "IO", Id = "Io_ReadFailed", Description = ex.Message });
            return rep;
        }
        if (bytes.Length >= 4 && bytes[0] == 0xD0 && bytes[1] == 0xCF && bytes[2] == 0x11 && bytes[3] == 0xE0) {
            rep.Fatal = true;
            fs.Add(new Finding {
                Category = "OPC", Id = "Opc_NotZip",
                Description = "file is an OLE2 compound document, not a zip package: an encrypted OOXML file or a legacy binary format."
            });
            return rep;
        }
        if (bytes.Length < 4 || bytes[0] != 0x50 || bytes[1] != 0x4B) {
            rep.Fatal = true;
            fs.Add(new Finding { Category = "OPC", Id = "Opc_NotZip", Description = "file is not a zip package." });
            return rep;
        }

        ZipArchive zip;
        try {
            zip = new ZipArchive(new MemoryStream(bytes, false), ZipArchiveMode.Read);
        } catch (InvalidDataException ex) {
            fs.Add(new Finding { Category = "OPC", Id = "Opc_CorruptZip", Description = $"zip structure cannot be read: {ex.Message}" });
            return rep;
        }

        using (zip) {
            PackageInfo pkg = PackageChecks.Run(zip, fs);
            if (pkg.MainPart != null && pkg.Entries.TryGetValue(pkg.MainPart, out ZipArchiveEntry? mainEntry) &&
                !PackageChecks.LooksLikeXml(pkg.ContentType(pkg.MainPart), mainEntry)) {
                rep.Fatal = true;
                fs.Add(new Finding {
                    Severity = Severity.Warning, Category = "OPC", Id = "Opc_BinaryMainPart", Part = pkg.MainPart,
                    Description = $"the main part is binary (content type '{pkg.ContentType(pkg.MainPart)}'), a format such as xlsb that is not defined by ECMA-376; only the package structure was checked"
                });
                Excerpts.Decorate(pkg, fs);
                return rep;
            }
            OpenXmlPackage? doc = Open(bytes, path, pkg.MainPart == null ? null : pkg.ContentType(pkg.MainPart), rep);
            if (doc == null) {
                CheckAllWellFormed(pkg, fs);
            } else {
                using (doc) {
                    ValidateDocument(doc, pkg, opt, schemas, fs);
                }
            }
            Excerpts.Decorate(pkg, fs);
        }
        return rep;
    }

    static OpenXmlPackage? Open(byte[] bytes, string path, string? mainType, FileReport rep) {
        string ext = System.IO.Path.GetExtension(path);
        int first = WordExtensions.Contains(ext) ? 1 : SlideExtensions.Contains(ext) ? 2 : 0;
        if (mainType != null) {
            if (mainType.Contains("spreadsheetml", StringComparison.OrdinalIgnoreCase)) first = 0;
            else if (mainType.Contains("wordprocessingml", StringComparison.OrdinalIgnoreCase)) first = 1;
            else if (mainType.Contains("presentationml", StringComparison.OrdinalIgnoreCase)) first = 2;
        }
        OpenSettings settings = new() { AutoSave = false };
        Exception? firstError = null;
        for (int k = 0; k < 3; k++) {
            int kind = (first + k) % 3;
            try {
                MemoryStream ms = new(bytes, false);
                OpenXmlPackage doc;
                bool hasMain;
                if (kind == 0) {
                    SpreadsheetDocument d = SpreadsheetDocument.Open(ms, false, settings);
                    hasMain = d.WorkbookPart != null;
                    doc = d;
                    rep.Kind = "spreadsheet";
                } else if (kind == 1) {
                    WordprocessingDocument d = WordprocessingDocument.Open(ms, false, settings);
                    hasMain = d.MainDocumentPart != null;
                    doc = d;
                    rep.Kind = "wordprocessing";
                } else {
                    PresentationDocument d = PresentationDocument.Open(ms, false, settings);
                    hasMain = d.PresentationPart != null;
                    doc = d;
                    rep.Kind = "presentation";
                }
                if (hasMain || k == 2) return doc;
                doc.Dispose();
                rep.Kind = "";
            } catch (Exception ex) {
                firstError ??= ex;
            }
        }
        rep.Findings.Add(new Finding {
            Category = "SDK", Id = "Sdk_OpenFailed",
            Description = $"the Open XML SDK cannot open the package: {firstError?.Message}"
        });
        return null;
    }

    static string ZipName(Uri? uri) {
        if (uri == null) return "";
        string s = uri.OriginalString;
        return Uri.UnescapeDataString(s.StartsWith('/') ? s[1..] : s);
    }

    static void CheckAllWellFormed(PackageInfo pkg, List<Finding> fs) {
        foreach (KeyValuePair<string, ZipArchiveEntry> kv in pkg.Entries) {
            string name = kv.Key;
            if (name == PackageInfo.ContentTypesName || name.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)) continue;
            string? ct = pkg.ContentType(name);
            if (ct == null) continue;
            if (ct.EndsWith("+xml", StringComparison.OrdinalIgnoreCase) || ct.EndsWith("/xml", StringComparison.OrdinalIgnoreCase))
                PackageChecks.CheckWellFormed(kv.Value, name, Severity.Error, fs);
        }
    }

    static void ValidateDocument(OpenXmlPackage doc, PackageInfo pkg, Options opt, SchemaStore? schemas, List<Finding> fs) {
        Dictionary<string, string> sheets = new(StringComparer.OrdinalIgnoreCase);
        if (doc is SpreadsheetDocument ss) {
            try {
                WorkbookPart? wbp = ss.WorkbookPart;
                Sheets? list = wbp?.Workbook?.Sheets;
                if (wbp != null && list != null) {
                    foreach (Sheet s in list.Elements<Sheet>()) {
                        string? id = s.Id?.Value;
                        if (id == null) continue;
                        if (wbp.TryGetPartById(id, out OpenXmlPart? p)) sheets[ZipName(p.Uri)] = s.Name?.Value ?? "";
                    }
                }
            } catch (Exception) {
                sheets.Clear();
            }
        }

        List<OpenXmlPart> parts = new();
        try {
            parts.AddRange(doc.GetAllParts());
        } catch (Exception ex) {
            fs.Add(new Finding { Category = "SDK", Id = "Sdk_PartsUnavailable", Description = $"parts cannot be enumerated: {ex.Message}" });
        }

        bool deep = opt.Info || schemas != null;
        if (deep) {
            foreach (OpenXmlPart p in parts) {
                if (p is ExtendedPart) continue;
                try {
                    _ = p.RootElement;
                } catch (Exception) {
                }
            }
        }

        OpenXmlValidator validator = new(opt.Target) { MaxNumberOfErrors = opt.MaxErrors };
        int count = 0;
        try {
            foreach (ValidationErrorInfo e in validator.Validate(doc)) {
                count++;
                string part = ZipName(e.Part?.Uri ?? e.Path?.PartUri);
                string desc = e.Description ?? "";
                if (e.Path?.Namespaces != null) {
                    foreach (KeyValuePair<string, string> ns in e.Path.Namespaces.GetNamespacesInScope(XmlNamespaceScope.All).OrderByDescending(p => p.Value.Length)) {
                        if (ns.Key.Length > 0 && ns.Value.Length > 0) desc = desc.Replace(ns.Value + ":", ns.Key + ":", StringComparison.Ordinal);
                    }
                }
                XmlPath? at = e.Path;
                bool samePart = e.RelatedPart == null || e.RelatedPart == e.Part;
                if (!samePart) desc += $" Related part: {ZipName(e.RelatedPart!.Uri)}.";
                if (e.RelatedNode != null) {
                    XmlPath related = new(e.RelatedNode);
                    if (samePart) at = related;
                    else desc += $" Related node: {related.XPath}.";
                }
                fs.Add(new Finding {
                    Category = e.ErrorType.ToString(),
                    Id = e.Id ?? "",
                    Part = part,
                    XPath = at?.XPath ?? "",
                    Key = Locator.KeyFromXPath(at?.XPath, at?.Namespaces),
                    Sheet = sheets.GetValueOrDefault(part, ""),
                    Description = desc
                });
            }
        } catch (Exception ex) {
            fs.Add(new Finding {
                Category = "SDK", Id = "Sdk_ValidationAborted",
                Description = $"validation stopped early: {ex.GetType().Name}: {ex.Message}"
            });
            CheckAllWellFormed(pkg, fs);
            LocateAll(pkg, sheets, fs);
            return;
        }
        if (opt.MaxErrors > 0 && count >= opt.MaxErrors) {
            fs.Add(new Finding {
                Severity = Severity.Info, Category = "SDK", Id = "Info_ErrorLimit",
                Description = $"stopped after {opt.MaxErrors} errors; further errors were not reported."
            });
        }

        HashSet<string> errorKeys = new(StringComparer.Ordinal);
        HashSet<string> exceptionParts = new(StringComparer.OrdinalIgnoreCase);
        foreach (Finding f in fs) {
            if (f.Key != null) errorKeys.Add(f.Part + f.Key);
            if (f.Id == "ExceptionError") exceptionParts.Add(f.Part);
        }

        HashSet<string> reachable = new(StringComparer.OrdinalIgnoreCase);
        foreach (OpenXmlPart p in parts) {
            string name = ZipName(p.Uri);
            reachable.Add(name);
            if (!pkg.Entries.TryGetValue(name, out ZipArchiveEntry? entry)) continue;
            if (!PackageChecks.LooksLikeXml(p.ContentType, entry)) continue;

            if (exceptionParts.Contains(name)) {
                int n = fs.Count;
                PackageChecks.CheckWellFormed(entry, name, Severity.Error, fs);
                if (fs.Count != n) fs.RemoveAll(f => f.Id == "ExceptionError" && string.Equals(f.Part, name, StringComparison.OrdinalIgnoreCase));
                continue;
            }

            bool known = false;
            if (p is not ExtendedPart) {
                try {
                    using OpenXmlReader rd = OpenXmlReader.Create(p);
                    known = rd.Read() && rd.ElementType != typeof(OpenXmlUnknownElement);
                } catch (Exception) {
                    known = false;
                }
            }

            if (!known) {
                bool xmlType = p.ContentType.EndsWith("+xml", StringComparison.OrdinalIgnoreCase) ||
                               p.ContentType.EndsWith("/xml", StringComparison.OrdinalIgnoreCase);
                int before = fs.Count;
                PackageChecks.CheckWellFormed(entry, name, xmlType ? Severity.Error : Severity.Warning, fs);
                if (fs.Count != before) continue;

                HashSet<string> validated = schemas != null
                    ? schemas.ValidatePart(entry, name, true, new HashSet<string>(), fs)
                    : new HashSet<string>();
                if (validated.Count == 0) {
                    fs.Add(new Finding {
                        Severity = Severity.Info, Category = "Coverage", Id = "Info_PartNotValidated", Part = name,
                        Description = $"the SDK has no schema for this part (content type '{p.ContentType}', relationship type '{p.RelationshipType}'); its content was not validated."
                    });
                }
                continue;
            }
            if (!deep) continue;

            OpenXmlPartRootElement? root;
            try {
                root = p.RootElement;
            } catch (Exception) {
                continue;
            }
            if (root == null) continue;

            Dictionary<string, Unknown> unknown = new(StringComparer.Ordinal);
            CollectUnknown(root, unknown);
            HashSet<string> fragmentNs = new(StringComparer.Ordinal);
            foreach (Unknown u in unknown.Values) {
                if (schemas != null && schemas.Has(u.Namespace) && !u.Attribute) fragmentNs.Add(u.Namespace);
            }
            HashSet<string> done = new(StringComparer.Ordinal);
            if (schemas != null && (fragmentNs.Count > 0 || opt.SchemasAll)) {
                done = schemas.ValidatePart(entry, name, opt.SchemasAll && schemas.Has(root.NamespaceUri), fragmentNs, fs);
            }

            foreach (Unknown u in unknown.Values) {
                if (done.Contains(u.Namespace)) continue;
                XmlPath xp = new(u.First);
                string? key = Locator.KeyFromXPath(xp.XPath, xp.Namespaces);
                if (key != null && !u.Attribute && errorKeys.Contains(name + key)) continue;
                string what = u.Attribute ? "attribute" : "element";
                string ns = u.Namespace.Length == 0 ? "no namespace" : $"namespace '{u.Namespace}'";
                fs.Add(new Finding {
                    Severity = Severity.Info, Category = "Coverage",
                    Id = u.Attribute ? "Info_UnknownAttribute" : "Info_UnknownElement",
                    Part = name, XPath = xp.XPath, Key = key, Sheet = sheets.GetValueOrDefault(name, ""),
                    Description = $"{what} '{u.Name}' ({ns}) is unknown to the SDK at target {opt.Target} and was not validated ({u.Count} occurrence{(u.Count == 1 ? "" : "s")})."
                });
            }
        }

        foreach (KeyValuePair<string, ZipArchiveEntry> kv in pkg.Entries) {
            string name = kv.Key;
            if (reachable.Contains(name) || name == PackageInfo.ContentTypesName ||
                name.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)) continue;
            if (pkg.Targets.Contains(name)) {
                fs.Add(new Finding {
                    Severity = Severity.Info, Category = "Coverage", Id = "Info_PartNotLoaded", Part = name,
                    Description = "part is the target of a relationship, but the SDK did not load it (see the package errors); it was not validated."
                });
                continue;
            }
            fs.Add(new Finding {
                Severity = Severity.Warning, Category = "OPC", Id = "Opc_UnreferencedPart", Part = name,
                Description = "part is not the target of any relationship; consumers ignore it and it was not validated."
            });
            if (PackageChecks.LooksLikeXml(pkg.ContentType(name), kv.Value))
                PackageChecks.CheckWellFormed(kv.Value, name, Severity.Warning, fs);
        }

        LocateAll(pkg, sheets, fs);
    }

    static void CollectUnknown(OpenXmlElement root, Dictionary<string, Unknown> found) {
        Stack<OpenXmlElement> stack = new();
        stack.Push(root);
        while (stack.Count > 0) {
            OpenXmlElement el = stack.Pop();
            if (el.HasAttributes) {
                foreach (OpenXmlAttribute a in el.ExtendedAttributes) {
                    string qn = a.Prefix.Length > 0 ? a.Prefix + ":" + a.LocalName : a.LocalName;
                    string k = "@" + a.NamespaceUri + "|" + a.LocalName;
                    if (!found.TryGetValue(k, out Unknown? u)) {
                        u = new Unknown { Namespace = a.NamespaceUri, Name = qn, Attribute = true, First = el };
                        found[k] = u;
                    }
                    u.Count++;
                }
            }
            if (!el.HasChildren) continue;
            foreach (OpenXmlElement c in el.ChildElements) {
                if (c is OpenXmlUnknownElement) {
                    string k = c.NamespaceUri + "|" + c.LocalName;
                    if (!found.TryGetValue(k, out Unknown? u)) {
                        string qn = c.Prefix.Length > 0 ? c.Prefix + ":" + c.LocalName : c.LocalName;
                        u = new Unknown { Namespace = c.NamespaceUri, Name = qn, First = c };
                        found[k] = u;
                    }
                    u.Count++;
                } else if (c is not OpenXmlMiscNode) {
                    stack.Push(c);
                }
            }
        }
    }

    static void LocateAll(PackageInfo pkg, Dictionary<string, string> sheets, List<Finding> fs) {
        Dictionary<string, List<Finding>> byPart = new(StringComparer.OrdinalIgnoreCase);
        foreach (Finding f in fs) {
            if (f.Sheet.Length == 0 && sheets.TryGetValue(f.Part, out string? sheet)) f.Sheet = sheet;
            if (f.Key == null || f.Line != 0 || f.Part.Length == 0) continue;
            if (!byPart.TryGetValue(f.Part, out List<Finding>? list)) {
                list = new List<Finding>();
                byPart[f.Part] = list;
            }
            list.Add(f);
        }
        foreach (KeyValuePair<string, List<Finding>> kv in byPart) {
            if (pkg.Entries.TryGetValue(kv.Key, out ZipArchiveEntry? entry))
                Locator.Locate(entry, kv.Value, sheets.ContainsKey(kv.Key));
        }
    }
}
