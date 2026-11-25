# Relationship View

DbClone draws a database's tables and foreign keys as one picture — every table, every relation, searchable, pannable, and readable on a normal screen. It is a **view**, not a mode: it changes nothing on the database it reads, and it costs that database no write.

## Where to open it

| Entry point | What it reads |
|-------------|---------------|
| **View relationships** under the **source** panel | The source database, read live |
| **View relationships** under the **destination** panel | The destination database, read live |
| The **folder** button next to the source panel's **View relationships** | Any catalog folder already on disk — no connection needed |

A **live** view reads the database's own metadata — schemas, tables, columns, keys, constraints and indexes. It never touches a row and asks for no objects the picture does not draw, so a large database does not wait for them, and nothing is written back. A **catalog** view reads the folder the store wrote, so a catalog can be explored on a machine that cannot reach the database it came from.

A finished store also draws the picture for you, so a catalog folder explains itself as soon as it exists — see below.

## The document

The picture is one self-contained HTML file: no server, no CDN, no asset folder, so it opens with the network disconnected and can be mailed or committed with the catalog.

When the WebView2 runtime is present the document opens in DbClone's own window, whose toolbar holds three actions:

| Action | What it does |
|--------|---------------|
| **Refresh** | Reads the origin again and draws it again, so a table created after the window opened appears. A live view re-runs the same metadata read; a catalog view re-reads the folder |
| **Open in browser** | Hands the very document on screen to your default browser |
| **Save as HTML…** | Keeps a copy wherever you choose, as one file |

The window opens **before** the work is done and names each phase with an elapsed timer (*Reading the database model…*, *Arranging the picture…*); the arrangement is computed off the UI thread, so the application stays responsive. A failure or an interrupted layout is stated there with its reason instead of leaving a stalled bar.

On a machine without the runtime the picture opens in your browser instead, and the log says why. That fallback uses one fixed file, `%LOCALAPPDATA%\DbClone\view\relationship.html`, overwritten by every view — so opening the view a hundred times leaves one file, never one per view. Neither route writes anything into a catalog folder: opening the view of a catalog leaves its files and their timestamps exactly as they were.

!!! info "Why the store's picture sits in the catalog folder"
    The picture a store draws is named after the catalog folder and written **inside** it — `my_app_db_2026-11-10_16-10/my_app_db_2026-11-10_16-10.html` — so a catalog and the picture of it travel together and the store folder stays clean. Like `summary.md`, the picture is not part of the catalog's content: it is not listed in `manifest.json`, not hashed, not verified and never restored, so deleting it or editing it cannot break a catalog. Every store rewrites the file when it finishes; viewing a catalog reads the folder and leaves that file alone. A picture that cannot be written never makes a store a failure, because the catalog is already complete — the log states the reason instead.

## The card: the name, and the count the origin states

Every table is one card, and its facts are printed **inside** it: the table's name on the first line, and on the second what the origin states about that table's rows — the count a store captured, or that the store captured no rows. A live read counted no rows for any table, so it prints no second line: the card is sized by its name alone and the header states once, for the whole picture, that rows are not counted. A card never prints a figure its origin did not measure, and never leaves an empty line where one would sit. The card is exactly as wide as the longer of its lines, so `ai_chat_provider_configuration` gets a wider card than `orders` — and no name is ever cut, wrapped or pushed outside the shape.

No dimension states a figure. Every card has the same height and a width that follows its own text, so a larger shape never means "a larger table": any figure the origin states is read in words inside the card, not off its size, and no colour carries a number either.

| Printed on the card | Meaning |
|---------------------|---------|
| `1 240 rows` | The row count the store captured |
| `0 rows` | A real table that was captured empty |
| `no data captured` | The structure was captured but not the rows — table data was switched off |
| `not in this catalog` | A constraint points at this table and the store did not capture it |
| `not in this view` | A live view: a constraint points at a table the read did not return |
| nothing under the name | A live view: the read measured no row count, and the header says so once |

## What the arrows say

| You see | It means |
|---------|-----------|
| A solid arrow with nothing on its tail | **Many-to-one (N:1)**: many rows of the table it starts from may name the one row it points at — the shape of nearly every foreign key, so it is the case that carries no mark |
| A bar across the child end | **One-to-one (1:1)**: at most one child row names each parent row — the referencing columns are exactly a primary key or a unique constraint of the child. A maximum, not "exactly one": whether a parent must have a child is a rule the catalog cannot see |
| A dashed arrow | The referenced table is not in this catalog |
| A straight line running behind a card | The cards are painted over the arrows, so a relation that passes a table it does not join is hidden by that card instead of bent round it — the line is there, and the card simply covers the part that crosses it |
| A loop on one card | The table points at itself. A cycle is not told by its line: the panel of every table inside one names the group (`cyclic group 2: 3 tables, this one points back into it`), and a large group is drawn as one box |
| A dashed card | A table a constraint names but the store did not capture. The panel prints its name and its constraints, and nothing the folder does not state |
| A `↺ N` box over a band | A cyclic group drawn as one shape; select it to expand its members in place |

The parent end carries no mark because it cannot vary: a foreign key always references a unique key, so one parent row is always what a child row names. The child end is where a relation differs, so it is the only end that is marked, and it is marked only for the exception: a bar where the catalog proves one, nothing where it proves many or cannot prove either. The proof comes from the keys the store captured, never from a guess: a key of the child must cover **exactly** the referencing columns, so a key on `(user_id, tenant)` does not make a constraint on `user_id` one-to-one. A unique index alone is not proof either: the catalog records an index's key columns, but nothing about its predicate or its expressions, so a `WHERE`-restricted unique index would be taken for a key over the whole table — such a pair is drawn with no bar, which reads as the default rather than as a claim the database does not allow.

Many-to-many is not an arrow, because no engine declares one. It is a table that points through two others, and when its key is exactly the columns it points through, the panel names it: `issue_labels · join table of issues and labels — inferred`. The word *inferred* is deliberate — a link table carrying a surrogate key or a discriminator column is not named at all rather than guessed at.

No encoding stands for two facts, and no fact has two encodings. The columns a constraint joins are never printed along its arrow: they are written beside it and appear when you point at the arrow or select it, so a corridor carries its line and nothing else and two routes that converge never stack their text on each other. Selecting an arrow does not ask you to land on its line — a wide invisible band follows every arrow, so a point a few pixels to the side of a line reveals and selects that constraint, and two parallel arrows stay separately selectable because each keeps its own band. A constraint loses nothing by that — it keeps its arrow, its tooltip, its row in the register and its place in the panel, where its columns are printed in full without touching anything. Two constraints between the same pair of tables stay two arrows you can select one at a time.

## How the cards are arranged

Direction is carried by the arrowhead, never by where a table sits. Every catalog is arranged the one way: the cards are placed to be read top-down, and every relation is drawn as one straight line between the two borders it joins, behind the cards. There is no arrangement to choose between and nothing to switch by hand — the same catalog settles the same way every time.

**The reading order.** A table is placed under the tables it points at wherever the arrows allow it, so the picture reads top-down. That order is a preference the arrangement is chosen by rather than a rule it is forced into: a group of tables that point at one another in a circle has no top, and a family that surrounds its parent is drawn round it. The cards are spread far enough apart that an arrow between two of them has room to be drawn, and a table nothing joins stands clear of the rest instead of being squeezed in among the tables that are joined.

**The arrows.** A relation is drawn as one straight line between the two borders it joins — never a set of right-angle turns and never a bow round anything. The cards are painted over the arrows, so a table standing between two others costs their relation nothing: the line runs behind the card and the card covers the part it crosses, which is why a relation is never bent round a table it does not join. Two straight lines may cross, and a crossing is read for what it is — two relations passing rather than a fault in either — so no arrow is bent round another to avoid one and no two arrows are ever drawn on one stroke, where they would read as a single arrow joining four tables. The one stroke held clear of every card is the loop a table draws to itself, out of its own top border and back into it. Every arrow is given its own point on the border of the card it joins, so two arrows that both point at the same face land beside one another instead of fusing into one thick arrow belonging to no constraint in particular; a table that more constraints reach than its border can hold is drawn wide enough to carry them all, and such a table is widened across the tables that name it, so each of their arrows leaves where its own table stands.

**The pieces.** A catalog that is several pictures — a family gathered around one table, a chain that joins nothing of it, a table no constraint reaches — is arranged piece by piece, and the pieces are stood beside one another at the line length that leaves the smallest page. A table of one piece never stands inside another's picture, where its arrows would run behind tables that have no part in it.

**The search.** Several arrangements are tried — a top-down starting point, a radial one and a force-directed one, each repaired until every relation is routed — and only the complete ones compete. They are compared on what a reader notices first: the number of arrows drawn across one another, then how many relations climb up the page, then how long the total run of ink is. The radial starting point turns its tables along their rings, and widens a ring, wherever that clears a relation from a table standing over it, so the busiest table's arrows run through open ground. A crossing is never traded for a shorter arrow and no arrow is bent to avoid one. The arrangement is a heuristic, not an optimum: drawing a graph with the shortest possible arrows and no crossing at all is a problem with no practical solution.

**When the picture takes too long.** The search is bounded. Twenty seconds is the ceiling: past it the search stops, and the picture you are shown is the best complete arrangement of the starting points it tried — every table and every relation is there and drawn by the same rules, carrying the crossings those starting points happened to give. The log says so beside the view. The pass that lowers crossings runs on a slice of the time the picture left over rather than on the whole remainder, so a catalog the search has already answered is not made to wait for it. A complete arrangement the search never proved is not hidden: the plain arrangement — every table and every relation in one straight line each — is named beside the view with a warning that some relations may cross one another. Only when there is no picture at all does the view report a failure naming the first relation it could not draw, and no picture is shown in its place. The picture is handed over once, when the arrangement is the one the search settled on — never a plain page that a refined one replaces a moment later.

## Narrowing what you look at

| Control | What it does |
|---------|---------------|
| **Search** | Matches a table, a column, a constraint or a comment. The result list names the field each row matched — every table in the catalog, not only the ones on screen — and selecting a row shows that table: a table the picture was not showing is revealed, switched back on and brought into view, then selected with its panel open |
| **Direction** | While a table is selected: **both ways**, **references** (the arrows leaving it) or **referenced by** (the arrows arriving). The arrows outside the chosen direction fade rather than disappear |
| A click on empty ground | Puts the selection down: the card, its arrows and its register row are released and the panel returns to the words it opened with. A drag that moved the picture never counts as one, so panning across the background keeps what you had selected |
| The bar of chips | One chip per schema, per cyclic group and for the tables in no relationship. A click hides a cluster, alt-click (or shift-click) shows only it. Each group of chips filters on its own, so switching a schema off hides every table of that schema even when a cyclic group also claims it, and the box standing over a collapsed group leaves with the tables it stands over. A chip changes what is drawn, never where you are looking: the picture keeps the zoom and the position you left it at, and so does **Refresh**. A catalog with a single schema states the schema instead of offering a switch whose other state is "hide everything" |
| **−** / **+** / **Fit** / **100%** | Zoom. Drag to pan, wheel to zoom. **100%** draws the picture at its own size and **Fit** fits the tables that are on screen into the window; a picture that fits the window is centred in it, and one larger than the window starts at its own top-left corner rather than in the empty middle of the canvas |
| The panel on the right | The selected table itself: its row count, its columns with their types and key marks, its primary key, its unique constraints, its indexes — each named by the key columns the store captured, so a reader never has to read a SQL statement to find out what an index covers, with the engine's own statement printed only for an index whose columns the folder does not state — and its check constraints with their expressions. Then both lists of constraints, each row naming the constraint, the columns it joins, its `on delete`/`on update` action, and how many child rows may name a parent — `exactly one`, `at most one`, or whether a child row may have no parent at all — so two constraints joining the same columns are told apart by name and rule, never printed as duplicates |
| The register at the bottom | Every constraint in the catalog, one row each, with its columns, its `on delete` and `on update` actions and its rule. Selecting a row selects that arrow |

Nothing is removed by narrowing: focus, direction, the chips and the search change what is in view, and the count line under the header says how much of the catalog that is. An arrow is drawn only while both of the tables it joins are: a table the picture is not showing takes its arrows out of the picture with it, so no line ever runs to a position where no card stands, and the arrows come back with the table. The register never narrows, so the picture can always be checked against the whole catalog.

## What the picture does not state

The header names the origin — `catalog <folder>` for a stored catalog, `live database <host:port/database>` for a database read — how many tables and relations the picture holds, the catalog's format version when there is one, the dialect, and when the picture was generated (a catalog) or read (a live database). It also carries the one sentence about rows that is true of the whole origin: a store that captured no table data says `no row data was captured`, and a live read says `rows are not counted here`. Two things it deliberately does not state:

- **Per-table storage bytes.** The catalog's byte figures are totals for the whole folder; it records a table's rows and chunk files, not a byte figure per table.
- **When the catalog was stored.** The picture is stamped with the moment it was generated. The catalog's own write time is in `export.json`.

Cardinality is stated where the keys prove it, and one case stays unsaid: a relation whose uniqueness rests on a **unique index** is drawn as many. The catalog records an index's name, its uniqueness and its key columns, but nothing about the predicate that restricts it, so an index unique only over the rows a `WHERE` clause admits would be read as a key over the whole table — and a mark that is sometimes missing is better than a mark that is sometimes wrong. The panel still lists the index, so a reader can see the disagreement; the picture will not guess.

The panel states the table's key, unique constraints, indexes and check constraints from the catalog itself — an index is named by the key columns the store captured as data, and nothing is printed for a fact the folder does not hold. A table comment is not shown at all, because the catalog records comments for columns and not for tables.

## See also

- [Store & Restore](store-restore.md) — where a catalog comes from, and the text register of the same relations in `summary.md`
- [Table Selection](connections/table-selection.md) — the smaller relationship list inside the selection dialog
- [Options Reference](options.md) — settings that decide what a catalog holds
