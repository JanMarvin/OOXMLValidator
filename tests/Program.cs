using System.Diagnostics;
using System.IO.Compression;
using System.Text;

namespace OOXMLValidator.Tests;

sealed class Row {
    public string Severity = "";
    public string Id = "";
    public string Part = "";
    public string Line = "";
    public string Sheet = "";
    public string Cell = "";
}

sealed class Expect {
    public string Id = "";
    public string? Part;
    public string? Sheet;
    public string? Cell;
    public bool HasLine;
}

static class Program {
    const string MainNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    const string RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    static string exe = "";
    static string tmp = "";
    static int passed;
    static int skipped;
    static readonly List<string> failures = new();

    static int Main(string[] args) {
        string root = FindRoot();
        exe = args.Length > 0 ? args[0] : FindBinary(root);
        if (!File.Exists(exe)) {
            Console.Error.WriteLine($"validator binary not found: '{exe}'; pass its path as the first argument");
            return 2;
        }
        tmp = Directory.CreateTempSubdirectory("ooxmlvalidator-tests-").FullName;
        Console.WriteLine($"testing {exe}");

        try {
            Spreadsheet(root);
            Package();
            Formats();
            Output();
            Schemas();
        } finally {
            Directory.Delete(tmp, true);
        }

        Console.WriteLine();
        foreach (string f in failures) Console.WriteLine($"FAIL {f}");
        Console.WriteLine($"{passed} passed, {failures.Count} failed, {skipped} skipped");
        return failures.Count == 0 ? 0 : 1;
    }

    static string FindRoot() {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d != null; d = d.Parent) {
            if (File.Exists(Path.Combine(d.FullName, "OOXMLValidator.csproj"))) return d.FullName;
        }
        return Directory.GetCurrentDirectory();
    }

    static string FindBinary(string root) {
        string name = OperatingSystem.IsWindows() ? "OOXMLValidator.exe" : "OOXMLValidator";
        string bin = Path.Combine(root, "bin");
        if (Directory.Exists(bin)) {
            List<string> found = Directory.EnumerateFiles(bin, name, SearchOption.AllDirectories)
                .Where(f => f.Contains(Path.DirectorySeparatorChar + "publish" + Path.DirectorySeparatorChar))
                .OrderByDescending(File.GetLastWriteTimeUtc).ToList();
            if (found.Count > 0) return found[0];
        }
        return Path.Combine(bin, name);
    }

    static List<(string Name, byte[] Data)> Workbook(string sheetData = "<row r=\"1\"><c r=\"A1\"><v>1</v></c></row>") {
        return new List<(string, byte[])> {
            ("[Content_Types].xml", Utf8($"""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
                  <Default Extension="xml" ContentType="application/xml"/>
                  <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
                  <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
                </Types>
                """)),
            ("_rels/.rels", Utf8($"""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="{RelNs}/officeDocument" Target="xl/workbook.xml"/>
                </Relationships>
                """)),
            ("xl/workbook.xml", Utf8($"""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <workbook xmlns="{MainNs}" xmlns:r="{RelNs}">
                  <sheets>
                    <sheet name="Data" sheetId="1" r:id="rId1"/>
                  </sheets>
                </workbook>
                """)),
            ("xl/_rels/workbook.xml.rels", Utf8($"""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="{RelNs}/worksheet" Target="worksheets/sheet1.xml"/>
                </Relationships>
                """)),
            ("xl/worksheets/sheet1.xml", Utf8($"""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <worksheet xmlns="{MainNs}" xmlns:r="{RelNs}">
                  <sheetData>
                    {sheetData}
                  </sheetData>
                </worksheet>
                """))
        };
    }

    static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    static List<(string Name, byte[] Data)> Load(string path) {
        List<(string, byte[])> parts = new();
        using ZipArchive zip = ZipFile.OpenRead(path);
        foreach (ZipArchiveEntry e in zip.Entries) {
            using MemoryStream ms = new();
            using (Stream s = e.Open()) s.CopyTo(ms);
            parts.Add((e.FullName, ms.ToArray()));
        }
        return parts;
    }

    static List<(string Name, byte[] Data)> Replace(List<(string Name, byte[] Data)> parts, string part, string from, string to) {
        List<(string, byte[])> result = new();
        bool hit = false;
        foreach ((string name, byte[] data) in parts) {
            if (name == part) {
                string text = Encoding.UTF8.GetString(data);
                if (!text.Contains(from, StringComparison.Ordinal)) throw new InvalidOperationException($"'{from}' not found in {part}");
                result.Add((name, Utf8(text.Replace(from, to))));
                hit = true;
            } else {
                result.Add((name, data));
            }
        }
        if (!hit) throw new InvalidOperationException($"part {part} not found");
        return result;
    }

    static string Write(string file, List<(string Name, byte[] Data)> parts) {
        string path = Path.Combine(tmp, file);
        using FileStream fs = File.Create(path);
        using ZipArchive zip = new(fs, ZipArchiveMode.Create);
        foreach ((string name, byte[] data) in parts) {
            using Stream s = zip.CreateEntry(name).Open();
            s.Write(data);
        }
        return path;
    }

    static (int Exit, string Stdout) Run(params string[] args) {
        ProcessStartInfo psi = new(exe) {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        psi.Environment["OOXMLVALIDATOR_NO_FETCH"] = "1";
        psi.Environment["NO_COLOR"] = "1";
        foreach (string a in args) psi.ArgumentList.Add(a);
        using Process p = Process.Start(psi)!;
        Task<string> err = p.StandardError.ReadToEndAsync();
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        err.Wait();
        return (p.ExitCode, output);
    }

    static List<Row> Tsv(string output) {
        List<Row> rows = new();
        string[] lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 1; i < lines.Length; i++) {
            string[] c = lines[i].TrimEnd('\r').Split('\t');
            if (c.Length < 12) continue;
            rows.Add(new Row { Severity = c[1], Id = c[3], Part = c[4], Line = c[5], Sheet = c[7], Cell = c[8] });
        }
        return rows;
    }

    static void Check(string name, string path, int exit, Expect[] expect, params string[] extra) {
        CheckWith(name, path, exit, expect, extra.Prepend("--no-schemas").ToArray());
    }

    static void CheckWith(string name, string path, int exit, Expect[] expect, params string[] extra) {
        List<string> args = new() { "-f", "tsv" };
        args.AddRange(extra);
        args.Add(path);
        (int code, string output) = Run(args.ToArray());
        List<Row> rows = Tsv(output);
        List<string> problems = new();
        if (code != exit) problems.Add($"exit {code}, expected {exit}");
        foreach (Expect e in expect) {
            bool ok = rows.Any(r => r.Id == e.Id &&
                                    (e.Part == null || r.Part == e.Part) &&
                                    (e.Sheet == null || r.Sheet == e.Sheet) &&
                                    (e.Cell == null || r.Cell == e.Cell) &&
                                    (!e.HasLine || r.Line.Length > 0));
            if (!ok) problems.Add($"missing {e.Id}{(e.Part != null ? " in " + e.Part : "")}{(e.Cell != null ? " at " + e.Cell : "")}");
        }
        if (expect.Length == 0) {
            foreach (Row r in rows.Where(r => r.Severity != "info")) problems.Add($"unexpected {r.Severity} {r.Id} in {r.Part}");
        }
        Report(name, problems, rows);
    }

    static void Report(string name, List<string> problems, List<Row>? rows = null) {
        if (problems.Count == 0) {
            passed++;
            Console.WriteLine($"ok   {name}");
            return;
        }
        Console.WriteLine($"FAIL {name}");
        foreach (string p in problems) Console.WriteLine($"       {p}");
        if (rows != null) {
            foreach (Row r in rows) Console.WriteLine($"       got {r.Severity} {r.Id} {r.Part}:{r.Line} {r.Sheet} {r.Cell}");
        }
        failures.Add(name);
    }

    static Expect E(string id, string? part = null, string? sheet = null, string? cell = null, bool line = true) =>
        new() { Id = id, Part = part, Sheet = sheet, Cell = cell, HasLine = line };

    static void Schemas() {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.DoNotVerify), "OOXMLValidator", "schemas");
        if (!File.Exists(Path.Combine(dir, ".ooxmlvalidator-schemas"))) {
            skipped++;
            Console.WriteLine("skip core.xml against the ECMA-376 schemas (run OOXMLValidator --fetch-schemas)");
            return;
        }

        List<(string, byte[])> parts = Workbook();
        parts = Replace(parts, "_rels/.rels", "</Relationships>",
            "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties\" Target=\"docProps/core.xml\"/></Relationships>");
        parts = Replace(parts, "[Content_Types].xml", "</Types>",
            "<Override PartName=\"/docProps/core.xml\" ContentType=\"application/vnd.openxmlformats-package.core-properties+xml\"/></Types>");
        const string core = """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <cp:coreProperties xmlns:cp="http://schemas.openxmlformats.org/package/2006/metadata/core-properties"
              xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:dcterms="http://purl.org/dc/terms/"
              xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
              <dc:creator>someone</dc:creator>
              <dcterms:created xsi:type="dcterms:W3CDTF">2026-10-31T11:00:34Z</dcterms:created>
            </cp:coreProperties>
            """;
        List<(string, byte[])> valid = new(parts) { ("docProps/core.xml", Utf8(core)) };
        CheckWith("core.xml is valid against the ECMA-376 schemas", Write("core.xlsx", valid), 0, Array.Empty<Expect>(), "--schemas", dir);

        List<(string, byte[])> invalid = new(parts) { ("docProps/core.xml", Utf8(core.Replace("2026-10-31T11:00:34Z", "yesterday"))) };
        CheckWith("invalid date in core.xml", Write("core_bad.xlsx", invalid), 1, new[] { E("Xsd_Invalid", "docProps/core.xml") }, "--schemas", dir);
    }

    static void Spreadsheet(string root) {
        const string sheet = "xl/worksheets/sheet1.xml";

        Check("minimal workbook is valid", Write("valid.xlsx", Workbook()), 0, Array.Empty<Expect>());

        Check("invalid enumeration value", Write("enum.xlsx", Workbook("<row r=\"1\"><c r=\"A1\" t=\"above\"><v>1</v></c></row>")), 1,
            new[] { E("Sch_AttributeValueDataTypeDetailed", sheet, "Data", "A1") });

        Check("invalid numeric value", Write("double.xlsx", Workbook("<row r=\"2\" ht=\"tall\"><c r=\"A2\"><v>1</v></c></row>")), 1,
            new[] { E("Sch_AttributeValueDataTypeDetailed", sheet, "Data", "row 2") });

        Check("undeclared attribute", Write("undeclared.xlsx", Workbook("<row r=\"1\"><c r=\"B1\" foo=\"1\"><v>1</v></c></row>")), 1,
            new[] { E("Sch_UndeclaredAttribute", sheet, "Data", "B1") });

        Check("wrong child element", Write("child.xlsx", Workbook("<row r=\"1\"><c r=\"C1\"><v>1</v><v>2</v></c></row>")), 1,
            new[] { E("Sch_UnexpectedElementContentExpectingComplex", sheet, "Data", "C1") });

        Check("missing required attribute",
            Write("required.xlsx", Replace(Workbook(), "xl/workbook.xml", " sheetId=\"1\"", "")), 1,
            new[] { E("Sch_MissRequiredAttribute", "xl/workbook.xml") });

        Check("not well-formed part",
            Write("xml.xlsx", Replace(Workbook(), sheet, "</sheetData>", "</sheetDat>")), 1,
            new[] { E("Xml_NotWellFormed", sheet, "Data") });

        Check("r:id without relationship",
            Write("rid.xlsx", Replace(Workbook(), "xl/workbook.xml", "r:id=\"rId1\"", "r:id=\"rId9\"")), 1,
            new[] { E("Sem_InvalidRelationshipId", "xl/workbook.xml") });

        string chartex = Path.Combine(root, "tests", "fixtures", "chartex.xlsx");
        Check("chartEx fixture is valid", chartex, 0, Array.Empty<Expect>());

        string excel = Path.Combine(root, "tests", "fixtures", "excel-histogram-pareto.xlsx");
        Check("Excel chartEx differs from the specification", excel, 1,
            new[] { E("Sch_UndeclaredAttribute", "xl/charts/chartEx2.xml"), E("Sem_UniqueAttributeValue", "xl/drawings/drawing1.xml") });
        Check("excel-compat.ignore covers Excel chartEx", excel, 0, Array.Empty<Expect>(),
            "--ignore-file", Path.Combine(root, "excel-compat.ignore"));

        string badChart = Write("chartex.xlsx",
            Replace(Load(chartex), "xl/charts/chartEx1.xml", "<cx:plotArea>", "<cx:bogus/><cx:plotArea>"));
        Check("invalid chartEx element", badChart, 1, new[] { E("Sch_InvalidElementContentExpectingComplex", "xl/charts/chartEx1.xml") });
        Check("chartEx is not checked at target Office2007", badChart, 0, Array.Empty<Expect>(), "--target", "Office2007");
    }

    static void Package() {
        const string sheet = "xl/worksheets/sheet1.xml";

        Check("relationship target missing",
            Write("target.xlsx", Replace(Workbook(), "xl/_rels/workbook.xml.rels", "worksheets/sheet1.xml", "worksheets/sheet9.xml")), 1,
            new[] { E("Opc_MissingTarget", "xl/_rels/workbook.xml.rels") });

        Check("fragment-only target refers to the source part",
            Write("fragment.xlsx", Replace(Workbook(), "xl/_rels/workbook.xml.rels", "</Relationships>",
                $"<Relationship Id=\"rId2\" Type=\"{RelNs}/hyperlink\" Target=\"#Data!A1\"/></Relationships>")), 0,
            Array.Empty<Expect>());

        Check("duplicate relationship id",
            Write("dupid.xlsx", Replace(Workbook(), "xl/_rels/workbook.xml.rels", "</Relationships>",
                $"<Relationship Id=\"rId1\" Type=\"{RelNs}/styles\" Target=\"worksheets/sheet1.xml\"/></Relationships>")), 1,
            new[] { E("Opc_DuplicateRelationshipId", "xl/_rels/workbook.xml.rels") });

        Check("malformed external target",
            Write("uri.xlsx", Replace(Workbook(), "xl/_rels/workbook.xml.rels", "</Relationships>",
                $"<Relationship Id=\"rId2\" Type=\"{RelNs}/hyperlink\" Target=\"http://[bad\" TargetMode=\"External\"/></Relationships>")), 1,
            new[] { E("Opc_MalformedUri", "xl/_rels/workbook.xml.rels") });

        List<(string, byte[])> orphan = Workbook();
        orphan.Add(("xl/worksheets/sheet2.xml", orphan.First(p => p.Item1 == sheet).Item2));
        orphan = Replace(orphan, "[Content_Types].xml", "</Types>",
            "<Override PartName=\"/xl/worksheets/sheet2.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/></Types>");
        Check("part in content types but not in any relationship", Write("orphan.xlsx", orphan), 0,
            new[] { E("Opc_UnreferencedPart", "xl/worksheets/sheet2.xml", line: false) });

        List<(string, byte[])> noType = Workbook();
        noType.Add(("xl/media/image1.png", new byte[] { 0x89, 0x50, 0x4E, 0x47 }));
        Check("part without content type", Write("notype.xlsx", noType), 1,
            new[] { E("Opc_MissingContentType", "xl/media/image1.png", line: false) });

        Check("override without part",
            Write("override.xlsx", Replace(Workbook(), "[Content_Types].xml", "</Types>",
                "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/></Types>")), 0,
            new[] { E("Opc_OverrideWithoutPart", "[Content_Types].xml", line: false) });

        List<(string, byte[])> dup = Workbook();
        dup.Add(dup.First(p => p.Item1 == sheet));
        Check("duplicate zip item", Write("dup.xlsx", dup), 1, new[] { E("Opc_DuplicatePart", sheet, line: false) });

        List<(string, byte[])> noRels = Workbook().Where(p => p.Item1 != "_rels/.rels").ToList();
        Check("missing package relationships", Write("norels.xlsx", noRels), 1,
            new[] { E("Opc_MissingPackageRelationships", line: false) });
    }

    static void Formats() {
        string notZip = Path.Combine(tmp, "text.xlsx");
        File.WriteAllText(notZip, "not a zip");
        Check("not a zip file", notZip, 2, new[] { E("Opc_NotZip", line: false) });

        string cfb = Path.Combine(tmp, "encrypted.xlsx");
        File.WriteAllBytes(cfb, new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0, 0, 0 });
        Check("encrypted or legacy file", cfb, 2, new[] { E("Opc_NotZip", line: false) });

        Check("missing file", Path.Combine(tmp, "missing.xlsx"), 2, new[] { E("Io_ReadFailed", line: false) });

        List<(string, byte[])> xlsb = Replace(Workbook(), "[Content_Types].xml",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml",
            "application/vnd.ms-excel.sheet.binary.macroEnabled.main");
        xlsb = xlsb.Select(p => p.Item1 == "xl/workbook.xml" ? (p.Item1, new byte[] { 0x83, 0x01, 0x00, 0x80, 0x01, 0x00 }) : p).ToList();
        Check("binary package is not validated", Write("binary.xlsb", xlsb), 2, new[] { E("Opc_BinaryMainPart", "xl/workbook.xml", line: false) });

        List<(string, byte[])> docx = new() {
            ("[Content_Types].xml", Utf8("""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
                  <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
                </Types>
                """)),
            ("_rels/.rels", Utf8($"""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="{RelNs}/officeDocument" Target="word/document.xml"/>
                </Relationships>
                """)),
            ("word/document.xml", Utf8("""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
                  <w:body><w:p><w:r><w:t>text</w:t></w:r></w:p></w:body>
                </w:document>
                """))
        };
        Check("minimal docx is valid", Write("valid.docx", docx), 0, Array.Empty<Expect>());
        Check("docx named xlsx is detected", Write("docx.xlsx", docx), 0, Array.Empty<Expect>());
        Check("invalid docx element", Write("bad.docx", Replace(docx, "word/document.xml", "<w:t>", "<w:x/><w:t>")), 1,
            new[] { E("Sch_InvalidElementContentExpectingComplex", "word/document.xml") });
    }

    static void Output() {
        string enumFile = Write("text.xlsx", Workbook("<row r=\"1\"><c r=\"A1\" t=\"above\"><v>1</v></c></row>"));

        (int code, string text) = Run("--no-schemas", enumFile);
        List<string> problems = new();
        if (code != 1) problems.Add($"exit {code}, expected 1");
        if (!text.Contains("(xl/worksheets/sheet1.xml):4:", StringComparison.Ordinal)) problems.Add("location file(part):line:column missing");
        if (!text.Contains("<c r=\"A1\" t=\"above\">", StringComparison.Ordinal)) problems.Add("source excerpt missing");
        if (!text.Contains("^~~~~~~~~", StringComparison.Ordinal)) problems.Add("caret missing");
        if (!text.Contains("note: in sheet 'Data', cell A1", StringComparison.Ordinal)) problems.Add("sheet and cell note missing");
        if (!text.Contains("1 error generated", StringComparison.Ordinal)) problems.Add("summary missing");
        if (text.Contains('\u001b')) problems.Add("colors written despite NO_COLOR");
        Report("text output", problems);
        if (problems.Count > 0) Console.WriteLine(text);

        Check("ignore rule by id and path", enumFile, 0, Array.Empty<Expect>(), "--ignore", "Sch_AttributeValueDataTypeDetailed@x:row/x:c");
        Check("ignore rule for another path keeps the error", enumFile, 1,
            new[] { E("Sch_AttributeValueDataTypeDetailed") }, "--ignore", "*@x:sheetData/x:row");

        string ignoreFile = Path.Combine(tmp, "rules.ignore");
        File.WriteAllText(ignoreFile, "# comment\n*@x:c   # any finding on a cell\n");
        Check("ignore file", enumFile, 0, Array.Empty<Expect>(), "--ignore-file", ignoreFile);

        (int jcode, string json) = Run("--no-schemas", "-f", "json", enumFile);
        List<string> jproblems = new();
        if (jcode != 1) jproblems.Add($"exit {jcode}, expected 1");
        if (!json.Contains("\"status\": \"invalid\"", StringComparison.Ordinal)) jproblems.Add("status missing");
        if (!json.Contains("\"cell\": \"A1\"", StringComparison.Ordinal)) jproblems.Add("cell missing");
        Report("json output", jproblems);

        (int qcode, string quiet) = Run("--no-schemas", "-q", Write("quiet.xlsx", Workbook()));
        Report("quiet prints nothing for valid files",
            qcode == 0 && quiet.Length == 0 ? new List<string>() : new List<string> { $"exit {qcode}, output '{quiet.Trim()}'" });

        (int ucode, _) = Run("--bogus");
        Report("unknown option", ucode == 2 ? new List<string>() : new List<string> { $"exit {ucode}, expected 2" });
    }
}
