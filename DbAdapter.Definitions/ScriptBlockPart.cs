namespace DbAdapter.Definitions;

/// <summary>
/// Which part of a table's block a statement belongs to: the whole table, or one of the parts a table larger
/// than the per-file budget was split into (D4, FR5).
/// </summary>
/// <remarks>
/// The product decides where a block is cut and numbers the parts, because only it knows how full the file has
/// become; this value is what the product tells the adapter so the banner can say which part a reader is
/// looking at. <see cref="Whole"/> is the unsplit case, so a catalog that fits one file carries no part line.
/// </remarks>
public readonly record struct ScriptBlockPart
{
    /// <summary>Initializes a new instance of the <see cref="ScriptBlockPart"/> struct.</summary>
    /// <param name="number">The part's number, counting from one; <c>0</c> for a table that is not split.</param>
    public ScriptBlockPart(int number) => Number = number < 0 ? 0 : number;

    /// <summary>Gets the block of a table that fits one file and is never numbered.</summary>
    public static ScriptBlockPart Whole { get; } = new(0);

    /// <summary>Gets the part number, counting from one; <c>0</c> when the table is not split.</summary>
    public int Number { get; }

    /// <summary>Gets whether this part is one of several the table was split into.</summary>
    public bool IsPart => Number > 0;
}
