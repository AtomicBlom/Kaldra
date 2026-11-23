// Loom LLM documentation generator.
// Usage: dotnet run --file tools/generate-llms-docs.cs [--level minimal|standard|full|all]
//
// Generates orientation files for LLMs at three token-budget levels:
//   minimal  (~2KB)  llms-minimal.txt  — pillars + API vocabulary table
//   standard (~8KB)  llms.txt          — architecture + vocabulary + pillars + getting-started
//   full     (~25KB) llms-full.txt     — all docs + XML doc summaries for public types
//
// Run from the repo root. XML docs are optional (omitted if assemblies not built yet).

using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

var level = "all";
for (var i = 0; i < args.Length; i++)
    if (args[i] == "--level" && i + 1 < args.Length)
        level = args[i + 1].ToLowerInvariant();

var repoRoot = FindRepoRoot(Directory.GetCurrentDirectory());
var docsDir  = Path.Combine(repoRoot, "docs");

// --- Load supplemental docs -----------------------------------------------

var docs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
foreach (var path in Directory.EnumerateFiles(docsDir, "*.md"))
    docs[Path.GetFileNameWithoutExtension(path)] = File.ReadAllText(path, Encoding.UTF8);

string Doc(string name) => docs.TryGetValue(name, out var s) ? s : $"<!-- {name}.md not found -->\n";

// --- Load XML doc summaries (optional) -------------------------------------

var xmlSummaries = new List<(string Member, string Summary)>();
// Only include key consumer-facing assemblies in XML summaries (keeps full output manageable)
var keyAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    { "Loom.Core", "Loom.Graph", "Loom.Assets", "BinaryVibrance.Vysara" };
// Every project that references an assembly copies its XML doc into its own output, so one assembly's doc is on disk
// many times over. Read each assembly once, from its newest copy, so the output depends on the sources rather than on
// how many projects happen to have been built.
var xmlPaths = Directory.EnumerateFiles(repoRoot, "*.xml", SearchOption.AllDirectories)
    .Where(p => !p.Contains("obj") && !p.Contains("external") && p.Contains("Release"))
    .OrderByDescending(File.GetLastWriteTimeUtc)
    .ThenBy(p => p, StringComparer.Ordinal);
var assembliesRead = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

foreach (var xmlPath in xmlPaths)
{
    try
    {
        var xd = new XmlDocument();
        xd.Load(xmlPath);
        var assemblyName = xd.SelectSingleNode("/doc/assembly/name")?.InnerText ?? "";
        if (!keyAssemblies.Contains(assemblyName) || !assembliesRead.Add(assemblyName)) continue;
        var members = xd.SelectNodes("/doc/members/member");
        if (members is null) continue;

        foreach (XmlNode member in members)
        {
            var name = member.Attributes?["name"]?.Value ?? "";
            // Full mode: types and their public members only — skip fields/properties of nested types
            if (!name.StartsWith("T:")) continue;

            var summary = member.SelectSingleNode("summary")?.InnerText;
            if (string.IsNullOrWhiteSpace(summary)) continue;

            // Collapse whitespace in summary
            summary = Regex.Replace(summary.Trim(), @"\s+", " ");
            // Trim planning-doc references (§N, M\d+, etc.) — they're stale in consumer docs
            summary = Regex.Replace(summary, @"\s*\((?:Planning\.md|PlanningUI\.md)[^)]*\)", "");
            summary = Regex.Replace(summary, @"\s*\(§[^)]+\)", "");
            summary = Regex.Replace(summary, @"\s*\(M\d+[^)]*\)", "");

            var shortName = name.TrimStart('T', 'M', ':');
            // Strip namespace prefix for readability (keep just the type.member)
            var dotIdx = shortName.IndexOf('.');
            var typeOnly = dotIdx > 0 ? shortName[(shortName.LastIndexOf('.', dotIdx - 1) + 1)..] : shortName;

            xmlSummaries.Add((typeOnly, summary));
        }
    }
    catch { /* skip malformed XML */ }
}

// --- Assemble levels -------------------------------------------------------

var header = $"""
    Loom {GetLoomVersion(repoRoot)} — render-graph engine for .NET
    Generated: {DateTimeOffset.UtcNow:yyyy-MM-dd}
    Source: https://github.com/AtomicBlom/Loom
    """;

if (level is "minimal" or "all")
    Write("llms-minimal.txt", BuildMinimal());

if (level is "standard" or "all")
    Write("llms.txt", BuildStandard());

if (level is "full" or "all")
    Write("llms-full.txt", BuildFull());

Console.WriteLine("Done.");

// --- Builders --------------------------------------------------------------

string BuildMinimal()
{
    var sb = new StringBuilder();
    sb.AppendLine(header);
    sb.AppendLine();
    // Pillar headlines only (## headings)
    sb.AppendLine("## Pillars of Design (abbreviated)");
    sb.AppendLine();
    foreach (var line in Doc("pillars").Split('\n'))
    {
        if (line.StartsWith("## "))
            sb.AppendLine(line.TrimStart('#').Trim());
    }
    sb.AppendLine();
    // Graph description table from api-vocabulary (~50 lines covers the key tables)
    sb.AppendLine(TakeLines(Doc("api-vocabulary"), 55));
    sb.AppendLine();
    sb.AppendLine("## Minimal example");
    sb.AppendLine();
    sb.AppendLine(ExtractSection(Doc("getting-started"), "3. Define the render graph", "4. Render loop"));
    return sb.ToString();
}

string BuildStandard()
{
    var sb = new StringBuilder();
    sb.AppendLine(header);
    sb.AppendLine();
    sb.AppendLine(Doc("architecture"));
    sb.AppendLine("---");
    sb.AppendLine();
    sb.AppendLine(Doc("api-vocabulary"));
    sb.AppendLine("---");
    sb.AppendLine();
    sb.AppendLine(Doc("pillars"));
    sb.AppendLine("---");
    sb.AppendLine();
    // getting-started up to (but not including) Loading assets
    sb.AppendLine(TakeUntilSection(Doc("getting-started"), "Loading assets"));
    return sb.ToString();
}

string BuildFull()
{
    var sb = new StringBuilder();
    sb.AppendLine(header);
    sb.AppendLine();
    foreach (var name in new[] { "architecture", "api-vocabulary", "pillars", "getting-started", "shader-authoring" })
    {
        sb.AppendLine(Doc(name));
        sb.AppendLine("---");
        sb.AppendLine();
    }

    if (xmlSummaries.Count > 0)
    {
        sb.AppendLine("## Public API Summaries (from XML docs)");
        sb.AppendLine();
        foreach (var (member, summary) in xmlSummaries.OrderBy(s => s.Member, StringComparer.Ordinal))
            sb.AppendLine($"- **{member}**: {summary}");
    }

    return sb.ToString();
}

// --- Helpers ---------------------------------------------------------------

void Write(string name, string content)
{
    var path = Path.Combine(repoRoot, name);
    File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    var kb = content.Length / 1024.0;
    Console.WriteLine($"  {name,-22} {kb:F1} KB");
}

static string TakeLines(string text, int count)
{
    var lines = text.Split('\n');
    return string.Join('\n', lines.Take(count));
}

static string ExtractSection(string text, string startHeading, string? endHeading = null)
{
    var lines = text.Split('\n');
    var inSection = false;
    var result = new List<string>();
    foreach (var line in lines)
    {
        if (line.StartsWith("## ") && line.Contains(startHeading))
            inSection = true;
        else if (inSection && line.StartsWith("## ") && endHeading is not null && line.Contains(endHeading))
            break;

        if (inSection) result.Add(line);
    }
    return string.Join('\n', result);
}

static string TakeUntilSection(string text, string endHeading)
{
    var lines = text.Split('\n');
    var result = new List<string>();
    foreach (var line in lines)
    {
        if (line.StartsWith("## ") && line.Contains(endHeading))
            break;
        result.Add(line);
    }
    return string.Join('\n', result);
}

static string FindRepoRoot(string start)
{
    var dir = start;
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir, "Loom.slnx")))
            return dir;
        dir = Path.GetDirectoryName(dir);
    }
    return start;
}

static string GetLoomVersion(string root)
{
    var versionProps = Path.Combine(root, "Version.props");
    if (!File.Exists(versionProps)) return "0.0.0";
    var match = Regex.Match(File.ReadAllText(versionProps), @"<LoomVersion>(.*?)</LoomVersion>");
    return match.Success ? match.Groups[1].Value : "0.0.0";
}
