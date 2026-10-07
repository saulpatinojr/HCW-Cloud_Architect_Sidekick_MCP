using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AdrMcp.Application.Abstractions;
using AdrMcp.Domain.Adrs;
using AdrMcp.Domain.Common;

namespace AdrMcp.Analysis.Technology;

internal static class DetectorHelpers
{
    public static DetectedTechnology Make(string detector, string version, string rawName, string? ver, TechnologyCategory fallback, EvidenceFile file, string? excerpt, DateTimeOffset now, double confidence = 0.95, TechnologyStatus status = TechnologyStatus.Observed)
    {
        var (name, category) = TechnologyCatalog.Normalize(rawName, fallback);
        return new DetectedTechnology(name, string.IsNullOrWhiteSpace(ver) ? null : ver!.Trim(), category, file.Path, Trunc(excerpt), now, now, Confidence.Observed(confidence, $"Parsed from {Path.GetFileName(file.Path)}"), detector, version, status, file.EvidenceId == "inline" ? null : file.EvidenceId);
    }

    public static string? Trunc(string? s) => s is null ? null : s.Length <= 200 ? s.Trim() : s[..200].Trim() + "…";
    public static string FileName(string path) => path.Replace('\\', '/').Split('/').Last();
    public static bool EndsWith(string path, params string[] suffixes) => suffixes.Any(s => path.EndsWith(s, StringComparison.OrdinalIgnoreCase));
}

/// <summary>.csproj / .fsproj / Directory.Packages.props / Directory.Build.props parser.</summary>
public sealed class CsprojDetector : ITechnologyDetector
{
    public string Name => "csproj"; public string Version => "1.0.0";
    public bool CanHandle(string path) => DetectorHelpers.EndsWith(path, ".csproj", ".fsproj", ".vbproj", "Directory.Packages.props", "Directory.Build.props");

    public IReadOnlyList<DetectedTechnology> Detect(EvidenceFile file, DateTimeOffset now)
    {
        var results = new List<DetectedTechnology>();
        XDocument doc;
        try { doc = XDocument.Parse(file.Text, LoadOptions.None); } catch (System.Xml.XmlException) { return results; }
        var root = doc.Root; if (root is null) return results;
        var sdk = root.Attribute("Sdk")?.Value;
        if (sdk is not null)
        {
            if (sdk.Contains("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase)) results.Add(DetectorHelpers.Make(Name, Version, "ASP.NET Core", null, TechnologyCategory.Framework, file, $"Sdk=\"{sdk}\"", now));
            if (sdk.Contains("Microsoft.NET.Sdk.Worker", StringComparison.OrdinalIgnoreCase)) results.Add(DetectorHelpers.Make(Name, Version, ".NET Worker Service", null, TechnologyCategory.Framework, file, $"Sdk=\"{sdk}\"", now));
        }
        foreach (var tfm in root.Descendants().Where(e => e.Name.LocalName is "TargetFramework" or "TargetFrameworks"))
            foreach (var t in tfm.Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var m = Regex.Match(t, @"^net(\d+\.\d+)", RegexOptions.IgnoreCase);
                var ver = m.Success ? m.Groups[1].Value : t.StartsWith("netstandard", StringComparison.OrdinalIgnoreCase) ? t : t.StartsWith("netcoreapp", StringComparison.OrdinalIgnoreCase) ? t : null;
                var name = t.StartsWith("netstandard", StringComparison.OrdinalIgnoreCase) ? ".NET Standard" : t.StartsWith("net4", StringComparison.OrdinalIgnoreCase) ? ".NET Framework" : ".NET";
                results.Add(DetectorHelpers.Make(Name, Version, name, ver ?? t, TechnologyCategory.Runtime, file, $"<{tfm.Name.LocalName}>{tfm.Value}</{tfm.Name.LocalName}>", now));
            }
        foreach (var afv in root.Descendants().Where(e => e.Name.LocalName == "AzureFunctionsVersion"))
            results.Add(DetectorHelpers.Make(Name, Version, "Azure Functions", afv.Value, TechnologyCategory.Runtime, file, $"<AzureFunctionsVersion>{afv.Value}</AzureFunctionsVersion>", now));
        foreach (var pr in root.Descendants().Where(e => e.Name.LocalName is "PackageReference" or "PackageVersion"))
        {
            var id = pr.Attribute("Include")?.Value ?? pr.Attribute("Update")?.Value; if (string.IsNullOrWhiteSpace(id)) continue;
            var ver = pr.Attribute("Version")?.Value ?? pr.Elements().FirstOrDefault(e => e.Name.LocalName == "Version")?.Value;
            results.Add(DetectorHelpers.Make(Name, Version, id, ver, TechnologyCategory.Library, file, pr.ToString(SaveOptions.DisableFormatting), now, ver is null ? 0.85 : 0.95));
        }
        foreach (var fr in root.Descendants().Where(e => e.Name.LocalName == "FrameworkReference"))
        {
            var id = fr.Attribute("Include")?.Value; if (id is null) continue;
            if (id.Contains("AspNetCore", StringComparison.OrdinalIgnoreCase)) results.Add(DetectorHelpers.Make(Name, Version, "ASP.NET Core", null, TechnologyCategory.Framework, file, fr.ToString(SaveOptions.DisableFormatting), now));
        }
        return results;
    }
}

/// <summary>package.json dependencies, devDependencies and engines.</summary>
public sealed class PackageJsonDetector : ITechnologyDetector
{
    public string Name => "package.json"; public string Version => "1.0.0";
    public bool CanHandle(string path) => DetectorHelpers.FileName(path).Equals("package.json", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<DetectedTechnology> Detect(EvidenceFile file, DateTimeOffset now)
    {
        var results = new List<DetectedTechnology>();
        JsonDocument doc;
        try { doc = JsonDocument.Parse(file.Text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }); } catch (JsonException) { return results; }
        using (doc)
        {
            var root = doc.RootElement; if (root.ValueKind != JsonValueKind.Object) return results;
            results.Add(DetectorHelpers.Make(Name, Version, "Node.js", root.TryGetProperty("engines", out var eng) && eng.ValueKind == JsonValueKind.Object && eng.TryGetProperty("node", out var nodeV) ? nodeV.GetString() : null, TechnologyCategory.Runtime, file, "package.json present", now, 0.9));
            foreach (var section in new[] { "dependencies", "devDependencies", "peerDependencies" })
            {
                if (!root.TryGetProperty(section, out var deps) || deps.ValueKind != JsonValueKind.Object) continue;
                foreach (var dep in deps.EnumerateObject())
                    results.Add(DetectorHelpers.Make(Name, Version, dep.Name, dep.Value.ValueKind == JsonValueKind.String ? dep.Value.GetString()?.TrimStart('^', '~') : null, section == "devDependencies" ? TechnologyCategory.Tooling : TechnologyCategory.Library, file, $"\"{dep.Name}\": {dep.Value}", now));
            }
        }
        return results;
    }
}

/// <summary>requirements*.txt, Pipfile-less pins, pyproject [project].dependencies (simple cases).</summary>
public sealed class PythonRequirementsDetector : ITechnologyDetector
{
    private static readonly Regex Line = new(@"^\s*([A-Za-z0-9_.\-\[\]]+)\s*(?:(==|>=|~=|<=|>|<|!=)\s*([A-Za-z0-9_.\-*+!]+))?", RegexOptions.Compiled);
    public string Name => "python-requirements"; public string Version => "1.0.0";
    public bool CanHandle(string path) { var f = DetectorHelpers.FileName(path).ToLowerInvariant(); return f.StartsWith("requirements") && f.EndsWith(".txt") || f == "pyproject.toml"; }

    public IReadOnlyList<DetectedTechnology> Detect(EvidenceFile file, DateTimeOffset now)
    {
        var results = new List<DetectedTechnology> { DetectorHelpers.Make(Name, Version, "Python", ExtractPythonVersion(file.Text), TechnologyCategory.Runtime, file, DetectorHelpers.FileName(file.Path), now, 0.9) };
        var inDeps = DetectorHelpers.FileName(file.Path).EndsWith(".txt", StringComparison.OrdinalIgnoreCase);
        foreach (var raw in file.Text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("dependencies") && line.Contains('[')) { inDeps = true; continue; }
            if (line.StartsWith('[') && line.EndsWith(']')) { inDeps = false; continue; }
            if (!inDeps || line.Length == 0 || line.StartsWith('#') || line.StartsWith('-')) continue;
            var cleaned = line.Trim('"', '\'', ',');
            var m = Line.Match(cleaned); if (!m.Success) continue;
            var name = m.Groups[1].Value.Split('[')[0];
            if (name.Length < 2) continue;
            results.Add(DetectorHelpers.Make(Name, Version, name, m.Groups[3].Success && m.Groups[2].Value == "==" ? m.Groups[3].Value : m.Groups[3].Success ? $"{m.Groups[2].Value}{m.Groups[3].Value}" : null, TechnologyCategory.Library, file, cleaned, now, m.Groups[2].Value == "==" ? 0.95 : 0.85));
        }
        return results;
    }

    private static string? ExtractPythonVersion(string text) { var m = Regex.Match(text, @"requires-python\s*=\s*""([^""]+)"""); return m.Success ? m.Groups[1].Value : null; }
}

/// <summary>Dockerfile FROM images.</summary>
public sealed class DockerfileDetector : ITechnologyDetector
{
    private static readonly Regex From = new(@"^\s*FROM\s+(?:--platform=\S+\s+)?([^\s]+)(?:\s+AS\s+\S+)?\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);
    public string Name => "dockerfile"; public string Version => "1.0.0";
    public bool CanHandle(string path) { var f = DetectorHelpers.FileName(path); return f.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".dockerfile", StringComparison.OrdinalIgnoreCase) || f.StartsWith("Dockerfile.", StringComparison.OrdinalIgnoreCase); }

    public IReadOnlyList<DetectedTechnology> Detect(EvidenceFile file, DateTimeOffset now)
    {
        var results = new List<DetectedTechnology> { DetectorHelpers.Make(Name, Version, "Docker", null, TechnologyCategory.Container, file, "Dockerfile present", now, 0.95) };
        foreach (Match m in From.Matches(file.Text))
        {
            var image = m.Groups[1].Value;
            if (image.Equals("scratch", StringComparison.OrdinalIgnoreCase)) continue;
            var parts = image.Split('@')[0];
            var colon = parts.LastIndexOf(':');
            var tag = colon > parts.LastIndexOf('/') && colon >= 0 ? parts[(colon + 1)..] : null;
            var name = colon > parts.LastIndexOf('/') && colon >= 0 ? parts[..colon] : parts;
            results.Add(DetectorHelpers.Make(Name, Version, name, tag, TechnologyCategory.Container, file, m.Value.Trim(), now, tag is null ? 0.8 : 0.95));
        }
        return results;
    }
}

/// <summary>Bicep resource and module declarations.</summary>
public sealed class BicepDetector : ITechnologyDetector
{
    private static readonly Regex Resource = new(@"resource\s+\w+\s+'([A-Za-z.]+/[A-Za-z./]+)@(\d{4}-\d{2}-\d{2}(?:-preview)?)'", RegexOptions.Compiled);
    private static readonly Regex Module = new(@"module\s+\w+\s+'(br/public:[^':]+):([^']+)'", RegexOptions.Compiled);
    public string Name => "bicep"; public string Version => "1.0.0";
    public bool CanHandle(string path) => DetectorHelpers.EndsWith(path, ".bicep");

    public IReadOnlyList<DetectedTechnology> Detect(EvidenceFile file, DateTimeOffset now)
    {
        var results = new List<DetectedTechnology> { DetectorHelpers.Make(Name, Version, "Bicep", null, TechnologyCategory.InfrastructureAsCode, file, "Bicep template", now) };
        foreach (Match m in Resource.Matches(file.Text))
        {
            var type = m.Groups[1].Value;
            results.Add(new DetectedTechnology(type, m.Groups[2].Value, TechnologyCatalog.CategoryForAzureResource(type), file.Path, DetectorHelpers.Trunc(m.Value), now, now, Confidence.Observed(0.95, "Bicep resource declaration"), Name, Version, TechnologyStatus.Observed, file.EvidenceId == "inline" ? null : file.EvidenceId));
        }
        foreach (Match m in Module.Matches(file.Text))
            results.Add(DetectorHelpers.Make(Name, Version, m.Groups[1].Value, m.Groups[2].Value, TechnologyCategory.InfrastructureAsCode, file, m.Value, now));
        return results;
    }
}

/// <summary>Terraform resources and required providers.</summary>
public sealed class TerraformDetector : ITechnologyDetector
{
    private static readonly Regex Resource = new(@"resource\s+""([a-z0-9_]+)""\s+""[^""]+""", RegexOptions.Compiled);
    private static readonly Regex Provider = new(@"(\w+)\s*=\s*\{\s*source\s*=\s*""([^""]+)""\s*(?:version\s*=\s*""([^""]+)"")?", RegexOptions.Compiled | RegexOptions.Singleline);
    public string Name => "terraform"; public string Version => "1.0.0";
    public bool CanHandle(string path) => DetectorHelpers.EndsWith(path, ".tf", ".tf.json");

    public IReadOnlyList<DetectedTechnology> Detect(EvidenceFile file, DateTimeOffset now)
    {
        var results = new List<DetectedTechnology> { DetectorHelpers.Make(Name, Version, "Terraform", null, TechnologyCategory.InfrastructureAsCode, file, "Terraform configuration", now) };
        foreach (Match m in Provider.Matches(file.Text))
            results.Add(DetectorHelpers.Make(Name, Version, $"terraform-provider {m.Groups[2].Value}", m.Groups[3].Success ? m.Groups[3].Value : null, TechnologyCategory.InfrastructureAsCode, file, m.Value.Replace("\n", " "), now));
        var providers = new HashSet<string>();
        foreach (Match m in Resource.Matches(file.Text))
        {
            var type = m.Groups[1].Value;
            var provider = type.Split('_')[0];
            var category = type.Contains("cosmos") || type.Contains("sql") || type.Contains("postgres") || type.Contains("mysql") || type.Contains("redis") || type.Contains("rds") || type.Contains("dynamodb") ? TechnologyCategory.Database
                : type.Contains("servicebus") || type.Contains("eventhub") || type.Contains("sqs") || type.Contains("sns") || type.Contains("pubsub") ? TechnologyCategory.Messaging
                : type.Contains("kubernetes") || type.Contains("eks") || type.Contains("gke") || type.Contains("container_app") ? TechnologyCategory.Orchestration : TechnologyCategory.CloudService;
            results.Add(new DetectedTechnology(type, null, category, file.Path, DetectorHelpers.Trunc(m.Value), now, now, Confidence.Observed(0.95, "Terraform resource"), Name, Version, TechnologyStatus.Observed, file.EvidenceId == "inline" ? null : file.EvidenceId));
            if (providers.Add(provider))
            {
                var cloud = provider switch { "azurerm" or "azuread" or "azapi" => "Microsoft Azure", "aws" => "Amazon Web Services", "google" => "Google Cloud" , _ => null };
                if (cloud is not null) results.Add(DetectorHelpers.Make(Name, Version, cloud, null, TechnologyCategory.CloudService, file, $"provider prefix {provider}", now, 0.9));
            }
        }
        return results;
    }
}

/// <summary>Kubernetes manifests and Helm charts (YAML).</summary>
public sealed class KubernetesDetector : ITechnologyDetector
{
    private static readonly Regex ApiVersion = new(@"^\s*apiVersion:\s*([^\s#]+)", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex Kind = new(@"^\s*kind:\s*([A-Za-z]+)", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex Image = new(@"^\s*(?:-\s*)?image:\s*[""']?([^\s""'#]+)", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex ChartField = new(@"^(name|version|appVersion):\s*[""']?([^\s""'#]+)", RegexOptions.Multiline | RegexOptions.Compiled);
    public string Name => "kubernetes"; public string Version => "1.0.0";
    public bool CanHandle(string path) => DetectorHelpers.EndsWith(path, ".yaml", ".yml") && !path.Contains(".github/workflows", StringComparison.OrdinalIgnoreCase) && !path.Contains("azure-pipelines", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<DetectedTechnology> Detect(EvidenceFile file, DateTimeOffset now)
    {
        var results = new List<DetectedTechnology>();
        var isChart = DetectorHelpers.FileName(file.Path).Equals("Chart.yaml", StringComparison.OrdinalIgnoreCase);
        if (isChart)
        {
            var fields = ChartField.Matches(file.Text).ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
            results.Add(DetectorHelpers.Make(Name, Version, "Helm", fields.GetValueOrDefault("version"), TechnologyCategory.Orchestration, file, $"Chart {fields.GetValueOrDefault("name")}", now));
            if (fields.TryGetValue("appVersion", out var appV) && fields.TryGetValue("name", out var n)) results.Add(DetectorHelpers.Make(Name, Version, n, appV, TechnologyCategory.Other, file, "Chart.yaml appVersion", now, 0.8));
            return results;
        }
        var kinds = Kind.Matches(file.Text).Select(m => m.Groups[1].Value).ToList();
        var apis = ApiVersion.Matches(file.Text).Select(m => m.Groups[1].Value).ToList();
        if (kinds.Count == 0 || apis.Count == 0) return results;
        if (kinds.Any(k => k is "Deployment" or "Service" or "StatefulSet" or "DaemonSet" or "Ingress" or "Pod" or "ConfigMap" or "Secret" or "Job" or "CronJob" or "Namespace" or "HorizontalPodAutoscaler"))
        {
            results.Add(DetectorHelpers.Make(Name, Version, "Kubernetes", null, TechnologyCategory.Orchestration, file, $"kinds: {string.Join(",", kinds.Distinct())}", now));
            foreach (Match m in Image.Matches(file.Text))
            {
                var image = m.Groups[1].Value; var colon = image.LastIndexOf(':');
                var tag = colon > image.LastIndexOf('/') ? image[(colon + 1)..] : null; var name = colon > image.LastIndexOf('/') ? image[..colon] : image;
                results.Add(DetectorHelpers.Make(Name, Version, name, tag, TechnologyCategory.Container, file, m.Value.Trim(), now, tag is null ? 0.8 : 0.95));
            }
        }
        return results;
    }
}

/// <summary>GitHub Actions and Azure Pipelines definitions.</summary>
public sealed class CiCdDetector : ITechnologyDetector
{
    private static readonly Regex Uses = new(@"^\s*-?\s*uses:\s*([^\s@#]+)@([^\s#]+)", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex Task = new(@"^\s*-?\s*task:\s*([A-Za-z0-9]+)@(\d+)", RegexOptions.Multiline | RegexOptions.Compiled);
    public string Name => "cicd"; public string Version => "1.0.0";
    public bool CanHandle(string path) => path.Contains(".github/workflows", StringComparison.OrdinalIgnoreCase) || DetectorHelpers.FileName(path).StartsWith("azure-pipelines", StringComparison.OrdinalIgnoreCase) || DetectorHelpers.FileName(path).Equals("Jenkinsfile", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<DetectedTechnology> Detect(EvidenceFile file, DateTimeOffset now)
    {
        var results = new List<DetectedTechnology>();
        if (path_IsGitHub(file.Path))
        {
            results.Add(DetectorHelpers.Make(Name, Version, "GitHub Actions", null, TechnologyCategory.CiCd, file, "workflow file", now));
            foreach (Match m in Uses.Matches(file.Text)) results.Add(DetectorHelpers.Make(Name, Version, $"action {m.Groups[1].Value}", m.Groups[2].Value, TechnologyCategory.CiCd, file, m.Value.Trim(), now));
        }
        else if (DetectorHelpers.FileName(file.Path).StartsWith("azure-pipelines", StringComparison.OrdinalIgnoreCase))
        {
            results.Add(DetectorHelpers.Make(Name, Version, "Azure Pipelines", null, TechnologyCategory.CiCd, file, "pipeline file", now));
            foreach (Match m in Task.Matches(file.Text)) results.Add(DetectorHelpers.Make(Name, Version, $"task {m.Groups[1].Value}", m.Groups[2].Value, TechnologyCategory.CiCd, file, m.Value.Trim(), now));
        }
        else results.Add(DetectorHelpers.Make(Name, Version, "Jenkins", null, TechnologyCategory.CiCd, file, "Jenkinsfile", now));
        return results;
    }

    private static bool path_IsGitHub(string p) => p.Contains(".github/workflows", StringComparison.OrdinalIgnoreCase);
}
