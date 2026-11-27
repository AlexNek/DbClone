using CanonicalModel.Interchange;

namespace DatabaseInterchange.Script;

/// <summary>
/// The order the table blocks of <c>data.sql</c> are written in: a table the rows reference comes before the
/// rows that reference it (D6).
/// </summary>
/// <remarks>
/// <para>
/// A foreign key is enforced on every insert the moment the constraint exists — the file declares it
/// <c>NOT VALID</c>, which skips the check of the rows already there and never the rows added afterwards — so
/// a block whose parent has not been loaded yet fails on the server. The order therefore comes from the
/// catalog's own keys rather than from the order the tables happen to sit in, which is the alphabetical order
/// the sections are written in.
/// </para>
/// <para>
/// Among the tables that are free to go next, the one the catalog recorded first goes first, so a catalog
/// whose keys join nothing keeps the order it was written in. A cycle has no loadable order at all — each
/// block is its own transaction, so the first block of a cycle needs rows a later block has not committed —
/// and its members keep the catalog's order too, because the file names them in the note above the blocks (D6).
/// </para>
/// <para>
/// An edge leaves the catalog (a reference to a table this store did not capture) or returns to its own table
/// (a self reference), and neither makes one block wait for another, so both are left out of the ordering.
/// </para>
/// </remarks>
internal static class TableLoadOrder
{
    /// <summary>
    /// Orders the catalog's tables so every table a row references is written before the rows that reference
    /// it.
    /// </summary>
    /// <param name="tables">The catalog's tables, in the order the catalog recorded them.</param>
    /// <returns>The same tables, in the order their rows may be inserted.</returns>
    internal static IReadOnlyList<TableDefinition> Order(IReadOnlyList<TableDefinition> tables)
    {
        ArgumentNullException.ThrowIfNull(tables);

        if (tables.Count < 2)
        {
            return tables;
        }

        var captured = new Dictionary<ObjectRef, TableDefinition>(tables.Count);
        var position = new Dictionary<ObjectRef, int>(tables.Count);

        for (var index = 0; index < tables.Count; index++)
        {
            captured[tables[index].Ref] = tables[index];
            position[tables[index].Ref] = index;
        }

        var waiting = new Dictionary<ObjectRef, List<ObjectRef>>();
        var outstanding = new Dictionary<ObjectRef, int>(tables.Count);
        var edges = new HashSet<(ObjectRef Child, ObjectRef Parent)>();

        foreach (var table in tables)
        {
            outstanding[table.Ref] = 0;
        }

        foreach (var table in tables)
        {
            foreach (var foreignKey in table.ForeignKeys)
            {
                var parent = foreignKey.ReferencedTableRef;

                if (!captured.ContainsKey(parent)
                    || parent.Equals(table.Ref)
                    || !edges.Add((table.Ref, parent)))
                {
                    // Outside the catalog, the table's own row, or an edge already counted for this pair:
                    // none of them makes this block wait for another one.
                    continue;
                }

                if (!waiting.TryGetValue(parent, out var children))
                {
                    children = [];
                    waiting[parent] = children;
                }

                children.Add(table.Ref);
                outstanding[table.Ref]++;
            }
        }

        var ready = new PriorityQueue<ObjectRef, int>();

        foreach (var table in tables)
        {
            if (outstanding[table.Ref] == 0)
            {
                ready.Enqueue(table.Ref, position[table.Ref]);
            }
        }

        var order = new List<TableDefinition>(tables.Count);
        var placed = new HashSet<ObjectRef>(tables.Count);

        while (ready.TryDequeue(out var next, out _))
        {
            if (!placed.Add(next))
            {
                continue;
            }

            order.Add(captured[next]);

            if (!waiting.TryGetValue(next, out var children))
            {
                continue;
            }

            foreach (var child in children)
            {
                outstanding[child]--;

                if (outstanding[child] == 0 && !placed.Contains(child))
                {
                    ready.Enqueue(child, position[child]);
                }
            }
        }

        // What is left waits on a cycle it is part of or downstream of. No order loads such a table, so the
        // rest keeps the catalog's own order rather than a second guess.
        foreach (var table in tables)
        {
            if (placed.Add(table.Ref))
            {
                order.Add(table);
            }
        }

        return order;
    }
}
