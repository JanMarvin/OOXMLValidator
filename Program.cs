using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DocumentFormat.OpenXml;

namespace OOXMLValidator;

sealed class IgnoreRule {
    public string Id = "*";
    public string? Path;

    public static IgnoreRule Parse(string text) {
        int at = text.IndexOf('@');
        IgnoreRule rule = new() { Id = at < 0 ? text : text[..at] };
        if (rule.Id.Length == 0) rule.Id = "*";
        if (at >= 0 && at + 1 < text.Length) rule.Path = text[(at + 1)..];
        return rule;
    }

    public bool Matches(Finding f) {
        if (Id != "*" && Id != f.Id) return false;
        if (Path == null) return true;
        StringBuilder sb = new(f.XPath.Length);
        int depth = 0;
        foreach (char c in f.XPath) {
            if (c == '[') depth++;
            else if (c == ']') depth--;
            else if (depth == 0) sb.Append(c);
        }
        string plain = sb.ToString();
        return Path.StartsWith('/') ? plain == Path : plain.EndsWith("/" + Path, StringComparison.Ordinal);
    }
}

sealed class Options {
    public FileFormatVersions Target = Latest();
    public int MaxErrors;
    public string Format = "text";
    public List<IgnoreRule> Ignore = new();
    public string? SchemaDir;
    public bool NoSchemas;
    public bool Fetch;
    public bool NoFetch;
    public bool FetchStrict;
    public bool SchemasAll;
    public bool Info;
    public bool Quiet;
    public string Color = "auto";
    public int Jobs = Environment.ProcessorCount;
    public List<string> Inputs = new();

    public static FileFormatVersions Latest() {
        FileFormatVersions max = FileFormatVersions.None;
        foreach (FileFormatVersions v in Enum.GetValues<FileFormatVersions>()) {
            if (v > max) max = v;
        }
        return max;
    }
}

static class Program {
    static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) {
        ".xlsx", ".xlsm", ".xltx", ".xltm", ".xlam",
        ".docx", ".docm", ".dotx", ".dotm",
        ".pptx", ".pptm", ".potx", ".potm", ".ppsx", ".ppsm", ".ppam"
    };

    const string Usage = """
        usage: OOXMLValidator [options] <file|directory|->...

        Validates OOXML packages (xlsx, docx, pptx and their variants) against the
        package rules (OPC) and the schemas and semantic constraints of the Open XML SDK.

          -t, --target <version>   file format version to validate against: latest (default),
                                   or one of --list-targets
          -f, --format <format>    text (default), tsv or json
          -m, --max-errors <n>     stop SDK validation after n errors per file (0 = no limit, default)
          -i, --ignore <rule,...>  drop matching findings (repeatable); a rule is an id, optionally
                                   followed by @ and the end of the element path without indices,
                                   e.g. Sch_UndeclaredAttribute@cx:binning/cx:binSize; '*' matches any id
              --ignore-file <file> read ignore rules from a file, one per line, '#' starts a comment
          -s, --schemas <dir>      additional XSD files; content unknown to the SDK whose namespace
                                   has a schema in <dir> is validated against it. Default:
                                   $OOXMLVALIDATOR_SCHEMAS, else a schemas folder next to the program
                                   or in a parent folder, else the per-user data folder. If none is
                                   found, the schemas are downloaded once into the per-user folder
              --no-schemas         do not use additional XSD files
              --no-fetch           never download automatically (also: OOXMLVALIDATOR_NO_FETCH=1)
              --fetch-schemas      download or update the ECMA-376 and Dublin Core schemas into the
                                   schema folder in use (default: the per-user folder) and exit
              --fetch-strict       as --fetch-schemas, also ECMA-376 Part 1 with the Strict schemas
              --schemas-all        validate every XML part with a matching schema in <dir>, not
                                   only content unknown to the SDK
              --info               report content that could not be validated
          -q, --quiet              print nothing for valid files
              --color <when>       auto (default), always or never; NO_COLOR is honored
          -j, --jobs <n>           files validated in parallel (default: number of cores)
              --list-targets       print the available target versions
              --version            print program and SDK versions
          -h, --help               print this help

        A directory is searched recursively for OOXML files, '-' reads paths from stdin.
        Exit status: 0 all files valid, 1 at least one file has errors, 2 a file could not be
        read or validated, or invalid arguments.
        """;

    static int Main(string[] args) {
        Options opt = new();
        try {
            if (!Parse(args, opt, out int exit)) return exit;
        } catch (ArgumentException ex) {
            Console.Error.WriteLine($"OOXMLValidator: {ex.Message}");
            Console.Error.WriteLine("run with --help for usage");
            return 2;
        }

        List<string> files = new();
        foreach (string input in opt.Inputs) {
            if (input == "-") {
                string? line;
                while ((line = Console.In.ReadLine()) != null) {
                    if (line.Trim().Length > 0) files.Add(line.Trim());
                }
            } else if (Directory.Exists(input)) {
                List<string> found = new();
                foreach (string f in Directory.EnumerateFiles(input, "*", SearchOption.AllDirectories)) {
                    if (Extensions.Contains(Path.GetExtension(f)) && !Path.GetFileName(f).StartsWith("~$", StringComparison.Ordinal)) found.Add(f);
                }
                found.Sort(StringComparer.Ordinal);
                files.AddRange(found);
            } else {
                files.Add(input);
            }
        }

        string? userDir = SchemaFetcher.UserDir();
        if (opt.SchemaDir == null && !opt.NoSchemas) {
            string? env = Environment.GetEnvironmentVariable("OOXMLVALIDATOR_SCHEMAS");
            if (!string.IsNullOrEmpty(env)) {
                opt.SchemaDir = env;
            } else {
                DirectoryInfo? d = new(AppContext.BaseDirectory);
                for (int depth = 0; d != null && depth < 8; depth++, d = d.Parent) {
                    string candidate = Path.Combine(d.FullName, "schemas");
                    if (File.Exists(Path.Combine(candidate, SchemaFetcher.Marker))) {
                        opt.SchemaDir = candidate;
                        break;
                    }
                }
                if (opt.SchemaDir == null && userDir != null && File.Exists(Path.Combine(userDir, SchemaFetcher.Marker)))
                    opt.SchemaDir = userDir;
            }
        }

        if (opt.Fetch) {
            string? target = opt.SchemaDir ?? userDir;
            if (target == null) {
                Console.Error.WriteLine("OOXMLValidator: no folder for the schemas, use --schemas <dir>");
                return 2;
            }
            try {
                return SchemaFetcher.Run(target, opt.FetchStrict, false, TimeSpan.FromSeconds(30), Console.Out);
            } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) {
                Console.Error.WriteLine($"OOXMLValidator: cannot write schemas to '{target}': {ex.Message}");
                return 2;
            }
        }

        bool fetchAllowed = !opt.NoFetch && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OOXMLVALIDATOR_NO_FETCH"));
        bool userIncomplete = userDir != null && (opt.SchemaDir == null || opt.SchemaDir == userDir) && SchemaFetcher.Incomplete(userDir);
        if (userIncomplete && !opt.NoSchemas && fetchAllowed && opt.Inputs.Count > 0) {
            string stamp = Path.Combine(userDir!, SchemaFetcher.FailedStamp);
            if (!File.Exists(stamp) || File.GetLastWriteTimeUtc(stamp) < DateTime.UtcNow.AddHours(-24)) {
                Console.Error.WriteLine(opt.SchemaDir == null
                    ? $"OOXMLValidator: no schemas found, downloading them once to '{userDir}' (--no-fetch to skip)"
                    : $"OOXMLValidator: schemas in '{userDir}' are incomplete, completing them (--no-fetch to skip)");
                int rc;
                try {
                    rc = SchemaFetcher.Run(userDir!, false, true, TimeSpan.FromSeconds(10), Console.Error);
                } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) {
                    Console.Error.WriteLine($"OOXMLValidator: cannot write schemas to '{userDir}': {ex.Message}");
                    rc = 2;
                }
                if (rc == 0) {
                    opt.SchemaDir = userDir;
                } else {
                    try {
                        Directory.CreateDirectory(userDir!);
                        File.WriteAllText(stamp, DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
                    } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) {
                        Console.Error.WriteLine($"OOXMLValidator: {ex.Message}");
                    }
                    Console.Error.WriteLine("OOXMLValidator: continuing without additional schemas, next attempt in 24 hours or with --fetch-schemas");
                }
            }
        }

        if (opt.SchemasAll && opt.SchemaDir == null) {
            Console.Error.WriteLine("OOXMLValidator: --schemas-all needs a schema directory, see --schemas");
            return 2;
        }

        SchemaStore? schemas = null;
        if (opt.SchemaDir != null) {
            if (!Directory.Exists(opt.SchemaDir)) {
                Console.Error.WriteLine($"OOXMLValidator: schema directory '{opt.SchemaDir}' does not exist");
                return 2;
            }
            schemas = new SchemaStore(opt.SchemaDir);
            if (schemas.NamespaceCount == 0) Console.Error.WriteLine($"OOXMLValidator: no schemas found in '{opt.SchemaDir}'");
        }

        FileReport[] reports = new FileReport[files.Count];
        Parallel.For(0, files.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, opt.Jobs) }, i => {
            FileReport rep;
            try {
                rep = Validation.Run(files[i], opt, schemas);
            } catch (Exception ex) {
                rep = new FileReport { Path = files[i], Fatal = true };
                rep.Findings.Add(new Finding {
                    Category = "Internal", Id = "Internal_Exception",
                    Description = $"{ex.GetType().Name}: {ex.Message}"
                });
            }
            Postprocess(rep, opt);
            reports[i] = rep;
        });

        using Stream stdout = Console.OpenStandardOutput();
        if (opt.Format == "json") {
            WriteJson(stdout, reports);
        } else {
            using StreamWriter w = new(stdout, new UTF8Encoding(false), 1 << 16);
            if (opt.Format == "tsv") WriteTsv(w, reports);
            else WriteText(w, reports, opt, schemas != null);
        }

        if (schemas != null) {
            foreach (string issue in schemas.Issues.Keys.OrderBy(s => s, StringComparer.Ordinal))
                Console.Error.WriteLine($"OOXMLValidator: schema issue: {issue}");
        }

        int status = 0;
        foreach (FileReport r in reports) {
            if (r.Fatal) status = 2;
            else if (status == 0 && r.Count(Severity.Error) > 0) status = 1;
        }
        return status;
    }

    static string Value(string[] args, ref int i) {
        string a = args[i];
        int eq = a.IndexOf('=');
        if (a.StartsWith("--", StringComparison.Ordinal) && eq > 0) return a[(eq + 1)..];
        if (i + 1 >= args.Length) throw new ArgumentException($"option '{a}' needs a value");
        return args[++i];
    }

    static bool Parse(string[] args, Options opt, out int exit) {
        exit = 0;
        bool positional = false;
        for (int i = 0; i < args.Length; i++) {
            string a = args[i];
            if (positional || a == "-" || !a.StartsWith('-')) {
                opt.Inputs.Add(a);
                continue;
            }
            string name = a.Contains('=') && a.StartsWith("--", StringComparison.Ordinal) ? a[..a.IndexOf('=')] : a;
            switch (name) {
                case "--":
                    positional = true;
                    break;
                case "-h":
                case "--help":
                    Console.WriteLine(Usage);
                    return false;
                case "--version":
                    Assembly sdk = typeof(OpenXmlElement).Assembly;
                    string sdkVersion = sdk.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                                        ?? sdk.GetName().Version?.ToString() ?? "unknown";
                    int plus = sdkVersion.IndexOf('+');
                    if (plus > 0) sdkVersion = sdkVersion[..plus];
                    string own = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "unknown";
                    Console.WriteLine($"OOXMLValidator {own}, DocumentFormat.OpenXml {sdkVersion}, latest target {Options.Latest()}");
                    return false;
                case "--list-targets":
                    foreach (FileFormatVersions v in Enum.GetValues<FileFormatVersions>()) {
                        if (v != FileFormatVersions.None) Console.WriteLine(v);
                    }
                    return false;
                case "-t":
                case "--target":
                    string t = Value(args, ref i);
                    if (t.Equals("latest", StringComparison.OrdinalIgnoreCase)) {
                        opt.Target = Options.Latest();
                    } else if (!Enum.TryParse(t, true, out opt.Target) || opt.Target == FileFormatVersions.None ||
                               !Enum.IsDefined(opt.Target)) {
                        throw new ArgumentException($"unknown target '{t}', see --list-targets");
                    }
                    break;
                case "-f":
                case "--format":
                    opt.Format = Value(args, ref i).ToLowerInvariant();
                    if (opt.Format != "text" && opt.Format != "tsv" && opt.Format != "json")
                        throw new ArgumentException($"unknown format '{opt.Format}', use text, tsv or json");
                    break;
                case "-m":
                case "--max-errors":
                    if (!int.TryParse(Value(args, ref i), out opt.MaxErrors) || opt.MaxErrors < 0)
                        throw new ArgumentException("--max-errors needs a non-negative integer");
                    break;
                case "-j":
                case "--jobs":
                    if (!int.TryParse(Value(args, ref i), out opt.Jobs) || opt.Jobs < 1)
                        throw new ArgumentException("--jobs needs a positive integer");
                    break;
                case "-i":
                case "--ignore":
                    foreach (string rule in Value(args, ref i).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        opt.Ignore.Add(IgnoreRule.Parse(rule));
                    break;
                case "--ignore-file":
                    string file = Value(args, ref i);
                    string[] lines;
                    try {
                        lines = File.ReadAllLines(file);
                    } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) {
                        throw new ArgumentException($"cannot read ignore file '{file}': {ex.Message}");
                    }
                    foreach (string line in lines) {
                        int hash = line.IndexOf('#');
                        string rule = (hash >= 0 ? line[..hash] : line).Trim();
                        if (rule.Length > 0) opt.Ignore.Add(IgnoreRule.Parse(rule));
                    }
                    break;
                case "-s":
                case "--schemas":
                    opt.SchemaDir = Value(args, ref i);
                    break;
                case "--fetch-schemas":
                    opt.Fetch = true;
                    break;
                case "--fetch-strict":
                    opt.Fetch = true;
                    opt.FetchStrict = true;
                    break;
                case "--no-fetch":
                    opt.NoFetch = true;
                    break;
                case "--no-schemas":
                    opt.NoSchemas = true;
                    break;
                case "--schemas-all":
                    opt.SchemasAll = true;
                    break;
                case "--info":
                    opt.Info = true;
                    break;
                case "--color":
                    opt.Color = Value(args, ref i).ToLowerInvariant();
                    if (opt.Color != "auto" && opt.Color != "always" && opt.Color != "never")
                        throw new ArgumentException($"unknown color mode '{opt.Color}', use auto, always or never");
                    break;
                case "-q":
                case "--quiet":
                    opt.Quiet = true;
                    break;
                default:
                    throw new ArgumentException($"unknown option '{a}'");
            }
        }
        if (opt.Inputs.Count == 0 && !opt.Fetch) {
            Console.Error.WriteLine(Usage);
            exit = 2;
            return false;
        }
        return true;
    }

    static void Postprocess(FileReport rep, Options opt) {
        IEnumerable<Finding> kept = rep.Findings;
        if (opt.Ignore.Count > 0) kept = kept.Where(f => !opt.Ignore.Any(rule => rule.Matches(f)));
        if (!opt.Info && opt.Format != "text") kept = kept.Where(f => f.Severity != Severity.Info);
        rep.Findings = kept
            .OrderBy(f => f.Part.Length == 0 ? 0 : 1)
            .ThenBy(f => f.Part, StringComparer.Ordinal)
            .ThenBy(f => f.Line)
            .ThenBy(f => f.Column)
            .ToList();
    }

    static string Plural(int n, string word) => n == 1 ? $"1 {word}" : $"{n} {word}s";

    static void WriteText(StreamWriter w, FileReport[] reports, Options opt, bool haveSchemas) {
        bool color = opt.Color == "always" ||
                     (opt.Color == "auto" && !Console.IsOutputRedirected &&
                      string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR")) &&
                      Environment.GetEnvironmentVariable("TERM") != "dumb" &&
                      (!OperatingSystem.IsWindows() || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WT_SESSION"))));
        string bold = color ? "\u001b[1m" : "";
        string red = color ? "\u001b[1;31m" : "";
        string magenta = color ? "\u001b[1;35m" : "";
        string cyan = color ? "\u001b[1;36m" : "";
        string green = color ? "\u001b[1;32m" : "";
        string reset = color ? "\u001b[0m" : "";

        foreach (FileReport r in reports) {
            int errors = r.Count(Severity.Error);
            int warnings = r.Count(Severity.Warning);
            int infos = r.Count(Severity.Info);
            if (opt.Quiet && !r.Fatal && errors == 0 && warnings == 0) continue;

            foreach (Finding f in r.Findings) {
                if (f.Severity == Severity.Info && !opt.Info) continue;
                string where = f.Part.Length == 0 ? r.Path : $"{r.Path}({f.Part})";
                if (f.Line > 0) where += $":{f.Line}:{f.Column}";
                string sev = f.Severity switch {
                    Severity.Error => red + "error:",
                    Severity.Warning => magenta + "warning:",
                    _ => cyan + "note:"
                };
                string msg = f.Description.EndsWith('.') ? f.Description[..^1] : f.Description;
                w.WriteLine($"{bold}{where}: {sev}{reset} {bold}{msg}{reset} [{f.Id}]");

                if (f.Excerpt.Length > 0) {
                    string gutter = f.Line.ToString();
                    string pad = new(' ', gutter.Length);
                    w.WriteLine($"  {gutter} | {f.Excerpt}");
                    w.WriteLine($"  {pad} | {new string(' ', f.CaretStart)}{green}^{new string('~', Math.Max(0, f.CaretLength - 1))}{reset}");
                } else if (f.XPath.Length > 0) {
                    w.WriteLine($"  {cyan}note:{reset} at {f.XPath}");
                }
                if (f.Sheet.Length > 0 || f.Cell.Length > 0) {
                    string loc = f.Sheet.Length > 0 ? $"sheet '{f.Sheet}'" : "";
                    if (f.Cell.Length > 0) loc += (loc.Length > 0 ? ", " : "") + (f.Cell.StartsWith("row ", StringComparison.Ordinal) ? f.Cell : "cell " + f.Cell);
                    w.WriteLine($"  {cyan}note:{reset} in {loc}");
                }
            }

            if (!opt.Info && !opt.Quiet && infos > 0) {
                w.WriteLine(haveSchemas
                    ? $"{bold}{r.Path}: {cyan}note:{reset} {Plural(infos, "part")} not covered by any schema, --info lists them and scans for unknown content"
                    : $"{bold}{r.Path}: {cyan}note:{reset} {Plural(infos, "part")} not covered by the SDK schemas, run with --fetch-schemas to add the ECMA-376 schemas, --info lists them");
            }

            List<string> counts = new();
            if (errors > 0) counts.Add(Plural(errors, "error"));
            if (warnings > 0) counts.Add(Plural(warnings, "warning"));
            if (r.Fatal) w.WriteLine($"{r.Path}: not validated");
            else if (counts.Count > 0) w.WriteLine($"{r.Path}: {string.Join(" and ", counts)} generated");
            else w.WriteLine($"{r.Path}: valid");
        }
    }

    static string Tsv(string s) => s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

    static void WriteTsv(StreamWriter w, FileReport[] reports) {
        w.WriteLine("file\tseverity\tcategory\tid\tpart\tline\tcolumn\tsheet\tcell\txpath\tnode\tdescription");
        foreach (FileReport r in reports) {
            foreach (Finding f in r.Findings) {
                string sev = f.Severity switch { Severity.Error => "error", Severity.Warning => "warning", _ => "info" };
                w.Write(Tsv(r.Path)); w.Write('\t');
                w.Write(sev); w.Write('\t');
                w.Write(Tsv(f.Category)); w.Write('\t');
                w.Write(Tsv(f.Id)); w.Write('\t');
                w.Write(Tsv(f.Part)); w.Write('\t');
                w.Write(f.Line > 0 ? f.Line.ToString() : ""); w.Write('\t');
                w.Write(f.Column > 0 ? f.Column.ToString() : ""); w.Write('\t');
                w.Write(Tsv(f.Sheet)); w.Write('\t');
                w.Write(Tsv(f.Cell)); w.Write('\t');
                w.Write(Tsv(f.XPath)); w.Write('\t');
                w.Write(Tsv(f.Node)); w.Write('\t');
                w.WriteLine(Tsv(f.Description));
            }
        }
    }

    static void WriteJson(Stream stdout, FileReport[] reports) {
        using Utf8JsonWriter j = new(stdout, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        j.WriteStartArray();
        foreach (FileReport r in reports) {
            int errors = r.Count(Severity.Error);
            j.WriteStartObject();
            j.WriteString("file", r.Path);
            j.WriteString("kind", r.Kind);
            j.WriteString("status", r.Fatal ? "not validated" : errors > 0 ? "invalid" : "valid");
            j.WriteNumber("errors", errors);
            j.WriteNumber("warnings", r.Count(Severity.Warning));
            j.WriteNumber("infos", r.Count(Severity.Info));
            j.WriteStartArray("findings");
            foreach (Finding f in r.Findings) {
                j.WriteStartObject();
                j.WriteString("severity", f.Severity switch { Severity.Error => "error", Severity.Warning => "warning", _ => "info" });
                j.WriteString("category", f.Category);
                j.WriteString("id", f.Id);
                j.WriteString("part", f.Part);
                if (f.Line > 0) {
                    j.WriteNumber("line", f.Line);
                    j.WriteNumber("column", f.Column);
                }
                if (f.Sheet.Length > 0) j.WriteString("sheet", f.Sheet);
                if (f.Cell.Length > 0) j.WriteString("cell", f.Cell);
                if (f.XPath.Length > 0) j.WriteString("xpath", f.XPath);
                if (f.Node.Length > 0) j.WriteString("node", f.Node);
                j.WriteString("description", f.Description);
                j.WriteEndObject();
            }
            j.WriteEndArray();
            j.WriteEndObject();
        }
        j.WriteEndArray();
        j.Flush();
        stdout.WriteByte((byte)'\n');
    }
}
