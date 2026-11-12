using Microsoft.Web.WebView2.Core;

namespace DbClone.UI.Services.Relationship;

/// <summary>
/// Asks the WebView2 loader which runtime version is installed (D4). A runtime that is absent, blocked or
/// half-installed answers "no" instead of throwing: the view then uses the browser presenter, which needs
/// nothing installed.
/// </summary>
internal sealed class WebViewRuntimeProbe : IWebViewRuntimeProbe
{
    /// <inheritdoc />
    public bool IsAvailable() => IsVersionUsable(InstalledVersion());

    /// <summary>
    /// Reads the installed runtime's version. Never throws — the loader throws when no runtime is
    /// registered, and that is the answer this probe is asking for.
    /// </summary>
    /// <returns>The version string, or <see langword="null"/> when no runtime answers.</returns>
    internal static string? InstalledVersion()
    {
        try
        {
            return CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch (Exception)
        {
            // WebView2RuntimeNotFoundException when none is installed, and DllNotFoundException or a
            // load failure when the loader itself cannot start. Both mean the same thing here.
            return null;
        }
    }

    /// <summary>Decides whether a reported version is a runtime this app can host a document in.</summary>
    /// <param name="version">What the loader reported, if anything.</param>
    /// <returns><see langword="true"/> only for a stated version.</returns>
    internal static bool IsVersionUsable(string? version) => !string.IsNullOrWhiteSpace(version);
}
