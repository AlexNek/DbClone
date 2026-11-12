using System.Globalization;
using System.Security.Cryptography;

using CanonicalModel.Interchange;

using DatabaseInterchange.Script;

using DbAdapter.PostgreSql.Adapters;

using Microsoft.Extensions.Logging.Abstractions;

namespace ScriptHarness;

/// <summary>
/// Drives the product's own script pass over one finished catalog folder, at each budget it is handed, and
/// prints what that pass wrote — every file with its size, its statement and row counts and its SHA-256, plus the
/// folder's own listing after the run.
/// </summary>
/// <remarks>
/// <para>
/// Feature 10's §10 verification is a statement about files: which shape a folder holds, whether a file respects
/// the budget, whether two runs wrote the same bytes, and whether a run that came after another shape left
/// anything of it behind. The pass is a function of the catalog folder (feature 08 FR14), so none of that needs
/// a source database; the renderer comes from the PostgreSQL adapter so the text is the engine's own, and the
/// cycles are empty because a catalog that holds none carries no cycle note.
/// </para>
/// <para>
/// The budgets run in the order given, so `0,204800,0` states the shape switch and the return to it in one
/// command. Nothing here is a test: it prints, and a person compares the print with `plan.md` §10.
/// </para>
/// </remarks>
internal static class Program
{
    private const string Usage =
        "usage: ScriptHarness <catalogDirectory> <pluginDirectory> <budget[,budget…]>\n"
        + "\n"
        + "  catalogDirectory  a finished catalog folder; its sql/ folder is rewritten in place\n"
        + "  pluginDirectory   the folder the adapter reads platforms/postgresql from\n"
        + "  budget            SqlScriptMaxDataFileBytes in bytes; 0 = one file, the shape before the split\n";

    private static async Task<int> Main(string[] args)
    {
        if (args.Length < 3)
        {
            await Console.Error.WriteLineAsync(Usage);
            return 2;
        }

        var catalogDirectory = Path.GetFullPath(args[0]);
        var pluginDirectory = Path.GetFullPath(args[1]);
        var budgets = ParseBudgets(args[2]);

        if (!Directory.Exists(catalogDirectory))
        {
            await Console.Error.WriteLineAsync($"No catalog folder at {catalogDirectory}.");
            return 2;
        }

        // The adapter reads its platform data from the folder it is handed, so this is the harness's own output
        // folder: a plugin never reads AppContext.BaseDirectory of its host.
        var renderer = new PostgreSqlAdapter(NullLoggerFactory.Instance, pluginDirectory).CreateScriptRenderer();
        var writer = new CatalogScriptWriter();

        foreach (var budget in budgets)
        {
            var issues = new List<InterchangeIssue>();

            Console.WriteLine($"=== budget {budget.ToString(CultureInfo.InvariantCulture)} B ===");

            var result = await writer.WriteAsync(
                catalogDirectory,
                renderer,
                cycles: [],
                issues,
                budget,
                CancellationToken.None);

            Report(catalogDirectory, result, issues, Console.Out);
        }

        Console.WriteLine($"=== folder after the run ({Path.Combine(catalogDirectory, CatalogScriptWriter.DirectoryName)}) ===");
        ListSqlFolder(catalogDirectory, Console.Out);

        return 0;
    }

    private static List<long> ParseBudgets(string text)
    {
        var budgets = new List<long>();

        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!long.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var budget) || budget < 0)
            {
                throw new ArgumentException($"'{part}' is not a budget in bytes.", nameof(text));
            }

            budgets.Add(budget);
        }

        if (budgets.Count == 0)
        {
            throw new ArgumentException("At least one budget is needed.", nameof(text));
        }

        return budgets;
    }

    private static void Report(
        string catalogDirectory,
        ScriptResult result,
        IReadOnlyList<InterchangeIssue> issues,
        TextWriter output)
    {
        if (result.Cancelled)
        {
            output.WriteLine("cancelled — nothing was written");
            return;
        }

        if (result.Refusal is { } refusal)
        {
            output.WriteLine($"REFUSED [{refusal.Code}] {refusal.Reason}");
            return;
        }

        output.WriteLine($"profile          {result.Profile}");
        output.WriteLine($"budget           {result.BudgetBytes.ToString(CultureInfo.InvariantCulture)} B");
        output.WriteLine($"largest table    {result.LargestTable} ({result.LargestTableBytes.ToString(CultureInfo.InvariantCulture)} B)");
        output.WriteLine($"rows             {result.Rows.ToString(CultureInfo.InvariantCulture)}");
        output.WriteLine($"payload bytes    {result.PayloadBytes.ToString(CultureInfo.InvariantCulture)}");
        output.WriteLine($"statements       {result.DataFiles.Sum(file => file.Statements).ToString(CultureInfo.InvariantCulture)} in the data files");

        if (result.SchemaPath is { } schema)
        {
            var path = Absolute(catalogDirectory, schema);

            output.WriteLine(
                $"schema           {schema}  {Size(path)} B  {result.SchemaStatements} statements  sha256 {Hash(path)}");
        }

        foreach (var file in result.DataFiles)
        {
            var path = Absolute(catalogDirectory, file.RelativePath);
            var part = file.IsPartOf is null ? string.Empty : $"  part of {file.IsPartOf}";

            output.WriteLine(
                $"data             {file.RelativePath}  {Size(path)} B  {file.Statements} statements  "
                + $"{file.Rows} rows  sha256 {Hash(path)}{part}");
        }

        if (result.LoadPath is { } load)
        {
            var path = Absolute(catalogDirectory, load);

            output.WriteLine($"driver           {load}  {Size(path)} B  sha256 {Hash(path)}");
            output.WriteLine("--- driver text ---");
            output.Write(File.ReadAllText(path));
            output.WriteLine("--- end of driver ---");
        }

        output.WriteLine($"files            {result.DataFiles.Count.ToString(CultureInfo.InvariantCulture)} data file(s)");

        foreach (var issue in issues)
        {
            output.WriteLine($"issue            [{issue.Severity}] {issue.Code}: {issue.Message}");
        }

        output.WriteLine();
    }

    private static void ListSqlFolder(string catalogDirectory, TextWriter output)
    {
        var directory = Path.Combine(catalogDirectory, CatalogScriptWriter.DirectoryName);

        if (!Directory.Exists(directory))
        {
            output.WriteLine("no sql folder");
            return;
        }

        var files = Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
            .Select(path => new FileInfo(path))
            .OrderBy(file => file.FullName, StringComparer.Ordinal)
            .Select(file => $"{Path.GetRelativePath(catalogDirectory, file.FullName).Replace('\\', '/')}  {file.Length} B");

        foreach (var line in files)
        {
            output.WriteLine(line);
        }
    }

    private static string Absolute(string catalogDirectory, string relativePath) =>
        Path.Combine(catalogDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static long Size(string path) => new FileInfo(path).Length;

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);

        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
