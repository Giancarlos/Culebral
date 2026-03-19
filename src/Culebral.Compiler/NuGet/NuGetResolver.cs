using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Culebral.Compiler.Diagnostics;

namespace Culebral.Compiler.NuGet;

/// <summary>
/// Resolves NuGet packages by generating a temporary .csproj, running dotnet restore,
/// and loading the resolved assemblies for compile-time type resolution.
/// </summary>
public sealed class NuGetResolver
{
    private readonly DiagnosticBag _diagnostics;
    private readonly List<string> _resolvedAssemblyPaths = [];

    /// <summary>Paths to resolved NuGet package assemblies.</summary>
    public IReadOnlyList<string> ResolvedAssemblyPaths => _resolvedAssemblyPaths;

    public NuGetResolver(DiagnosticBag diagnostics)
    {
        _diagnostics = diagnostics;
    }

    /// <summary>
    /// Resolve NuGet packages from a culebral.toml project file.
    /// Generates a temp .csproj, runs dotnet restore, and discovers assembly paths.
    /// </summary>
    public bool Resolve(ProjectFileParser projectFile)
    {
        if (projectFile.Dependencies.Count == 0)
            return true;

        var tempDir = Path.Combine(Path.GetTempPath(), $"culebral_nuget_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);

            // Generate temporary .csproj with package references
            var csprojPath = Path.Combine(tempDir, "resolve.csproj");
            GenerateCsproj(csprojPath, projectFile);

            // Run dotnet restore
            if (!RunDotNetRestore(csprojPath, tempDir))
                return false;

            // Parse the assets file to find resolved assembly paths
            var assetsPath = Path.Combine(tempDir, "obj", "project.assets.json");
            if (!File.Exists(assetsPath))
            {
                _diagnostics.Error("LEB5001", "NuGet restore did not produce an assets file",
                    new SourceSpan(default, default));
                return false;
            }

            DiscoverAssemblyPaths(assetsPath, projectFile.TargetFramework);

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
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
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

    private static void GenerateCsproj(string path, ProjectFileParser projectFile)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<Project Sdk=\"Microsoft.NET.Sdk\">");
        sb.AppendLine("  <PropertyGroup>");
        sb.AppendLine($"    <TargetFramework>{projectFile.TargetFramework}</TargetFramework>");
        sb.AppendLine("    <OutputType>Library</OutputType>");
        sb.AppendLine("  </PropertyGroup>");
        sb.AppendLine("  <ItemGroup>");

        foreach (var dep in projectFile.Dependencies)
        {
            if (dep.IsFrameworkReference)
            {
                sb.AppendLine($"    <FrameworkReference Include=\"{dep.PackageId}\" />");
            }
            else
            {
                sb.AppendLine($"    <PackageReference Include=\"{dep.PackageId}\" Version=\"{dep.Version}\" />");
            }
        }

        sb.AppendLine("  </ItemGroup>");
        sb.AppendLine("</Project>");

        File.WriteAllText(path, sb.ToString());
    }

    private bool RunDotNetRestore(string csprojPath, string workingDir)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"restore \"{csprojPath}\" --verbosity quiet",
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var process = Process.Start(psi);
        if (process is null)
        {
            _diagnostics.Error("LEB5003", "Failed to start 'dotnet restore'",
                new SourceSpan(default, default));
            return false;
        }

        process.WaitForExit(60_000); // 60 second timeout

        if (!process.HasExited)
        {
            process.Kill(true);
            _diagnostics.Error("LEB5004", "NuGet restore timed out after 60 seconds",
                new SourceSpan(default, default));
            return false;
        }

        if (process.ExitCode != 0)
        {
            var stderr = process.StandardError.ReadToEnd();
            _diagnostics.Error("LEB5005", $"NuGet restore failed: {stderr.Trim()}",
                new SourceSpan(default, default));
            return false;
        }

        return true;
    }

    private void DiscoverAssemblyPaths(string assetsPath, string targetFramework)
    {
        try
        {
            var json = File.ReadAllText(assetsPath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Get the NuGet global packages folder
            var packageFolders = root.GetProperty("packageFolders");
            var nugetDir = packageFolders.EnumerateObject().FirstOrDefault().Name;

            if (string.IsNullOrEmpty(nugetDir))
                return;

            // Navigate to targets → target framework
            if (!root.TryGetProperty("targets", out var targets))
                return;

            // Find the matching target framework (e.g., "net10.0")
            JsonElement? targetSection = null;
            foreach (var target in targets.EnumerateObject())
            {
                if (target.Name.Contains(targetFramework, StringComparison.OrdinalIgnoreCase))
                {
                    targetSection = target.Value;
                    break;
                }
            }

            if (targetSection is null)
                return;

            // Enumerate each package and find its compile-time assemblies
            foreach (var package in targetSection.Value.EnumerateObject())
            {
                if (!package.Value.TryGetProperty("compile", out var compile))
                    continue;

                var packageParts = package.Name.Split('/');
                if (packageParts.Length != 2) continue;

                var packageId = packageParts[0];
                var packageVersion = packageParts[1];

                foreach (var assembly in compile.EnumerateObject())
                {
                    var relativePath = assembly.Name;
                    if (relativePath.EndsWith("_._")) continue; // Placeholder, not a real assembly

                    var fullPath = Path.Combine(nugetDir, packageId.ToLowerInvariant(),
                        packageVersion, relativePath.Replace('/', Path.DirectorySeparatorChar));

                    if (File.Exists(fullPath))
                        _resolvedAssemblyPaths.Add(fullPath);
                }
            }
        }
        catch (Exception ex)
        {
            _diagnostics.Warning("LEB5006",
                $"Failed to parse NuGet assets file: {ex.Message}",
                new SourceSpan(default, default));
        }
    }
}
