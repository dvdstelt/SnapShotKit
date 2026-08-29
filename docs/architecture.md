# SnapShotKit architecture

SnapShotKit is a screenshot and annotation tool for Linux, in the spirit of Snagit: press Print, capture, select, annotate in a real editor, and keep everything in a searchable library. Editing is non-destructive, so an arrow drawn today can be moved tomorrow.

This document describes the intended shape. Anything not yet validated by a spike is marked as such.

## Platform constraints

Fedora on GNOME Wayland dictates most of the design. Three constraints matter.

**No overlay on the live screen.** Mutter does not implement `wlr-layer-shell`, so an app cannot draw over the desktop. The selection UI therefore works on a frozen bitmap: capture the whole screen first, then show a fullscreen borderless window displaying that capture and draw the dimmer, crosshair and magnifier on top of it. This matches the workflow of timing a capture and choosing the region afterwards, and it means the region is chosen against exactly the pixels that were captured.

**No window geometry.** Wayland gives an app no way to enumerate window rectangles, so Snagit-style window highlighting cannot be built from the client side. Options are the portal's interactive mode, which hands control to GNOME's own picker, or a small GNOME Shell extension exposing window rects over its own D-Bus name. Deferred past v1.

**No global hotkey grab.** Print must be handed over by GNOME. Clear `org.gnome.shell.keybindings show-screenshot-ui` and register a custom media-keys binding pointing at the SnapShotKit client. The `GlobalShortcuts` portal exists here at version 1 but the gsettings route is more predictable for rebinding Print specifically.

## Capture backend

GNOME's private `org.gnome.Shell.Screenshot` API is closed to third-party apps by a sender whitelist and is not usable. That leaves two portals, and both have been measured.

**ScreenCast with a parked PipeWire stream is the primary path.** Validated in [spike 002](spikes/002-screencast-capture.md) and [spike 003](spikes/003-pipewire-shim.md): a session established with a restore token takes about 100 ms with no dialog, and once a stream is connected and negotiated, capturing a raw 5120x1440 frame costs about 35 ms with no encoding and no disk anywhere in the path. It costs one consent dialog, ever.

The daemon connects a stream at startup, takes one throwaway frame to force format negotiation, and parks it inactive. A parked stream costs the compositor nothing measurable, while an active one costs about 17 points of CPU, so parking is what makes an always-running daemon acceptable.

**The Screenshot portal is the fallback.** Validated in [spike 001](spikes/001-portal-capture.md): `interactive: false` captures the full desktop with no permission prompt at all, in roughly 700 ms, writing a PNG into `~/Pictures` that the caller must move. Slower, but it never asks for anything.

The fallback is not hypothetical. ScreenCast only issues a restore token if the user ticks the remember box in the consent dialog, and a user who does not tick it would otherwise be prompted on every single capture. SnapShotKit detects the missing token, falls back to the Screenshot portal, and explains why rather than prompting forever.

Whether the daemon can hold a ScreenCast session open permanently is unresolved. A live session means the compositor captures continuously, so the likely design is a lazily started session with an idle timeout. See the open questions in spike 002.

## Components

A daemon plus thin front-ends, because cold-starting a GUI on every Print press would add startup cost on top of the capture cost.

- **`snapshotkitd`**, a systemd user service. Holds the portal connection warm, owns the library and its index, exposes D-Bus.
- **`snapshotkit capture`**, the tiny client the hotkey invokes. Tells the daemon to capture and show the overlay.
- **Overlay window.** Fullscreen and borderless, shows the frozen capture, handles region selection with magnifier and live dimensions, Escape cancels, Enter accepts the whole screen.
- **Editor window.** The annotation surface: arrows, callouts, blur, highlight, step numbers, crop.
- **Library window.** Grid of past captures grouped by day, backed by the thumbnail cache.
- **`SnapShotKit.Ui`.** The design system as code: tokens, the registration-mark frame, the Lucide glyphs and the shared controls. Referenced by both GUI processes so they cannot drift apart.

## Technology

C# on .NET 10 with Avalonia for the UI. An annotation editor is a retained-mode scene graph problem, which is what Avalonia is good at, and Skia underneath handles both rendering and export. `Tmds.DBus.Protocol` covers the portal, `Microsoft.Data.Sqlite` the index, `System.Text.Json` the document format.

Avalonia runs through XWayland rather than a native Wayland backend. On a single monitor at scale 1 that is pixel-exact and invisible. It would need revisiting for fractional scaling or HiDPI.

## Native capture layer

Frames are pulled by `src/native/snapshotkit-capture`, a small C program that owns the libpipewire connection and answers frame requests from the daemon over a pipe. Frames cross the process boundary through a shared file in `XDG_RUNTIME_DIR`, which is tmpfs and therefore RAM.

It is a separate process because libpipewire and the .NET runtime cannot reliably share an address space for this workload: in-process the stream reaches STREAMING and never receives a buffer. See [spike 005](spikes/005-thread-pool-capture-failure.md).

The helper connects a stream per grab and disconnects afterwards. An idle SnapShotKit therefore costs the compositor nothing measurable, 24.9% against a 23.3% control, and a grab pays only for activating a stream that is already set up. This shape is forced by measurement: a continuously active stream costs gnome-shell roughly 17 points of CPU, so holding one open permanently is not an option.

### Dependencies and packaging

`pipewire-devel` and a C compiler are build-time only, in the same way a compiler is. They provide headers, not code. End users need `pipewire-libs`, which supplies `libpipewire-0.3.so.0` and is already present on any system running PipeWire, which on Fedora means any system running a desktop.

A package therefore carries `BuildRequires: pipewire-devel` and `Requires: pipewire-libs`, and ships the compiled `libsnapshotkitpw.so` inside itself. Nothing is installed by hand.

### The pure P/Invoke alternative is off the table

Calling `libpipewire-0.3.so.0` directly from C# would have removed the native build step and made SnapShotKit a pure .NET artifact. That option died with spike 005: the problem is precisely that libpipewire cannot be driven from inside the .NET process, so there is nothing to gain by removing the C and everything to lose.

## Storage format

A `.ssk` file, for SnapShotKit snapshot, is a zip container in the manner of ODF and OOXML:

```
snapshot-01.ssk  (zip)
├── document.json     the canvas rectangle and the annotation objects, each with its own geometry
├── original.png      the capture as taken, never modified
└── meta.json         when it was taken, the source screen size, the region within it
```

The point of the format is that editing stays non-destructive. `original.png` is never touched, and every arrow, callout or blur is an object with coordinates rather than pixels burned into the image, so anything drawn today can be moved or deleted next week. Exporting to PNG or JPEG renders the document rather than being the document.

The canvas is recorded as a rectangle rather than a size, because it is not obliged to match the capture: it carries an offset saying where its top-left corner sits relative to the capture's. A document written before that existed has no offset, and zero is exactly what it meant.

Snapshots are numbered, `snapshot-01.ssk` upwards, because they are working documents a person refers to by name. Straight captures that skip the editor are timestamped instead, since nobody refers to those by number.

## The editor

`snapshotkit-editor` is a standalone tool. Editing a snapshot has nothing to do with taking one, and the two are wanted at different times, so the editor is never on the capture path and the daemon merely launches it and forgets about it.

It draws through the same renderer used for export, so what lands in the exported file is what was on screen. Two rendering paths would drift.

Blur is done by keeping a blurred copy of the whole capture per radius in use. A blur region is then the corresponding patch of an already blurred image, which costs the same as drawing any other bitmap; the alternative is a gaussian blur on every repaint.

Annotations are drawn in the order they are in, and that order can be changed: forward, backward, to the front, to the back. An earlier version drew every blur first regardless of order, so that a blur could never hide an arrow, but a rule like that quietly overrides the choice the user is now able to make. The default is kept instead by where a new blur is filed rather than by how it is drawn, and documents written under the old rule are reordered as they are opened so they still look exactly as they did. That is what the format version is for.

The window is a stack of full-width bands separated by hairlines: a menu bar, one band carrying both the drawing tools and their settings, the capture on a mat, and the recent captures along the bottom. Nothing floats over the picture. Commands live in the menus and settings live in the band, so saving and exporting do not spend permanent screen space on things pressed once at the end of a session.

What the band shows follows the selection when there is one and the tool otherwise: selecting an object is a statement about what you mean to work on, whichever tool is in hand. It shows only the settings that apply, but never moves the ones it shows: colour is always in the same place, weight always the next along. The hand learns each position once. A selection overrides the tool when deciding what to show, because acting on a selected object is what the user is doing.

Settings lead with presets rather than sliders. The values worth having are mostly discrete, the choice is visible without being dragged, and a click is naturally one undo step where a drag is a hundred. Every setting still reaches any value: colours through a picker, numbers through a slider and a box behind a trailing segment. That segment shows the value whenever it is off the preset scale, so a custom choice never disappears from the band.

The colour picker and the slider are drawn rather than taken from the toolkit. Avalonia's colour picker is a set of rounded pill controls and its slider thumb is a filled circle in the toolkit's own blue, both of which read as foreign objects in a design whose grammar is square and hairline. The same applies to buttons: they carry a template that binds the content presenter to the button and nothing else, because the stock theme repaints the presenter on hover with translucent brushes that win over whatever the button itself carries.

Text is typed on the picture rather than in a field somewhere else. Placing it opens a text box positioned and styled to match exactly what the renderer would draw, and the annotation underneath is left undrawn while that box is open, so the words never appear twice and committing never moves them. Enter finishes, Shift and Enter make a new line, Escape puts the words back as they were. Text left empty is dropped rather than left invisible on the canvas.

While it is open the words sit on a plate, backed light for dark text and dark for light text, with a two-toned outline around it. Typing over a screenshot means typing over anything at all, and against a busy photograph neither a thin box nor a one pixel caret can be found; the plate guarantees contrast for both. The caret and the selection take their colours from it for the same reason. All of it is chrome and goes the moment the edit is finished.

Selection outlines are two-toned everywhere for the same reason: a dark line under a light one, which is the trick the capture overlay already uses to stay visible over an unknown desktop.

It is a real text box rather than a caret painted by hand: editing text means selection, arrow keys, home and end, backspace across a line break, the clipboard and input methods, and reimplementing that on a drawing surface produces a worse version of what the toolkit already has.

Unsaved changes are a three-way question: keep editing, discard, or save. The honest answer is usually the third, and a dialog offering only the first two leaves the user to dismiss it and save by hand. Saving carries on only if it actually succeeded, so a failed write cannot lose the work it was meant to protect.

A blurred region shows a hairline edge on the canvas and nothing else. The strength is in the band whenever the region is selected, and a permanent readout painted over the picture is noise on every other glance.

A numbered marker is a disc with a number in it, and the number is an ordinary field rather than a position in a sequence. New markers take the next number up so a walkthrough numbers itself, but nothing stops two of them saying the same thing, because a picture with two separate first steps is a real thing to want. The number takes whichever of black or white reads on the disc rather than being another colour to choose and get wrong.

Text can sit on a plate. No single ink colour is legible over a photograph or a gradient, so a background is the only thing that reliably makes text usable on a screenshot.

Annotations that are defined by a rectangle share a `RectAnnotation` base, so the canvas moves and resizes a blur or a box without knowing which it has. Ellipse, highlight and step numbers would all fit the same way.

## Resizing the canvas

The canvas is the rectangle that gets exported, and it does not have to match the capture. Dragging an edge in crops the picture, dragging one out adds space that is transparent, and dragging the middle aims the canvas at the part worth keeping. Nothing touches `original.png`: a crop is geometry, so an edge pulled in can be pulled back out and the pixels are still there, which is the same promise the annotations get.

Coordinates stay measured from the capture rather than from the canvas. That is what makes resizing cheap: every annotation is positioned against the picture it was drawn on, so moving the canvas moves nothing else, and cropping never rewrites a document to say where everything is now. Annotations that fall outside the canvas are clipped rather than deleted, on the editing canvas exactly as in the export.

**It is a mode, and while it lasts the editor shows more than the canvas.** This is the whole of the idea. A canvas clipped to itself gives no way to see what an edge is about to cut away, so the mode lays the picture out on a working surface larger than both the canvas and the capture, dims everything outside the canvas rather than hiding it, and leaves a margin around the pair to drag outward into. What is being cropped stays on screen, greyed, until it is actually cropped.

The boundary is drawn as hairlines with the thirds marked inside it, not as the heavy two-toned outline a selected annotation gets. A thick line over the boundary hides the very pixels being decided about, and the dimmed surround already says which side of the line is which.

Nothing reaches the document until the resize is applied, so the whole negotiation is one undo step or none, and a resize abandoned costs nothing. Applying and abandoning are offered on a small bar under the canvas, which is the one place in this window where anything floats over the picture: the band is right for settings, which are always there, while this is a question with two answers, asked only while the mode is open and answered where the eye already is. Enter and Escape answer it too.

The scale is frozen for the length of the mode and the working surface only ever grows, so the picture never moves while an edge is being dragged. A surface that refits as the canvas grows would take the picture out from under the pointer that is sizing it, and the drag would chase its own tail. When the surface does have to grow, because the canvas was dragged clean out of it, the window places it by hand so that the capture stays exactly where it is on screen.

Transparency is drawn as a chequerboard on the editing canvas and as nothing at all in an export, which is the same split as a blurred region's hairline edge. The affordance belongs to editing; the picture is the picture. JPEG has no alpha, so what would have been transparent is filled with white on the way out rather than arriving black.

Blur strength is stored 1 to 100 and squared into a gaussian sigma, not stored as sigma. Sigma is only interesting between roughly 0.5 and 8, so a linear slider spends its bottom on invisible changes and its top on a region that is already flat grey. Squaring puts fine control where small differences are visible and still reaches a full redaction at the end.

A blurred region carries a hairline edge and a level caption on the canvas but not in an export. Both say where the object is and how hard it is blurred, which is editing chrome: an exported screenshot must not come out with a label printed across the thing being hidden. This is the one place where canvas and export deliberately differ, and it is limited to affordances, never to the picture.

Anything already drawn can be selected whatever tool is active. Requiring a switch to the select tool before touching an existing arrow is the kind of friction that makes an editor feel stiff, and the drawing tools lose nothing: a new annotation starts from empty canvas, which is where you would start one anyway.

## Where things live

| Path | Contents |
|---|---|
| `~/Pictures/snapshotkit/` | Exports. Images the user deliberately kept, and the only directory they are expected to browse. |
| `~/.local/share/snapshotkit/snapshots/` | `.ssk` working documents. |
| `~/.local/state/snapshotkit/` | Restore token, keybinding backup, and eventually the library index. |
| `$XDG_RUNTIME_DIR/snapshotkit/` | The shared frame. tmpfs, so RAM, cleared on logout. |

Snapshots sit in `XDG_DATA_HOME` rather than Pictures because they are application data, not photographs, and a folder full of them would bury the images the user actually wants. They are not cache either: a snapshot cannot be regenerated, so losing one loses work. Keeping them in an ordinary folder means anyone can open it and delete from it, and backups already cover it.

The consequence is that they are out of sight, which is right for files you should rarely think about and wrong if there is no way to see them at all. `snapshotkit snapshots` lists them from a terminal, and the editor's library window shows them as a grid grouped by day, with what is drawn on each one read from its document in the background.

## The design system

The interface follows the Industry design system, whose handoff and token sheet live in `docs/design/`. Its tokens, the registration-mark frame every framed object wears, and the Lucide glyphs live in `SnapShotKit.Ui`, which the editor and the overlay both reference. They are separate processes and would otherwise drift apart a shade at a time.

Three deliberate departures from the handoff, each for a reason worth keeping:

- **Annotations default to red, not the steel accent.** The single-accent rule governs the application's own surfaces. An annotation is a mark on somebody else's screenshot: it has to read as deliberate against arbitrary pixels underneath, and red is the convention every reader of a screenshot already knows.
- **Eight resize handles, not six.** The design's corners and top/bottom mid-points leave no way to change one horizontal edge without also moving a corner. The two extra handles resize width alone.
- **Arrows are straight.** The mock draws a curved arrow. Curvature needs a third control point in the document and a way to drag it, which is a feature rather than a finish, so it is not built.

Searching and tagging in the library are deferred rather than dropped. A search worth having looks inside documents for text drawn on a capture, which wants an index rather than several hundred archives opened per keystroke.

The library index, when it exists, is a SQLite cache for search and thumbnails, rebuildable from the files and never the source of truth.
