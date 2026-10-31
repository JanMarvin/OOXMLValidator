using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;

namespace OOXMLValidator;

static class SchemaFetcher {
    public const string Marker = ".ooxmlvalidator-schemas";

    static readonly (string Name, string Url, bool Strict)[] Sources = {
        ("ecma-376-2", "https://ecma-international.org/wp-content/uploads/ECMA-376-2_5th_edition_december_2021.zip", false),
        ("ecma-376-4", "https://ecma-international.org/wp-content/uploads/ECMA-376-4_5th_edition_december_2016.zip", false),
        ("dcmi", "https://www.dublincore.org/schemas/xmls/qdc/2003/04/02/dc.xsd", false),
        ("dcmi", "https://www.dublincore.org/schemas/xmls/qdc/2003/04/02/dcterms.xsd", false),
        ("dcmi", "https://www.dublincore.org/schemas/xmls/qdc/2003/04/02/dcmitype.xsd", false),
        ("w3c", "https://www.w3.org/2001/xml.xsd", false),
        ("ecma-376-1", "https://ecma-international.org/wp-content/uploads/ECMA-376-1_5th_edition_december_2016.zip", true)
    };

    public const string FailedStamp = ".fetch-failed";

    public static string? UserDir() {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);
        return root.Length == 0 ? null : Path.Combine(root, "OOXMLValidator", "schemas");
    }

    public static bool Incomplete(string dir) {
        if (!File.Exists(Path.Combine(dir, Marker))) return true;
        foreach ((string name, string _, bool isStrict) in Sources) {
            if (!isStrict && !Directory.Exists(Path.Combine(dir, name))) return true;
        }
        return false;
    }

    public static int Run(string dir, bool strict, bool skipIfPresent, TimeSpan connectTimeout, TextWriter log) {
        Directory.CreateDirectory(dir);
        FileStream? lockFile = null;
        DateTime until = DateTime.UtcNow.AddMinutes(10);
        while (lockFile == null) {
            try {
                lockFile = new FileStream(Path.Combine(dir, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            } catch (IOException) {
                if (DateTime.UtcNow > until) {
                    Console.Error.WriteLine($"OOXMLValidator: another process keeps '{dir}' locked");
                    return 2;
                }
                Thread.Sleep(500);
            }
        }
        using (lockFile) {
            if (skipIfPresent && !Incomplete(dir)) return 0;
            int rc = Update(dir, strict, connectTimeout, log);
            if (rc == 0) File.Delete(Path.Combine(dir, FailedStamp));
            return rc;
        }
    }

    static int Update(string dir, bool strict, TimeSpan connectTimeout, TextWriter log) {
        string cache = Path.Combine(dir, ".download");
        Directory.CreateDirectory(cache);
        SortedSet<string> changed = new(StringComparer.Ordinal);
        bool complete = File.Exists(Path.Combine(dir, Marker));

        using SocketsHttpHandler handler = new() { ConnectTimeout = connectTimeout };
        using HttpClient http = new(handler) { Timeout = TimeSpan.FromMinutes(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("OOXMLValidator");

        foreach ((string name, string url, bool isStrict) in Sources) {
            if (isStrict && !strict) continue;
            string file = Path.Combine(cache, Path.GetFileName(new Uri(url).AbsolutePath));
            string tmp = file + ".part";
            log.WriteLine($"checking {url}");
            try {
                using HttpRequestMessage req = new(HttpMethod.Get, url);
                if (File.Exists(file)) req.Headers.IfModifiedSince = File.GetLastWriteTimeUtc(file);
                using HttpResponseMessage resp = http.Send(req, HttpCompletionOption.ResponseHeadersRead);
                if (resp.StatusCode != HttpStatusCode.NotModified) {
                    resp.EnsureSuccessStatusCode();
                    using (Stream body = resp.Content.ReadAsStream())
                    using (FileStream fsOut = File.Create(tmp)) {
                        body.CopyTo(fsOut);
                    }
                    bool same = File.Exists(file) &&
                                SHA256.HashData(File.ReadAllBytes(tmp)).AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes(file)));
                    if (same) {
                        File.Delete(tmp);
                    } else {
                        File.Move(tmp, file, true);
                        changed.Add(name);
                    }
                    File.SetLastWriteTimeUtc(file, resp.Content.Headers.LastModified?.UtcDateTime ?? DateTime.UtcNow);
                }
            } catch (Exception ex) when (ex is HttpRequestException || ex is IOException || ex is TaskCanceledException ||
                                         ex is UnauthorizedAccessException) {
                if (File.Exists(tmp)) File.Delete(tmp);
                Console.Error.WriteLine($"OOXMLValidator: download of {url} failed: {ex.Message}");
                return 2;
            }
            if (!complete || !Directory.Exists(Path.Combine(dir, name))) changed.Add(name);
        }

        if (changed.Count > 0) File.Delete(Path.Combine(dir, Marker));

        foreach (string name in changed) {
            log.WriteLine($"extracting {name}");
            string target = Path.Combine(dir, name);
            if (Directory.Exists(target)) Directory.Delete(target, true);
            Directory.CreateDirectory(target);
            int count = 0;
            foreach ((string n, string url, bool _) in Sources) {
                if (n != name) continue;
                string file = Path.Combine(cache, Path.GetFileName(new Uri(url).AbsolutePath));
                if (file.EndsWith(".xsd", StringComparison.OrdinalIgnoreCase)) {
                    File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
                    count++;
                    continue;
                }
                try {
                    using ZipArchive outer = ZipFile.OpenRead(file);
                    foreach (ZipArchiveEntry e in outer.Entries) {
                        if (e.FullName.EndsWith(".xsd", StringComparison.OrdinalIgnoreCase)) {
                            e.ExtractToFile(Path.Combine(target, e.Name), true);
                            count++;
                        } else if (e.FullName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) {
                            string sub = Path.GetFullPath(Path.Combine(target, Path.GetFileNameWithoutExtension(e.Name)));
                            using MemoryStream ms = new();
                            using (Stream s = e.Open()) {
                                s.CopyTo(ms);
                            }
                            ms.Position = 0;
                            using ZipArchive inner = new(ms, ZipArchiveMode.Read);
                            foreach (ZipArchiveEntry ie in inner.Entries) {
                                if (!ie.FullName.EndsWith(".xsd", StringComparison.OrdinalIgnoreCase)) continue;
                                string dest = Path.GetFullPath(Path.Combine(sub, ie.FullName));
                                if (!dest.StartsWith(sub + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
                                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                                ie.ExtractToFile(dest, true);
                                count++;
                            }
                        }
                    }
                } catch (InvalidDataException ex) {
                    Console.Error.WriteLine($"OOXMLValidator: {file} is not a valid zip file: {ex.Message}");
                    return 2;
                }
            }
            if (count == 0) {
                Console.Error.WriteLine($"OOXMLValidator: no .xsd files found for {name}, the download layout may have changed");
                return 2;
            }
            log.WriteLine($"  {count} schema files");
        }

        using (StreamWriter w = new(Path.Combine(dir, Marker))) {
            w.WriteLine("# created by OOXMLValidator --fetch-schemas, used to find this folder");
            w.WriteLine(DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture));
            List<string> files = new(Directory.EnumerateFiles(cache));
            files.Sort(StringComparer.Ordinal);
            foreach (string f in files) {
                w.WriteLine($"{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))).ToLowerInvariant()}  {Path.GetFileName(f)}");
            }
        }
        log.WriteLine($"schemas are up to date in {dir}");
        return 0;
    }
}
