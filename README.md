# OOXMLValidator

Validates OOXML packages (xlsx/xlsm/xltx, docx, pptx and variants) against

* the package rules (OPC): zip items, `[Content_Types].xml`, relationships and their targets, well-formed XML,
* the schemas and semantic constraints shipped with the Open XML SDK, by default for the newest
  format version the SDK knows (chartEx, chart styles/colors, x14/x15/... extensions included),
* optionally additional XSD files for content the SDK has no schema for.

Every finding reports the part, line and column, the element path, the start tag of the offending
element and, for worksheets, the sheet name and cell.

## Build

Requires the .NET 10 SDK. The default publish is a native (AOT) binary, which starts in a few
milliseconds instead of half a second:

```sh
dotnet publish -c Release -r osx-arm64
./bin/Release/net10.0/osx-arm64/publish/OOXMLValidator file.xlsx
```

Without a native toolchain, publish a regular build with `-p:PublishAot=false`, or run from source
with `dotnet run -- file.xlsx`.

## Usage

```
OOXMLValidator [options] <file|directory|->...
```

```
$ OOXMLValidator chart.xlsx
chart.xlsx(xl/charts/style1.xml):4:176: error: The element has invalid child element 'a:schemeClr' [Sch_InvalidElementContentExpectingComplex]
  4 | ...eClr val="bg1"/></a:solidFill></cs:spPr><a:schemeClr val="tx1"/><cs:defRPr/></cs:chartArea>
    |                                            ^~~~~~~~~~~~~~~~~~~~~~~~
chart.xlsx(xl/worksheets/sheet1.xml):1:2432: error: The attribute 't' has invalid value 'q'. The Enumeration constraint failed [Sch_AttributeValueDataTypeDetailed]
  1 | ...</v></c><c r="B5"><v>6</v></c><c r="C5" t="q"><v>258</v></c><c r="D5"><v>110</v></c><c r="E5"><v>3.08</v>...
    |                                            ^~~~~
  note: in sheet 'dat', cell C5
chart.xlsx: 2 errors generated
```

Locations are `file(part):line:column`. Colors are used on a terminal (`--color auto|always|never`, `NO_COLOR`
is honored). The tsv and json output additionally carry the element path.

| option | |
|---|---|
| `-t, --target <version>` | format version, `latest` (default) or one of `--list-targets` |
| `-f, --format <format>` | `text` (default), `tsv`, `json` |
| `-m, --max-errors <n>` | stop SDK validation after n errors per file, 0 = no limit (default) |
| `-i, --ignore <rule,...>` | drop matching findings, see below |
| `--ignore-file <file>` | ignore rules, one per line, `#` starts a comment |
| `-s, --schemas <dir>` | additional XSD files, searched recursively, default see below |
| `--no-schemas` | do not use additional XSD files |
| `--no-fetch` | never download the schemas automatically |
| `--fetch-schemas`, `--fetch-strict` | download or update the schemas and exit |
| `--schemas-all` | validate every XML part that has a schema in `<dir>`, not only content unknown to the SDK |
| `--info` | list content that was not validated |
| `-q, --quiet` | print nothing for valid files |
| `--color <when>` | `auto` (default), `always`, `never` |
| `-j, --jobs <n>` | files validated in parallel, default: number of cores |
| `--version`, `--list-targets`, `-h` | |

Directories are searched recursively, `-` reads file names from stdin.

Exit status: `0` all files valid, `1` at least one file has errors, `2` a file could not be read or
validated (missing, encrypted, not a zip), or invalid arguments. Warnings and infos do not change
the exit status.

### Ignore rules

A rule is a finding id, optionally followed by `@` and the end of the element path without indices.
`*` matches any id. Paths starting with `/` must match completely.
`excel-compat.ignore` collects known differences between the specification and what Excel writes.

```
# MS-ODRAWXML defines binSize/binCount as text content, Excel writes and expects a val attribute
*@cx:binning/cx:binSize
*@cx:binning/cx:binCount
```

### Additional schemas

The SDK has no schemas for some parts (for example `docProps/core.xml` or VML drawings) and does
not know every extension namespace. `--info` lists what was skipped. This content is validated
against the official ECMA-376 Part 2 (OPC) and Part 4 (Transitional) schemas and the Dublin Core and W3C
`xml.xsd` schemas used by `core.xml`. Binary packages such as xlsb are not defined by ECMA-376;
for them only the package structure is checked.

If no schema folder is found, the validator downloads them once from ecma-international.org and
dublincore.org into a per-user folder, so the binary can be copied anywhere:

| system | folder |
|---|---|
| macOS | `~/Library/Application Support/OOXMLValidator/schemas` |
| Linux | `$XDG_DATA_HOME/OOXMLValidator/schemas`, usually `~/.local/share/...` |
| Windows | `%LOCALAPPDATA%\OOXMLValidator\schemas` |

Without internet access, validation continues without these schemas and the download is retried
after 24 hours. Parallel runs wait for a single download. `--no-fetch` or `OOXMLVALIDATOR_NO_FETCH=1`
disables the automatic download. `--fetch-schemas` updates the folder in use (only files that
changed upstream are downloaded again), `--fetch-strict` also adds Part 1 with the Strict schemas.

The schema folder is, in this order: `--schemas <dir>`, `$OOXMLVALIDATOR_SCHEMAS`, a `schemas`
folder next to the program or in one of its parent folders, or the per-user folder. `--no-schemas`
turns this off. Content is validated against any XSD whose `targetNamespace` matches. If the
root element of a part has no schema, like the `<xml>` container of VML drawings, every subtree
below it in a namespace with a schema is validated instead. Imports are
resolved by `schemaLocation` within the folder or by namespace, never over the network. Markup
compatibility (`mc:Ignorable`, `mc:AlternateContent`) is applied first, treating every namespace
with a schema in the folder as understood.

## Tests

```sh
dotnet publish -c Release -r osx-arm64
dotnet run --project tests
```

The tests run the published binary (or the path given as first argument) against workbooks built
in code with one defect each, plus `tests/fixtures/chartex.xlsx` written by encharter, and check
ids, locations and exit codes. The `core.xml` checks run once the schemas have been downloaded.

## Fuzzing

```sh
Rscript fuzz/fuzz.R --target=openxlsx2 --n=2000 --seed=1
Rscript fuzz/fuzz.R --target=encharter --n=1000
```

Each case is a small valid workbook plus one call of an openxlsx2 or encharter function with one
to three fuzzed arguments (edge-case strings, numbers, colors, ranges, enumerations). Calls that
stop with an error rejected their input; files that save but fail validation are reported, grouped
by function, argument and finding, with the argument that alone reproduces the finding. A
reproducing script and the file of the first case in each group are kept in the output folder.
Arguments that take raw XML are not fuzzed. Findings that are known and accepted are listed in
`fuzz/known.ignore`, in addition to `excel-compat.ignore`. Needs openxlsx2 (and encharter for that
target and for charts on fuzzed chartsheets).

## Updating

Nothing about the standard is hardcoded: `latest` resolves to the newest version of the installed
SDK, so updating means bumping `DocumentFormat.OpenXml` in `OOXMLValidator.csproj` and rebuilding.
`--version` prints the SDK version in use. `--fetch-schemas` refreshes the ECMA-376 schemas.
New part types or namespaces can be covered without a rebuild by dropping their XSD into the schema folder, and known false positives are handled with
an ignore file.

## R

openxlsx2 runs the validator after saving if the option is set:

```r
options("openxlsx2.ooxml_validator" = "~/Source/OOXMLValidator/bin/Release/net10.0/osx-arm64/publish/OOXMLValidator")
```

For use in other code, the tsv output reads with base R:

```r
out <- suppressWarnings(system2(validator, c("-f", "tsv", shQuote(file)), stdout = TRUE))
read.delim(text = out, quote = "", na.strings = "", colClasses = "character")
```
