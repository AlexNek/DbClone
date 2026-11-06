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

    /// <summary>Gets engine names, driver packages and SQL keywords that must not appear in the contract's code.</summary>
    private static readonly Regex DefinitionsEngineOrSqlInCode = new(
        @"\b(postgres(?:ql)?|supabase|aiven|neon|mysql|npgsql)\b|\bpg_[a-z_]+|\b(select|insert|update|delete|create|drop|alter|truncate)\b|information_schema",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    [Fact]
    public void DefinitionsCodeNamesNoEngineSqlOrDriver()
    {
        // Arrange
        const string definitionsProject = DefinitionsDirName + "/" + DefinitionsDirName + ".csproj";

        // Act
        // The contract now holds the shared copy runner and the platform reader, so the old
        // "declarations only" rule is replaced by a narrower, checkable one (refactors/09 P5):
        // no file here writes SQL and no file here names an engine or a driver. Prose is out of
        // scope — the project's existing remarks (in files this refactor does not move) carry
        // engine examples, so the scan runs over code with comments stripped, not raw text.
        var offenders = ProjectFiles(definitionsProject)
            .Select(file => new { Path = file, Code = StripComments(File.ReadAllText(file)) })
            .Where(x => DefinitionsEngineOrSqlInCode.IsMatch(x.Code))
            .Select(x => Path.GetRelativePath(RepoRoot, x.Path))
            .ToList();

        // Assert
        offenders.Should().BeEmpty(
            "DbAdapter.Definitions holds no SQL, no engine name and no driver reference in code (refactors/09 P5)");
    }


    /// <summary>Gets the regular expression matching a using of a Microsoft.Extensions namespace other than the logging abstraction.</summary>
    private static readonly Regex NonLoggingExtensionsUsing = new(@"^using\s+Microsoft\.Extensions\.(?!Logging[\w.]*\s*;)[\w.]+\s*;$", RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>Gets the regular expression matching the engine block's project or adapter class name.</summary>
    private static readonly Regex EngineBlockOrAdapterName = new(@"\bPostgreSqlAdapter\b|\bDbAdapter\.PostgreSql\b", RegexOptions.Compiled);

    [Fact]
    public void NoSrcFileNamesTheEngineBlockOrAdapter()
    {
        // Arrange — refactors/09 Phase 4 box 78.
        var srcRoot = Path.Combine(RepoRoot, "src");

        // Act
        var offenders = Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                           && !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(file => EngineBlockOrAdapterName.IsMatch(File.ReadAllText(file)))
            .Select(file => Path.GetRelativePath(RepoRoot, file))
            .ToList();

        // Assert — only DbClone.UI.csproj may name the adapter project (a build-ordering ProjectReference
        // plus the PublishAdapters target), and only that csproj — no C# file under src.
        offenders.Should().BeEmpty(
            "no C# file under src may name the engine block or its adapter class (refactors/09 Phase 4)");
    }

    [Fact]
    public void BlockProjectDoesNotReferenceApplication()
    {
        // Arrange — refactors/09 Phase 4 box 78; the strict one-way layer rule.
        const string blockProject = BlockDirName + "/" + BlockDirName + ".csproj";

        // Act
        var references = ProjectReferencesOf(blockProject).ToList();

        // Assert — the block depends only on the shared boundary and the shared model.
        references.Should().NotContain(
            reference => reference.Equals("DbClone.Application.csproj", StringComparison.Ordinal),
            "the engine block must not reference the application (refactors/09 Phase 4)");
    }

    [Fact]
    public void DefinitionsProjectReferencesOnlyTheSharedModel()
    {
        // Arrange
        const string definitionsProject = DefinitionsDirName + "/" + DefinitionsDirName + ".csproj";

        // Act
        var projectReferences = ProjectReferencesOf(definitionsProject);
        var packageReferences = PackageReferencesOf(definitionsProject);
        // The shared copy runner and platform reader log, so the logging abstraction is now the
        // one package the contract may declare and the one Microsoft.Extensions namespace its code
        // may reach (refactors/09 P5). Anything beyond logging still fails the rule.
        var offenders = ProjectFiles(definitionsProject)
            .Where(file => NonLoggingExtensionsUsing.IsMatch(File.ReadAllText(file)))
            .Select(file => Path.GetRelativePath(RepoRoot, file))
            .ToList();

        // Assert
        projectReferences.Should().BeEquivalentTo(new[] { "CanonicalModel.csproj" },
            "the contract speaks the shared language and nothing else");
        packageReferences.Should().BeEquivalentTo(new[] { "Microsoft.Extensions.Logging.Abstractions" },
            "the logging abstraction is the only package DbAdapter.Definitions declares");
        offenders.Should().BeEmpty("DbAdapter.Definitions may reach only the Microsoft.Extensions.Logging namespace");
    }


    /// <summary>
    /// Strips C# line and block comments so the contract's engine-free rule is measured over code
    /// only. Approximate by design: the definitions project writes no <c>//</c> or <c>/*</c> inside a
    /// string literal, so a text strip is enough to ignore the prose the rule deliberately excludes.
    /// </summary>
    private static string StripComments(string source)
    {
        var noBlock = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return Regex.Replace(noBlock, @"//[^\n]*", string.Empty);
    }

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
