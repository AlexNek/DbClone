namespace DatabaseInterchange.Transform;

/// <summary>
/// Request to extract binary values from a catalog table to individual files.
/// </summary>
/// <param name="CatalogPath">Path to the catalog root directory.</param>
/// <param name="Schema">Schema name (e.g., "public").</param>
/// <param name="Table">Table name (e.g., "images").</param>
/// <param name="OutputDirectory">Directory where extracted files will be written.</param>
public sealed record BlobExtractionRequest(
    string CatalogPath,
    string Schema,
    string Table,
    string OutputDirectory);
