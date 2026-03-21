using System.Reflection;
using System.Text.Json;
using Culebral.Compiler.Diagnostics;

namespace Culebral.Compiler.NuGet;

/// <summary>
/// Reads project.assets.json produced by `dotnet restore` to discover resolved
/// assembly paths for compile-time type resolution.
///
/// This is the standard NuGet lock file that contains the full dependency graph
/// with resolved versions and file paths. By reading it directly, we avoid
/// reimplementing NuGet's dependency resolution logic.
/// </summary>
public sealed class ProjectAssetsReader
{
    private readonly DiagnosticBag _diagnostics;
    private readonly List<string> _resolvedAssemblyPaths = [];

    /// <summary>Paths to resolved compile-time assemblies.</summary>
    public IReadOnlyList<string> ResolvedAssemblyPaths => _resolvedAssemblyPaths;

    public ProjectAssetsReader(DiagnosticBag diagnostics)
    {
        _diagnostics = diagnostics;
    }

    /// <summary>
    /// Run `dotnet restore` on a .lebproj file and then read the resulting
    /// project.assets.json to discover assembly paths.
    /// </summary>
    /// <returns>True if restore and resolution succeeded.</returns>
    public bool RestoreAndResolve(string lebprojPath)
    {
        var projectDir = Path.GetDirectoryName(Path.GetFullPath(lebprojPath));
        if (projectDir is null)
        {
            _diagnostics.Error("LEB5010", "Could not determine project directory.",
                new SourceSpan(default, default));
            return false;
        }

        // Run dotnet restore on the actual .lebproj file
        if (!RunDotnetRestore(lebprojPath))
            return false;

        // Read project.assets.json from the obj/ directory
        var assetsPath = Path.Combine(projectDir, "obj", "project.assets.json");
        if (!File.Exists(assetsPath))
        {
            _diagnostics.Error("LEB5011",
                $"project.assets.json not found at '{assetsPath}'. Did dotnet restore succeed?",
                new SourceSpan(default, default));
            return false;
        }

        return ReadAssetsFile(assetsPath);
    }

    /// <summary>
    /// Read an existing project.assets.json without running restore.
    /// Useful when restore has already been run externally.
    /// </summary>
    public bool ReadExistingAssets(string lebprojPath)
    {
        var projectDir = Path.GetDirectoryName(Path.GetFullPath(lebprojPath));
        if (projectDir is null)
            return false;

        var assetsPath = Path.Combine(projectDir, "obj", "project.assets.json");
        if (!File.Exists(assetsPath))
            return false;

        return ReadAssetsFile(assetsPath);
    }

    /// <summary>
    /// Run `dotnet restore` on the project file.
    /// </summary>
    private bool RunDotnetRestore(string projectPath)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"restore \"{projectPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null)
            {
                _diagnostics.Error("LEB5012", "Failed to start 'dotnet restore' process.",
                    new SourceSpan(default, default));
                return false;
            }

            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                _diagnostics.Error("LEB5013",
                    $"dotnet restore failed (exit code {process.ExitCode}): {stderr.Trim()}",
                    new SourceSpan(default, default));
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _diagnostics.Error("LEB5014",
                $"dotnet restore failed: {ex.Message}",
                new SourceSpan(default, default));
            return false;
        }
    }

    /// <summary>
    /// Parse project.assets.json and extract compile-time assembly paths.
    ///
    /// The assets file structure (simplified):
    /// {
    ///   "targets": {
    ///     "net10.0": {
    ///       "PackageName/1.0.0": {
    ///         "type": "package",
    ///         "compile": {
    ///           "lib/net10.0/PackageName.dll": {}
    ///         }
    ///       }
    ///     }
    ///   },
    ///   "packageFolders": {
    ///     "/home/user/.nuget/packages/": {}
    ///   }
    /// }
    /// </summary>
    private bool ReadAssetsFile(string assetsPath)
    {
        try
        {
            var json = File.ReadAllText(assetsPath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Get package folders (where NuGet packages are cached)
            var packageFolders = new List<string>();
            if (root.TryGetProperty("packageFolders", out var foldersElement))
            {
                foreach (var folder in foldersElement.EnumerateObject())
                {
                    packageFolders.Add(folder.Name);
                }
            }

            if (packageFolders.Count == 0)
            {
                // Default to the global NuGet cache
                packageFolders.Add(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".nuget", "packages"));
            }

            // Get the targets section
            if (!root.TryGetProperty("targets", out var targetsElement))
            {
                _diagnostics.Warning("LEB5015",
                    "No 'targets' section found in project.assets.json",
                    new SourceSpan(default, default));
                return true; // Not fatal — just no packages
            }

            // Iterate through target frameworks (usually just one, e.g., "net10.0")
            foreach (var tfm in targetsElement.EnumerateObject())
            {
                foreach (var package in tfm.Value.EnumerateObject())
                {
                    // package.Name is like "Newtonsoft.Json/13.0.3"
                    var packageKey = package.Name;
                    var parts = packageKey.Split('/');
                    if (parts.Length != 2)
                        continue;

                    var packageId = parts[0];
                    var version = parts[1];

                    // Check for compile assets
                    if (!package.Value.TryGetProperty("compile", out var compileElement))
                        continue;

                    foreach (var compileAsset in compileElement.EnumerateObject())
                    {
                        var relativePath = compileAsset.Name;

                        // Skip placeholder entries like "_._"
                        if (relativePath.EndsWith("_._"))
                            continue;

                        // Find the assembly in one of the package folders
                        foreach (var folder in packageFolders)
                        {
                            var fullPath = Path.Combine(folder,
                                packageId.ToLowerInvariant(),
                                version,
                                relativePath.Replace('/', Path.DirectorySeparatorChar));

                            if (File.Exists(fullPath))
                            {
                                _resolvedAssemblyPaths.Add(fullPath);
                                break;
                            }
                        }
                    }
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            _diagnostics.Error("LEB5016",
                $"Failed to parse project.assets.json: {ex.Message}",
                new SourceSpan(default, default));
            return false;
        }
    }

    /// <summary>
    /// Load all resolved assemblies into the runtime for compile-time type resolution.
    /// </summary>
    public void LoadResolvedAssemblies()
    {
        foreach (var asmPath in _resolvedAssemblyPaths)
        {
            try
            {
                Assembly.LoadFrom(asmPath);
            }
            catch (Exception ex)
            {
                _diagnostics.Warning("LEB5017",
                    $"Could not load assembly '{Path.GetFileName(asmPath)}': {ex.Message}",
                    new SourceSpan(default, default));
            }
        }
    }
}
