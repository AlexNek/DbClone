using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;

using DbAdapter.Definitions;

using FluentAssertions;

namespace Application.Tests;

/// <summary>
/// Guards the layer facts the adapter refactor (<c>refactors/08</c>) rests on.
/// Phase 0 records the tree as it stands today; later phases extend the rule set.
/// </summary>
public class ArchitectureTests
{
    private static readonly string RepoRoot = LocateRepoRoot();

    /// <summary>Gets the directory of the engine block, relative to the repository root.</summary>
    private const string BlockDirName = "DbAdapter.PostgreSql";

    /// <summary>Gets the directory of the shared model, relative to the repository root.</summary>
    private const string ModelDirName = "CanonicalModel";

    /// <summary>Gets the directory of the boundary contract, relative to the repository root.</summary>
    private const string DefinitionsDirName = "DbAdapter.Definitions";

    /// <summary>Gets the source roots the layer rules are measured over.</summary>
    private static readonly string[] SourceRoots = [ModelDirName, DefinitionsDirName, "src", BlockDirName];

    /// <summary>Gets the regular expression matching a using of a Microsoft.Extensions namespace.</summary>
    private static readonly Regex ExtensionsUsing = new(@"^using\s+Microsoft\.Extensions\.[\w.]+\s*;$", RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>Gets the regular expression matching a type name from the provider library.</summary>
    private static readonly Regex NpgsqlTypeName = new(@"\bNpgsql\b", RegexOptions.Compiled);

    [Fact]
    public void NoFileOutsideTheBlockNamesAnNpgsqlType()
    {
        // Arrange
        var outsideBlock = SourceFiles().Where(IsOutsideBlock).ToList();

        // Act
        var offenders = outsideBlock
            .Where(file => NpgsqlTypeName.IsMatch(File.ReadAllText(file)))
            .Select(file => Path.GetRelativePath(RepoRoot, file))
            .ToList();

        // Assert
        offenders.Should().BeEmpty(
            "only the engine block may name a provider library type");
    }

    [Fact]
    public void ApplicationProjectHasNoProjectReferenceOutsideTheAllowedSet()
    {
        // Arrange
        var allowed = new[] { "CanonicalModel.csproj", "DbAdapter.Definitions.csproj" };

        // Act
        var references = ProjectReferencesOf("src/DbClone.Application/DbClone.Application.csproj");

        // Assert
        // The shared model is the language every layer speaks and the definitions assembly is
        // the one boundary contract the application may name; a reference to the block or to
        // the UI would invert the layer.
        references.Should().NotContain(
            reference => reference.EndsWith(BlockDirName + ".csproj", StringComparison.Ordinal)
                         || reference.EndsWith("DbClone.UI.csproj", StringComparison.Ordinal),
            "the application must never reference the engine block or the UI");
        references.Should().BeSubsetOf(allowed, "the application's project references are fixed by the target graph");
    }

    [Fact]
    public void CanonicalModelDeclaresNoReferenceAndReachesNoMicrosoftExtensionsNamespace()
    {
        // Arrange
        const string modelProject = ModelDirName + "/" + ModelDirName + ".csproj";

        // Act
        var projectReferences = ProjectReferencesOf(modelProject);
        var packageReferences = PackageReferencesOf(modelProject);
        var offenders = ProjectFiles(modelProject)
            .Where(file => ExtensionsUsing.IsMatch(File.ReadAllText(file)))
            .Select(file => Path.GetRelativePath(RepoRoot, file))
            .ToList();

        // Assert
        // D10: the shared language is dependency-free. The only package it receives is the
        // build-time GitVersion stamper the root Directory.Build.props adds to every project.
        projectReferences.Should().BeEmpty("CanonicalModel references nothing");
        packageReferences.Should().BeEmpty("CanonicalModel declares no package of its own");
        offenders.Should().BeEmpty("CanonicalModel must not reach a Microsoft.Extensions namespace");
    }

    [Fact]
    public void NoStageImplementationExistsOutsideTheBlock()
    {
        // Arrange
        var implementsStage = new Regex(@":\s*(ICopyStage|DbClone\.Application\.Copy\.ICopyStage)\b");

        // Act
        var stages = SourceFiles()
            .Where(IsOutsideBlock)
            .Where(file => implementsStage.IsMatch(File.ReadAllText(file)))
            .Select(file => Path.GetFileNameWithoutExtension(file))
            .ToList();

        // Assert
        // Table filtering left the pipeline in Phase 3, so every stage now lives in the block.
        stages.Should().BeEmpty(
            "the pipeline's stages all live in the engine block");
    }

    [Fact]
    public void DefinitionsAssemblyDeclaresOnlyInterfacesRecordsAndEnums()
    {
        // Act
        var declared = typeof(AdapterInfo).Assembly
            .GetTypes()
            .Where(type => !type.IsDefined(typeof(CompilerGeneratedAttribute), false))
            .ToList();

        // Assert
        declared.Should().NotBeEmpty("the boundary contract declares the roles and the travelling values");
        declared.Where(type => !IsDeclaration(type))
            .Select(type => type.FullName)
            .Should()
            .BeEmpty("DbAdapter.Definitions contains no implementation (D10)");
    }

    [Fact]
    public void DefinitionsProjectReferencesOnlyTheSharedModel()
    {
        // Arrange
        const string definitionsProject = DefinitionsDirName + "/" + DefinitionsDirName + ".csproj";

        // Act
        var projectReferences = ProjectReferencesOf(definitionsProject);
        var packageReferences = PackageReferencesOf(definitionsProject);
        var offenders = ProjectFiles(definitionsProject)
            .Where(file => ExtensionsUsing.IsMatch(File.ReadAllText(file)))
            .Select(file => Path.GetRelativePath(RepoRoot, file))
            .ToList();

        // Assert
        projectReferences.Should().BeEquivalentTo(new[] { "CanonicalModel.csproj" },
            "the contract speaks the shared language and nothing else");
        packageReferences.Should().BeEmpty("DbAdapter.Definitions declares no package of its own");
        offenders.Should().BeEmpty("DbAdapter.Definitions must not reach a Microsoft.Extensions namespace");
    }

    /// <summary>Tests whether a type is one of the three declaration kinds the contract allows.</summary>
    private static bool IsDeclaration(Type type) =>
        type.IsInterface
        || type.IsEnum
        || IsRecord(type);

    /// <summary>
    /// Tests whether a type is a record. The compiler synthesizes <c>PrintMembers</c> for every
    /// record, which is the only reliable reflection marker it leaves on the type itself.
    /// </summary>
    private static bool IsRecord(Type type) =>
        type.IsClass
        && type.GetMethod("PrintMembers",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public) is not null;

    private static IEnumerable<string> ProjectReferencesOf(string relativeProjectPath) =>
        ReferencesOf(relativeProjectPath, "ProjectReference");

    private static IEnumerable<string> PackageReferencesOf(string relativeProjectPath) =>
        ReferencesOf(relativeProjectPath, "PackageReference");

    private static IEnumerable<string> ReferencesOf(string relativeProjectPath, string itemName)
    {
        var path = Path.Combine(RepoRoot, relativeProjectPath.Replace('/', Path.DirectorySeparatorChar));
        var project = XDocument.Load(path);
        var ns = project.Root!.GetDefaultNamespace();

        return project.Descendants(ns + itemName)
            .Select(e => e.Attribute("Include")?.Value)
            .Where(v => v is not null)
            .Select(v => Path.GetFileName(v!))!;
    }

    private static IEnumerable<string> ProjectFiles(string relativeProjectPath)
    {
        var directory = Path.Combine(RepoRoot, Path.GetDirectoryName(relativeProjectPath)!.Replace('/', Path.DirectorySeparatorChar));

        return Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                           && !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));
    }

    private static bool IsOutsideBlock(string file) =>
        !Path.GetRelativePath(RepoRoot, file)
            .Replace(Path.DirectorySeparatorChar, '/')
            .StartsWith(BlockDirName + "/", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> SourceFiles() => SourceRoots
        .Select(root => Path.Combine(RepoRoot, root.Replace('/', Path.DirectorySeparatorChar)))
        .Where(Directory.Exists)
        .SelectMany(root => Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                       && !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));

    private static string LocateRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "DbClone.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
               ?? throw new InvalidOperationException("DbClone.slnx not found above the test assembly.");
    }
}
