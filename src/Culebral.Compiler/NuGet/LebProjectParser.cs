using System.Xml.Linq;

namespace Culebral.Compiler.NuGet;

/// <summary>
/// Parses .lebproj files (MSBuild XML format) for Culebral projects.
/// Extracts TargetFramework, OutputType, PackageReferences, and FrameworkReferences
/// from standard MSBuild XML — the same format used by .csproj files.
///
/// This allows Culebral projects to participate in the .NET ecosystem:
/// - dotnet restore works on .lebproj files
/// - IDEs (VS, Rider) can open the project
/// - NuGet transitive dependencies are handled by MSBuild
/// </summary>
public sealed class LebProjectParser
{
    public string? ProjectName { get; private set; }
    public string TargetFramework { get; private set; } = "net10.0";
    public string OutputType { get; private set; } = "Exe";
    public string? SdkAttribute { get; private set; }
    public List<PackageReference> Dependencies { get; } = [];
    public List<string> FrameworkReferences { get; } = [];
    public string ProjectDirectory { get; private set; } = "";

    /// <summary>
    /// Parse a .lebproj file and extract project metadata, package references,
    /// and framework references.
    /// </summary>
    public static LebProjectParser Parse(string lebprojPath)
    {
        var parser = new LebProjectParser();
        if (!File.Exists(lebprojPath))
            return parser;

        parser.ProjectDirectory = Path.GetDirectoryName(Path.GetFullPath(lebprojPath)) ?? "";
        parser.ProjectName = Path.GetFileNameWithoutExtension(lebprojPath);

        var doc = XDocument.Load(lebprojPath);
        var root = doc.Root;
        if (root is null)
            return parser;

        // Read the Sdk attribute from <Project Sdk="...">
        parser.SdkAttribute = root.Attribute("Sdk")?.Value;

        // Parse PropertyGroup elements
        foreach (var pg in root.Elements("PropertyGroup"))
        {
            var tfm = pg.Element("TargetFramework")?.Value;
            if (tfm is not null)
                parser.TargetFramework = tfm;

            var outputType = pg.Element("OutputType")?.Value;
            if (outputType is not null)
                parser.OutputType = outputType;
        }

        // Parse ItemGroup elements for PackageReference and FrameworkReference
        foreach (var ig in root.Elements("ItemGroup"))
        {
            foreach (var pkgRef in ig.Elements("PackageReference"))
            {
                var include = pkgRef.Attribute("Include")?.Value;
                var version = pkgRef.Attribute("Version")?.Value ?? "*";
                if (include is not null)
                {
                    parser.Dependencies.Add(new PackageReference(include, version));
                }
            }

            foreach (var fwRef in ig.Elements("FrameworkReference"))
            {
                var include = fwRef.Attribute("Include")?.Value;
                if (include is not null)
                {
                    parser.FrameworkReferences.Add(include);
                    // Also add as a dependency with framework flag for NuGet resolver compatibility
                    parser.Dependencies.Add(new PackageReference(include, "*") { IsFrameworkReference = true });
                }
            }
        }

        // Infer framework references from the Sdk attribute
        // Microsoft.NET.Sdk.Web implies Microsoft.AspNetCore.App
        if (parser.SdkAttribute is "Microsoft.NET.Sdk.Web")
        {
            if (!parser.FrameworkReferences.Contains("Microsoft.AspNetCore.App"))
            {
                parser.FrameworkReferences.Add("Microsoft.AspNetCore.App");
                parser.Dependencies.Add(
                    new PackageReference("Microsoft.AspNetCore.App", "*") { IsFrameworkReference = true });
            }
        }

        return parser;
    }

    /// <summary>
    /// Convert to a ProjectFileParser-compatible object so the existing NuGet resolution
    /// pipeline can work with .lebproj files without changes.
    /// </summary>
    public ProjectFileParser ToProjectFileParser()
    {
        return ProjectFileParser.FromLebProject(
            ProjectName, TargetFramework, Dependencies);
    }
}
