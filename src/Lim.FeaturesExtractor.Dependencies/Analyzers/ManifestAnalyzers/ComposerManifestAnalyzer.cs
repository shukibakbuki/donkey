#nullable disable

using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Lim.Common.DotNET;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Lim.FeaturesExtractor.Dependencies;

public class ComposerManifestAnalyzer(
    IFileSystem fileSystem,
    IAsyncProcessExecutor asyncProcessExecutor,
    IPackageUrlGenerator packageUrlGenerator,
    ILogger<ComposerManifestAnalyzer> logger,
    IMonitor monitor,
    IProxyConnector proxyConnector
) : IManifestAnalyzer<ComposerManifest>
{
    private const string ComposerJsonFileName = "composer.json";
    private const string ComposerLockFileName = "composer.lock";
    private const string UnknownVersion = "?";
    private const int MaximumMissingDependencies = 500;
    private static readonly IReadOnlySet<string> RequirementsToIgnore = new HashSet<string> { "php", "hhvm" };
    private static readonly IReadOnlySet<string> RequirementPrefixesToIgnore = new HashSet<string> { "lib-", "ext-" };

    private readonly IEnumerable<Regex> _missingPackageRegexes = new[]
    {
        RegexUtils.GetCompiledRegex("Root composer.json requires (.*), it could not be found in any version, there may be a typo in the package name.$"),
        RegexUtils.GetCompiledRegex("Root composer.json requires (.*?) (.*), found .* but it does not match the constraint.$")
    };

    public DependencyType DependencyType => DependencyType.Composer;

    public async Task RestoreOrMockAsync(AnalysisContext analysisContext, IList<UnresolvedPackage> unresolvedPackages, IReadOnlyDictionary<string, Component> components, CancellationToken cancellationToken)
    {
    }

    public async Task<(bool Success, IReadOnlySet<DependencyGraphNode> DependencyGraphNodes, IReadOnlySet<UnresolvedPackage> UnresolvedPackages)> TryAnalyzeDependenciesAsync(
        AnalysisContext analysisContext,
        CancellationToken cancellationToken
    )
    {
        logger.LogDebug("TryAnalyzeDependenciesAsync START");
        var mockFolderPath = Path.Join(analysisContext.RepositoryCloneRootPath, "mock-packages");
        fileSystem.CreateDirectoryIfNotExists(mockFolderPath);
        try
        {
            var result = await TryAnalyzeDependenciesSingleFileAsync(new ComposerAnalysisContext(analysisContext, mockFolderPath), cancellationToken);
            return (result.Success, result.DependencyGraphNodes, ImmutableHashSet<UnresolvedPackage>.Empty);
        }
        catch (Exception exception) when (!exception.IsCanceledException())
        {
            logger.LogError(exception, "Error analyzing dependencies");
            return (false, ImmutableHashSet<DependencyGraphNode>.Empty, ImmutableHashSet<UnresolvedPackage>.Empty);
        }
        finally
        {
            logger.LogDebug("TryAnalyzeDependenciesAsync END");
        }
    }

    public async Task<(bool Success, IReadOnlySet<DependencyGraphNode> DirectDependencyNodes)> TryCollectDependenciesAsync(AnalysisContext analysisContext, CancellationToken cancellationToken)
        => (true, ImmutableHashSet<DependencyGraphNode>.Empty);

    private async Task<(bool Success, IReadOnlySet<DependencyGraphNode> DependencyGraphNodes)> TryAnalyzeDependenciesSingleFileAsync(
        ComposerAnalysisContext analysisContext,
        CancellationToken cancellationToken
    )
    {
        var manifestFilePath = analysisContext.ManifestFilePath;
        if (Path.GetFileName(manifestFilePath) == ComposerJsonFileName)
        {
            logger.LogDebug("Installing manifest");
            (var success, manifestFilePath) = await InstallComposerJsonAsync(analysisContext, cancellationToken);
            if (!success)
            {
                return (false, ImmutableHashSet<DependencyGraphNode>.Empty);
            }
        }

        if (Path.GetFileName(manifestFilePath) == ComposerLockFileName)
        {
            logger.LogDebug("Handling lock");
            var (result, handleComposerLockDuration) = await TimingUtils.MeasureAsync(
                async () => await HandleComposerLockFileAsync(
                    manifestFilePath,
                    analysisContext,
                    cancellationToken
                )
            );
            monitor.RecordTiming("composer_handle_lock_file_duration", handleComposerLockDuration);
            logger.LogDebug(
                "composer_handle_lock_file_duration = {ComposerHandleLockFileDuration} with Result = {LockFileResult}",
                handleComposerLockDuration,
                result.Success
            );

            return result;
        }

        return (false, ImmutableHashSet<DependencyGraphNode>.Empty);
    }

    private async Task<(bool Success, IReadOnlySet<DependencyGraphNode> DependencyGraphNodes)> HandleComposerLockFileAsync(
        string composerLockFilePath,
        ComposerAnalysisContext analysisContext,
        CancellationToken cancellationToken
    )
    {
        var (success, composerLock) = await AnalyzersUtils.TryReadJsonAsync<ComposerLock>(
            composerLockFilePath,
            fileSystem,
            logger,
            cancellationToken
        );

        if (!success)
        {
            return (false, ImmutableHashSet<DependencyGraphNode>.Empty);
        }

        var dependencyGraphNodesByPackageName = CreateDependencyGraphNodesByPackageName(composerLock, analysisContext);

        var nonTopLevelDependencyGraphNodeKeys = new HashSet<string>();

        var dependencyGraphNodes = dependencyGraphNodesByPackageName.Values.ToHashSet();

        foreach (var dependencyGraphNode in dependencyGraphNodes)
        {
            var immediateDependencies = dependencyGraphNode.ImmediateDependencies
                .SelectToHashSet(
                    _ => dependencyGraphNodesByPackageName[_]
                        .Key
                );

            nonTopLevelDependencyGraphNodeKeys.AddRange(immediateDependencies);

            dependencyGraphNode.ImmediateDependencies = immediateDependencies;
        }

        foreach (var dependencyGraphNode in dependencyGraphNodes.Where(_ => !nonTopLevelDependencyGraphNodeKeys.Contains(_.Key)))
        {
            dependencyGraphNode.IsTopLevelDependency = true;
        }

        AnalyzersUtils.SetTopLevelDependencies(dependencyGraphNodes);

        return (true, dependencyGraphNodes);
    }

    private Dictionary<string, DependencyGraphNode> CreateDependencyGraphNodesByPackageName(ComposerLock composerLock, ComposerAnalysisContext analysisContext)
    {
        var dependencyGraphNodesByPackageName = new Dictionary<string, DependencyGraphNode>();

        var allRequirements = new Dictionary<string, string>();

        foreach (var composerLockPackage in composerLock.Packages.Union(composerLock.PackagesDev))
        {
            var (dependencyGraphNode, packageRequirements) = ComposerLockPackageToDependencyGraphNode(composerLockPackage, analysisContext);
            foreach (var (requiredPackage, requiredPackageVersion) in packageRequirements)
            {
                allRequirements[requiredPackage] = requiredPackageVersion;
            }

            dependencyGraphNodesByPackageName[composerLockPackage.Name] = dependencyGraphNode;
            foreach (var (replaceName, _) in composerLockPackage.Replace.EmptyIfNull())
            {
                dependencyGraphNodesByPackageName[replaceName] = dependencyGraphNode;
            }
        }

        foreach (var (requiredPackage, requiredPackageVersion) in allRequirements)
        {
            if (dependencyGraphNodesByPackageName.ContainsKey(requiredPackage))
            {
                continue;
            }

            dependencyGraphNodesByPackageName[requiredPackage] = CreateDependencyGraphNode(
                analysisContext,
                requiredPackage,
                FormatVersionForComposerJson(requiredPackageVersion),
                [],
                isUnresolvedDependency: true
            );
        }

        return dependencyGraphNodesByPackageName;
    }

    private (DependencyGraphNode CreatedDependencyGraphNode, Dictionary<string, string> Requirements) ComposerLockPackageToDependencyGraphNode(ComposerLockPackage composerLockPackage, ComposerAnalysisContext analysisContext)
    {
        var requirements = composerLockPackage
            .Require
            .EmptyIfNull()
            .Where(_ => !ShouldIgnoreRequirement(_.Key))
            .ToDictionary(_ => _.Key, _ => _.Value);

        var createdDependencyGraphNode = CreateDependencyGraphNode(
            analysisContext,
            composerLockPackage.Name,
            composerLockPackage.Version.RemovePrefix("v"),
            requirements.Keys.ToHashSet()
        );

        return (createdDependencyGraphNode, requirements);
    }

    private DependencyGraphNode CreateDependencyGraphNode(
        ComposerAnalysisContext analysisContext,
        string packageName,
        string packageVersion,
        HashSet<string> immediateDependencies,
        bool isUnresolvedDependency = false
    )
        => new()
        {
            PackageName = packageName,
            PackageVersion = packageVersion,
            IsUnresolvedDependency = isUnresolvedDependency || analysisContext.MockPackages.ContainsKey(packageName),
            IsTopLevelDependency = false,
            PackageUrl = packageUrlGenerator.GeneratePackageUrl(
                "composer",
                packageName,
                packageVersion
            ),
            ManifestFilePath = analysisContext.RelativeManifestFilePath,
            RepositoryKey = analysisContext.RepositoryKey,
            CommitSha = analysisContext.CommitSha,
            DependencyType = DependencyType.Composer,
            ImmediateDependencies = immediateDependencies
        };

    private async Task<(bool Success, string InstalledLockFilePath)> InstallComposerJsonAsync(
        ComposerAnalysisContext analysisContext,
        CancellationToken cancellationToken
    )
    {
        for (var i = 0; i < MaximumMissingDependencies; i++)
        {
            var ((installationSuccess, installationOutput), installDuration) = await TimingUtils.MeasureAsync(
                async () => await RunComposerInstallAsync(analysisContext, cancellationToken)
            );
            monitor.RecordTiming("composer_install_duration", installDuration);
            logger.LogDebug(
                "composer_install_duration = {ComposerInstallDuration} with Result = {ComposerInstallResult}",
                installDuration,
                installationSuccess
            );

            if (!installationSuccess)
            {
                var (handledErrors, handleErrorsDuration) = await TimingUtils.MeasureAsync(
                    async () => await HandleInstallationErrorsAsync(installationOutput, analysisContext)
                );
                monitor.RecordTiming("composer_handle_installation_errors_duration", handleErrorsDuration);
                logger.LogDebug(
                    "composer_handle_installation_errors_duration = {HandleErrorsDuration} with Result = {HandleErrorsResult}",
                    handleErrorsDuration,
                    handledErrors
                );

                if (handledErrors)
                {
                    continue;
                }

                return (false, string.Empty);
            }

            var composerLockFilePath = Path.Join(analysisContext.ManifestDirectoryPath, ComposerLockFileName);

            if (fileSystem.FileExists(composerLockFilePath))
            {
                return (true, composerLockFilePath);
            }

            var filePaths = fileSystem.GetFilePaths(analysisContext.ManifestDirectoryPath);
            logger.LogError(
                "Could not install composer file. File paths are: {FilePaths}, installation output: {InstallationOutput}",
                filePaths.JoinToString(", "),
                installationOutput
            );
            return (false, string.Empty);
        }

        return (false, string.Empty);
    }

    private async Task<(bool Success, string Output)> RunComposerInstallAsync(AnalysisContext analysisContext, CancellationToken cancellationToken)
    {
        try
        {
            var output = await asyncProcessExecutor.RunProcessAsync(
                new RunProcessOptions
                {
                    WorkingDirectory = analysisContext.ManifestDirectoryPath
                }.ApplyProxyIfNeeded(proxyConnector),
                cancellationToken,
                "composer install --no-scripts --ignore-platform-reqs"
            );
            return (true, output);
        }
        catch (ProcessExecutionFailedException processExecutionFailedException)
        {
            return (false, processExecutionFailedException.StandardError);
        }
    }

    private async Task<bool> HandleInstallationErrorsAsync(string errorMessage, ComposerAnalysisContext analysisContext)
    {
        var currentInstallationMissingPackages = ErrorsToMissingPackages(errorMessage.Split("\n"));
        if (currentInstallationMissingPackages.None())
        {
            logger.LogInformation("Got the following errors without missing packages:\n{Errors}", errorMessage);
            return false;
        }

        if (!ValidateMissingDependencies(currentInstallationMissingPackages, analysisContext))
        {
            return false;
        }

        foreach (var (missingPackageName, missingPackageVersion) in currentInstallationMissingPackages)
        {
            analysisContext.MockPackages[missingPackageName] = missingPackageVersion;
        }

        var mockPackageFolderPaths = new HashSet<string>();
        foreach (var (missingPackageName, missingPackageVersion) in currentInstallationMissingPackages)
        {
            var ((success, lockFilePath), createMockPackageDuration) = await TimingUtils.MeasureAsync(
                async () => await CreateMockPackageAsync(
                    missingPackageName,
                    missingPackageVersion,
                    analysisContext
                )
            );
            monitor.RecordTiming("composer_create_mock_package_duration", createMockPackageDuration);
            logger.LogDebug(
                "composer_create_mock_package_duration = {CreateMockPackageDuration} with Result = {MockResult}",
                createMockPackageDuration,
                success
            );

            if (!success)
            {
                return false;
            }

            mockPackageFolderPaths.Add(lockFilePath);
        }

        var (result, addRepositoriesDuration) = await TimingUtils.MeasureAsync(
            async () =>
                await AddRepositoriesToComposerJsonFileAsync(mockPackageFolderPaths, analysisContext)
        );
        monitor.RecordTiming("composer_add_repositories_to_json_duration", addRepositoriesDuration);
        logger.LogDebug(
            "composer_add_repositories_to_json_duration = {AddRepositoriesDuration} with Result = {AddRepositoriesResult}",
            addRepositoriesDuration,
            result
        );

        return result;
    }

    private bool ValidateMissingDependencies(List<(string, string)> currentInstallationMissingPackages, ComposerAnalysisContext analysisContext)
    {
        var failures = new List<(string, string)>();

        foreach (var (missingPackageName, missingPackageVersion) in currentInstallationMissingPackages)
        {
            if (analysisContext.MockPackages.TryGetValue(missingPackageName, out var existingVersion) &&
                existingVersion != UnknownVersion)
            {
                failures.Add((missingPackageName, missingPackageVersion));
            }
        }

        if (failures.None())
        {
            return true;
        }

        logger.LogError(
            "Could not add the following packages to cache: {RepeatingMissingPackages}",
            failures
                .Select(_ => $"{_.Item1} {_.Item2}")
                .JoinToString(", ")
        );
        return false;
    }

    private List<(string, string)> ErrorsToMissingPackages(IReadOnlyList<string> errors)
        => _missingPackageRegexes
            .SelectMany(
                missingPackageRegex => errors
                    .SelectMany(line => missingPackageRegex.Matches(line))
                    .Select(
                        match =>
                        {
                            var packageName = match.Groups[1]
                                .Value;
                            var packageVersion = match.Groups.Count > 2
                                ? match.Groups[2]
                                    .Value
                                : UnknownVersion;
                            return (packageName, packageVersion);
                        }
                    )
            )
            .ToList();

    private async Task<(bool Success, string LockFilePath)> CreateMockPackageAsync(string packageName, string packageVersion, ComposerAnalysisContext analysisContext)
    {
        var folderPath = Path.Join(analysisContext.MockPackagesFolderPath, packageName.Replace(Path.DirectorySeparatorChar, newChar: '-'));
        fileSystem.CreateDirectoryIfNotExists(folderPath);
        var composerLockFilePath = Path.Join(folderPath, ComposerJsonFileName);

        var composerJsonFileContent = new Dictionary<string, string> { ["name"] = packageName };
        if (packageVersion != UnknownVersion)
        {
            var formattedVersion = FormatVersionForComposerJson(packageVersion);
            if (string.IsNullOrEmpty(formattedVersion))
            {
                logger.LogError("Could not convert version constraint \"{VersionConstraint}\" to a concrete version", packageVersion);
                return (false, string.Empty);
            }

            composerJsonFileContent["version"] = formattedVersion;
        }

        await fileSystem.WriteJsonAsync(composerJsonFileContent, composerLockFilePath);

        return (true, folderPath);
    }

    private async Task<bool> AddRepositoriesToComposerJsonFileAsync(ISet<string> mockPackageFolderPaths, AnalysisContext analysisContext)
    {
        JObject composerJsonFileContent;
        try
        {
            composerJsonFileContent = await fileSystem.ReadJsonAsync(analysisContext.ManifestFilePath, cancellationToken: default);
        }
        catch (JsonReaderException jsonReaderException)
        {
            logger.LogWarning(
                jsonReaderException,
                "Could not load composer.json file \"{PackageFilePath}\"",
                analysisContext.ManifestFilePath
            );
            return false;
        }

        var repositoriesArray = new JArray();
        if (composerJsonFileContent.TryGetValue("repositories", out var repositoriesElement))
        {
            if (repositoriesElement is not JArray repositoriesElementJArray)
            {
                logger.LogError("\"Repositories\" element of composer.json is not an array");
                return false;
            }

            repositoriesArray = repositoriesElementJArray;
        }

        foreach (var mockPackageFolderPath in mockPackageFolderPaths)
        {
            repositoriesArray.Add(
                new JObject(
                    new JProperty("type", "path"),
                    new JProperty("url", mockPackageFolderPath)
                )
            );
        }

        composerJsonFileContent["repositories"] = repositoriesArray;

        await fileSystem.WriteJsonAsync(composerJsonFileContent, analysisContext.ManifestFilePath);

        return true;
    }

    private static bool ShouldIgnoreRequirement(string requiredPackage)
        => RequirementsToIgnore.Contains(requiredPackage) || RequirementPrefixesToIgnore.Any(requiredPackage.StartsWith);

    private static string FormatVersionForComposerJson(string versionConstraint)
    {
        if (versionConstraint == "*")
        {
            return "1.0.0";
        }

        if (versionConstraint.Contains('|'))
        {
            versionConstraint = versionConstraint
                .Split("|")
                .First()
                .RemoveAllWhitespaces();
        }

        if (versionConstraint.StartsWith("~"))
        {
            versionConstraint = versionConstraint.RemovePrefix("~");

            if (versionConstraint.Contains("@"))
            {
                var versionConstraintParts = versionConstraint.Split("@");
                return versionConstraintParts.Length != 2
                    ? string.Empty
                    : BumpVersion(versionConstraintParts[0]);
            }

            return BumpVersion(versionConstraint);
        }

        for (var i = 0; i < versionConstraint.Length; i++)
        {
            if (char.IsDigit(versionConstraint[i]))
            {
                return versionConstraint[i..];
            }
        }

        return string.Empty;
    }

    private static string BumpVersion(string version)
    {
        if (!version.Contains('.'))
        {
            return $"{version}.1";
        }

        return version.EndsWith("0")
            ? $"{version.RemoveSuffix("0")}1"
            : $"{version}1";
    }

    private class ComposerAnalysisContext : AnalysisContext
    {
        public ComposerAnalysisContext(AnalysisContext context, string mockFolderPath)
        {
            ManifestFilePath = context.ManifestFilePath;
            RepositoryCloneRootPath = context.RepositoryCloneRootPath;
            RepositoryKey = context.RepositoryKey;
            CommitSha = context.CommitSha;
            MockPackagesFolderPath = mockFolderPath;
        }

        public Dictionary<string, string> MockPackages { get; } = new();

        public string MockPackagesFolderPath { get; }
    }
}
