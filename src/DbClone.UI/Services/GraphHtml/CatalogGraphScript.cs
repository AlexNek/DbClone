namespace DbClone.UI.Services.GraphHtml;

/// <summary>
/// The document's only executable script: pan and zoom, selection, the cluster fold, search and the
/// direction switch. It reads the data island and moves classes; it never computes a coordinate, a
/// count or a rank, and it never converts a string into markup.
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

  var offChips = new Set();
  var focus = -1;
  var direction = "both";

  function element(tag, className, text) {
    var node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined && text !== null) node.textContent = text;
    return node;
  }

  function rowsPhrase(rows) {
    if (rows === null || rows === undefined) return "no data captured";
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
      var claims = (element2.getAttribute("data-c") || "").split(" ").filter(Boolean);
      var shown = claims.length === 0 || claims.some(function (chip) { return !offChips.has(Number(chip)); });
      element2.classList.toggle("off", !shown);
      if (shown) visible.add(Number(element2.getAttribute("data-n")));
    });
    edges.forEach(function (element2) {
      var edge = data.edges[Number(element2.getAttribute("data-e"))];
      element2.classList.toggle("off", !visible.has(edge.c) || !visible.has(edge.p));
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
    panel.appendChild(element("p", "meta", detail.cap ? rowsPhrase(detail.rows) : "not in this catalog"));
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

  function cardOf(index) {
    var found = null;
    nodes.forEach(function (element2) {
      if (Number(element2.getAttribute("data-n")) === index) found = element2;
    });
    return found;
  }

  // A hit may name a table the picture is not showing: outside the neighbourhood it opened on,
  // switched off by a chip, or folded into a collapsed cyclic group. Selecting the hit has to show
  // that table — a search whose result selects a card nobody can see reads as a search that does
  // nothing at all — so the three ways of hiding a card are undone before the card is selected.
  function showCard(index) {
    var card = cardOf(index);
    if (!card) return;

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

    // The chips own their filter, so a chip that hides this table is switched back on rather than the
    // card's own class being removed behind the chip's back: the next chip click must not undo this.
    var switched = false;
    (card.getAttribute("data-c") || "").split(" ").filter(Boolean).forEach(function (claim) {
      var chip = document.querySelector("#chips .chip[data-c=\"" + claim + "\"]");
      if (chip && !chip.classList.contains("on")) {
        setChip(chip, true);
        switched = true;
      }
    });
    if (switched) applyChips();

    // An arrow is shown only while both of its tables are, so the arrows this card joins to tables the
    // picture already shows come back with it; the ones to tables still out of sight stay hidden rather
    // than pointing at blank space.
    edges.forEach(function (element2) {
      var edge = data.edges[Number(element2.getAttribute("data-e"))];
      var other = edge.c === index ? edge.p : edge.p === index ? edge.c : null;
      if (other === null) return;
      var neighbour = cardOf(other);
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
    var card = cardOf(index);
    if (!card) return;
    var stage = svg.parentNode.getBoundingClientRect();
    var canvas = svg.getBoundingClientRect();
    var left = Math.max(stage.left, canvas.left);
    var top = Math.max(stage.top, canvas.top);
    var right = Math.min(stage.right, canvas.right);
    var bottom = Math.min(stage.bottom, canvas.bottom);
    var box = card.getBoundingClientRect();
    if (box.left >= left && box.top >= top && box.right <= right && box.bottom <= bottom) return;
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

  function applyTransform() {
    view.setAttribute("transform",
      "translate(" + shiftX + "," + shiftY + ") scale(" + scale + ")");
  }

  function zoomAround(factor, clientX, clientY) {
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
    var width = Number(svg.getAttribute("width"));
    var height = Number(svg.getAttribute("height"));
    if (!width || !height) return;
    scale = Math.min(stage.width / width, stage.height / height) * 0.97;
    shiftX = (stage.width - width * scale) / 2;
    shiftY = (stage.height - height * scale) / 2;
    applyTransform();
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
    svg.classList.add("dragging");
    svg.setPointerCapture(event.pointerId);
  });

  svg.addEventListener("pointermove", function (event) {
    if (!dragging) return;
    travelled += Math.abs(event.clientX - pointerX) + Math.abs(event.clientY - pointerY);
    shiftX += event.clientX - pointerX;
    shiftY += event.clientY - pointerY;
    pointerX = event.clientX;
    pointerY = event.clientY;
    applyTransform();
  });

  function stopDrag(event) {
    dragging = false;
    svg.classList.remove("dragging");
    if (event && event.pointerId !== undefined) {
      try { svg.releasePointerCapture(event.pointerId); } catch (ignored) { }
    }
  }

  svg.addEventListener("pointerup", stopDrag);
  svg.addEventListener("pointercancel", stopDrag);

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

  function setChip(chip, on) {
    chip.classList.toggle("on", on);
    chip.setAttribute("aria-pressed", on ? "true" : "false");
    var index = Number(chip.getAttribute("data-c"));
    if (on) offChips.delete(index); else offChips.add(index);
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
    if (action === "fit") fit();
    if (action === "reset") { scale = 1; shiftX = 0; shiftY = 0; applyTransform(); }
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
  applyTransform();
}());
""";
}
