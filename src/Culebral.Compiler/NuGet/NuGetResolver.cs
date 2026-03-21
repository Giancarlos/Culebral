using System.Reflection;
using Culebral.Compiler.Diagnostics;
using NuGet.Common;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace Culebral.Compiler.NuGet;

/// <summary>
/// Resolves NuGet packages using the official NuGet client libraries.
/// Downloads missing packages from nuget.org, caches in the global NuGet folder,
/// and loads resolved assemblies for compile-time type resolution.
///
/// Framework references (e.g., Microsoft.AspNetCore.App) are resolved from
/// the .NET shared runtime installation — they are NOT NuGet packages.
/// </summary>
public sealed class NuGetResolver
{
    private readonly DiagnosticBag _diagnostics;
    private readonly List<string> _resolvedAssemblyPaths = [];

    private static readonly string NuGetV3Feed = "https://api.nuget.org/v3/index.json";
    private static readonly string GlobalPackagesFolder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");

    /// <summary>Paths to resolved NuGet package assemblies.</summary>
    public IReadOnlyList<string> ResolvedAssemblyPaths => _resolvedAssemblyPaths;

    public NuGetResolver(DiagnosticBag diagnostics)
    {
        _diagnostics = diagnostics;
    }

    /// <summary>
    /// Resolve NuGet packages from a culebral.toml project file.
    /// Uses NuGet client libraries to resolve versions, download packages,
    /// and discover compile-time assemblies. No subprocess spawning.
    /// </summary>
    public bool Resolve(ProjectFileParser projectFile)
    {
        if (projectFile.Dependencies.Count == 0)
            return true;

        try
        {
            var tfm = NuGetFramework.Parse(projectFile.TargetFramework);

            // Resolve framework references from the .NET shared runtime
            foreach (var dep in projectFile.Dependencies.Where(d => d.IsFrameworkReference))
            {
                ResolveFrameworkReference(dep.PackageId);
            }

            // Resolve NuGet packages
            var nugetDeps = projectFile.Dependencies.Where(d => !d.IsFrameworkReference).ToList();
            if (nugetDeps.Count > 0)
            {
                ResolvePackagesAsync(nugetDeps, tfm).GetAwaiter().GetResult();
            }

            // Load resolved assemblies into the runtime for type resolution
            foreach (var asmPath in _resolvedAssemblyPaths)
            {
                try
                {
                    Assembly.LoadFrom(asmPath);
                }
                catch (Exception ex)
                {
                    _diagnostics.Warning("LEB5002",
                        $"Could not load NuGet assembly '{Path.GetFileName(asmPath)}': {ex.Message}",
                        new SourceSpan(default, default));
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            _diagnostics.Error("LEB5000", $"NuGet resolution failed: {ex.Message}",
                new SourceSpan(default, default));
            return false;
        }
    }

    /// <summary>Generate package reference entries for .runtimeconfig.json.</summary>
    public List<string> GetFrameworkReferences(ProjectFileParser projectFile)
    {
        return projectFile.Dependencies
            .Where(d => d.IsFrameworkReference)
            .Select(d => d.PackageId)
            .ToList();
    }

    /// <summary>
    /// Resolve NuGet packages using the NuGet.Protocol client libraries.
    /// Checks the global cache first, downloads from nuget.org if missing.
    /// </summary>
    private async Task ResolvePackagesAsync(List<PackageReference> packages, NuGetFramework tfm)
    {
        var repository = Repository.Factory.GetCoreV3(NuGetV3Feed);
        var resource = await repository.GetResourceAsync<FindPackageByIdResource>();
        using var cacheContext = new SourceCacheContext();
        var logger = NullLogger.Instance;
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        foreach (var pkg in packages)
        {
            try
            {
                await ResolvePackageAsync(pkg, tfm, resource, cacheContext, logger, cts.Token);
            }
            catch (OperationCanceledException)
            {
                _diagnostics.Error("LEB5004",
                    $"NuGet resolution timed out for package '{pkg.PackageId}'",
                    new SourceSpan(default, default));
            }
            catch (Exception ex)
            {
                _diagnostics.Error("LEB5001",
                    $"Failed to resolve package '{pkg.PackageId}' version '{pkg.Version}': {ex.Message}",
                    new SourceSpan(default, default));
            }
        }
    }

    private async Task ResolvePackageAsync(
        PackageReference pkg,
        NuGetFramework tfm,
        FindPackageByIdResource resource,
        SourceCacheContext cacheContext,
        ILogger logger,
        CancellationToken ct)
    {
        // Resolve the version
        var resolvedVersion = await ResolveVersionAsync(pkg, resource, cacheContext, logger, ct);
        if (resolvedVersion is null)
        {
            _diagnostics.Error("LEB5005",
                $"Could not resolve version '{pkg.Version}' for package '{pkg.PackageId}'",
                new SourceSpan(default, default));
            return;
        }

        // Check global cache first
        var packageDir = Path.Combine(GlobalPackagesFolder,
            pkg.PackageId.ToLowerInvariant(), resolvedVersion.ToNormalizedString());

        if (!Directory.Exists(packageDir))
        {
            // Download from nuget.org
            await DownloadPackageAsync(pkg.PackageId, resolvedVersion, resource, cacheContext, logger, ct);
        }

        // Extract compile-time assembly paths from the cached package
        DiscoverCompileAssemblies(pkg.PackageId, resolvedVersion, tfm);
    }

    /// <summary>
    /// Resolve the concrete version for a package. Supports exact versions and wildcard (*).
    /// </summary>
    private static async Task<NuGetVersion?> ResolveVersionAsync(
        PackageReference pkg,
        FindPackageByIdResource resource,
        SourceCacheContext cacheContext,
        ILogger logger,
        CancellationToken ct)
    {
        if (pkg.Version == "*")
        {
            // Get latest stable version
            var versions = await resource.GetAllVersionsAsync(pkg.PackageId, cacheContext, logger, ct);
            return versions
                .Where(v => !v.IsPrerelease)
                .OrderByDescending(v => v)
                .FirstOrDefault()
                ?? versions.OrderByDescending(v => v).FirstOrDefault();
        }

        // Try parsing as an exact version first
        if (NuGetVersion.TryParse(pkg.Version, out var exactVersion))
        {
            // Verify the version exists
            var exists = await resource.DoesPackageExistAsync(
                pkg.PackageId, exactVersion, cacheContext, logger, ct);
            if (exists)
                return exactVersion;

            // If exact version doesn't exist, return null
            return null;
        }

        // Try parsing as a version range (e.g., "[1.0,2.0)")
        if (VersionRange.TryParse(pkg.Version, out var versionRange))
        {
            var versions = await resource.GetAllVersionsAsync(pkg.PackageId, cacheContext, logger, ct);
            return versionRange.FindBestMatch(versions);
        }

        return null;
    }

    /// <summary>
    /// Download a package from nuget.org and extract it into the global packages folder.
    /// </summary>
    private async Task DownloadPackageAsync(
        string packageId,
        NuGetVersion version,
        FindPackageByIdResource resource,
        SourceCacheContext cacheContext,
        ILogger logger,
        CancellationToken ct)
    {
        using var packageStream = new MemoryStream();
        var success = await resource.CopyNupkgToStreamAsync(
            packageId, version, packageStream, cacheContext, logger, ct);

        if (!success)
        {
            _diagnostics.Error("LEB5003",
                $"Failed to download package '{packageId}' version '{version}'",
                new SourceSpan(default, default));
            return;
        }

        // Extract the nupkg into the global packages folder
        packageStream.Position = 0;
        var targetDir = Path.Combine(GlobalPackagesFolder,
            packageId.ToLowerInvariant(), version.ToNormalizedString());

        Directory.CreateDirectory(targetDir);

        using var reader = new PackageArchiveReader(packageStream);
        var files = await reader.GetFilesAsync(ct);

        foreach (var file in files)
        {
            // Skip directories and content types
            if (file.EndsWith("/") || file == "[Content_Types].xml" || file.StartsWith("_rels/"))
                continue;

            var targetPath = Path.Combine(targetDir, file.Replace('/', Path.DirectorySeparatorChar));
            var dir = Path.GetDirectoryName(targetPath);
            if (dir is not null)
                Directory.CreateDirectory(dir);

            using var entryStream = await reader.GetStreamAsync(file, ct);
            using var fileStream = File.Create(targetPath);
            await entryStream.CopyToAsync(fileStream, ct);
        }

        // Write the .nupkg.metadata file that NuGet expects
        var metadataPath = Path.Combine(targetDir, ".nupkg.metadata");
        await File.WriteAllTextAsync(metadataPath,
            $$"""{"version":2,"contentHash":"","source":"https://api.nuget.org/v3/index.json"}""", ct);
    }

    /// <summary>
    /// Discover compile-time assemblies from a cached NuGet package.
    /// Looks in lib/{tfm}/ directories for the best matching framework.
    /// </summary>
    private void DiscoverCompileAssemblies(string packageId, NuGetVersion version, NuGetFramework tfm)
    {
        var packageDir = Path.Combine(GlobalPackagesFolder,
            packageId.ToLowerInvariant(), version.ToNormalizedString());

        if (!Directory.Exists(packageDir))
        {
            _diagnostics.Warning("LEB5006",
                $"Package directory not found: {packageDir}",
                new SourceSpan(default, default));
            return;
        }

        // Look for compile-time assemblies in ref/ first, then lib/
        var refDir = Path.Combine(packageDir, "ref");
        var libDir = Path.Combine(packageDir, "lib");

        var assembliesFound = false;

        // Prefer ref/ assemblies (reference assemblies for compilation)
        if (Directory.Exists(refDir))
        {
            assembliesFound = DiscoverAssembliesInFrameworkDirs(refDir, tfm);
        }

        // Fall back to lib/ assemblies
        if (!assembliesFound && Directory.Exists(libDir))
        {
            assembliesFound = DiscoverAssembliesInFrameworkDirs(libDir, tfm);
        }

        if (!assembliesFound)
        {
            _diagnostics.Warning("LEB5006",
                $"No compile-time assemblies found for '{packageId}' {version} targeting {tfm.GetShortFolderName()}",
                new SourceSpan(default, default));
        }
    }

    /// <summary>
    /// Scan framework subdirectories (e.g., lib/net10.0/, lib/net8.0/) to find
    /// the best-matching TFM directory and add all DLLs from it.
    /// </summary>
    private bool DiscoverAssembliesInFrameworkDirs(string baseDir, NuGetFramework tfm)
    {
        var frameworkDirs = new List<(NuGetFramework framework, string path)>();

        foreach (var dir in Directory.GetDirectories(baseDir))
        {
            var dirName = Path.GetFileName(dir);
            var framework = NuGetFramework.Parse(dirName);
            if (framework.IsSpecificFramework)
            {
                frameworkDirs.Add((framework, dir));
            }
        }

        if (frameworkDirs.Count == 0)
            return false;

        // Use NuGet's framework reducer to find the best match
        var reducer = new FrameworkReducer();
        var frameworks = frameworkDirs.Select(f => f.framework).ToList();
        var nearest = reducer.GetNearest(tfm, frameworks);

        if (nearest is null)
            return false;

        var bestDir = frameworkDirs.First(f => f.framework.Equals(nearest)).path;
        var dlls = Directory.GetFiles(bestDir, "*.dll");

        foreach (var dll in dlls)
        {
            _resolvedAssemblyPaths.Add(dll);
        }

        return dlls.Length > 0;
    }

    /// <summary>
    /// Resolve a framework reference (e.g., Microsoft.AspNetCore.App) from the .NET shared runtime.
    /// Framework references ship with the .NET runtime and are NOT NuGet packages.
    /// </summary>
    private void ResolveFrameworkReference(string frameworkName)
    {
        var runtimePath = DiscoverSharedRuntimePath(frameworkName);
        if (runtimePath is null)
        {
            _diagnostics.Warning("LEB5007",
                $"Could not locate shared runtime for framework reference '{frameworkName}'. " +
                $"Ensure the .NET runtime includes '{frameworkName}'.",
                new SourceSpan(default, default));
            return;
        }

        foreach (var dll in Directory.GetFiles(runtimePath, "*.dll"))
        {
            try
            {
                Assembly.LoadFrom(dll);
            }
            catch
            {
                // Non-fatal — some DLLs may be native-only or otherwise incompatible
            }
        }
    }

    /// <summary>
    /// Discover the shared runtime path for a framework reference.
    /// Uses runtime introspection to find the .NET installation directory,
    /// then navigates to the correct shared framework directory.
    /// No subprocess spawning.
    /// </summary>
    private static string? DiscoverSharedRuntimePath(string frameworkName)
    {
        // The running .NET runtime tells us where it's installed.
        // typeof(object).Assembly.Location gives us something like:
        //   /usr/share/dotnet/shared/Microsoft.NETCore.App/10.0.4/System.Private.CoreLib.dll
        var coreLibPath = typeof(object).Assembly.Location;
        if (string.IsNullOrEmpty(coreLibPath))
            return null;

        // Navigate: /usr/share/dotnet/shared/Microsoft.NETCore.App/10.0.4/
        var coreAppVersionDir = Path.GetDirectoryName(coreLibPath);
        if (coreAppVersionDir is null)
            return null;

        // Navigate: /usr/share/dotnet/shared/Microsoft.NETCore.App/
        var coreAppDir = Path.GetDirectoryName(coreAppVersionDir);
        if (coreAppDir is null)
            return null;

        // Navigate: /usr/share/dotnet/shared/
        var sharedDir = Path.GetDirectoryName(coreAppDir);
        if (sharedDir is null)
            return null;

        // The version we're running (e.g., "10.0.4")
        var runtimeVersion = Path.GetFileName(coreAppVersionDir);

        // If the framework is Microsoft.NETCore.App, we already have the path
        if (frameworkName == "Microsoft.NETCore.App")
            return coreAppVersionDir;

        // For other frameworks (e.g., Microsoft.AspNetCore.App),
        // look in /usr/share/dotnet/shared/{frameworkName}/{version}/
        var frameworkVersionDir = Path.Combine(sharedDir, frameworkName, runtimeVersion!);
        if (Directory.Exists(frameworkVersionDir))
            return frameworkVersionDir;

        // If exact version doesn't match, find the best matching version
        var frameworkDir = Path.Combine(sharedDir, frameworkName);
        if (!Directory.Exists(frameworkDir))
            return null;

        // Find the highest version directory that shares the same major.minor
        var runtimeVer = NuGetVersion.Parse(runtimeVersion!);
        string? bestPath = null;
        NuGetVersion? bestVersion = null;

        foreach (var versionDir in Directory.GetDirectories(frameworkDir))
        {
            var dirName = Path.GetFileName(versionDir);
            if (!NuGetVersion.TryParse(dirName, out var ver))
                continue;

            if (ver.Major == runtimeVer.Major && ver.Minor == runtimeVer.Minor)
            {
                if (bestVersion is null || ver > bestVersion)
                {
                    bestVersion = ver;
                    bestPath = versionDir;
                }
            }
        }

        return bestPath;
    }
}
