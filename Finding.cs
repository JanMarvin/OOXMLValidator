namespace OOXMLValidator;

enum Severity { Info, Warning, Error }

sealed class Finding {
    public Severity Severity = Severity.Error;
    public string Category = "";
    public string Id = "";
    public string Part = "";
    public string XPath = "";
    public string? Key;
    public int Line;
    public int Column;
    public string Sheet = "";
    public string Cell = "";
    public string Node = "";
    public string Description = "";
    public string Excerpt = "";
    public int CaretStart;
    public int CaretLength;
}

sealed class FileReport {
    public string Path = "";
    public string Kind = "";
    public bool Fatal;
    public List<Finding> Findings = new();

    public int Count(Severity severity) {
        int n = 0;
        foreach (Finding f in Findings) {
            if (f.Severity == severity) n++;
        }
        return n;
    }
}
