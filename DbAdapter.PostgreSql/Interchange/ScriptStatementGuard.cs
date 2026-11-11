using System.Text;

namespace DbAdapter.PostgreSql.Interchange;

/// <summary>
/// Turns one statement the restore executes into the statements a script writes in its place, guarded so
/// the file may be run against a database that already holds what the same file created (D3).
/// </summary>
/// <remarks>
/// <para>
/// The rule is the plan's: use the command's own keyword where one exists, otherwise wrap or drop the
/// object the statement is about, and never drop anything that holds data. So a schema, a sequence, a
/// table, a materialized view and an index take <c>IF NOT EXISTS</c>; a view takes <c>OR REPLACE</c>; a
/// type, which has no keyword, is wrapped in a block that treats a duplicate as done; a foreign key is
/// dropped and re-added <c>NOT VALID</c>; and a trigger is dropped before it is created again.
/// </para>
/// <para>
/// The input is the text the writer already produces, so this is the one place the script's text and the
/// restore's text could drift apart — which is why a statement whose class is not in the table above is
/// refused rather than written unchanged: an unguarded statement in the file would report a second run
/// as applied while it failed.
/// </para>
/// </remarks>
internal static class ScriptStatementGuard
{
    /// <summary>
    /// The tag the type wrapper's block is written in. A dollar tag rather than <c>$$</c>, so a function
    /// body's own quotes cannot close the block early.
    /// </summary>
    private const string BlockTag = "$dbclone$";

    /// <summary>
    /// Guards one statement the restore would execute.
    /// </summary>
    /// <param name="statement">The statement, as the restore builds it.</param>
    /// <returns>
    /// The statement(s) that take its place in the file, in the order they must run; more than one when the
    /// object has no <c>IF NOT EXISTS</c> of its own.
    /// </returns>
    /// <exception cref="NotSupportedException">
    /// The statement belongs to no class this guard knows, so the file cannot promise a second run works.
    /// </exception>
    internal static IReadOnlyList<string> Guard(string statement)
    {
        ArgumentNullException.ThrowIfNull(statement);

        var sql = Normalize(statement);

        if (StartsWith(sql, "CREATE SCHEMA"))
        {
            return [Guarded(sql, "CREATE SCHEMA", "IF NOT EXISTS")];
        }

        if (StartsWith(sql, "CREATE SEQUENCE"))
        {
            return [Guarded(sql, "CREATE SEQUENCE", "IF NOT EXISTS")];
        }

        // A partition is created by the same verb — "CREATE TABLE child PARTITION OF parent" — and the
        // keyword stands in the same place, so one rule covers a table and a partition.
        if (StartsWith(sql, "CREATE TABLE"))
        {
            return [Guarded(sql, "CREATE TABLE", "IF NOT EXISTS")];
        }

        if (StartsWith(sql, "CREATE MATERIALIZED VIEW"))
        {
            return [Guarded(sql, "CREATE MATERIALIZED VIEW", "IF NOT EXISTS")];
        }

        // A view has no IF NOT EXISTS, and OR REPLACE is the keyword that makes a second run succeed
        // for every shape whose column list is unchanged (D3.2). It belongs between CREATE and VIEW, not
        // after the verb, which is why this one case cannot use the single insertion point below.
        if (StartsWith(sql, "CREATE VIEW"))
        {
            return [Replaceable(sql)];
        }

        if (StartsWith(sql, "CREATE UNIQUE INDEX"))
        {
            return [Guarded(sql, "CREATE UNIQUE INDEX", "IF NOT EXISTS")];
        }

        if (StartsWith(sql, "CREATE INDEX"))
        {
            return [Guarded(sql, "CREATE INDEX", "IF NOT EXISTS")];
        }

        if (StartsWith(sql, "CREATE TRIGGER") || StartsWith(sql, "CREATE CONSTRAINT TRIGGER"))
        {
            return GuardTrigger(sql);
        }

        if (StartsWith(sql, "ALTER TABLE"))
        {
            return GuardAlterTable(sql);
        }

        if (StartsWith(sql, "CREATE TYPE") || StartsWith(sql, "CREATE DOMAIN"))
        {
            return [WrapType(sql)];
        }

        // A routine arrives as CREATE OR REPLACE FUNCTION, the engine's own re-runnable form.
        if (StartsWith(sql, "CREATE OR REPLACE"))
        {
            return [sql];
        }

        throw new NotSupportedException(
            $"The statement \"{Head(sql)}\" is not one the script can make re-runnable, so nothing was "
            + "written for it.");
    }

    /// <summary>
    /// Drops the trigger before the captured text creates it: <c>CREATE OR REPLACE TRIGGER</c> exists only
    /// from PostgreSQL 14, and dropping a trigger destroys no data.
    /// </summary>
    private static IReadOnlyList<string> GuardTrigger(string sql)
    {
        var prefix = StartsWith(sql, "CREATE CONSTRAINT TRIGGER")
            ? "CREATE CONSTRAINT TRIGGER"
            : "CREATE TRIGGER";

        var nameStart = SkipSpace(sql, prefix.Length);
        var name = sql[nameStart..ReadNameEnd(sql, nameStart)];

        // The table is the first ON at the top level: everything before it is the timing and the event
        // list, where a column of that name would be quoted, and everything after it — the row
        // orientation, the WHEN condition, the function — carries no ON that names a relation.
        var onIndex = FindWord(sql, "ON", nameStart + name.Length);
        if (onIndex < 0)
        {
            throw new NotSupportedException(
                $"The captured definition of the trigger {name} names no table the script could drop it "
                + "from, so no statement was written for it.");
        }

        var tableStart = SkipSpace(sql, onIndex + 2);
        var table = sql[tableStart..ReadQualifiedNameEnd(sql, tableStart)];

        return [$"DROP TRIGGER IF EXISTS {name} ON {table};", sql];
    }

    /// <summary>
    /// Drops a named constraint before adding it again — there is no <c>IF NOT EXISTS</c> for a constraint
    /// — and adds the new one <c>NOT VALID</c>, so a re-run against a target that already holds rows fails
    /// on nothing until the tail validates it.
    /// </summary>
    /// <remarks>
    /// The restore's stage hands over two kinds of statement: this one, and the <c>DISABLE TRIGGER</c> of a
    /// trigger the source had switched off. The second changes nothing a re-run has to guard against — it
    /// names the trigger the same file created — so it is passed through as it is.
    /// </remarks>
    private static IReadOnlyList<string> GuardAlterTable(string sql)
    {
        var addIndex = FindWord(sql, "ADD", 0);
        var constraintIndex = addIndex < 0 ? -1 : FindWord(sql, "CONSTRAINT", addIndex + 3);

        if (constraintIndex < 0)
        {
            return [sql];
        }

        var nameStart = SkipSpace(sql, constraintIndex + "CONSTRAINT".Length);
        var name = sql[nameStart..ReadNameEnd(sql, nameStart)];
        var table = sql[..addIndex].TrimEnd();

        return
        [
            $"{table} DROP CONSTRAINT IF EXISTS {name};",
            $"{WithoutTerminator(sql)} NOT VALID;",
        ];
    }

    /// <summary>
    /// Wraps a statement that has no idempotent keyword in a block that treats a duplicate as done.
    /// </summary>
    private static string WrapType(string sql)
    {
        var tag = BlockTagFor(sql);

        return $"DO {tag}\nBEGIN\n    {sql}\nEXCEPTION\n    WHEN duplicate_object OR duplicate_table THEN NULL;\nEND\n{tag};";
    }

    /// <summary>
    /// The tag the wrapper's block is written in: the first candidate the statement's own text does not
    /// contain, so a body carrying the same tag cannot close the block early. The choice depends on the
    /// statement alone, which keeps the file byte-identical from one store to the next (FR11).
    /// </summary>
    private static string BlockTagFor(string sql)
    {
        if (!sql.Contains(BlockTag, StringComparison.Ordinal))
        {
            return BlockTag;
        }

        for (var number = 2; ; number++)
        {
            var candidate = $"$dbclone{number}$";

            if (!sql.Contains(candidate, StringComparison.Ordinal))
            {
                return candidate;
            }
        }
    }

    /// <summary>
    /// Puts every line break the reused text carries into its <c>\n</c> form (FR11).
    /// </summary>
    /// <remarks>
    /// Only the line breaks the statement has as SQL are rewritten. A line break inside a quoted literal or
    /// a dollar-quoted body is part of a value — a check that compares a text with a newline in it, a
    /// function body the source wrote — and rewriting it would change what the script says the database
    /// holds. A quoted name is stepped over for the same reason.
    /// </remarks>
    private static string Normalize(string text)
    {
        if (!text.Contains('\r'))
        {
            return text;
        }

        var normalized = new StringBuilder(text.Length + 16);
        var i = 0;

        while (i < text.Length)
        {
            var c = text[i];

            if (c is '"' or '\'' or '$')
            {
                var end = c == '"' ? ReadNameEnd(text, i) : SkipLiteral(text, i);
                normalized.Append(text, i, end - i);
                i = end;
                continue;
            }

            if (c == '\r')
            {
                normalized.Append('\n');
                i += i + 1 < text.Length && text[i + 1] == '\n' ? 2 : 1;
                continue;
            }

            normalized.Append(c);
            i++;
        }

        return normalized.ToString();
    }

    private static bool StartsWith(string sql, string keyword) =>
        sql.StartsWith(keyword, StringComparison.OrdinalIgnoreCase)
        && (sql.Length == keyword.Length || !IsWordCharacter(sql[keyword.Length]));

    private static string InsertAfter(string sql, string keyword, string insertion) =>
        string.Concat(sql[..keyword.Length], " ", insertion, sql[keyword.Length..]);

    /// <summary>
    /// Puts <paramref name="keyword"/>'s re-runnable form into the statement, unless it is already there.
    /// </summary>
    /// <remarks>
    /// The statements the restore builds are not all alike: <c>CREATE SCHEMA</c> and <c>CREATE OR REPLACE
    /// VIEW</c> already arrive in their re-runnable form from the generator the copy pipeline shares, while
    /// a sequence and a materialized view do not. Adding a keyword a statement already carries would make
    /// the file a syntax error, so the check is part of the guard rather than a promise about its input.
    /// </remarks>
    private static string Guarded(string sql, string keyword, string insertion)
    {
        var rest = sql[keyword.Length..].TrimStart();

        return rest.StartsWith(insertion, StringComparison.OrdinalIgnoreCase)
            ? sql
            : InsertAfter(sql, keyword, insertion);
    }

    /// <summary>
    /// Puts <c>OR REPLACE</c> between <c>CREATE</c> and the verb it belongs to, unless it is already there.
    /// </summary>
    private static string Replaceable(string sql)
    {
        const string Create = "CREATE";
        const string Replace = "OR REPLACE";

        var rest = sql[Create.Length..].TrimStart();

        return rest.StartsWith(Replace, StringComparison.OrdinalIgnoreCase)
            ? sql
            : "CREATE OR REPLACE" + sql[Create.Length..];
    }

    private static string WithoutTerminator(string sql)
    {
        var trimmed = sql.TrimEnd();

        return trimmed.EndsWith(';') ? trimmed[..^1].TrimEnd() : trimmed;
    }

    private static string Head(string sql)
    {
        var head = sql.Replace('\n', ' ').Trim();

        return head.Length <= 80 ? head : string.Concat(head.AsSpan(0, 80), "…");
    }

    private static int SkipSpace(string sql, int from)
    {
        var i = from;
        while (i < sql.Length && char.IsWhiteSpace(sql[i]))
        {
            i++;
        }

        return i;
    }

    /// <summary>
    /// Reads one identifier — quoted or not — and answers the position just after it.
    /// </summary>
    private static int ReadNameEnd(string sql, int start)
    {
        if (start >= sql.Length)
        {
            throw new NotSupportedException(
                "A statement the script has to guard ends where a name is expected, so no statement was "
                + "written for it.");
        }

        if (sql[start] != '"')
        {
            var plain = start;
            while (plain < sql.Length && !char.IsWhiteSpace(sql[plain]) && sql[plain] is not ('(' or ';' or ','))
            {
                plain++;
            }

            return plain;
        }

        var i = start + 1;
        while (i < sql.Length)
        {
            if (sql[i] != '"')
            {
                i++;
                continue;
            }

            // A doubled quote inside a quoted name is one character of that name, not its end.
            if (i + 1 < sql.Length && sql[i + 1] == '"')
            {
                i += 2;
                continue;
            }

            return i + 1;
        }

        throw new NotSupportedException(
            $"A statement the script has to guard holds an unclosed quoted name, so no statement was "
            + $"written for it: {Head(sql)}");
    }

    private static int ReadQualifiedNameEnd(string sql, int start)
    {
        var end = ReadNameEnd(sql, start);

        while (end < sql.Length && sql[end] == '.')
        {
            end = ReadNameEnd(sql, end + 1);
        }

        return end;
    }

    /// <summary>
    /// Finds a word outside every quoted name, literal and parenthesized group, or -1 when it is absent.
    /// </summary>
    private static int FindWord(string sql, string word, int from)
    {
        var depth = 0;
        var i = from;

        while (i < sql.Length)
        {
            var c = sql[i];

            if (c == '"')
            {
                i = ReadNameEnd(sql, i);
                continue;
            }

            if (c is '\'' or '$')
            {
                i = SkipLiteral(sql, i);
                continue;
            }

            if (c == '(')
            {
                depth++;
                i++;
                continue;
            }

            if (c == ')')
            {
                depth = depth > 0 ? depth - 1 : 0;
                i++;
                continue;
            }

            if (depth == 0
                && (i == 0 || !IsWordCharacter(sql[i - 1]))
                && string.Compare(sql, i, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) == 0
                && (i + word.Length >= sql.Length || !IsWordCharacter(sql[i + word.Length])))
            {
                return i;
            }

            i++;
        }

        return -1;
    }

    /// <summary>
    /// Steps over a quoted literal or a dollar-quoted body, whose text the guard must not read as SQL.
    /// </summary>
    private static int SkipLiteral(string sql, int start)
    {
        if (sql[start] == '\'')
        {
            var i = start + 1;
            while (i < sql.Length)
            {
                if (sql[i] != '\'')
                {
                    i++;
                    continue;
                }

                if (i + 1 < sql.Length && sql[i + 1] == '\'')
                {
                    i += 2;
                    continue;
                }

                return i + 1;
            }

            return sql.Length;
        }

        var tagEnd = sql.IndexOf('$', start + 1);
        if (tagEnd < 0 || !IsDollarTag(sql, start + 1, tagEnd))
        {
            // A "$" that opens no body — part of an identifier, say — is one ordinary character, not the
            // start of text the guard must not read.
            return start + 1;
        }

        var tag = sql[start..(tagEnd + 1)];
        var close = sql.IndexOf(tag, tagEnd + 1, StringComparison.Ordinal);

        return close < 0 ? sql.Length : close + tag.Length;
    }

    /// <summary>Says whether the characters between two <c>$</c> are a tag a dollar-quoted body can carry.</summary>
    private static bool IsDollarTag(string sql, int start, int end)
    {
        for (var i = start; i < end; i++)
        {
            if (!IsWordCharacter(sql[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsWordCharacter(char c) => char.IsLetterOrDigit(c) || c == '_';
}
