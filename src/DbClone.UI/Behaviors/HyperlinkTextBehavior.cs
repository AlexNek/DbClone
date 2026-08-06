using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Navigation;

namespace DbClone.UI.Behaviors;

/// <summary>
/// Attached behavior that parses URLs in text and renders them as clickable hyperlinks.
/// </summary>
public static partial class HyperlinkTextBehavior
{
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.RegisterAttached(
            "Text",
            typeof(string),
            typeof(HyperlinkTextBehavior),
            new PropertyMetadata(null, OnTextChanged));

    public static readonly DependencyProperty ForegroundProperty =
        DependencyProperty.RegisterAttached(
            "Foreground",
            typeof(Brush),
            typeof(HyperlinkTextBehavior),
            new PropertyMetadata(null, OnTextChanged));

    public static Brush? GetForeground(DependencyObject obj) =>
        (Brush?)obj.GetValue(ForegroundProperty);

    public static void SetForeground(DependencyObject obj, Brush? value) =>
        obj.SetValue(ForegroundProperty, value);

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock textBlock) return;

        textBlock.Inlines.Clear();

        var text = GetText(textBlock);
        if (string.IsNullOrEmpty(text)) return;

        var brush = GetForeground(textBlock) ?? textBlock.Foreground;
        var urlRegex = UrlRegex();
        var lastIndex = 0;

        foreach (Match match in urlRegex.Matches(text))
        {
            // Add text before the URL
            if (match.Index > lastIndex)
            {
                textBlock.Inlines.Add(new Run(text[lastIndex..match.Index]) { Foreground = brush });
            }

            // Add clickable hyperlink
            var uri = match.Value;
            var hyperlink = new Hyperlink(new Run(uri))
                                {
                                    NavigateUri = new Uri(uri),
                                    Foreground = brush,
                                    ToolTip = "Open in browser"
                                };
            hyperlink.RequestNavigate += OnRequestNavigate;
            textBlock.Inlines.Add(hyperlink);

            lastIndex = match.Index + match.Length;
        }

        // Add remaining text after last URL
        if (lastIndex < text.Length)
        {
            textBlock.Inlines.Add(new Run(text[lastIndex..]) { Foreground = brush });
        }
    }

    public static string? GetText(DependencyObject obj) => (string?)obj.GetValue(TextProperty);

    public static void SetText(DependencyObject obj, string? value) =>
        obj.SetValue(TextProperty, value);

    private static void OnRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(
            new ProcessStartInfo { FileName = e.Uri.AbsoluteUri, UseShellExecute = true });
        e.Handled = true;
    }

    [GeneratedRegex(@"https?://[^\s\)]+", RegexOptions.Compiled)]
    private static partial Regex UrlRegex();
}
