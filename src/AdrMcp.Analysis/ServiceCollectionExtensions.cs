using AdrMcp.Analysis.Claims;
using AdrMcp.Analysis.Contradiction;
using AdrMcp.Analysis.Drift;
using AdrMcp.Analysis.Safety;
using AdrMcp.Analysis.SafetyReview;
using AdrMcp.Analysis.Technology;
using AdrMcp.Application.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AdrMcp.Analysis;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers every deterministic analyzer plugin. Add a detector by registering one more ITechnologyDetector/IDriftDetector; nothing else changes.</summary>
    public static IServiceCollection AddAdrAnalysis(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ContentSafetyOptions>(configuration.GetSection(ContentSafetyOptions.Section));
        services.Configure<SafetyPolicyOptions>(configuration.GetSection(SafetyPolicyOptions.Section));

        services.AddSingleton<ISensitivePatternProvider, ConfigurationSensitivePatternProvider>();
        services.AddSingleton<IContentSafetyService, CompositeContentSafetyService>();

        services.AddSingleton<ITechnologyDetector, CsprojDetector>();
        services.AddSingleton<ITechnologyDetector, PackageJsonDetector>();
        services.AddSingleton<ITechnologyDetector, PythonRequirementsDetector>();
        services.AddSingleton<ITechnologyDetector, DockerfileDetector>();
        services.AddSingleton<ITechnologyDetector, BicepDetector>();
        services.AddSingleton<ITechnologyDetector, TerraformDetector>();
        services.AddSingleton<ITechnologyDetector, KubernetesDetector>();
        services.AddSingleton<ITechnologyDetector, CiCdDetector>();

        services.AddSingleton<IClaimExtractor, RegexClaimExtractor>();

        services.AddSingleton<IDriftDetector, ProhibitedDependencyDriftDetector>();
        services.AddSingleton<IDriftDetector, InfrastructureRequirementDriftDetector>();
        services.AddSingleton<IDriftDetector, DatabaseOwnershipDriftDetector>();

        services.AddSingleton<IContradictionRule, ProhibitedVsRequiredRule>();
        services.AddSingleton<IContradictionRule, IntegrationStyleRule>();
        services.AddSingleton<IContradictionRule, DatabaseOwnershipRule>();
        services.AddSingleton<IContradictionRule, CloudProviderRule>();
        services.AddSingleton<IContradictionRule, AuthStrategyRule>();
        services.AddSingleton<IContradictionRule, DataResidencyRule>();
        services.AddSingleton<IContradictionRule, ProhibitedVsObservedRule>();
        services.AddSingleton<ISemanticContradictionAnalyzer, NullSemanticContradictionAnalyzer>();

        services.AddSingleton<IPolicyProvider, ConfigurationPolicyProvider>();
        services.AddSingleton<ISafetyReviewRule, EolTechnologyRule>();
        services.AddSingleton<ISafetyReviewRule, MissingRollbackPlanRule>();
        services.AddSingleton<ISafetyReviewRule, MissingMigrationPlanRule>();
        services.AddSingleton<ISafetyReviewRule, WeakEvidenceRule>();
        services.AddSingleton<ISafetyReviewRule, SecurityWeaknessRule>();
        services.AddSingleton<ISafetyReviewRule, AvailabilityAndDataLossRule>();
        services.AddSingleton<ISafetyReviewRule, PrivacyAndComplianceRule>();
        services.AddSingleton<ISafetyReviewRule, VendorLockInAndCostRule>();
        services.AddSingleton<ISafetyReviewRule, ScalabilityObservabilityRule>();
        services.AddSingleton<ISafetyReviewRule, IrreversibilityAndBlastRadiusRule>();
        services.AddSingleton<ISafetyReviewRule, ResponsibleAiAccessibilityRule>();
        services.AddSingleton<ISafetyReviewRule, ConflictsWithAcceptedRule>();
        return services;
    }
}
