using System.Text;

using DbClone.UI.Services.GraphHtml;

namespace DbClone.UI.Services.Relationship;

/// <summary>
/// The page the viewer holds before the graph exists, and the one it holds when the graph could not be
/// had. It borrows the document's own stylesheet, so the window does not change its look between the
/// progress page and the picture that replaces it.
/// </summary>
/// <remarks>
/// The page is self-contained — no script, no asset, no address — because it is served from memory to
/// the same viewer the document is, and it must paint before anything else has been read.
/// </remarks>
internal static class RelationshipNoticePage
{
    /// <summary>Writes the notice as one whole document.</summary>
    /// <param name="notice">The origin, the sentence and the reason or the promise beside it.</param>
    /// <returns>The page, as text.</returns>
    public static string Of(RelationshipNotice notice)
    {
        ArgumentNullException.ThrowIfNull(notice);

        var html = new StringBuilder(8 * 1024);
        html.Append("<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n");
        html.Append("<meta charset=\"utf-8\">\n");
        html.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        html.Append("<title>Relationship view \u2014 ").Append(GraphHtmlText.Escape(notice.Origin)).Append("</title>\n");
        html.Append("<style>\n").Append(CatalogGraphStyles.Css).Append('\n').Append(NoticeCss).Append("</style>\n");
        html.Append("</head>\n<body>\n");

        html.Append("<header><h1>").Append(GraphHtmlText.Escape(notice.Origin)).Append("</h1></header>\n");
        html.Append("<main class=\"notice\"><div class=\"notice-card\">\n");
        html.Append("<p class=\"notice-headline\">").Append(GraphHtmlText.Escape(notice.Headline)).Append("</p>\n");
        html.Append("<p class=\"notice-detail\">").Append(GraphHtmlText.Escape(notice.Detail)).Append("</p>\n");

        // The bar moves while the work is under way and disappears when the work has failed: a still bar
        // beside a failure would say the request is still running.
        if (!notice.IsFailure)
        {
            html.Append("<div class=\"notice-bar\" role=\"progressbar\" aria-label=\"Arranging the picture\"><span></span></div>\n");
        }

        html.Append("</div></main>\n");
        html.Append("</body>\n</html>\n");

        return html.ToString();
    }

    /// <summary>
    /// The three rules the notice needs beyond the document's sheet: a centred column, the two sentences
    /// and the bar. Every colour and font comes from the sheet the picture uses.
    /// </summary>
    private const string NoticeCss =
        """
        main.notice{flex:1 1 auto;display:flex;align-items:center;justify-content:center;padding:24px}
        .notice-card{max-width:560px;text-align:center}
        .notice-headline{margin:0;font-size:15px;font-weight:600}
        .notice-detail{margin:8px 0 0;color:var(--muted);font-size:12px;word-break:break-word}
        .notice-bar{width:260px;height:3px;margin:22px auto 0;border-radius:999px;background:#e3e8ee;overflow:hidden}
        .notice-bar span{display:block;width:40%;height:100%;border-radius:999px;background:var(--hot);animation:notice-slide 1.15s ease-in-out infinite}
        @keyframes notice-slide{0%{transform:translateX(-100%)}100%{transform:translateX(350%)}}
        @media (prefers-reduced-motion: reduce){.notice-bar span{animation:none;width:100%;opacity:.45}}
        """;
}
