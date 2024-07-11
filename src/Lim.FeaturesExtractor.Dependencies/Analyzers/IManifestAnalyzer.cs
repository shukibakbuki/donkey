#nullable disable

using Lim.Common.DotNET;

namespace Lim.FeaturesExtractor.Dependencies;

public interface IManifestAnalyzer<T> where T : DependencyManifest
{
    DependencyType DependencyType { get; }

    Task RestoreOrMockAsync(AnalysisContext analysisContext, IList<UnresolvedPackage> unresolvedPackages, IReadOnlyDictionary<string, Component> components, CancellationToken cancellationToken);

    Task<(bool Success, IReadOnlySet<DependencyGraphNode> DependencyGraphNodes, IReadOnlySet<UnresolvedPackage> UnresolvedPackages)> TryAnalyzeDependenciesAsync(
        AnalysisContext analysisContext,
        CancellationToken cancellationToken
    );

    Task<(bool Success, IReadOnlySet<DependencyGraphNode> DirectDependencyNodes)> TryCollectDependenciesAsync(
        AnalysisContext analysisContext,
        CancellationToken cancellationToken
    );
}
