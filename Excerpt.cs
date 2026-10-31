using System.IO.Compression;
using System.Text;
using System.Xml;

namespace OOXMLValidator;

static class Excerpts {
    const int Before = 40;
    const int After = 60;
    const int MaxCaret = 80;
    const int MaxCapture = 1 << 21;

    public static void Decorate(PackageInfo pkg, List<Finding> fs) {
        Dictionary<string, List<Finding>> byPart = new(StringComparer.OrdinalIgnoreCase);
        foreach (Finding f in fs) {
            if (f.Part.Length == 0) continue;
            if (!byPart.TryGetValue(f.Part, out List<Finding>? list)) {
                list = new List<Finding>();
                byPart[f.Part] = list;
            }
            list.Add(f);
        }
        foreach (KeyValuePair<string, List<Finding>> kv in byPart) {
            if (!pkg.Entries.TryGetValue(kv.Key, out ZipArchiveEntry? entry)) continue;
            ShortenNamespaces(entry, kv.Value);
            try {
                Capture(entry, kv.Value);
            } catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is DecoderFallbackException) {
            }
        }
    }

    static void ShortenNamespaces(ZipArchiveEntry entry, List<Finding> fs) {
        Dictionary<string, string> prefixes = new(StringComparer.Ordinal);
        try {
            using XmlReader r = XmlReader.Create(entry.Open(), PackageChecks.ReaderSettings);
            r.MoveToContent();
            if (r.MoveToFirstAttribute()) {
                do {
                    if (r.Prefix == "xmlns" && r.Value.Length > 0) prefixes.TryAdd(r.Value, r.LocalName);
                } while (r.MoveToNextAttribute());
            }
        } catch (Exception ex) when (ex is XmlException || ex is IOException || ex is InvalidDataException) {
            return;
        }
        if (prefixes.Count == 0) return;
        List<KeyValuePair<string, string>> ordered = prefixes.OrderByDescending(p => p.Key.Length).ToList();
        foreach (Finding f in fs) {
            string d = f.Description;
            foreach (KeyValuePair<string, string> p in ordered) d = d.Replace(p.Key + ":", p.Value + ":", StringComparison.Ordinal);
            f.Description = d;
        }
    }

    sealed class LineCapture {
        public List<Finding> Findings = new();
        public int Start;
        public int End;
        public StringBuilder Text = new();
        public int Length;
    }

    static void Capture(ZipArchiveEntry entry, List<Finding> fs) {
        Dictionary<int, LineCapture> lines = new();
        int maxLine = 0;
        foreach (Finding f in fs) {
            if (f.Line <= 0 || f.Column <= 0) continue;
            if (!lines.TryGetValue(f.Line, out LineCapture? lc)) {
                lc = new LineCapture { Start = int.MaxValue, End = 0 };
                lines[f.Line] = lc;
            }
            lc.Findings.Add(f);
            lc.Start = Math.Min(lc.Start, Math.Max(1, f.Column - Before - 1));
            lc.End = Math.Max(lc.End, f.Column + MaxCaret + After + 400);
            maxLine = Math.Max(maxLine, f.Line);
        }
        if (lines.Count == 0) return;
        foreach (LineCapture lc in lines.Values) {
            if (lc.End - lc.Start > MaxCapture) lc.End = lc.Start + MaxCapture;
        }

        using StreamReader sr = new(entry.Open(), Encoding.UTF8, true);
        char[] buf = new char[1 << 16];
        int line = 1, col = 1;
        bool prevCr = false;
        lines.TryGetValue(1, out LineCapture? cur);
        int n;
        while ((n = sr.Read(buf, 0, buf.Length)) > 0) {
            for (int i = 0; i < n; i++) {
                char c = buf[i];
                if (prevCr && c == '\n') {
                    prevCr = false;
                    continue;
                }
                prevCr = false;
                if (c == '\n' || c == '\r') {
                    if (cur != null) {
                        cur.Length = col - 1;
                        Build(cur);
                    }
                    line++;
                    col = 1;
                    prevCr = c == '\r';
                    if (line > maxLine) return;
                    lines.TryGetValue(line, out cur);
                    continue;
                }
                if (cur != null && col >= cur.Start && col <= cur.End) cur.Text.Append(c == '\t' ? ' ' : c);
                col++;
            }
        }
        if (cur != null) {
            cur.Length = col - 1;
            Build(cur);
        }
    }

    static string? AttributeName(string d) {
        string? name = null;
        int i = d.IndexOf("attribute '", StringComparison.Ordinal);
        if (i >= 0) {
            int start = i + 11;
            int end = d.IndexOf('\'', start);
            if (end > start) name = d[start..end];
        } else {
            i = d.IndexOf("' attribute", StringComparison.Ordinal);
            if (i > 0) {
                int start = d.LastIndexOf('\'', i - 1) + 1;
                if (start > 0) name = d[start..i];
            }
        }
        return name == null ? null : name[(name.LastIndexOf(':') + 1)..];
    }

    static int SkipQuoted(string t, int i) {
        char q = t[i];
        int j = t.IndexOf(q, i + 1);
        return j < 0 ? t.Length - 1 : j;
    }

    static void Build(LineCapture lc) {
        string t = lc.Text.ToString();
        foreach (Finding f in lc.Findings) {
            int pos = f.Column - lc.Start;
            if (pos < 0 || pos >= t.Length) continue;

            int caret = pos;
            int len = 1;
            if (f.Id != "Xml_NotWellFormed" && pos > 0 && t[pos - 1] == '<') {
                int tagStart = pos - 1;
                int tagEnd = tagStart;
                while (tagEnd < t.Length && t[tagEnd] != '>') {
                    if (t[tagEnd] == '"' || t[tagEnd] == '\'') tagEnd = SkipQuoted(t, tagEnd);
                    tagEnd++;
                }
                caret = tagStart;
                len = Math.Min(tagEnd, t.Length - 1) - tagStart + 1;
                string? attr = AttributeName(f.Description);
                if (attr != null) {
                    int k = tagStart;
                    while (k < tagStart + len) {
                        int hit = t.IndexOf(attr + "=", k, tagStart + len - k, StringComparison.Ordinal);
                        if (hit < 0) break;
                        char before = t[hit - 1];
                        if (before == ' ' || before == ':' || before == '\t') {
                            int nameStart = hit;
                            while (nameStart > tagStart && t[nameStart - 1] != ' ' && t[nameStart - 1] != '\t') nameStart--;
                            int v = hit + attr.Length + 1;
                            int vEnd = v < t.Length && (t[v] == '"' || t[v] == '\'') ? SkipQuoted(t, v) : v;
                            caret = nameStart;
                            len = vEnd - nameStart + 1;
                            break;
                        }
                        k = hit + 1;
                    }
                }
            } else if (f.Id != "Xml_NotWellFormed") {
                int eq = t.IndexOf('=', pos);
                if (eq > pos && eq - pos < 100 && eq + 1 < t.Length && (t[eq + 1] == '"' || t[eq + 1] == '\'')) len = SkipQuoted(t, eq + 1) - pos + 1;
            }
            len = Math.Clamp(len, 1, MaxCaret);

            int from = Math.Max(0, caret - Before);
            int to = Math.Min(t.Length, caret + len + After);
            string view = t[from..to];
            bool cutLeft = lc.Start + from > 1;
            bool cutRight = lc.Start + to - 1 < lc.Length;
            f.Excerpt = (cutLeft ? "..." : "") + view + (cutRight ? "..." : "");
            f.CaretStart = caret - from + (cutLeft ? 3 : 0);
            f.CaretLength = Math.Min(len, view.Length - (caret - from));
        }
    }
}
