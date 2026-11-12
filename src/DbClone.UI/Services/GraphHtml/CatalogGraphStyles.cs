namespace DbClone.UI.Services.GraphHtml;

/// <summary>
/// The document's stylesheet. One rule per line, no external asset, no CDN: the file opens with the
/// network disconnected.
/// </summary>
internal static class CatalogGraphStyles
{
    internal const string Css =
"""
:root{--bg:#fbfbfd;--ink:#1b1f24;--muted:#6a737d;--line:#c9d1d9;--hot:#0b64c4;--warm:#8a5a00}
*{box-sizing:border-box}
html,body{height:100%}
body{margin:0;font:13px/1.45 "Segoe UI",system-ui,-apple-system,sans-serif;color:var(--ink);background:var(--bg);display:flex;flex-direction:column}
header{flex:0 0 auto;padding:14px 18px 8px;border-bottom:1px solid var(--line)}
h1{margin:0;font-size:17px;font-weight:600}
.sub{margin:4px 0 0;color:var(--muted);font-size:12px}
.sub b{color:var(--ink);font-weight:600}
#counts{margin:6px 0 0;font-weight:600}
.keyed{margin:6px 0 0}
.keyed>summary{width:max-content;padding:1px 0;cursor:pointer;color:var(--muted);font-size:12px;font-weight:600}
.keyed>summary:hover{color:var(--ink)}
.keyed>summary:focus-visible{outline:2px solid var(--hot);outline-offset:2px;border-radius:3px}
.keyed .legend{margin-top:6px}
.legend{display:flex;flex-wrap:wrap;gap:4px 20px;margin:8px 0 0;padding:0;list-style:none;color:var(--muted);font-size:12px}
.legend b{color:var(--ink)}
.legend li{display:flex;gap:7px;align-items:flex-start;max-width:430px}
.marks{position:absolute}
.legend .key{flex:0 0 auto;width:48px;height:16px;margin-top:1px;overflow:visible}
.legend .key rect{fill:#fff;stroke:#8b949e;stroke-width:1.2}
.legend .key rect.super{fill:#eef4fc;stroke:#5b8dd6}
.chips{flex:0 0 auto;display:flex;flex-wrap:wrap;gap:8px 20px;align-items:center;padding:8px 18px;border-bottom:1px solid var(--line)}
.chips .group{display:flex;flex-wrap:wrap;gap:5px;align-items:center}
.chips .group>span{color:var(--muted);font-size:11px;text-transform:uppercase;letter-spacing:.05em;margin-right:2px}
.chips .group>b{font-size:12px;font-weight:600}
.chips .hint{margin-left:auto;color:var(--muted);font-size:11px}
.chip{font:inherit;font-size:12px;padding:2px 9px;border:1px solid var(--line);border-radius:999px;background:#fff;color:var(--ink);cursor:pointer}
.chip:not(.on){opacity:.5;text-decoration:line-through}
.chip.on{background:#e8f0fb;border-color:#9dc0ee}
.layout{flex:1 1 auto;min-height:0;display:flex;align-items:stretch}
main{flex:1 1 auto;min-width:0;min-height:0;display:flex;flex-direction:column}
.toolbar{display:flex;flex-wrap:wrap;gap:6px 12px;align-items:center;padding:8px 18px;border-bottom:1px solid var(--line);font-size:12px}
.toolbar label{display:flex;gap:6px;align-items:center}
.toolbar input[type=search]{font:inherit;padding:3px 8px;border:1px solid var(--line);border-radius:6px;min-width:190px}
.toolbar fieldset{border:0;margin:0;padding:0;display:flex;gap:8px;align-items:center}
.toolbar legend{display:none}
.toolbar button{font:inherit;font-size:12px;padding:3px 10px;border:1px solid var(--line);border-radius:6px;background:#fff;cursor:pointer}
#matches{border:1px solid var(--line);border-radius:8px;background:#fff;margin:0 18px;padding:6px 8px;max-height:190px;overflow:auto;font-size:12px}
#matches[hidden]{display:none}
#matches .hits-title{margin:0 0 4px;color:var(--muted);font-size:11px;text-transform:uppercase;letter-spacing:.05em}
.hit{display:flex;gap:10px;align-items:baseline;width:100%;text-align:left;font:inherit;padding:3px 6px;border:0;border-radius:5px;background:none;cursor:pointer}
.hit:hover,.hit:focus-visible{background:#eef4fc;outline:none}
.hit span{color:var(--muted)}
#pic{position:absolute;top:0;left:0;cursor:grab;touch-action:none}
#pic.dragging{cursor:grabbing}
.stage{position:relative;flex:1 1 auto;overflow:hidden;min-height:240px;background:#fff}
#viewport text{font:12px/1 "Segoe UI",system-ui,sans-serif;fill:var(--ink)}
.node rect{fill:#fff;stroke:#8b949e;stroke-width:1.2}
.node.out rect{fill:#fffdf5;stroke-dasharray:4 3}
.node.hot rect{stroke:var(--hot);stroke-width:2.4;fill:#eaf3ff}
.node .name{font-weight:600}
.node .count{fill:var(--muted);font-size:11px}
.edge{fill:none;stroke:#8b949e;stroke-width:1.3}
.edge.out{stroke-dasharray:5 4;stroke:#b08850}
.edge.hot{stroke:var(--hot);stroke-width:2.6}
.edge.dim{opacity:.18}
.edge.pick{stroke:transparent;stroke-width:10px;stroke-dasharray:none;pointer-events:stroke;cursor:pointer}
.node.dim{opacity:.4}
.elabel{font-size:10px;fill:var(--muted);paint-order:stroke;stroke:#fff;stroke-width:3px;text-anchor:middle;dominant-baseline:central;pointer-events:none}
.elabel.quiet{display:none}
path.edge:not(.off).hot+text.elabel,path.edge.pick:not(.off):hover+text.elabel{display:inline;fill:var(--hot);font-weight:600}
.super rect{fill:#eef4fc;stroke:#5b8dd6;stroke-width:1.6;rx:8}
.super text{font-size:13px;font-weight:600;fill:#1a4f96;text-anchor:middle}
.super{cursor:pointer}
.node.off,.edge.off,.super.off,.away,.hidden{display:none}
aside{flex:0 0 330px;border-left:1px solid var(--line);padding:12px 16px;overflow:auto;font-size:12px}
aside h2{margin:0 0 2px;font-size:14px;word-break:break-word}
aside h3{margin:14px 0 4px;font-size:12px;text-transform:uppercase;letter-spacing:.05em;color:var(--muted)}
aside .meta{margin:2px 0;color:var(--muted)}
aside .dirs{display:flex;gap:14px;margin:6px 0 0;font-variant-numeric:tabular-nums}
aside table{border-collapse:collapse;width:100%;margin-top:6px}
aside td,aside th{border-bottom:1px solid #eaeef2;padding:2px 4px 2px 0;text-align:left;vertical-align:top}
aside th{font-size:10px;text-transform:uppercase;letter-spacing:.04em;color:var(--muted);font-weight:600}
aside .mono{font-family:Consolas,"Cascadia Mono",monospace;font-size:11px}
aside .note{color:var(--muted);font-size:11px}
aside ul.rels{margin:0;padding:0;list-style:none}
aside ul.rels li+li{margin-top:2px}
aside ul.rels button{font:inherit;font-size:11px;text-align:left;width:100%;padding:3px 4px;border:0;border-radius:4px;background:none;cursor:pointer;color:var(--ink)}
aside ul.rels button:hover{background:#eef4fc}
aside ul.rels button b,aside ul.rels button span{display:block}
aside ul.rels button b{font-weight:600}
aside ul.defs{margin:0;padding:0;list-style:none}
aside ul.defs li{padding:3px 0;border-bottom:1px solid #eaeef2}
aside ul.defs li:last-child{border-bottom:0}
aside ul.defs b{font-weight:600;margin-right:6px}
aside ul.defs .note{margin-right:6px}
aside ul.defs div{margin-top:2px;white-space:pre-wrap;word-break:break-word;color:var(--ink)}
.register{flex:0 1 auto;min-height:0;max-height:24vh;overflow:auto;border-top:1px solid var(--line);padding:10px 18px 18px}
.register h2{margin:0 0 6px;font-size:14px}
.register .scroll{overflow:auto;border:1px solid var(--line);border-radius:8px;background:#fff}
.register table{border-collapse:collapse;width:100%;font-size:11.5px}
.register th,.register td{text-align:left;padding:3px 8px;border-bottom:1px solid #eaeef2;white-space:nowrap}
.register th{position:sticky;top:0;background:#f6f8fa;font-size:10px;text-transform:uppercase;letter-spacing:.04em;color:var(--muted)}
.register tbody tr{cursor:pointer}
.register tbody tr:hover,.register tbody tr.hot{background:#eef4fc}
.register .mono{font-family:Consolas,"Cascadia Mono",monospace}
""";
}
