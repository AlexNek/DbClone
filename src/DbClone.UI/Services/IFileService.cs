namespace DbClone.UI.Services;

/// <summary>
/// The file system facts a view model is allowed to ask for. A view model decides what to write
/// and when; this is the only thing that touches disk for it, so the rule "no <c>System.IO</c> in a
/// view model" stays true and a test can say what was written without writing it.
/// </summary>
public interface IFileService
{
    /// <summary>Whether the directory exists.</summary>
    bool DirectoryExists(string path);

    /// <summary>Writes the text to the path, replacing the file when it is there.</summary>
    void WriteAllText(string path, string contents);
}
