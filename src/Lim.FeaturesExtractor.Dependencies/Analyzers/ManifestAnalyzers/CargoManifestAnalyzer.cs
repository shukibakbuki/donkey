#nullable disable

using System.Collections;
using System.Collections.Immutable;
using Lim.Common.DotNET;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Tomlyn;
using Tomlyn.Model;

namespace Lim.FeaturesExtractor.Dependencies;

public class CargoManifestAnalyzer(
    ILogger<CargoManifestAnalyzer> logger,
    IFileSystem fileSystem,
    IPackageUrlGenerator packageUrlGenerator,
    IMonitor monitor
)
    : IManifestAnalyzer<CargoManifest>
{
    public DependencyType DependencyType => DependencyType.Cargo;

    public async Task RestoreOrMockAsync(AnalysisContext analysisContext, IList<UnresolvedPackage> unresolvedPackages, IReadOnlyDictionary<string, Component> components, CancellationToken cancellationToken)
    {
    }

    public async Task<(bool Success, IReadOnlySet<DependencyGraphNode> DependencyGraphNodes, IReadOnlySet<UnresolvedPackage> UnresolvedPackages)> TryAnalyzeDependenciesAsync(
        AnalysisContext analysisContext,
        CancellationToken cancellationToken
    )
    {
        logger.LogDebug("TryAnalyzeDependenciesAsync START");
        var (topLevelDependencies, getTopLevelDuration) =
            await TimingUtils.MeasureAsync(
                async () =>
                    await GetTopLevelPackagesAsync(analysisContext, cancellationToken)
            );
        monitor.RecordTiming("cargo_get_top_level_dependencies_duration", getTopLevelDuration);
        logger.LogDebug("cargo_get_top_level_dependencies_duration = {GetTopLevelDependenciesDuration}", getTopLevelDuration);

        var result = await TryAnalyzeDependenciesSingleFileAsync(
            analysisContext,
            topLevelDependencies,
            cancellationToken
        );
        logger.LogDebug("TryAnalyzeDependenciesAsync END");
        return (result.Success, result.DependencyGraphNodes, ImmutableHashSet<UnresolvedPackage>.Empty);
    }

    public async Task<(bool Success, IReadOnlySet<DependencyGraphNode> DirectDependencyNodes)> TryCollectDependenciesAsync(AnalysisContext analysisContext, CancellationToken cancellationToken)
        => (true, ImmutableHashSet<DependencyGraphNode>.Empty);

    private async Task<IReadOnlySet<string>> GetTopLevelPackagesAsync(AnalysisContext context, CancellationToken cancellationToken)
    {
        var path = Path.Combine(context.ManifestDirectoryPath, CargoConstants.CargoTomlFileName);
        return fileSystem.FileExists(path)
            ? await ReadCargoFileAsync(path, cancellationToken)
            : ImmutableHashSet<string>.Empty;
    }

    private async Task<HashSet<string>> ReadCargoFileAsync(string path, CancellationToken cancellationToken)
    {
        var topLevelDependencies = new HashSet<string>();

        try
        {
            var toml = await fileSystem.FileContentAsync(path, cancellationToken);
            var model = Toml.ToModel(toml);
            if (!model.TryGetValue("dependencies", out var dependencies))
            {
                return topLevelDependencies;
            }

            foreach (var dependency in (IEnumerable)dependencies)
            {
                topLevelDependencies.Add(((KeyValuePair<string, object>)dependency).Key);
            }
        }
        catch (Exception e)
        {
            logger.LogInformation(
                e,
                "Failed to read {CargoTomlPath}",
                path
            );
        }

        return topLevelDependencies;
    }

    private async Task<(bool Success, IReadOnlySet<DependencyGraphNode> DependencyGraphNodes)> TryAnalyzeDependenciesSingleFileAsync(
        AnalysisContext analysisContext,
        IReadOnlySet<string> topLevelDependencies,
        CancellationToken cancellationToken
    )
    {
        try
        {
            return await AnalyzeDependenciesSingleFileInternalAsync(
                analysisContext,
                topLevelDependencies,
                cancellationToken
            );
        }
        catch (Exception e) when (!e.IsCanceledException())
        {
            logger.LogInformation(e, "Error analyzing dependencies");

            return (false, ImmutableHashSet<DependencyGraphNode>.Empty);
        }
    }

    private async Task<(bool Success, IReadOnlySet<DependencyGraphNode> DependencyGraphNodes)> AnalyzeDependenciesSingleFileInternalAsync(
        AnalysisContext analysisContext,
        IReadOnlySet<string> topLevelDependencies,
        CancellationToken cancellationToken
    )
    {
        CargoLock cargoLock;

        try
        {
            cargoLock = await ReadLockFileAsync(analysisContext.ManifestFilePath, cancellationToken);
        }
        catch (Exception e)
        {
            logger.LogInformation(
                e,
                "Failed to read Cargo.lock file {Manifest}",
                analysisContext.RelativeManifestFilePath
            );
            return (false, ImmutableHashSet<DependencyGraphNode>.Empty);
        }

        if (cargoLock.Version != 3)
        {
            logger.LogWarning(
                "Manifest file {ManifestFile} has lockFileVersion={LockFileVersion}",
                analysisContext.RelativeManifestFilePath,
                cargoLock.Version
            );
        }

        if (cargoLock.Packages == null)
        {
            logger.LogInformation("Manifest file {ManifestFile} does not have \"packages\" key", analysisContext.RelativeManifestFilePath);
            return (false, ImmutableHashSet<DependencyGraphNode>.Empty);
        }

        try
        {
            return GetDependencyGraphNodesFromPackages(
                analysisContext,
                cargoLock.Packages,
                topLevelDependencies
            );
        }
        catch (Exception e)
        {
            logger.LogInformation(e, "Failed to build dependencies graph");
            return (false, ImmutableHashSet<DependencyGraphNode>.Empty);
        }
    }

    private (bool Success, IReadOnlySet<DependencyGraphNode> DependencyGraphNodes) GetDependencyGraphNodesFromPackages(
        AnalysisContext analysisContext,
        Dictionary<string, List<CargoPackage>> packageByFullName,
        IReadOnlySet<string> topLevelDependencies
    )
    {
        var dependencyGraphNodeByNameVersion = new Dictionary<string, List<DependencyGraphNode>>();
        foreach (var (name, packages) in packageByFullName)
        {
            dependencyGraphNodeByNameVersion[name] = packages.SelectToList(
                package => new DependencyGraphNode
                {
                    PackageName = package.Name,
                    PackageVersion = package.Version,
                    PackageUrl = packageUrlGenerator.GeneratePackageUrl(
                        "cargo",
                        package.Name,
                        package.Version
                    ),
                    IsUnresolvedDependency = false,
                    IsTopLevelDependency = topLevelDependencies.Contains(package.Name),
                    ManifestFilePath = analysisContext.RelativeManifestFilePath,
                    RepositoryKey = analysisContext.RepositoryKey,
                    CommitSha = analysisContext.CommitSha,
                    DependencyType = DependencyType.Cargo,
                    ImmediateDependencies = package.Dependencies.ToHashSet()
                }
            );
        }

        UpdateImmediateDependencies(dependencyGraphNodeByNameVersion);

        var dependencies = dependencyGraphNodeByNameVersion
            .Values.SelectMany(_ => _)
            .ToHashSet();
        AnalyzersUtils.SetTopLevelDependencies(dependencies);

        return (true, dependencies);
    }

    private void UpdateImmediateDependencies(Dictionary<string, List<DependencyGraphNode>> dependencyGraphNodeByName)
    {
        foreach (var node in dependencyGraphNodeByName.Values.Flatten())
        {
            node.ImmediateDependencies = node
                .ImmediateDependencies.SelectNotNull(
                    dependency =>
                    {
                        var (name, version) = SplitDependency(dependency);
                        if (version == null)
                        {
                            return dependencyGraphNodeByName[dependency]
                                .First()
                                .Key;
                        }

                        return dependencyGraphNodeByName[name]
                            .FirstOrDefault(_ => _.PackageVersion == version)
                            ?.Key;
                    }
                )
                .ToHashSet();
        }
    }

    private static (string Name, string Version) SplitDependency(string dependency)
    {
        if (!dependency.Contains(' '))
        {
            return (dependency, null);
        }

        var parts = dependency.Split(' ');
        return (parts[0], parts[1]);
    }

    private async Task<CargoLock> ReadLockFileAsync(string path, CancellationToken cancellationToken)
    {
        var toml = await fileSystem.FileContentAsync(path, cancellationToken);

        var model = Toml.ToModel(toml);
        var tomlVersion = GetTomlVersion(model);
        var cargoLock = new CargoLock(tomlVersion, new Dictionary<string, List<CargoPackage>>());
        if (!model.Keys.Contains("package"))
        {
            return cargoLock;
        }

        foreach (var package in model["package"] as TomlTableArray)
        {
            var cargoPackage = CreatePackage(package);
            cargoLock
                .Packages.GetOrAdd(cargoPackage.Name, () => [])
                .Add(cargoPackage);
        }

        return cargoLock;
    }

    private static long GetTomlVersion(TomlTable model)
    {
        try
        {
            return (long)model["version"];
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static CargoPackage CreatePackage(TomlTable package)
    {
        var name = string.Empty;
        var version = string.Empty;
        var url = string.Empty;
        var dependencies = new List<string>();
        foreach (var (key, value) in package)
        {
            switch (key)
            {
                case "name":
                    name = value as string;
                    break;
                case "version":
                    version = value as string;
                    break;
                case "source":
                    url = value as string;
                    break;
                case "dependencies":
                    dependencies = JsonConvert.DeserializeObject<List<string>>(value.ToJson());
                    break;
            }
        }

        return new CargoPackage(
            name,
            version,
            url,
            dependencies
        );
    }
}
