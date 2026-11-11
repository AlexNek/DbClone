namespace DbClone.UI.Services;

/// <summary>
/// Hands a written file to whatever the user's system uses for it, so no view model names a browser,
/// a shell or an executable (D6).
/// </summary>
public interface IHtmlFileOpener
{
    /// <summary>Opens a file that is already on disk.</summary>
    /// <param name="path">The absolute path of the written file.</param>
    void Open(string path);
}
