using AdrMcp.Analysis.Drift;
using AdrMcp.Analysis.Technology;
using AdrMcp.Application.Abstractions;
using AdrMcp.Domain.Adrs;
using AdrMcp.Domain.Findings;

namespace AdrMcp.UnitTests.Analysis;

public class TechnologyDetectorTests
{
    private static EvidenceFile F(string path, string text) => new("e1", path, "text/plain", text, "sha", null);
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public void Csproj_detector_finds_runtime_sdk_and_packages()
    {
        var r = new CsprojDetector().Detect(F("src/Api/Api.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup><TargetFramework>net8.0</TargetFramework><AzureFunctionsVersion>v4</AzureFunctionsVersion></PropertyGroup><ItemGroup><PackageReference Include=\"Microsoft.Azure.Cosmos\" Version=\"3.63.2\" /><PackageReference Include=\"Newtonsoft.Json\" Version=\"13.0.3\" /></ItemGroup></Project>"), Now);
        Assert.Contains(r, t => t.NormalizedName == ".NET" && t.DetectedVersion == "8.0" && t.Category == TechnologyCategory.Runtime);
        Assert.Contains(r, t => t.NormalizedName == "ASP.NET Core");
        Assert.Contains(r, t => t.NormalizedName == "Azure Functions" && t.DetectedVersion == "v4");
        Assert.Contains(r, t => t.NormalizedName == "Azure Cosmos DB SDK" && t.DetectedVersion == "3.63.2" && t.Category == TechnologyCategory.Database);
        Assert.Contains(r, t => t.NormalizedName == "Newtonsoft.Json");
        Assert.All(r, t => { Assert.Equal(TechnologyStatus.Observed, t.Status); Assert.True(t.Confidence.Score >= 0.85); Assert.Equal("csproj", t.DetectorName); });
    }

    [Fact]
    public void Csproj_detector_ignores_malformed_xml() => Assert.Empty(new CsprojDetector().Detect(F("a.csproj", "<Project><Unclosed"), Now));

    [Fact]
    public void PackageJson_detector_finds_frameworks_and_node_engine()
    {
        var r = new PackageJsonDetector().Detect(F("web/package.json", "{\"engines\":{\"node\":\">=20\"},\"dependencies\":{\"react\":\"^18.3.1\",\"@azure/cosmos\":\"4.1.0\"},\"devDependencies\":{\"vitest\":\"2.0.0\"}}"), Now);
        Assert.Contains(r, t => t.NormalizedName == "Node.js" && t.DetectedVersion == ">=20");
        Assert.Contains(r, t => t.NormalizedName == "React" && t.DetectedVersion == "18.3.1" && t.Category == TechnologyCategory.Framework);
        Assert.Contains(r, t => t.NormalizedName == "Azure Cosmos DB SDK" && t.Category == TechnologyCategory.Database);
        Assert.Contains(r, t => t.NormalizedName == "Vitest" && t.Category == TechnologyCategory.Tooling);
    }

    [Fact]
    public void Dockerfile_detector_parses_base_images()
    {
        var r = new DockerfileDetector().Detect(F("Dockerfile", "FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build\nFROM mcr.microsoft.com/dotnet/aspnet:8.0\nEXPOSE 8080"), Now);
        Assert.Contains(r, t => t.NormalizedName == ".NET SDK image" && t.DetectedVersion == "8.0");
        Assert.Contains(r, t => t.NormalizedName == "ASP.NET Core runtime image" && t.DetectedVersion == "8.0");
    }

    [Fact]
    public void Bicep_detector_records_resource_types_and_api_versions()
    {
        var r = new BicepDetector().Detect(F("infra/main.bicep", "resource sa 'Microsoft.Storage/storageAccounts@2023-05-01' = { name: 'x' }\nresource c 'Microsoft.DocumentDB/databaseAccounts@2024-11-15' = { name: 'y' }\nmodule m 'br/public:avm/res/web/site:0.21.0' = {}"), Now);
        Assert.Contains(r, t => t.NormalizedName == "Microsoft.Storage/storageAccounts" && t.DetectedVersion == "2023-05-01" && t.Category == TechnologyCategory.CloudService);
        Assert.Contains(r, t => t.NormalizedName == "Microsoft.DocumentDB/databaseAccounts" && t.Category == TechnologyCategory.Database);
        Assert.Contains(r, t => t.NormalizedName == "br/public:avm/res/web/site" && t.DetectedVersion == "0.21.0");
    }

    [Fact]
    public void Terraform_and_kubernetes_detectors_work()
    {
        var tf = new TerraformDetector().Detect(F("infra/main.tf", "terraform { required_providers { azurerm = { source = \"hashicorp/azurerm\" version = \"~> 4.0\" } } }\nresource \"azurerm_cosmosdb_account\" \"db\" { name = \"x\" }"), Now);
        Assert.Contains(tf, t => t.NormalizedName == "azurerm_cosmosdb_account" && t.Category == TechnologyCategory.Database);
        Assert.Contains(tf, t => t.NormalizedName == "Microsoft Azure");
        var k8s = new KubernetesDetector().Detect(F("deploy/api.yaml", "apiVersion: apps/v1\nkind: Deployment\nspec:\n  template:\n    spec:\n      containers:\n        - name: api\n          image: ghcr.io/org/api:1.4.2"), Now);
        Assert.Contains(k8s, t => t.NormalizedName == "Kubernetes");
        Assert.Contains(k8s, t => t.NormalizedName == "ghcr.io/org/api" && t.DetectedVersion == "1.4.2");
    }

    [Fact]
    public void Requirements_detector_pins_versions()
    {
        var r = new PythonRequirementsDetector().Detect(F("requirements.txt", "fastapi==0.115.0\npsycopg2-binary>=2.9\n# comment\n-r other.txt"), Now);
        Assert.Contains(r, t => t.NormalizedName == "FastAPI" && t.DetectedVersion == "0.115.0" && t.Confidence.Score >= 0.95);
        Assert.Contains(r, t => t.NormalizedName == "PostgreSQL (psycopg2)" && t.DetectedVersion == ">=2.9");
    }
}

public class DriftDetectorTests
{
    private static EvidenceSet Set(params (string Path, string Text)[] files) => new(files.Select((f, i) => new EvidenceFile($"e{i}", f.Path, "text/plain", f.Text, $"sha{i}", null)).ToList());

    [Fact]
    public void Prohibited_dependency_is_drift_when_present_compliant_when_absent_uncertain_without_manifests()
    {
        var d = new ProhibitedDependencyDriftDetector();
        var claim = new DecisionClaim("c1", ClaimKind.ProhibitedTechnology, "Newtonsoft.Json", "must not use", null, ClaimScope.Global);
        Assert.True(d.Supports(claim));
        var drifted = d.Evaluate(claim, Set(("src/Api/Api.csproj", "<PackageReference Include=\"Newtonsoft.Json\" Version=\"13.0.3\" />"), ("src/Api/Program.cs", "using Newtonsoft.Json;")));
        Assert.Equal(DriftClaimResult.Drifted, drifted.Result);
        Assert.Equal(2, drifted.Evidence.Count);
        Assert.Contains(drifted.Evidence, e => e.Kind == "import");
        var compliant = d.Evaluate(claim, Set(("src/Api/Api.csproj", "<PackageReference Include=\"System.Text.Json\" Version=\"8.0.5\" />")));
        Assert.Equal(DriftClaimResult.Compliant, compliant.Result);
        var uncertain = d.Evaluate(claim, Set(("README.md", "Newtonsoft is mentioned in prose only")));
        Assert.Equal(DriftClaimResult.Uncertain, uncertain.Result);
    }

    [Fact]
    public void Infrastructure_requirement_detector_checks_bicep_and_terraform_properties()
    {
        var d = new InfrastructureRequirementDriftDetector();
        var claim = new DecisionClaim("c2", ClaimKind.InfrastructureRequirement, "storage accounts", "must have", "TLS 1.2", ClaimScope.Global);
        Assert.True(d.Supports(claim));
        var good = "resource sa 'Microsoft.Storage/storageAccounts@2023-05-01' = {\n  name: 'a'\n  properties: {\n    minimumTlsVersion: 'TLS1_2'\n  }\n}";
        var bad = "resource sb 'Microsoft.Storage/storageAccounts@2023-05-01' = {\n  name: 'b'\n  properties: {\n    minimumTlsVersion: 'TLS1_0'\n  }\n}";
        Assert.Equal(DriftClaimResult.Compliant, d.Evaluate(claim, Set(("infra/a.bicep", good))).Result);
        var drifted = d.Evaluate(claim, Set(("infra/a.bicep", good + "\n" + bad)));
        Assert.Equal(DriftClaimResult.Drifted, drifted.Result);
        Assert.Contains(drifted.Evidence, e => e.Kind == "violating-resource" && e.LineNumber == 7);
        var tf = "resource \"azurerm_storage_account\" \"x\" {\n  name = \"x\"\n  min_tls_version = \"TLS1_2\"\n}";
        Assert.Equal(DriftClaimResult.Compliant, d.Evaluate(claim, Set(("main.tf", tf))).Result);
        Assert.Equal(DriftClaimResult.NotApplicable, d.Evaluate(claim, Set(("main.tf", "resource \"azurerm_key_vault\" \"kv\" {\n name = \"k\"\n}"))).Result);
        Assert.Equal(DriftClaimResult.Uncertain, d.Evaluate(claim, Set(("README.md", "no iac"))).Result);
        var pe = new DecisionClaim("c3", ClaimKind.InfrastructureRequirement, "storage accounts", "must have", "private endpoints", ClaimScope.Global);
        Assert.Equal(DriftClaimResult.Drifted, d.Evaluate(pe, Set(("infra/a.bicep", good))).Result);
        Assert.Equal(DriftClaimResult.Compliant, d.Evaluate(pe, Set(("infra/a.bicep", good.Replace("minimumTlsVersion: 'TLS1_2'", "publicNetworkAccess: 'Disabled'")))).Result);
        Assert.False(d.Supports(new DecisionClaim("c4", ClaimKind.InfrastructureRequirement, "storage accounts", "must have", "a unicorn", ClaimScope.Global)));
    }

    [Fact]
    public void Infrastructure_requirement_detector_ends_each_resource_at_its_own_brace()
    {
        // An apostrophe in a comment or an escaped backslash used to leave the brace matcher "inside a string", so a
        // non-compliant resource swallowed the compliant one after it and the claim came out Compliant.
        var d = new InfrastructureRequirementDriftDetector();
        var claim = new DecisionClaim("c5", ClaimKind.InfrastructureRequirement, "storage accounts", "must have", "TLS 1.2", ClaimScope.Global);
        var good = "resource sa 'Microsoft.Storage/storageAccounts@2023-05-01' = {\n  name: 'a'\n  properties: {\n    minimumTlsVersion: 'TLS1_2'\n  }\n}";
        var bad = new[]
        {
            "resource sb 'Microsoft.Storage/storageAccounts@2023-05-01' = {\n  // legacy account, don't touch yet\n  name: 'b'\n  properties: {\n    minimumTlsVersion: 'TLS1_0'\n  }\n}",
            "resource sb 'Microsoft.Storage/storageAccounts@2023-05-01' = {\n  name: 'b'\n  tags: { root: 'C:\\\\' }\n  properties: {\n    minimumTlsVersion: 'TLS1_0'\n  }\n}",
            "resource sb 'Microsoft.Storage/storageAccounts@2023-05-01' = {\n  name: 'b'\n  /* it's { not } code */\n  properties: {\n    description: '''\nit's a } brace\n'''\n    minimumTlsVersion: 'TLS1_0'\n  }\n}",
            "resource sb 'Microsoft.Storage/storageAccounts@2023-05-01' = {\n  name: 'st${replace(prefix, '-', '')}'\n  properties: {\n    minimumTlsVersion: 'TLS1_0'\n  }\n}",
        };
        foreach (var b in bad)
        {
            var result = d.Evaluate(claim, Set(("infra/a.bicep", b + "\n" + good)));
            Assert.Equal(DriftClaimResult.Drifted, result.Result);
            Assert.Contains(result.Evidence, e => e.Kind == "violating-resource" && e.LineNumber == 1);
            Assert.Contains(result.Evidence, e => e.Kind == "compliant-resource");
        }
        var tfBad = "resource \"azurerm_storage_account\" \"bad\" {\n  # it's legacy\n  name = \"bad-${lookup(var.names, \"k\")}\"\n  min_tls_version = \"TLS1_0\"\n}";
        var tfGood = "resource \"azurerm_storage_account\" \"good\" {\n  name = \"good\"\n  min_tls_version = \"TLS1_2\"\n}";
        var tf = d.Evaluate(claim, Set(("main.tf", tfBad + "\n" + tfGood)));
        Assert.Equal(DriftClaimResult.Drifted, tf.Result);
        Assert.Contains(tf.Evidence, e => e.Kind == "violating-resource" && e.LineNumber == 1);
    }

    [Fact]
    public void Database_ownership_detector_flags_foreign_service_references()
    {
        var d = new DatabaseOwnershipDriftDetector();
        var claim = new DecisionClaim("c5", ClaimKind.DatabaseOwnership, "OrderService", "owns", "OrdersDb", ClaimScope.Global);
        Assert.True(d.Supports(claim));
        var ownerOnly = Set(("src/OrderService/appsettings.json", "{\"ConnectionStrings\":{\"Orders\":\"Server=sql;Database=OrdersDb;\"}}"));
        Assert.Equal(DriftClaimResult.Compliant, d.Evaluate(claim, ownerOnly).Result);
        var foreign = Set(("src/OrderService/appsettings.json", "Database=OrdersDb;"), ("src/BillingService/appsettings.json", "{\"Db\":\"Server=sql;Database=OrdersDb;\"}"));
        var r = d.Evaluate(claim, foreign);
        Assert.Equal(DriftClaimResult.Drifted, r.Result);
        Assert.Contains(r.Evidence, e => e.SourcePath.Contains("BillingService") && e.Kind == "foreign");
        Assert.Equal(DriftClaimResult.Uncertain, d.Evaluate(claim, Set(("docs/readme.md", "no refs"))).Result);
        Assert.Equal("BillingService", DatabaseOwnershipDriftDetector.ServiceFromPath("src/BillingService/appsettings.json"));
    }
}
