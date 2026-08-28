# Handoff: SnapShotKit editor, capture overlay and library

## Overview

A redesign of SnapShotKit's three windows so the app stops reading like Snagit:

- **Editor** — menu bar for commands, one top band for drawing tools *and* their properties, canvas in the middle, a plain strip of recent captures along the bottom. No floating tool palette, no object/layer list, no right-hand properties panel.
- **Capture overlay** — appears on the already-frozen screen. Two states: crosshair only (whole-screen actions available immediately), and region drawn (actions attach to the corner where the drag ended).
- **Library** — thumbnail grid with a search field, tag filters and date groups.

Chosen direction is `1A` ("Bench") in the design file. An earlier alternative (`1B`) was removed.

## About the design files

The files in this bundle are **design references written in HTML** — prototypes showing intended look, structure and behaviour. They are not production code to port.

The target codebase is the attached `shotkit` repo: **C# on .NET 10 with Avalonia 12** (`Avalonia`, `Avalonia.Desktop`, `Avalonia.Themes.Fluent`), UI built in code-behind (no XAML files), targeting Fedora / GNOME Wayland. Recreate these designs as Avalonia views using the repo's existing patterns — `EditorWindow.cs`, `CanvasView.cs`, `LibraryWindow.cs`, `ColourRow.cs`, `Icons.cs`, `SnapshotRow.cs` are the natural homes for most of this.

## Fidelity

**High-fidelity.** Colours, type, spacing, borders and copy are final and taken from the bound *Industry* design system (bundled as `styles.css`). Recreate pixel-accurately; only substitute where an Avalonia control genuinely cannot express something, and note the substitution.

Two things are deliberately placeholder: the captured-screen image inside the canvas and every thumbnail are drawn as diagonal hairline hatch. In the app these are the real screenshot bitmaps.

## Design tokens

Taken verbatim from `styles.css` (`:root`). Suggest mirroring these as a static `Theme` class or an Avalonia `ResourceDictionary`.

### Colour

| Token | Value | Use |
| --- | --- | --- |
| `--color-bg` | `#f2f2f3` | window ground, menu bar, bands, dropdowns |
| `--color-surface` | `#e9e9ea` | reserved |
| `--color-text` | `#1d1f20` | body text |
| `--color-accent` | `#5980a6` | active tool, primary button, selection, annotation default |
| `--color-divider` | `#1d1f20` at 16% alpha | every hairline border (1px) |
| `--color-neutral-100 … 900` | `#f5f5f8`, `#e7e7ea`, `#d4d4d7`, `#b7b7ba`, `#98989b`, `#7a7a7d`, `#5d5d60`, `#424244`, `#2b2b2d` | hatch, canvas mat, secondary text |
| `--color-accent-100 … 900` | `#eef6ff`, `#d6ebff`, `#b5d9fd`, `#94bce3`, `#749dc4`, `#597ea3`, `#416180`, `#2c455d`, `#1d2d3d` | menu-item hover (100), overlay dim (900), pressed (600/700) |

Rules from the system: one accent only, no decorative colour; accent-on-ground is 3:1 so accent text at body size must use `--color-accent-700` or darker.

### Type

- Headings / labels: **Barlow Condensed** 600 — uppercase, letter-spacing `0.14em`–`0.22em`, sizes 11–13px for chrome labels, 23–30px for titles.
- Body / UI: **Barlow** 400/500 — 12px (meta), 12.5–13.5px (menus, buttons, fields), 15px (text annotations on canvas).
- Both are Google Fonts; ship them with the app rather than relying on system fonts.

### Spacing

`--space-1` 3.4px · `--space-2` 6.8px · `--space-3` 10.2px · `--space-4` 13.6px · `--space-6` 20.4px · `--space-8` 27.2px. Density is 0.85×; use these, not round numbers.

### Shape and elevation

- **Radius 0 on everything** — buttons, fields, cards, dialogs, thumbnails. The one exception is icon interiors (Lucide's own `rx="2"` on the square glyph).
- Every framed object (window frame, canvas, thumbnail, dropdown, primary button) carries four **registration marks**: an 11×11px crosshair at each corner, drawn *outside* the box by 6px, colour `--color-text` at 55% alpha, 1px arms. In the HTML this is `.blueprint` + four `<i class="corner tl|tr|bl|br">`. In Avalonia: a reusable decorator that draws four crosshairs around its child. Do not drop them.
- Shadows: `--shadow-sm` `0 1px 2px #2b2b2d@14%`, `--shadow-md` `0 3px 10px #2b2b2d@16%`, `--shadow-lg` `0 12px 32px #2b2b2d@22%`.

### Icons

**Lucide at stroke-width 1.5**, `viewBox 0 0 24 24`, `stroke="currentColor"`, no fill. Glyphs used, by name:

| Where | Glyph |
| --- | --- |
| Select tool | `mouse-pointer-2` |
| Arrow tool | `arrow-up-right` |
| Box tool | `square` |
| Blur tool | `droplet` |
| Text tool | `type` |
| Save to disk | `hard-drive-download` |
| Open in editor | `square-pen` |
| Copy to clipboard | `clipboard-copy` |
| Cancel | `x` |
| Whole screen | `monitor` |

Tool icons render at 17×17px; button icons at 16×16px with an 8px gap to the label. Pull the real paths from lucide.dev (or the copies in the design HTML) into `Icons.cs`.

## Screen 1 — Editor

Frame 1180×740 in the mock; the real window is resizable. Vertical stack, all bands full width, each separated by a 1px `--color-divider` rule.

### 1. Menu bar — 34px, fixed

- Left: wordmark `SNAPSHOTKIT`, Barlow Condensed 13px, letter-spacing `0.22em`, colour `--color-neutral-800`, then `--space-6` of padding.
- Menus: `File · Edit · Draw · View · Library · Help`, Barlow 13.5px, padding `0 var(--space-3)`, full-height hit area. Hover fills `--color-neutral-200`; the open menu fills `--color-accent`, label `--color-bg`.
- Right: `capture-08.ssk` in `--color-neutral-600`, then `unsaved` in `--color-accent-700` (12.5px). Replace with `saved` in `--color-neutral-500` when clean.

**File menu** (252px wide, `--color-bg`, 1px divider border, `--shadow-lg`, registration marks, 3.4px vertical padding). Items are 13.5px, `5px var(--space-4)` padding, label left / shortcut right in `--color-neutral-500`; hover fills `--color-accent-100`. Separators are a 1px divider rule with `--space-1` margin.

| Item | Shortcut |
| --- | --- |
| New capture | Print |
| Open… | Ctrl+O |
| *(separator)* | |
| Save | Ctrl+S |
| Save as… | Ctrl+Shift+S |
| *(separator)* | |
| Export PNG | Ctrl+E |
| Export JPEG… | Ctrl+Shift+E |
| Copy to clipboard | Ctrl+C |
| *(separator)* | |
| Close | Ctrl+W |

Save and export live **only** here — no toolbar buttons for them. Populate `Edit` with undo/redo/delete, `Draw` with the five tools and their shortcuts, `View` with zoom, `Library` with open-library.

### 2. Tool band — 52px, fixed

Left to right, `--space-4` between groups, 1px divider rules (26px tall) between them:

1. **Tools** — five 40×34px cells, 2px apart: select, arrow, box, blur, text (shortcuts V, A, B, L, T). Inactive icon `--color-neutral-800`, hover fill `--color-neutral-200`; active cell fills `--color-accent` with `--color-bg` icon.
2. **Tool name** — Barlow Condensed 11.5px, `0.16em`, `--color-neutral-600` (e.g. `ARROW`); reflects the active tool.
3. **Colour** — five 18×18px square swatches, 4px apart, each 1px divider border: `--color-accent`, `--color-accent-900`, `--color-accent-400`, `--color-neutral-900`, `--color-neutral-100`. Selected swatch gets a 1px `--color-accent-700` outline at 2px offset. (`ColourRow.cs`.)
4. **WEIGHT** — segmented control, 28px tall, options `2 · 4 · 6 · 8`, 1px divider border and internal rules, radius 0; checked segment fills `--color-accent` with `--color-bg` text.
5. **HEAD** — same control, `Single · Double` (arrow tool only).
6. Right-aligned: `Undo · Redo` and the zoom level `100%`, 12.5px.

The band is **tool-sensitive**: groups 3–5 change per tool (box → colour, weight, fill on/off; blur → radius; text → colour, size). Group positions stay put so muscle memory holds.

### 3. Canvas — fills remaining height

Mat `--color-neutral-200`, `--space-8` padding. The image sits centred, max-width 940px, `--color-bg` behind it, `--shadow-sm`, registration marks, contents clipped. Annotation geometry in the mock is percentage-based only so the placeholder scales — in the app annotations are in image coordinates.

Annotation defaults as drawn: box = 3px `--color-accent` stroke, no fill; blur = filled rect with a 1px divider inset edge and a `BLUR 12` caption (Barlow Condensed 10.5px, `0.16em`); arrow = 6px `--color-accent` curve with a solid triangular head; text = Barlow 15px, `--color-neutral-900`.

Selection (from the earlier `1B` exploration, still the intended treatment): keep the object's own stroke, add a 1px dashed `--color-accent-700` outline at 5px offset plus six 8×8px handles — `--color-bg` fill, 1px `--color-accent-700` border — at corners and top/bottom mid-points.

### 4. Recent strip — height driven by content (~76px), fixed

- Caption row: `TODAY · 27 AUG · 34 CAPTURES` (Barlow Condensed 11.5px, `0.18em`, `--color-neutral-600`) left; `11:01:54 — capture-08` (12.5px, `--color-neutral-700`) right.
- Strip: horizontal row, `--space-4` gaps, **newest on the left**, 8 visible items, no shrinking (scroll horizontally when there are more). Each item: 116×52px hatch thumbnail with registration marks, and its time below in 11.5px. Current capture gets a 2px `--color-accent` outline, `--shadow-md`, and its label in `--color-accent-800`; the rest label in `--color-neutral-600`.
- Band padding `var(--space-3) var(--space-6)`; the strip must not shrink below its content or the labels clip.

## Screen 2 — Capture overlay

Fullscreen, over the already-captured frozen frame, so nothing moves under the cursor. Dim is `--color-accent-900` at 55% opacity over the frozen image.

### State A — before the drag

- Crosshair: a 1px `--color-accent-200` line across the full width and another down the full height, meeting at the pointer.
- Readout: 10px right/below the intersection — `264 · 316` (12px, `--color-neutral-800`) on `--color-bg` with a 1px divider border, `3px 8px` padding.
- Action row, top centre, 30px from the top, 38px tall buttons, `--space-2` apart, radius 0:
  - **Whole screen** — primary: `--color-accent` fill, `--color-bg` label, registration marks, `monitor` icon.
  - **Whole screen to clipboard** — secondary: `--color-bg` fill, 1px divider border, `clipboard-copy` icon.
  - **Cancel** — secondary, `x` icon.
- Hint bar, bottom centre, 26px up: `--color-bg`, 1px divider border, `var(--space-2) var(--space-6)` padding, three 12.5px `--color-neutral-700` items `--space-6` apart: `Drag to draw a region`, `Space — whole screen`, `Esc — cancel`.

### State B — region drawn

- The region shows the frozen image at full brightness (undimmed), with a 1px `--color-accent-200` outline; the crosshair rules stay, extended to the frame edges.
- Six handles as in the editor's selection treatment.
- Inside the region, centred: the size in Barlow Condensed 34px, `0.04em`, `--color-accent-900` (e.g. `1486 × 778`) with the origin below it — `x 264 · y 316`, 12.5px `--color-neutral-700`.
- Actions attach 14px under the region's bottom-left corner, 38px tall, `--space-2` apart: **Save to disk** (primary, `hard-drive-download`), **Open in editor** (`square-pen`), **Copy to clipboard** (`clipboard-copy`), **Cancel** (`x`).
- Same hint bar as state A.

Behaviour: Print captures immediately and shows state A. Drag → state B. Space or the primary button in state A takes the whole screen. Arrows nudge the region 1px, Shift+arrows resize. Enter = the primary action of the current state. Esc cancels and discards. Save to disk writes the file and closes without opening the editor; Open in editor hands off to `EditorWindow`.

## Screen 3 — Library

Frame 1180×740. Same 34px menu bar, with `Library` shown active (`--color-neutral-200` fill).

### Filter bar — 58px, fixed

- Search field: 360px × 34px, 1px divider border, radius 0, `--color-bg`, 13.5px text. Placeholder `Search captures, tags, text in shots`. Matches filename, tags **and** text drawn on the shot — annotations are stored as objects, so their text is searchable.
- Active filter chips: `has blur ×` filled `--color-accent-100` with `--color-accent-800` text; available tags as outline chips (1px `--color-accent`, accent text): `wayland`, `bug report`, `docs`. 11px, `3px 10px`, radius 0.
- Right: result count `31 of 412 captures` (12.5px, `--color-neutral-600`) and a `Grid · List` segmented control, Grid active.

### Grid — scrolls

`var(--space-6)` padding, `--space-6` between groups. Each date group:

- Header: label in Barlow Condensed 13px, `0.18em`, `--color-neutral-800` (`TODAY · 27 AUGUST`, `YESTERDAY · 26 AUGUST`, then explicit dates), count beside it in 12.5px `--color-neutral-500`, and a 1px divider rule under the row (`--space-2` padding-bottom).
- Cells: 5-column grid, `--space-6` gaps. Each cell = 116px-tall hatch thumbnail with registration marks, then name (13px, `--color-neutral-900`) with time right-aligned (11.5px, `--color-neutral-500`), then size (11.5px, `--color-neutral-600`) and what is on the shot (11.5px, `--color-accent-700`) — e.g. `4 objects`, `blur, 2 arrows`, `no objects`. Selected cell: 2px `--color-accent` outline + `--shadow-md`.

Double-click opens the capture in the editor. Keep the last-used group ordering; newest first everywhere.

## Interactions and behaviour

- **Hover** on any interactive surface: `--color-neutral-200` fill (chrome) or `--color-accent-100` (menu items). **Pressed**: one accent step past base — `--color-accent-600`, or `--color-accent-700` for the primary button.
- **Keyboard focus** is always visible: 2px `--color-accent` outline at 2px offset. Never leave a platform default ring.
- **Disabled**: 45% opacity.
- Tool shortcuts V / A / B / L / T; Ctrl+Z / Ctrl+Shift+Z for undo/redo; Delete removes the selected object.
- Annotations remain editable objects for the life of the file — moving or deleting one never touches the stored original.
- No animation is specified. If any is added, keep it under 120ms and to opacity/position only.

## State

Editor: `activeTool`, `activeColour`, `strokeWeight`, `arrowHead`, `fillOn`, `blurRadius`, `selectedObjectId`, `objects[]`, `zoom`, `isDirty`, `recentCaptures[]` (newest first), `currentCaptureId`.
Overlay: `phase` (crosshair | region), `pointer`, `region` (x, y, w, h), `frozenFrame`.
Library: `query`, `activeTags[]`, `viewMode` (grid | list), `groups[]`, `selectedId`.

## Assets

None to ship beyond fonts (Barlow, Barlow Condensed) and the Lucide glyphs listed above. All imagery in the mock is placeholder hatch standing in for real screenshot bitmaps.

## Files in this bundle

- `SnapShotKit Screens.dc.html` — the design. Open it in a browser: turn 2 (top) is the capture overlay and library, turn 1 (below) is the editor `1A`.
- `support.js` — runtime the HTML needs to render. No production relevance.
- `industry-design-system/styles.css` — the token sheet and component layer the design is built on. Authoritative for every value above.
- `industry-design-system/readme.md` — the design system's own guide (direction, do's and don'ts).
