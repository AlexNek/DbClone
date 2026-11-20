namespace DbClone.UI.Services.GraphHtml;

/// <summary>
/// The document's only executable script: pan and zoom, selection, the cluster fold, search and the
/// direction switch, and a card the reader carries by hand. It reads the data island and moves classes;
/// it never computes a count or a rank, and it never converts a string into markup. The coordinates it
/// writes are the ones a reader states with the pointer — a card they move, and the arrows that join it,
/// drawn again from box to box. Every other coordinate is the layout's, in whichever arrangement the
/// engine kept the picture.
/// </summary>
internal static class CatalogGraphScript
{
    internal const string Javascript =
"""
(function () {
  "use strict";

  var data = JSON.parse(document.getElementById("data").textContent);
  var svg = document.getElementById("pic");
  var view = document.getElementById("viewport");
  var panel = document.getElementById("panel");
  var hits = document.getElementById("matches");
  var counts = document.getElementById("counts");
  var nodes = Array.prototype.slice.call(document.querySelectorAll("#viewport .node"));
  var edges = Array.prototype.slice.call(document.querySelectorAll("#viewport .edge"));
  var detailOf = new Map();
  data.details.forEach(function (detail) { detailOf.set(detail.i, detail); });
  // The card row holds the words that name a link table; the panel prints them beside the name, so
  // the detail rows do not repeat a fact the card row already carries (D14.3).
  var cardOf = new Map();
  data.nodes.forEach(function (card) { cardOf.set(card.i, card); });

  // The selection the picture was drawn for: the host leaves the tables of a switched-off cluster
  // out of the picture and out of the layout, so the cards that remain are arranged for the picture
  // the reader is looking at (T28).
  var offChips = new Set(data.off || []);
  var focus = -1;
  var direction = "both";

  function element(tag, className, text) {
    var node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined && text !== null) node.textContent = text;
    return node;
  }

  function rowsPhrase(rows) {
    if (rows === null || rows === undefined) return data.rowsNote;
    return rows + (rows === 1 ? " row" : " rows");
  }

  function caption(field) {
    if (field === "Table") return "table";
    if (field === "Column") return "column";
    if (field === "Constraint") return "constraint";
    if (field === "Comment") return "comment";
    return "schema";
  }

  // What the picture is showing: a card is shown unless a chip switched it off or the picture opened on
  // a neighbourhood it stands outside. A collapsed group's members count as shown — the supernode over
  // them is their card — and an arrow is shown only while both of the tables it joins are.
  function shown(element2) {
    return !element2.classList.contains("off") && !element2.classList.contains("away");
  }

  // The chip axes, read from the island so the script names no kind of its own. A table is claimed by
  // one chip per axis — its schema, the cyclic group it sits in, and the unconnected set — and each
  // axis filters on its own, so switching a schema off hides every one of its tables even when a
  // cyclic-group chip also claims them (T27).
  var chipKind = new Map();
  data.chips.forEach(function (chip) { chipKind.set(chip.i, chip.kind); });

  function claimsOf(element2) {
    return (element2.getAttribute("data-c") || "").split(" ").filter(Boolean).map(Number);
  }

  function axisOn(claims, kind) {
    var ofKind = claims.filter(function (claim) { return chipKind.get(claim) === kind; });
    return ofKind.length === 0 || ofKind.some(function (claim) { return !offChips.has(claim); });
  }

  function updateCounts() {
    var tables = 0;
    var relations = 0;
    nodes.forEach(function (element2) {
      if (element2.classList.contains("cap") && shown(element2)) tables++;
    });
    // Every arrow is written twice — the stroke and the wide transparent band that takes the pointer —
    // so only the painted path is counted; counting both would double every relation in the header.
    edges.forEach(function (element2) {
      if (!element2.classList.contains("pick") && shown(element2)) relations++;
    });
    var filtered = tables !== data.tables || relations !== data.relations;
    counts.textContent = "Showing " + tables + " of " + data.tables + " tables \u00b7 " +
      relations + " of " + data.relations + " relations" + (filtered ? " \u2014 filtered" : "");
  }

  function applyChips() {
    var visible = new Set();
    nodes.forEach(function (element2) {
      var claims = claimsOf(element2);
      var onScreen = axisOn(claims, "Schema")
        && axisOn(claims, "CyclicGroup")
        && axisOn(claims, "Unconnected");
      element2.classList.toggle("off", !onScreen);
      if (onScreen) visible.add(Number(element2.getAttribute("data-n")));
    });
    edges.forEach(function (element2) {
      var edge = data.edges[Number(element2.getAttribute("data-e"))];
      element2.classList.toggle("off", !visible.has(edge.c) || !visible.has(edge.p));
    });
    // A collapsed group is drawn as one box over its members, so the box follows them: a schema the
    // reader switched off leaves no labelled box standing over an empty part of the picture (T27).
    Array.prototype.forEach.call(document.querySelectorAll("#viewport .super"), function (element2) {
      var group = element2.getAttribute("data-g");
      var anyMember = false;
      nodes.forEach(function (card) {
        if (card.getAttribute("data-m") === group && !card.classList.contains("off")) anyMember = true;
      });
      element2.classList.toggle("off", !anyMember);
    });
    updateCounts();
  }

  function applyDirection() {
    edges.forEach(function (element2) {
      var edge = data.edges[Number(element2.getAttribute("data-e"))];
      var shown = true;
      if (focus >= 0 && direction === "out") shown = edge.c === focus;
      if (focus >= 0 && direction === "in") shown = edge.p === focus;
      element2.classList.toggle("dim", focus >= 0 && !shown);
    });
  }

  function clearSelection() {
    focus = -1;
    nodes.forEach(function (element2) { element2.classList.remove("hot"); });
    edges.forEach(function (element2) { element2.classList.remove("hot", "dim"); });
    Array.prototype.forEach.call(document.querySelectorAll(".register tbody tr"), function (row) {
      row.classList.remove("hot");
    });
  }

  function highlightEdge(index) {
    clearSelection();
    edges.forEach(function (element2) {
      if (Number(element2.getAttribute("data-e")) === index) element2.classList.add("hot");
    });
    var edge = data.edges[index];
    panel.textContent = "";
    panel.appendChild(element("h2", null, edge.name || "(unnamed constraint)"));
    panel.appendChild(element("p", "meta", edge.from + " \u2192 " + edge.to));
    panel.appendChild(element("p", null, edge.label));
    if (edge.self) panel.appendChild(element("p", "meta", "the table points at itself"));
    panel.appendChild(element("p", "meta", parentText(edge)));
  }

  // What the constraint says about the child end: how many child rows may name one parent row, and
  // whether the child row may be absent at all. A key of the child that covers exactly the referencing
  // columns is the only proof the catalog carries, so a constraint it cannot prove keeps the fork
  // (D14.1, D14.2).
  function parentText(edge) {
    if (edge.one) {
      return edge.exact
        ? "exactly one child row names each parent"
        : "at most one child row names each parent";
    }
    return edge.mand ? "the child row cannot lose its parent" : "the child row may have no parent";
  }

  // What a constraint does to the row that points through it. Two constraints can join the same
  // columns of the same pair of tables — the catalog names them apart and nothing else does — so the
  // name and the rule are printed on the row, not hidden behind the click that selects it.
  function ruleText(edge) {
    var parts = [];
    if (edge.del) parts.push("on delete " + edge.del.toLowerCase());
    if (edge.upd) parts.push("on update " + edge.upd.toLowerCase());
    parts.push(parentText(edge));
    return parts.join(" \u00b7 ");
  }

  // The label ends in the delete action when the constraint states one, and the row prints that
  // action on its own line already: the columns text stops where the action begins, so no fact is
  // stated twice on one row.
  function columnText(edge) {
    var at = edge.label.indexOf(" on delete: ");
    return at < 0 ? edge.label : edge.label.substring(0, at);
  }

  function edgeList(title, indexes) {
    if (indexes.length === 0) return;
    panel.appendChild(element("h3", null, title + " (" + indexes.length + ")"));
    var list = element("ul", "rels");
    indexes.forEach(function (edgeIndex) {
      var edge = data.edges[edgeIndex];
      var item = element("li");
      var button = element("button", "rel");
      button.setAttribute("data-e", String(edge.i));
      button.appendChild(element("b", "mono", edge.name || "(unnamed constraint)"));
      button.appendChild(element("span", "mono", columnText(edge)));
      button.appendChild(element("span", "note", ruleText(edge)));
      item.appendChild(button);
      list.appendChild(item);
    });
    panel.appendChild(list);
  }

  // The table's own definitions: its key, its unique constraints, its indexes by the columns they
  // cover, and its check constraints by their expressions. The panel states them here so that reading a
  // table never means leaving the document for a JSON file (D10). A table the catalog does not hold has
  // none of this and gets no heading at all.
  function definitionList(detail) {
    var rows = [];
    if (detail.pk) {
      rows.push({ name: detail.pk.name, what: "primary key (" + detail.pk.cols.join(", ") + ")" });
    }
    (detail.uq || []).forEach(function (key) {
      rows.push({ name: key.name, what: "unique (" + key.cols.join(", ") + ")" });
    });
    (detail.idx || []).forEach(function (index) {
      // The columns are the fact a reader wants; the engine's own statement is printed only where the
      // folder states no columns, so a reader who has the key is not asked to read SQL for it and one
      // who has not still finds the only place the catalog wrote it down.
      var known = index.cols && index.cols.length > 0;
      rows.push({
        name: index.name,
        what: (known ? "index (" + index.cols.join(", ") + ")" : "index")
          + (index.unique ? " \u00b7 unique" : ""),
        text: known ? null : index.def,
      });
    });
    (detail.ck || []).forEach(function (check) {
      rows.push({ name: check.name, what: "check", text: check.text });
    });

    if (rows.length === 0) return;
    panel.appendChild(element("h3", null, "Definition (" + rows.length + ")"));
    var list = element("ul", "defs");
    rows.forEach(function (row) {
      var item = element("li", "def");
      item.appendChild(element("b", "mono", row.name));
      item.appendChild(element("span", "note", row.what));
      if (row.text) item.appendChild(element("div", "mono", row.text));
      list.appendChild(item);
    });
    panel.appendChild(list);
  }

  // The table's name, and the words that name it as a link between two others when the analysis
  // inferred one. The words are marked as the inference they are: no engine declares many-to-many,
  // so the picture names the table together with the fact that the name is derived (D14.3).
  function titleOf(detail, card) {
    return card && card.join
      ? detail.name + " \u00b7 " + card.join + " \u2014 inferred"
      : detail.name;
  }

  function renderPanel(index) {
    var detail = detailOf.get(index);
    if (!detail) return;
    panel.textContent = "";
    panel.appendChild(element("h2", null, titleOf(detail, cardOf.get(index))));
    panel.appendChild(element("p", "meta", detail.cap ? rowsPhrase(detail.rows) : data.outsideNote));
    if (detail.g !== null && detail.g !== undefined) {
      panel.appendChild(element("p", "meta",
        "cyclic group " + detail.g + ": " + detail.members.length + " tables, this one points back into it"));
    }
    var dirs = element("p", "dirs");
    dirs.appendChild(element("b", null, "\u2192 references " + detail.outE.length));
    dirs.appendChild(element("b", null, "\u2190 referenced by " + detail.inE.length));
    panel.appendChild(dirs);

    if (detail.cols.length > 0) {
      var table = element("table", "columns");
      var head = element("tr");
      ["column", "type", "null", "pk", "fk", "ref"].forEach(function (label) {
        head.appendChild(element("th", null, label));
      });
      table.appendChild(head);
      detail.cols.forEach(function (column) {
        var row = element("tr");
        var name = element("td", "mono", column.name);
        if (column.comment) name.appendChild(element("div", "note", column.comment));
        row.appendChild(name);
        row.appendChild(element("td", null, column.logicalType));
        row.appendChild(element("td", null, column.nullable ? "null" : "not null"));
        row.appendChild(element("td", null, column.pk ? "pk" : ""));
        row.appendChild(element("td", null, column.fk ? "declares" : ""));
        row.appendChild(element("td", null, column.refd ? "referenced" : ""));
        table.appendChild(row);
      });
      panel.appendChild(table);
    }

    definitionList(detail);

    edgeList("\u2192 References", detail.outE);
    edgeList("\u2190 Referenced by", detail.inE);
  }

  function focusNode(index) {
    clearSelection();
    focus = index;
    nodes.forEach(function (element2) {
      element2.classList.toggle("hot", Number(element2.getAttribute("data-n")) === index);
    });
    edges.forEach(function (element2) {
      var edge = data.edges[Number(element2.getAttribute("data-e"))];
      element2.classList.toggle("hot", edge.c === index || edge.p === index);
    });
    applyDirection();
    renderPanel(index);
  }

  function toggleGroup(number) {
    var members = 0;
    nodes.forEach(function (element2) {
      if (Number(element2.getAttribute("data-m")) === number) {
        element2.classList.toggle("hidden", !element2.classList.contains("hidden"));
        members++;
      }
    });
    if (members === 0) return;
    document.querySelectorAll("#viewport .super").forEach(function (element2) {
      if (Number(element2.getAttribute("data-g")) === number) element2.classList.toggle("hidden");
    });
  }

  // The card a table is drawn with, by the index the island states. The map above is the island's own
  // lookup and this one answers with an element, so the two cannot share a name: a function and a
  // variable of one name leave the map where the reveal calls the function, and the call throws — a
  // search that reports a match and then shows nothing at all.
  function cardElement(index) {
    var found = null;
    nodes.forEach(function (element2) {
      if (Number(element2.getAttribute("data-n")) === index) found = element2;
    });
    return found;
  }

  // Where a card stands now, in canvas coordinates: the rectangle the layout drew, carried by however
  // far the reader has moved it. A card that is not drawn at all has no box, and an arrow to it is
  // left exactly as the layout routed it.
  function cardBox(index) {
    var card = cardElement(index);
    if (!card) return null;
    var rect = card.querySelector("rect");
    if (!rect) return null;
    var far = carried.get(index) || { x: 0, y: 0 };
    var left = Number(rect.getAttribute("x")) + far.x;
    var top = Number(rect.getAttribute("y")) + far.y;
    var width = Number(rect.getAttribute("width"));
    var height = Number(rect.getAttribute("height"));
    return {
      left: left, top: top, right: left + width, bottom: top + height,
      cx: left + width / 2, cy: top + height / 2,
    };
  }

  // The point on a card's border where a straight line to another card leaves it.
  function borderPoint(box, towards) {
    var dx = towards.cx - box.cx;
    var dy = towards.cy - box.cy;
    if (dx === 0 && dy === 0) return { x: box.cx, y: box.cy };
    var halfWidth = (box.right - box.left) / 2;
    var halfHeight = (box.bottom - box.top) / 2;
    var reach = Math.min(
      dx === 0 ? Infinity : halfWidth / Math.abs(dx),
      dy === 0 ? Infinity : halfHeight / Math.abs(dy));
    return { x: box.cx + dx * reach, y: box.cy + dy * reach };
  }

  // A number written into the document: one decimal is finer than the picture is drawn at and keeps
  // the markup short, and it is the same rounding the writers use.
  function drawable(value) {
    return Math.round(value * 10) / 10;
  }

  // A card the reader carries is drawn where they put it, and the arrows that join it are drawn again
  // from box to box: a route that was turned in a corridor of the layout means nothing once the card
  // it was turned for has moved, so the arrow states the same fact the shortest way — this table, that
  // table — and the panel and the register keep saying everything else. No other card, arrow or line
  // of the picture is touched.
  function reflow(index) {
    (edgesOfNode.get(index) || []).forEach(function (edgeIndex) {
      var edge = data.edges[edgeIndex];
      var from = cardBox(edge.c);
      var to = cardBox(edge.p);
      if (!from || !to) return;
      var start = borderPoint(from, to);
      var end = borderPoint(to, from);
      setEdge(edgeIndex,
        "M " + drawable(start.x) + " " + drawable(start.y) + " L " + drawable(end.x) + " " + drawable(end.y),
        (start.x + end.x) / 2, (start.y + end.y) / 2);
    });
  }

  // The route an arrow was served with, and where its label stood, so a card the reader puts back is
  // served the line it was drawn along — the script has already overwritten it.
  var routed = new Map();
  var labelAt = new Map();
  Array.prototype.forEach.call(document.querySelectorAll("#viewport path[data-e]"), function (element2) {
    if (element2.classList.contains("pick")) return;
    routed.set(Number(element2.getAttribute("data-e")), element2.getAttribute("d"));
  });
  Array.prototype.forEach.call(document.querySelectorAll("#viewport text.elabel[data-e]"), function (element2) {
    labelAt.set(Number(element2.getAttribute("data-e")),
      { x: element2.getAttribute("x"), y: element2.getAttribute("y") });
  });

  // One arrow, drawn as the path it now is: both the stroke and the wide band that takes the pointer
  // carry the same geometry, and the label follows the middle of it.
  function setEdge(edgeIndex, path, middleX, middleY) {
    Array.prototype.forEach.call(
      document.querySelectorAll("#viewport path[data-e=\"" + edgeIndex + "\"]"),
      function (element2) { element2.setAttribute("d", path); });
    var label = document.querySelector("#viewport text.elabel[data-e=\"" + edgeIndex + "\"]");
    if (!label) return;
    label.setAttribute("x", String(drawable(middleX)));
    label.setAttribute("y", String(drawable(middleY)));
  }

  // Putting a card back is the reader's other gesture: it takes the place the layout gave it, and the
  // arrows drawn for it are served again exactly as the routing wrote them — or, when the card at the
  // other end is still where the reader carried it, drawn again from box to box.
  function release(index) {
    carried.delete(index);
    var card = cardElement(index);
    if (card) card.removeAttribute("transform");
    shiftLoops(index, { x: 0, y: 0 });
    (edgesOfNode.get(index) || []).forEach(function (edgeIndex) {
      var edge = data.edges[edgeIndex];
      var other = edge.c === index ? edge.p : edge.c;
      if (carried.has(other)) { reflow(index); return; }
      var path = routed.get(edgeIndex);
      var at = labelAt.get(edgeIndex);
      if (path !== undefined && at) setEdge(edgeIndex, path, Number(at.x), Number(at.y));
    });
  }

  // A hit may name a table the picture is not showing: outside the neighbourhood it opened on,
  // switched off by a chip, or folded into a collapsed cyclic group. Selecting the hit has to show
  // that table — a search whose result selects a card nobody can see reads as a search that does
  // nothing at all — so the three ways of hiding a card are undone before the card is selected.
  function showCard(index) {
    var card = cardElement(index);
    if (!card) return;

    // The chips own their filter, so a chip that hides this table is switched back on rather than the
    // card's own class being removed behind the chip's back: the next chip click must not undo this.
    // Every axis the table is claimed by has to be on again, or the reveal would leave it hidden.
    var switched = false;
    claimsOf(card).forEach(function (claim) {
      var chip = document.querySelector("#chips .chip[data-c=\"" + claim + "\"]");
      if (chip && !chip.classList.contains("on")) {
        setChip(chip, true);
        switched = true;
      }
    });
    if (switched) applyChips();

    card.classList.remove("away");

    if (card.classList.contains("hidden")) {
      var group = card.getAttribute("data-m");
      var supernode = group === null ? null
        : document.querySelector("#viewport .super[data-g=\"" + group + "\"]");
      if (supernode && !supernode.classList.contains("hidden")) {
        toggleGroup(Number(group));
      } else {
        card.classList.remove("hidden");
      }
    }

    // An arrow is shown only while both of its tables are, so the arrows this card joins to tables the
    // picture already shows come back with it; the ones to tables still out of sight stay hidden rather
    // than pointing at blank space.
    edges.forEach(function (element2) {
      var edge = data.edges[Number(element2.getAttribute("data-e"))];
      var other = edge.c === index ? edge.p : edge.p === index ? edge.c : null;
      if (other === null) return;
      var neighbour = cardElement(other);
      if (neighbour && shown(neighbour)) element2.classList.remove("away");
    });

    updateCounts();
    focusNode(index);
    bringIntoView(index);
  }

  // A revealed table can be outside the part of the picture on screen: the layout of a large catalog is
  // far wider and taller than the stage it is clipped to. Taking the card to the middle is what "the
  // picture shows it" means, and the scale is raised to the document's own size because below 100% a
  // card's name is smaller than the text it was drawn with. A card the reader can already see is left
  // where they put it. The move is measured on the screen, not read from the card's coordinates: the
  // stage shows whatever part of the picture it was laid out over, so only the two boxes say where the
  // card is now.
  function bringIntoView(index) {
    var card = cardElement(index);
    if (!card) return;
    var stage = svg.parentNode.getBoundingClientRect();
    var canvas = svg.getBoundingClientRect();
    var left = Math.max(stage.left, canvas.left);
    var top = Math.max(stage.top, canvas.top);
    var right = Math.min(stage.right, canvas.right);
    var bottom = Math.min(stage.bottom, canvas.bottom);
    var box = card.getBoundingClientRect();
    if (box.left >= left && box.top >= top && box.right <= right && box.bottom <= bottom) return;
    taken = true;
    scale = Math.max(scale, 1);
    applyTransform();

    // The picture is painted on the svg's own box, which is the canvas, so a shift that carries a card
    // past the canvas paints nothing at all: the middle a reveal aims at is the middle of the part of the
    // canvas the stage can show, not the middle of the window.
    box = card.getBoundingClientRect();
    shiftX += (left + right) / 2 - (box.left + box.width / 2);
    shiftY += (top + bottom) / 2 - (box.top + box.height / 2);
    applyTransform();
  }

  function runSearch(query) {
    while (hits.firstChild) hits.removeChild(hits.firstChild);
    var needle = query.trim().toLowerCase();
    if (!needle) { hits.hidden = true; return; }
    var matches = data.search.filter(function (entry) {
      return entry.text.toLowerCase().indexOf(needle) >= 0;
    });
    hits.hidden = false;
    hits.appendChild(element("p", "hits-title",
      matches.length === 1 ? "1 match" : matches.length + " matches"));
    if (matches.length === 0) {
      hits.appendChild(element("p", "meta", "no table, column, constraint or comment carries that word"));
      return;
    }
    matches.forEach(function (entry) {
      var row = element("button", "hit");
      row.setAttribute("data-n", String(entry.n));
      row.appendChild(element("b", null, data.nodes[entry.n].name));
      row.appendChild(element("span", null, caption(entry.field) + ": " + entry.text));
      hits.appendChild(row);
    });
  }

  var scale = 1;
  var shiftX = 0;
  var shiftY = 0;
  var pointerX = 0;
  var pointerY = 0;
  var dragging = false;
  var travelled = 0;
  var pressed = null;
  // The camera belongs to the reader from the moment they take it: a drag, a wheel or a zoom button
  // ends the document's own placing, so the picture never re-arranges itself under a reader who has
  // already chosen where to look.
  var taken = false;
  // Where the reader has carried a card, by node index. The layout stays the picture's own; this is
  // the one coordinate a reader may state, and it never travels back into the arrangement.
  var carried = new Map();
  var moving = null;
  // The constraints that meet each table, so a card the reader carries takes its own arrows with it
  // and leaves every other arrow where the layout drew it.
  var edgesOfNode = new Map();
  // A table that names itself is drawn as a ring around its own card, and a ring has no second box to be
  // routed between: it is carried with the card it belongs to, by the distance the card went.
  var loopsOfNode = new Map();
  data.edges.forEach(function (edge) {
    if (edge.c === edge.p) { addLoopOf(edge.c, edge.i); return; }
    addEdgeOf(edge.c, edge.i);
    addEdgeOf(edge.p, edge.i);
  });

  function addEdgeOf(node, edge) {
    if (!edgesOfNode.has(node)) edgesOfNode.set(node, []);
    edgesOfNode.get(node).push(edge);
  }

  function addLoopOf(node, edge) {
    if (!loopsOfNode.has(node)) loopsOfNode.set(node, []);
    loopsOfNode.get(node).push(edge);
  }

  // The ring keeps the shape the routing drew for it and is carried by the same distance as the card: a
  // loop left behind would stand around blank canvas the moment the table it names was moved, and the
  // text it carries is moved with it rather than staying where the routing put it.
  function shiftLoops(index, far) {
    (loopsOfNode.get(index) || []).forEach(function (edgeIndex) {
      var at = labelAt.get(edgeIndex);
      var label = document.querySelector("#viewport text.elabel[data-e=\"" + edgeIndex + "\"]");
      if (label && at) {
        label.setAttribute("x", String(drawable(Number(at.x) + far.x)));
        label.setAttribute("y", String(drawable(Number(at.y) + far.y)));
      }
      Array.prototype.forEach.call(
        document.querySelectorAll("#viewport path[data-e=\"" + edgeIndex + "\"]"),
        function (element2) {
          if (far.x === 0 && far.y === 0) {
            element2.removeAttribute("transform");
          } else {
            element2.setAttribute("transform",
              "translate(" + drawable(far.x) + "," + drawable(far.y) + ")");
          }
        });
    });
  }

  function applyTransform() {
    view.setAttribute("transform",
      "translate(" + shiftX + "," + shiftY + ") scale(" + scale + ")");
  }

  // Where the picture stands, read from the address the viewer serves the document at: a filter is a
  // different picture, never a different place, so the document a chip produces opens where the one
  // it replaced stood (T28). A copy opened on its own states no camera and keeps opening at its size.
  function openingCamera() {
    var query = new URLSearchParams(window.location.search);
    if (!query.has("s") || !query.has("x") || !query.has("y")) return null;
    var camera = { s: Number(query.get("s")), x: Number(query.get("x")), y: Number(query.get("y")) };
    return isFinite(camera.s) && isFinite(camera.x) && isFinite(camera.y) ? camera : null;
  }

  function applyCamera(camera) {
    scale = Math.min(4, Math.max(0.02, camera.s));
    shiftX = camera.x;
    shiftY = camera.y;
    applyTransform();
  }

  // The viewer reads the camera back before it serves the document again, so the place travels through
  // the host instead of through the one message a document may post (D5). It states three numbers and
  // nothing else, and a copy opened without a viewer has nobody to ask.
  window.dbcloneCamera = function () {
    return { s: scale, x: shiftX, y: shiftY };
  };

  function zoomAround(factor, clientX, clientY) {
    taken = true;
    var box = svg.getBoundingClientRect();
    var x = clientX - box.left;
    var y = clientY - box.top;
    var next = Math.min(4, Math.max(0.12, scale * factor));
    var ratio = next / scale;
    shiftX = x - (x - shiftX) * ratio;
    shiftY = y - (y - shiftY) * ratio;
    scale = next;
    applyTransform();
  }

  function fit() {
    var stage = svg.parentNode.getBoundingClientRect();
    var box = pictureBox();
    if (!box) return;
    var width = Math.max(1, box.right - box.left);
    var height = Math.max(1, box.bottom - box.top);
    placeContent(Math.min(stage.width / width, stage.height / height) * 0.97);
  }

  // The part of the canvas the reader can actually look at: the cards and the collapsed group boxes on
  // screen, read off the page and carried back into canvas coordinates with the camera, so the answer
  // is the same at every zoom and follows a card the reader has carried to another place. A blank
  // corner of the canvas is not part of it.
  function pictureBox() {
    var stage = svg.getBoundingClientRect();
    var box = null;
    Array.prototype.forEach.call(document.querySelectorAll("#viewport .node, #viewport .super"), function (element2) {
      if (!shown(element2) || element2.classList.contains("hidden")) return;
      var rect = element2.querySelector("rect");
      if (!rect) return;
      var painted = rect.getBoundingClientRect();
      if (painted.width <= 0 || painted.height <= 0) return;
      var left = (painted.left - stage.left - shiftX) / scale;
      var top = (painted.top - stage.top - shiftY) / scale;
      var right = (painted.right - stage.left - shiftX) / scale;
      var bottom = (painted.bottom - stage.top - shiftY) / scale;
      box = box === null
        ? { left: left, top: top, right: right, bottom: bottom }
        : {
          left: Math.min(box.left, left), top: Math.min(box.top, top),
          right: Math.max(box.right, right), bottom: Math.max(box.bottom, bottom),
        };
    });
    return box;
  }

  // Where the picture goes: the scale asked for, and then either centred in the stage or aligned to its
  // own top-left corner. The canvas of a large database is many screens across, so the corner the
  // document used to open on was empty canvas with no table in it — and "100%" put the reader back on
  // that empty corner (T27). Centring is the answer only while the picture fits: the middle of a
  // picture taller than the stage is a corridor as often as it is a table, and a reader who asks for
  // 100% wants the first table, not the middle of nothing.
  function placeContent(wanted) {
    var stage = svg.parentNode.getBoundingClientRect();
    // A stage the document has not laid out yet measures nothing, and a camera taken against nothing is
    // wider than nothing on every axis: the picture then lands in the corner with the stage's own blank
    // white beside it. So a placement is refused until there is a stage to place into, whatever asked for
    // it — the load and the resize observer take it again once the real size is there.
    if (stage.width <= 0 || stage.height <= 0) return false;
    // The picture's box is taken before the scale is: pictureBox reads the page, and the page still shows
    // the camera the document is leaving, so measuring after the change answers with the picture's size at
    // the old zoom and places the new one by that stale measure — which puts a fitted picture outside the
    // stage, blank.
    var box = pictureBox();
    // The wheel's floor is where a card stops being readable; a fit is allowed below it, because a
    // picture that does not fit its stage is not a fit and a reader who asked for one gets all of it.
    scale = Math.min(4, Math.max(0.02, wanted));
    if (box === null) {
      shiftX = 0;
      shiftY = 0;
      applyTransform();
      return true;
    }
    var margin = 16;
    var width = (box.right - box.left) * scale;
    var height = (box.bottom - box.top) * scale;
    shiftX = width <= stage.width
      ? (stage.width - width) / 2 - box.left * scale
      : margin - box.left * scale;
    shiftY = height <= stage.height
      ? (stage.height - height) / 2 - box.top * scale
      : margin - box.top * scale;
    applyTransform();
    return true;
  }

  svg.addEventListener("wheel", function (event) {
    event.preventDefault();
    zoomAround(event.deltaY < 0 ? 1.15 : 1 / 1.15, event.clientX, event.clientY);
  }, { passive: false });

  svg.addEventListener("pointerdown", function (event) {
    dragging = true;
    travelled = 0;
    pressed = event.target;
    pointerX = event.clientX;
    pointerY = event.clientY;
    // A press on a card carries that card; a press anywhere else carries the picture. Where a table
    // stands is the reader's judgement before it is the layout's, so a card moves by hand and the
    // arrows that join it follow.
    moving = event.target.closest ? event.target.closest("#viewport .node") : null;
    if (moving && (!shown(moving) || moving.classList.contains("hidden"))) moving = null;
    if (moving) moving.classList.add("moving");
    svg.classList.add("dragging");
    svg.setPointerCapture(event.pointerId);
  });

  svg.addEventListener("pointermove", function (event) {
    if (!dragging) return;
    taken = true;
    travelled += Math.abs(event.clientX - pointerX) + Math.abs(event.clientY - pointerY);
    var stepX = event.clientX - pointerX;
    var stepY = event.clientY - pointerY;
    pointerX = event.clientX;
    pointerY = event.clientY;

    if (moving) {
      var index = Number(moving.getAttribute("data-n"));
      var far = carried.get(index) || { x: 0, y: 0 };
      far = { x: far.x + stepX / scale, y: far.y + stepY / scale };
      carried.set(index, far);
      moving.setAttribute("transform", "translate(" + drawable(far.x) + "," + drawable(far.y) + ")");
      reflow(index);
      shiftLoops(index, far);
      return;
    }

    shiftX += stepX;
    shiftY += stepY;
    applyTransform();
  });

  function stopDrag(event) {
    dragging = false;
    if (moving) moving.classList.remove("moving");
    moving = null;
    svg.classList.remove("dragging");
    if (event && event.pointerId !== undefined) {
      try { svg.releasePointerCapture(event.pointerId); } catch (ignored) { }
    }
  }

  svg.addEventListener("pointerup", stopDrag);
  svg.addEventListener("pointercancel", stopDrag);

  // A card carried by hand is put back the same way it was moved: pointing at it twice. A picture a
  // reader has arranged is theirs to undo card by card, without closing the view.
  svg.addEventListener("dblclick", function (event) {
    var card = event.target.closest ? event.target.closest("#viewport .node") : null;
    if (!card) return;
    var index = Number(card.getAttribute("data-n"));
    if (!carried.has(index)) return;
    event.preventDefault();
    release(index);
  });

  svg.addEventListener("click", function (event) {
    // Capturing the pointer hands the pointerup to the svg, and the click that follows is built from
    // that pair, so its target is the svg even when the press began on a card. The element actually
    // pressed is remembered at pointerdown instead of read from the event.
    var target = pressed || event.target;
    pressed = null;
    if (travelled > 4) { return; }
    var supernode = target.closest ? target.closest("[data-g]") : null;
    if (supernode) { toggleGroup(Number(supernode.getAttribute("data-g"))); return; }
    var node = target.closest ? target.closest(".node") : null;
    if (node) { focusNode(Number(node.getAttribute("data-n"))); return; }
    var edge = target.closest ? target.closest(".edge") : null;
    if (edge) highlightEdge(Number(edge.getAttribute("data-e")));
  });

  // The collapsed group's box is reachable with Tab — it states role="button" and tabindex="0" — so
  // Enter and Space have to expand it: a control that takes the focus and ignores the keyboard is a
  // button that does not work.
  svg.addEventListener("keydown", function (event) {
    if (event.key !== "Enter" && event.key !== " ") return;
    var supernode = event.target.closest ? event.target.closest("[data-g]") : null;
    if (!supernode) return;
    event.preventDefault();
    toggleGroup(Number(supernode.getAttribute("data-g")));
  });

  function setChip(chip, on) {
    chip.classList.toggle("on", on);
    chip.setAttribute("aria-pressed", on ? "true" : "false");
    var index = Number(chip.getAttribute("data-c"));
    if (on) offChips.delete(index); else offChips.add(index);
  }

  // The picture is drawn again for the clusters that are on: the host re-lays it out, because a
  // lattice sized for the tables this chip hides is what leaves a hole where they used to stand.
  // A copy opened outside the viewer has no host, and keeps the class toggle it always had.
  var host = window.chrome && window.chrome.webview;
  var redraw = 0;

  function requestRedraw() {
    if (!host || typeof host.postMessage !== "function") return;
    clearTimeout(redraw);
    redraw = setTimeout(function () {
      host.postMessage(JSON.stringify({ off: Array.from(offChips) }));
    }, 250);
  }

  document.getElementById("chips").addEventListener("click", function (event) {
    var chip = event.target.closest(".chip");
    if (!chip) return;
    if (event.altKey || event.shiftKey) {
      Array.prototype.forEach.call(chip.parentElement.querySelectorAll(".chip"), function (other) {
        setChip(other, other === chip);
      });
    } else {
      setChip(chip, !chip.classList.contains("on"));
    }
    applyChips();
    requestRedraw();
  });

  document.getElementById("query").addEventListener("input", function (event) {
    runSearch(event.target.value);
  });

  hits.addEventListener("click", function (event) {
    var row = event.target.closest(".hit");
    if (row) showCard(Number(row.getAttribute("data-n")));
  });

  document.querySelectorAll("input[name=direction]").forEach(function (input) {
    input.addEventListener("change", function () {
      direction = input.value;
      applyDirection();
    });
  });

  document.getElementById("toolbar").addEventListener("click", function (event) {
    var button = event.target.closest("button[data-zoom]");
    if (!button) return;
    var action = button.getAttribute("data-zoom");
    var stage = svg.parentNode.getBoundingClientRect();
    if (action === "in") zoomAround(1.25, stage.left + stage.width / 2, stage.top + stage.height / 2);
    if (action === "out") zoomAround(1 / 1.25, stage.left + stage.width / 2, stage.top + stage.height / 2);
    if (action === "fit") { taken = true; fit(); }
    if (action === "reset") { taken = true; placeContent(1); }
  });

  var showAll = document.getElementById("showall");
  if (showAll) {
    showAll.addEventListener("click", function (event) {
      nodes.forEach(function (element2) { element2.classList.remove("away"); });
      edges.forEach(function (element2) { element2.classList.remove("away"); });
      updateCounts();
      event.target.hidden = true;
    });
  }

  panel.addEventListener("click", function (event) {
    var button = event.target.closest("button.rel");
    if (button) highlightEdge(Number(button.getAttribute("data-e")));
  });

  document.querySelector(".register tbody").addEventListener("click", function (event) {
    var row = event.target.closest("tr[data-e]");
    if (!row) return;
    Array.prototype.forEach.call(document.querySelectorAll(".register tbody tr"), function (other) {
      other.classList.remove("hot");
    });
    row.classList.add("hot");
    highlightEdge(Number(row.getAttribute("data-e")));
  });

  applyChips();
  // The document opens at its own size with the picture placed in the stage, not on the canvas origin:
  // the picture is laid out across a canvas many screens wide, so the corner it used to open on was
  // empty canvas and no table at all. Fitting on open would answer with a thumbnail of a large
  // database, which is why the opening scale is the document's own and "Fit" stays a button (T27).
  // A picture the viewer serves again for another cluster selection opens where the reader left the
  // one it replaces: switching a chip changes what is drawn, never where the reader is looking.
  //
  // The stage has no size until the document is laid out, so a place taken before then is refused by
  // placeContent itself rather than written — and taken again as soon as the load event has passed and
  // on every resize that follows, until the reader takes the camera. Placing means the scale and the
  // pan both: a picture left at the canvas origin with the stage's blank white beside it is the one
  // thing an opening view may not be, whatever its size.
  function placeOnce() {
    if (taken) return;
    var stage = svg.parentNode.getBoundingClientRect();
    if (stage.width <= 0 || stage.height <= 0) return;
    placeContent(scale);
  }

  // A place read from the picture a chip replaces is not this picture's place: a selection is drawn
  // from another arrangement, so the same pan can point at canvas no card stands on — the reader is
  // then looking at an empty part of a picture that has none of the tables they asked for. A place
  // that shows no card is refused the way an unreadable camera is, and the picture is placed at the
  // zoom it kept.
  function onStage() {
    var stage = svg.parentNode.getBoundingClientRect();
    var box = pictureBox();
    if (!box || stage.width <= 0 || stage.height <= 0) return true;
    return shiftX + box.right * scale > 0 && shiftY + box.bottom * scale > 0
      && shiftX + box.left * scale < stage.width && shiftY + box.top * scale < stage.height;
  }

  var opening = openingCamera();
  if (opening) {
    applyCamera(opening);
    if (!onStage()) placeContent(scale);
  } else {
    placeContent(1);
    window.addEventListener("load", placeOnce, { once: true });
    window.addEventListener("resize", placeOnce);
    if (typeof ResizeObserver === "function") new ResizeObserver(placeOnce).observe(svg.parentNode);
  }
}());
""";
}
