using System.Text;

using DatabaseInterchange.Script;

namespace DatabaseInterchange.Report;

/// <summary>
/// Renders the whole <c>summary.md</c> document (§6) from the statistics model and the relation facts.
/// </summary>
/// <remarks>
/// <para>
/// Pure: it turns two in-memory models into one string and touches no file, no database and no clock
/// (D2). Everything measurable has already been measured by the time it gets here, so this class decides
/// only where a fact is printed and how it reads.
/// </para>
/// <para>
/// The section order is §6's: identity, then what is unusual, then the numbers a user skims, then the
/// per-table detail they search, then the relationship half last — because a fan-in count means nothing
/// until the reader has seen which side of the relation is heavy. Output is `\n` only, with one blank
/// line between sections and a final newline (D9.2); the caller writes these bytes unchanged.
/// </para>
/// </remarks>
internal sealed class MarkdownCatalogReportRenderer
{
    private readonly RelationshipRenderer _relationships = new();

    /// <summary>
    /// Renders the complete document for one catalog.
    /// </summary>
    /// <param name="statistics">The measured account of the catalog.</param>
    /// <param name="map">The relation facts analysed from the same snapshot.</param>
    /// <param name="scripts">
    /// What the SQL script pass produced, or <see langword="null"/> when the option was off — the section that
    /// names the files is present exactly when the pass ran (FR7, D9).
    /// </param>
    /// <returns>The Markdown text, ending in a newline.</returns>
    public string Render(CatalogStatistics statistics, RelationMap map, ScriptResult? scripts = null)
    {
        ArgumentNullException.ThrowIfNull(statistics);
        ArgumentNullException.ThrowIfNull(map);

        var sections = new List<string>
        {
            RenderHeader(statistics),
            RenderObservations(statistics, map),
            RenderGlobalStatistics(statistics),
            RenderDataTotals(statistics),
            RenderSchemaStatistics(statistics),
            RenderTableStatistics(statistics),
            RenderMetadataFiles(statistics),
            RenderSqlScripts(scripts),
            _relationships.Render(map, statistics),
        };

        return string.Join("\n\n", sections.Where(s => s.Length > 0)) + "\n";
    }

    /// <summary>
    /// Renders §6's four identity lines plus the unit statement. FR17's scope line is always present: a
    /// count of captured objects must never read as a count of the source database.
    /// </summary>
    private static string RenderHeader(CatalogStatistics statistics)
    {
        var scope = statistics.ExcludedTableCount == 0
            ? "Statistics describe the objects included in this catalog; objects excluded by the selection are not counted."
            : $"Statistics describe the objects included in this catalog; objects excluded by the selection " +
              $"({ReportText.Number(statistics.ExcludedTableCount)} tables) are not counted.";

        return string.Join('\n', new[]
        {
            $"# Catalog summary — {ReportText.Cell(statistics.DatabaseName)}",
            ReportText.Facts(
                $"Produced by {statistics.Producer}",
                $"exported {ReportText.Timestamp(statistics.ExportedAt)}"),
            ReportText.Facts(
                $"Format version {ReportText.Number(statistics.FormatVersion)}",
                $"source dialect {statistics.SourceDialect}",
                $"consistency: {statistics.Consistency}"),
            scope,
            "Sizes are exact bytes; prose sizes use binary units (KiB, MiB, GiB) on 1024 divisors.",
        });
    }

    /// <summary>
    /// Renders the observation index (FR16, §5.10): the lines are produced by the relationship renderer,
    /// because every one of them restates a count that half already computes.
    /// </summary>
    private string RenderObservations(CatalogStatistics statistics, RelationMap map)
    {
        var lines = _relationships.RenderObservations(map, statistics)
            .Take(ReportBudget.MaxObservations);

        return "## Catalog observations\n" +
               $"{string.Join('\n', lines)}\n\n" +
               "See ## Statistics per table and ## Relationships for the complete detail.";
    }

    /// <summary>
    /// Renders one row of counts over every captured object kind (FR2). The row is the catalog's own
    /// account of what it holds, so a kind the source did not yield reads as <c>0</c> — the capture
    /// checked it and found nothing.
    /// </summary>
    private static string RenderGlobalStatistics(CatalogStatistics statistics)
    {
        IReadOnlyList<string> row =
        [
            ReportText.Number(statistics.SchemaCount),
            ReportText.Number(statistics.TableCount),
            ReportText.Number(statistics.ColumnCount),
            ReportText.Number(statistics.ViewCount),
            ReportText.Number(statistics.MaterializedViewCount),
            ReportText.Number(statistics.RoutineCount),
            ReportText.Number(statistics.SequenceCount),
            ReportText.Number(statistics.ExtensionCount),
            ReportText.Number(statistics.ForeignKeyCount),
            ReportText.Number(statistics.ViewReferenceCount),
            ReportText.Number(statistics.RoleCount),
            ReportText.Number(statistics.GrantCount),
            ReportText.Number(statistics.PolicyCount),
        ];

        return "## Global statistics\n\n" + ReportText.Table(
            ["schemas", "tables", "columns", "views", "matviews", "routines", "sequences", "extensions",
             "foreign keys", "view references", "roles", "grants", "policies"],
            [row]);
    }

    /// <summary>
    /// Renders the three byte measures of FR3 as the distinct columns they are, never one conflated
    /// figure, and adds the two prose statements that make the numbers actionable: how much of the data
    /// sits in the largest tables, and which tables were written in source order (FR4).
    /// </summary>
    private static string RenderDataTotals(CatalogStatistics statistics)
    {
        if (!statistics.HasData)
        {
            // FR10: a schema-only store has no row figures at all. Printing zeros would state that the
            // source tables were empty, which is a different fact and one this store never measured.
            return "## Data totals\nno data captured — this store wrote no table data\n\n" +
                   ReportText.Table(["metadata bytes", "catalog bytes"], [[
                       ReportText.Bytes(statistics.MetadataBytes),
                       ReportText.Bytes(statistics.CatalogBytes),
                   ]]);
        }

        var largest = statistics.LargestTable is { } table
            ? $"{ReportText.Cell(table.QualifiedName)} ({ReportText.Bytes(table.DataBytes)})"
            : ReportText.None;

        IReadOnlyList<string> row =
        [
            ReportText.Number(statistics.Rows),
            ReportText.Number(statistics.ChunkFiles),
            ReportText.Bytes(statistics.RowBytes),
            ReportText.Number(statistics.BlobFiles),
            ReportText.Bytes(statistics.BlobBytes),
            ReportText.Bytes(statistics.DataBytes),
            ReportText.Bytes(statistics.MetadataBytes),
            ReportText.Bytes(statistics.CatalogBytes),
            ReportText.Number(statistics.EmptyTableCount),
            largest,
        ];

        return "## Data totals\n\n" +
               ReportText.Table(
                   ["rows", "chunk files", "row bytes", "blob files", "blob bytes", "data bytes",
                    "metadata bytes", "catalog bytes", "empty tables", "largest table"],
                   [row]) +
               "\n" + DataProse(statistics);
    }

    /// <summary>
    /// The two prose facts under the data row. Both are stated with their raw counts beside the
    /// percentage (D9.1), so a rounded figure never stands in for a measured one.
    /// </summary>
    private static string DataProse(CatalogStatistics statistics)
    {
        var share = statistics.DataBytes > 0
            ? $"Top {ReportBudget.TopTablesForShare} tables hold " +
              $"{ReportText.Percent(statistics.TopTablesDataBytes, statistics.DataBytes)} % of the data bytes " +
              $"({ReportText.Bytes(statistics.TopTablesDataBytes)} of {ReportText.Bytes(statistics.DataBytes)})"
            : string.Empty;

        var sourceOrder = statistics.SourceOrderTables.Count > 0
            ? $"{ReportText.Number(statistics.SourceOrderTables.Count)} tables stored in source order, so their " +
              "row order is not guaranteed to repeat: " +
              string.Join(", ", statistics.SourceOrderTables.Select(ReportText.Code))
            : string.Empty;

        return ReportText.Facts(share, sourceOrder);
    }

    /// <summary>Renders the per-schema rollup with each schema's share of the catalog artifact (FR3).</summary>
    private static string RenderSchemaStatistics(CatalogStatistics statistics)
    {
        if (statistics.Schemas.Count == 0)
        {
            return "## Statistics per schema\n" + ReportText.None;
        }

        var rows = statistics.Schemas
            .Select(schema => (IReadOnlyList<string>)[
                ReportText.Cell(schema.Name),
                ReportText.Number(schema.Tables),
                ReportText.Number(schema.Rows),
                ReportText.Bytes(schema.DataBytes),
                ReportText.Percent(schema.DataBytes, statistics.CatalogBytes),
            ])
            .ToList();

        return "## Statistics per schema\n\n" +
               ReportText.Table(["schema", "tables", "rows", "data bytes", "% of catalog bytes"], rows);
    }

    /// <summary>
    /// Renders one row per captured table (FR4, FR10). This listing is never capped: a table named in no
    /// line would be a table the report lost, and the catalog holds it.
    /// </summary>
    private static string RenderTableStatistics(CatalogStatistics statistics)
    {
        if (statistics.Tables.Count == 0)
        {
            return "## Statistics per table\n" + ReportText.None;
        }

        var rows = statistics.Tables
            .Select(table => (IReadOnlyList<string>)[
                ReportText.Cell(table.QualifiedName),
                ReportText.Number(table.Columns),
                ReportText.Number(table.Rows),
                ReportText.Number(table.Chunks),
                ReportText.Bytes(table.RowBytes),
                ReportText.Number(table.BlobFiles),
                ReportText.Bytes(table.BlobBytes),
                table.HasPrimaryKey ? "yes" : "no",
                ReportText.Number(table.ForeignKeysOut),
                ReportText.Number(table.ForeignKeysIn),
                ReportText.Cell(table.RowOrder),
            ])
            .ToList();

        return "## Statistics per table\n\n" +
               ReportText.Table(
                   ["table", "columns", "rows", "chunks", "row bytes", "blobs", "blob bytes", "PK",
                    "FK out", "FK in", "row order"],
                   rows);
    }

    /// <summary>
    /// Renders the size of every metadata file in the three groups FR5 names, each group with its own
    /// total. The two catalog root files cannot appear in <c>manifest.json</c>, so they are reported as
    /// one combined row — the only figure the writer is able to keep for them (D3).
    /// </summary>
    private static string RenderMetadataFiles(CatalogStatistics statistics)
    {
        var groups = statistics.MetadataFiles
            .Where(group => group.Files.Count > 0)
            .Select(RenderMetadataGroup)
            .ToList();

        if (groups.Count == 0)
        {
            return "## Metadata files\n" + ReportText.None;
        }

        var sections = new List<string>(groups.Count + 1) { "## Metadata files" };
        sections.AddRange(groups);

        return string.Join("\n\n", sections);
    }

    private static string RenderMetadataGroup(SectionStat group)
    {
        var rows = group.Files
            .Select(file => (IReadOnlyList<string>)[ReportText.Cell(file.Path), ReportText.Bytes(file.Bytes)])
            .ToList();

        rows.Add([ReportText.Cell("total"), ReportText.Bytes(group.TotalBytes)]);

        return ReportText.Table([ReportText.Cell(group.Label), "bytes"], rows);
    }

    /// <summary>
    /// Renders what the SQL script pass wrote (FR7, D9), or nothing at all when the option was off.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The section describes files rather than measuring them, so it sits after <c>## Metadata files</c> and
    /// before the relationships. It is present whenever the pass ran — a refusal is one sentence naming the
    /// reason, because a reader who asked for scripts and got none deserves that reason where they look — and
    /// absent when nothing was asked for, where a section saying "nothing" would only raise a question.
    /// </para>
    /// <para>
    /// One row per file the pass wrote, and the profile, the budget and the largest table the plan reasoned
    /// with: the files are what happened, the plan facts are why (FR12, D10). The single-file catalog keeps
    /// the two-row table it always had.
    /// </para>
    /// <para>
    /// The last line is FR9's: the scripts describe the catalog rather than belong to it, so the byte measures
    /// above are the folder's size with them and without them (D10). A reader comparing the two would
    /// otherwise conclude that one of them is wrong.
    /// </para>
    /// </remarks>
    private static string RenderSqlScripts(ScriptResult? scripts)
    {
        if (scripts is null)
        {
            return string.Empty;
        }

        if (scripts.Refusal is { } refusal)
        {
            return "## SQL scripts\n" + ReportText.EscapeCell(refusal.Reason);
        }

        if (scripts.Cancelled)
        {
            return "## SQL scripts\nno script was written — the pass was cancelled";
        }

        var rows = new List<IReadOnlyList<string>>(2);

        if (scripts.SchemaPath is { } schemaPath)
        {
            rows.Add([
                ReportText.Cell(schemaPath),
                ReportText.Bytes(scripts.SchemaBytes),
                ReportText.Number(scripts.SchemaStatements),
                ReportText.NotStated,
            ]);
        }

        foreach (var dataFile in scripts.DataFiles)
        {
            rows.Add([
                ReportText.Cell(dataFile.RelativePath),
                ReportText.Bytes(dataFile.Bytes),
                ReportText.Number(dataFile.Statements),
                ReportText.Number(dataFile.Rows),
            ]);
        }

        if (rows.Count == 0)
        {
            return "## SQL scripts\nno script was written";
        }

        // The tail — key validation and the materialized-view refresh — belongs to the last file written, so
        // the sentence has to name that file rather than assume how many there are (D2, FR9).
        var last = ReportText.Code(
            scripts.DataFiles.Count > 0
                ? scripts.DataFiles[^1].RelativePath
                : scripts.SchemaPath ?? string.Empty);

        return string.Join('\n', new[]
        {
            "## SQL scripts",
            string.Empty,
            ReportText.Table(["file", "bytes", "statements", "rows"], rows),
            string.Empty,
            "Every statement in these files may be run again: the second run creates no object and adds no row.",
            $"Foreign keys are added unvalidated and validated at the end of {last}.",
            DescribeProfile(scripts),
            DescribeLoad(scripts),
            DescribePayloads(scripts),
            "A table that already exists is left as it is — no column, key or check is reconciled.",
            "A sequence keeps the position its own declaration gives it — a catalog records no current value.",
            "No role, grant or policy is written. Not a substitute for Restore, which also verifies the catalog.",
            "These files are not part of the catalog bytes above: they describe the catalog, they do not belong to it.",
        }.Where(line => line.Length > 0));
    }

    /// <summary>
    /// States the profile the plan chose and the number it reasoned with, which is what makes the layout
    /// explainable rather than arbitrary (FR12, D10).
    /// </summary>
    private static string DescribeProfile(ScriptResult scripts)
    {
        var budget = ReportText.HumanSize(scripts.BudgetBytes);
        var largest = scripts.LargestTable is { } table
            ? $"{ReportText.Code(table.ToString())} ({ReportText.HumanSize(scripts.LargestTableBytes)})"
            : "which the catalog records";

        return scripts.Profile switch
        {
            EScriptProfile.NoRows => scripts.DataFiles.Count == 0
                ? "The catalog holds no rows, so no data file was written."
                : "The catalog holds no rows the data files could write.",
            EScriptProfile.Fits => scripts.BudgetBytes > 0
                ? $"The whole catalog fits one file: its largest table, {largest}, is within the {budget} per-file budget."
                : "The per-file budget is 0, so the rows are written in one file.",
            EScriptProfile.ManySmall =>
                $"The largest table, {largest}, is within the {budget} per-file budget and the catalog does not "
                + $"fit one file, so its tables are packed into {ReportText.Number(scripts.DataFiles.Count)} files.",
            _ => DescribeGiant(scripts, largest, budget),
        };
    }

    /// <summary>States how many parts the largest table's block was actually written in (D10).</summary>
    private static string DescribeGiant(ScriptResult scripts, string largest, string budget)
    {
        var name = scripts.LargestTable?.ToString();
        var parts = name is null
            ? 0
            : scripts.DataFiles.Count(file => string.Equals(file.IsPartOf, name, StringComparison.Ordinal));

        return parts > 1
            ? $"The largest table, {largest}, is larger than the {budget} per-file budget, so it is written in "
              + $"{ReportText.Number(parts)} parts."
            : $"The largest table, {largest}, is larger than the {budget} per-file budget, so its block is cut "
              + "wherever the file no longer holds the next statement.";
    }

    /// <summary>
    /// Names the one command that loads the catalog, built from the files that exist rather than from the plan
    /// (FR10, FR12).
    /// </summary>
    private static string DescribeLoad(ScriptResult scripts)
    {
        if (scripts.LoadPath is { } driver)
        {
            return $"Load it with {ReportText.Code("psql -f " + driver)}, which includes the files in the order they run.";
        }

        if (scripts.DataFiles.Count == 1 && scripts.SchemaPath is { } schema)
        {
            return $"Load it with {ReportText.Code("psql -f " + schema)}, then "
                + $"{ReportText.Code("psql -f " + scripts.DataFiles[0].RelativePath)}.";
        }

        return scripts.SchemaPath is { } path
            ? $"Load it with {ReportText.Code("psql -f " + path)}."
            : string.Empty;
    }

    /// <summary>States why the files can be larger than the data they describe, when they carry payloads (D5).</summary>
    private static string DescribePayloads(ScriptResult scripts)
    {
        if (scripts.PayloadBytes <= 0)
        {
            return string.Empty;
        }

        var bytes = ReportText.Number(scripts.PayloadBytes);

        return scripts.DataFiles.Count == 1
            ? $"{ReportText.Code(scripts.DataFiles[0].RelativePath)} inlines {bytes} bytes of binary payloads as "
              + "base64, which is why it can be larger than the data it describes."
            : $"The data files inline {bytes} bytes of binary payloads as base64, which is why they can be "
              + "larger than the data they describe.";
    }
}
