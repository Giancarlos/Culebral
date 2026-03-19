using System.Text.RegularExpressions;

namespace Culebral.Compiler.NuGet;

/// <summary>
/// Parses culebral.toml project files for dependency declarations.
/// Supports the [dependencies] section with version strings and table-style entries.
/// </summary>
public sealed class ProjectFileParser
{
    public string? ProjectName { get; private set; }
    public string? ProjectVersion { get; private set; }
    public string TargetFramework { get; private set; } = "net10.0";
    public List<PackageReference> Dependencies { get; } = [];

    /// <summary>
    /// Parse a culebral.toml file and extract project metadata and dependencies.
    /// </summary>
    public static ProjectFileParser Parse(string tomlPath)
    {
        var parser = new ProjectFileParser();
        if (!File.Exists(tomlPath))
            return parser;

        var lines = File.ReadAllLines(tomlPath);
        var currentSection = "";

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();

            // Skip empty lines and comments
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
                continue;

            // Section header
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                currentSection = line[1..^1].Trim();
                continue;
            }

            // Key-value pair
            var eqIndex = line.IndexOf('=');
            if (eqIndex < 0) continue;

            var key = line[..eqIndex].Trim().Trim('"');
            var value = line[(eqIndex + 1)..].Trim();

            switch (currentSection)
            {
                case "project":
                    switch (key)
                    {
                        case "name": parser.ProjectName = UnquoteToml(value); break;
                        case "version": parser.ProjectVersion = UnquoteToml(value); break;
                        case "target": parser.TargetFramework = UnquoteToml(value); break;
                    }
                    break;

                case "dependencies":
                    // Simple version: "Package.Name" = "1.0.0"
                    if (value.StartsWith('"'))
                    {
                        var version = UnquoteToml(value);
                        parser.Dependencies.Add(new PackageReference(key, version));
                    }
                    // Table-style: "Package.Name" = { framework = true }
                    else if (value.StartsWith('{'))
                    {
                        var isFramework = value.Contains("framework") && value.Contains("true");
                        if (isFramework)
                            parser.Dependencies.Add(new PackageReference(key, "*") { IsFrameworkReference = true });
                        else
                        {
                            // Extract version from table if present
                            var versionMatch = Regex.Match(value, @"version\s*=\s*""([^""]+)""");
                            var ver = versionMatch.Success ? versionMatch.Groups[1].Value : "*";
                            parser.Dependencies.Add(new PackageReference(key, ver));
                        }
                    }
                    break;
            }
        }

        return parser;
    }

    private static string UnquoteToml(string value)
    {
        value = value.Trim();
        if (value.StartsWith('"') && value.EndsWith('"') && value.Length >= 2)
            return value[1..^1];
        return value;
    }
}

public sealed class PackageReference
{
    public string PackageId { get; }
    public string Version { get; }
    public bool IsFrameworkReference { get; init; }

    public PackageReference(string packageId, string version)
    {
        PackageId = packageId;
        Version = version;
    }
}
