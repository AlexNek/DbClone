using System.Diagnostics;

namespace DbClone.UI.Services;

/// <summary>
/// The shell opener: the one place the relationship view touches the operating system, kept behind
/// <see cref="IHtmlFileOpener"/> so the workflow that writes the file can be tested without opening
/// anything.
/// </summary>
public sealed class WpfHtmlFileOpener : IHtmlFileOpener
{
    /// <inheritdoc />
    public void Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }
}
