namespace DbClone.Application.Interchange;

/// <summary>
/// One data file a store wrote beside its catalog, as the application reports it: the absolute path, its
/// size, what it holds and, when it is one part of a split table, which table it belongs to.
/// </summary>
/// <remarks>
/// The product names its files relative to the catalog root, because it is the one that knows the layout;
/// this record carries the absolute path the application formed from that name, so no consumer above this
/// layer needs to know a folder or a file name.
/// </remarks>
/// <param name="Path">The file's absolute path.</param>
/// <param name="Bytes">The file's own length in bytes.</param>
/// <param name="Statements">How many executable statements the file holds.</param>
/// <param name="Rows">How many rows those statements insert.</param>
/// <param name="IsPartOf">
/// The qualified name of the one table the file's blocks belong to when that table was written in parts,
/// or <see langword="null"/> when the file packs whole tables.
/// </param>
public sealed record SqlScriptFile(
    string Path,
    long Bytes,
    int Statements,
    long Rows,
    string? IsPartOf);
