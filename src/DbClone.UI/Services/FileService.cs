using System.IO;

namespace DbClone.UI.Services;

/// <summary>
/// The real file system behind <see cref="IFileService"/>. Stateless, so the one instance the
/// container registers can be handed to every view model that writes.
/// </summary>
public sealed class FileService : IFileService
{
    /// <summary>Gets the one instance; a view model's own graph may use it instead of a container.</summary>
    public static FileService Instance { get; } = new();

    /// <inheritdoc />
    public bool DirectoryExists(string path) => Directory.Exists(path);

    /// <inheritdoc />
    public void WriteAllText(string path, string contents) => File.WriteAllText(path, contents);
}
