#nullable disable

using System.Collections.Immutable;
using AsyncUtilities;
using Lim.Common.DotNET;
using Microsoft.Extensions.Logging;

namespace Lim.FeaturesExtractor.Dependencies;

public class CsProjManifestAnalyzer : NuGetManifestAnalyzer<CsProjManifest>
{
    private const int MaxNumberOfRestores = 8;
    private const string NopString = "Nothing to do";
    private const string EnableWindowsTargetingErrorString = "set the EnableWindowsTargeting property to true";
    private const string EnableWindowsTargetingPropertyName = "EnableWindowsTargeting";
    private const string TrailingSlashErrorString = "error MSB4115: The \"HasTrailingSlash\" function only accepts a scalar value";
    private const string ConfigurationPropertyName = "Configuration";
    private const string RequestedSdkErrorString = "Requested SDK version";
    private const string GlobalJsonFileName = "global.json";
    private const string UnableToFindPackageErrorString = "error NU1101: Unable to find package";
    private const string MissingProjectFileErrorString = "error MSB4025: The project file could not be loaded";
    private const string NugetOrgUrlString = "http://nuget.org/";
    private readonly IAsyncProcessExecutor _asyncProcessExecutor;
    private readonly int _dotnetRestoreTimeoutInMilliseconds;
    private readonly IPackageUrlGenerator _packageUrlGenerator;
    private readonly IProxyConnector _proxyConnector;
    private readonly Func<AnalysisContext, string, Dictionary<string, string>, CancellationToken, Task<(bool Success, string Output)>> _restoreManifestAsync;

    private readonly StripedAsyncLock<string> _stripedAsyncLock = new(100);

    public CsProjManifestAnalyzer(
        IAsyncProcessExecutor asyncProcessExecutor,
        IPackageUrlGenerator packageUrlGenerator,
        IFileSystem fileSystem,
        ILogger<CsProjManifestAnalyzer> logger,
        IMonitor monitor,
        IPackageRestorer<CsProjComponent> packageRestorer,
        IProxyConnector proxyConnector,
        TimeSpan dotnetRestoreTimeoutTimespan,
        bool globalCacheEnabled
    ) : base(
        fileSystem,
        logger,
        monitor,
        packageRestorer
    )
    {
        _asyncProcessExecutor = asyncProcessExecutor;
        _packageUrlGenerator = packageUrlGenerator;
        _proxyConnector = proxyConnector;
        _dotnetRestoreTimeoutInMilliseconds = (int)dotnetRestoreTimeoutTimespan.TotalMilliseconds;
        _restoreManifestAsync = globalCacheEnabled
            ? SafeRestoreManifestAsync
            : RestoreManifestAsync;
        Logger.LogInformation(
            "CsProjDependenciesAnalyzer was initialized with dotnet restore timeout: [{TimeoutTimespan}]",
            dotnetRestoreTimeoutTimespan
        );
    }

    public static IEnumerable<string> OtherManifestNames => new[]
    {
        "packages.config",
        "project.json",
        "project.lock.json"
    };

    public override async Task<(bool Success, IReadOnlySet<DependencyGraphNode> DirectDependencyNodes)> TryCollectDependenciesAsync(AnalysisContext analysisContext, CancellationToken cancellationToken)
        => (true, ImmutableHashSet<DependencyGraphNode>.Empty);

    protected override async Task<(bool Success, IReadOnlySet<DependencyGraphNode> DependencyGraphNodes, IReadOnlySet<UnresolvedPackage> unresolvedPackages)> AnalyzeManifestAsync(
        AnalysisContext context,
        CancellationToken cancellationToken
    )
    {
        Logger.LogDebug("AnalyzeManifestAsync START");
        var allUnresolvedDependencies = new HashSet<UnresolvedPackage>();
        (bool Success, IReadOnlySet<DependencyGraphNode> DependencyGraphNodes) result = (false, ImmutableHashSet<DependencyGraphNode>.Empty);
        var additionalProperties = new Dictionary<string, string>();
        try
        {
            var monitoringTags = new Dictionary<string, string>
            {
                ["AnalyzerName"] = GetType()
                    .Name
            };
            for (var i = 0; i < MaxNumberOfRestores; i++)
            {
                (result, var shouldRetry) = await RestoreAndParseAsync();
                if (!result.Success)
                {
                    Logger.LogWarning("Failed to restore or parse attempt #{Attempt}", i + 1);
                    if (!shouldRetry)
                    {
                        return (false, result.DependencyGraphNodes, ImmutableHashSet<UnresolvedPackage>.Empty);
                    }
                }

                if (result.Success &&
                    result.DependencyGraphNodes.None(_ => _.IsUnresolvedDependency))
                {
                    return (true, result.DependencyGraphNodes, ImmutableHashSet<UnresolvedPackage>.Empty);
                }

                var unresolvedDependencies = result
                    .DependencyGraphNodes.Where(_ => _.IsUnresolvedDependency)
                    .ToList();
                if (Logger.IsEnabled(LogLevel.Debug))
                {
                    Logger.LogDebug(
                        "[{UnresolvedDependenciesCount}] unresolved dependencies: [{UnresolvedDependencies}]",
                        unresolvedDependencies.Count,
                        unresolvedDependencies
                            .Select(_ => _.PackageName + " v" + _.PackageVersion)
                            .JoinToString()
                    );
                }

                var (managedToRestoreAnyUnresolved, unresolvedPackages) = await TryRestoreUnresolvedAsync(
                    context.CacheFolderPath,
                    unresolvedDependencies.Select(node => (node.PackageName, node.PackageVersion)),
                    monitoringTags,
                    cancellationToken
                );
                allUnresolvedDependencies.AddRange(unresolvedPackages);

                if (!managedToRestoreAnyUnresolved)
                {
                    return (result.Success, result.DependencyGraphNodes, allUnresolvedDependencies);
                }
            }

            Logger.LogWarning("Exceeded maximum number of restores");
            return (result.Success, result.DependencyGraphNodes, allUnresolvedDependencies);
        }
        finally
        {
            Logger.LogDebug("AnalyzeManifestAsync END");
        }

        async Task<((bool Success, IReadOnlySet<DependencyGraphNode> DependencyGraphNodes), bool SholdRetry)> RestoreAndParseAsync()
        {
            var ((restoreSuccess, restoreOutput), restoreDuration) = await TimingUtils.MeasureAsync(
                async () =>
                    await _restoreManifestAsync(
                        context,
                        context.ManifestDirectoryPath,
                        additionalProperties,
                        cancellationToken
                    )
            );
            Monitor.RecordTiming("dotnet_restore_duration", restoreDuration);
            if (restoreSuccess && restoreOutput.Contains(NopString))
            {
                return ((true, ImmutableHashSet<DependencyGraphNode>.Empty), false);
            }

            if (!restoreSuccess &&
                !string.IsNullOrEmpty(restoreOutput))
            {
                foreach (var (key, value) in ParseErrorsToProperties(restoreOutput))
                {
                    additionalProperties[key] = value;
                }

                HandleSdkErrors(context, restoreOutput);
            }

            var projectAssetsJsonPath = Path.Combine(
                context.ManifestDirectoryPath,
                "obj",
                "project.assets.json"
            );
            var (parseResult, parseDuration) = await TimingUtils.MeasureAsync(
                async () =>
                    await ParseProjectAssetsJsonAsync(
                        projectAssetsJsonPath,
                        context,
                        cancellationToken
                    )
            );
            Monitor.RecordTiming("parse_project_assets_duration", parseDuration);
            if (!parseResult.Success)
            {
                Logger.LogInformation("Failed to restore manifest. Error is: {Error}", restoreOutput);
            }

            return (parseResult, true);
        }
    }

    private IEnumerable<(string Key, string Value)> ParseErrorsToProperties(string error)
    {
        if (error.Contains(EnableWindowsTargetingErrorString))
        {
            Logger.LogInformation("Setting {Property} to true", EnableWindowsTargetingPropertyName);
            return new List<(string, string)> { (EnableWindowsTargetingPropertyName, "true") };
        }

        if (error.Contains(TrailingSlashErrorString))
        {
            Logger.LogInformation("Setting {Property} to Release", ConfigurationPropertyName);
            return new List<(string, string)> { (ConfigurationPropertyName, "Release") };
        }

        return Enumerable.Empty<(string, string)>();
    }

    private bool HandleSdkErrors(AnalysisContext context, string error)
    {
        var handled = false;
        if (error.Contains(RequestedSdkErrorString))
        {
            var globalJsonPaths = FileSystem.GetFilePathsRecursive(context.RepositoryCloneRootPath, GlobalJsonFileName);
            foreach (var path in globalJsonPaths)
            {
                handled = true;
                Logger.LogInformation(
                    "Deleting global.json file at {GlobalJsonPath}",
                    Path.GetRelativePath(context.RepositoryCloneRootPath, path)
                );
                FileSystem.DeleteFile(path);
            }
        }

        return handled;
    }

    private async Task<(bool Success, string Output)> SafeRestoreManifestAsync(
        AnalysisContext context,
        string workingDirectory,
        Dictionary<string, string> additionalProperties,
        CancellationToken cancellationToken
    )
    {
        using var _ = await _stripedAsyncLock.LockAsync(context.CacheFolderPath, cancellationToken);
        return await RestoreManifestAsync(
            context,
            workingDirectory,
            additionalProperties,
            cancellationToken
        );
    }

    private async Task<(bool Success, string Output)> RestoreManifestAsync(
        AnalysisContext context,
        string workingDirectory,
        Dictionary<string, string> additionalProperties,
        CancellationToken cancellationToken
    )
    {
        using var cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cancellationTokenSource.CancelAfter(_dotnetRestoreTimeoutInMilliseconds);
        try
        {
            var additionalPropertiesString = additionalProperties
                .EmptyIfNull()
                .Select(_ => $"/p:{_.Key}={_.Value}")
                .JoinToString(" ");
            var output = await _asyncProcessExecutor.RunProcessAsync(
                new RunProcessOptions
                {
                    WorkingDirectory = workingDirectory,
                    EnvironmentVariables = new Dictionary<string, string>()
                }.ApplyProxyIfNeeded(_proxyConnector),
                cancellationTokenSource.Token,
                $"dotnet restore /p:RestoreUseStaticGraphEvaluation=true /p:BaseIntermediateOutputPath=obj/ {additionalPropertiesString} \"{context.ManifestFilePath}\" --packages \"{context.CacheFolderPath}\" --source https://api.nuget.org/v3/index.json"
            );
            return (true, output ?? string.Empty);
        }
        catch (Exception exception) when (exception.IsCanceledException() && !cancellationToken.IsCancellationRequested)
        {
            Logger.LogWarning("dotnet restore timed out, setting Alive = false");
            LivenessController.Alive = false;
            throw;
        }
        catch (ProcessExecutionFailedException processExecutionFailedException)
        {
            if (OtherManifestNames.Any(manifest => File.Exists(Path.Combine(context.ManifestDirectoryPath, manifest))))
            {
                Logger.LogDebug("Failed to analyze manifest - other manifest types detected");
            }
            else if (processExecutionFailedException.Message.Contains(MissingProjectFileErrorString))
            {
                Logger.LogDebug("Failed to analyze manifest - missing reference manifest file");
            }
            else if (!(processExecutionFailedException.Message.Contains(UnableToFindPackageErrorString) &&
                       processExecutionFailedException.Message.Contains(NugetOrgUrlString)))
            {
                Logger.LogDebug(processExecutionFailedException, "Failed to analyze manifest");
            }

            return (false, processExecutionFailedException.Message);
        }
    }

    private async Task<(bool Success, IReadOnlySet<DependencyGraphNode> DependencyGraphNodes)> ParseProjectAssetsJsonAsync(
        string projectAssetsJsonPath,
        AnalysisContext context,
        CancellationToken cancellationToken
    )
    {
        if (!FileSystem.FileExists(projectAssetsJsonPath))
        {
            Logger.LogDebug("ProjectAsset doesn't exist");
            return (false, ImmutableHashSet<DependencyGraphNode>.Empty);
        }

        try
        {
            var projectAssets = await FileSystem.ReadJsonAsync<ProjectAssets>(projectAssetsJsonPath, cancellationToken: cancellationToken);
            return (true, projectAssets.ToGraphNodes(
                context,
                _packageUrlGenerator
            ));
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "Failed to generate nodes");
            return (false, ImmutableHashSet<DependencyGraphNode>.Empty);
        }
    }
}
