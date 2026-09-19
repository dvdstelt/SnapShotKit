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

## Picking a window

Wayland tells a client nothing about any window but its own, so the only thing that knows where the windows are is the compositor. The shell extension answers that one question. It exports `org.snapshotkit.Shell` at `/org/snapshotkit/Shell` on the shell's own bus connection, since an extension has no bus name of its own, with a single method, `Windows`, returning the frame rectangles of the windows showing on the current workspace, topmost first, and the monitors, primary first.

**It answers only the daemon.** GNOME closed its own window introspection to outside callers because where the windows are is nobody's business but the user's, and an extension handing the same thing to anything on the session bus would be reopening it. The extension watches the daemon's bus name and refuses any caller that does not own it. The watch is asynchronous on purpose: a blocking bus call made from inside the compositor stalls the desktop while it waits.

The daemon asks straight after taking the frame, so the rectangles describe the same moment the picture does, and gives the shell 300 ms: the overlay is waiting behind the answer, so a shell busy with something else costs the window picking rather than the capture. The shell measures in logical pixels across every monitor and the frame is one monitor in device pixels. Which monitor is not something the capture stream says, so it is taken to be the first whose shape matches the frame, and the scale follows from the widths, which covers fractional scaling without anybody saying what the factor is.

In the overlay a window is a region somebody else has already drawn. Pointing at one lights it up and a click takes it, and from then on it is an ordinary selection with grips and the same actions. There is no window mode to be in, and without the extension nothing changes: the rectangles are simply not offered.

## Scrolling capture

A region can be followed while it is scrolled, and what passes through it joined into one picture. The user does the scrolling because nothing else can: Wayland gives a client no way to send a wheel event to another client's window, and the portal that can ask for that asks for control of the whole desktop's input, which is a great deal to grant a screenshot tool for the sake of not turning a wheel.

**Nothing is on screen while it runs.** Nothing can be drawn over the live desktop here, and the one thing that could say "recording", a notification, slides down over the top of the screen and would be captured with the page. So the overlay says what is about to happen before it closes, and the end is either asked for or noticed. Asked for is the capture key again: a scrolling capture holds the capture gate for as long as it runs, so a capture requested meanwhile is not a second one waiting its turn, it is the end of the first. Noticed is a page that was scrolled and has then sat still for three and a half seconds. It needs the fast path, since following a scroll with portal screenshots at most of a second each is not following it, and the first frame is the one the overlay was shown, the only frame certain not to have the overlay in it.

**Joining is a matter of finding how far the content moved between two frames.** `ScrollStitcher` boils every row down to the brightness of sixteen stretches across it and tries every offset on those, which is cheap, then checks the best few against the real pixels, because two different rows of text can average out alike and a join made on a false match puts a tear through the picture that cannot be taken out. Among offsets that fit equally the nearest wins: a table lines up at every multiple of its row height, frames come often enough that the true answer is the small one, and a larger one would drop the rows between.

Three things on a page do not scroll with it. A header or footer that stays put is found as the rows at the top and bottom that did not change while the middle did, and the match is made on the band between; the footer is kept off the picture until the end, where it goes once. The scrollbar moves the wrong way at the wrong speed, so the right-hand edge is left out of every comparison. The pointer sits wherever it was left, which is what the tolerance in the pixel check is for. An overlap of plain background says nothing about how far it moved, so a match found only there is not believed.

**How often it looks decides how fast the page may be scrolled**, since two looks have to share something to be lined up. The search gives up on an offset as soon as it has cost more than a fit is allowed to, which nearly every offset does within a few rows; walking the whole overlap for each was most of the cost, and on a large region it was the difference between ten looks a second and three, at which an ordinary scroll moves further between looks than the frames overlap. The log line for each capture says how many looks a second it managed.

**The bottom of the page is where a capture is most likely to fall short, so the end is handled on purpose.** The capture key is pressed right behind the wheel, while a smooth scroll is still running on, so one more look is taken a third of a second after it. And whatever was on screen at the end goes in whether or not it could be placed: a last hard flick is the one most likely to carry the page further than could be followed, and a frame put underneath with a seam may repeat a little, where one left out is the bottom of the page missing.

A frame that cannot be placed is left out and the next is tried against the same one. After about a second and a half of those the frame is put underneath regardless, with a seam, because the alternative is losing everything scrolled from then on; that is allowed once until a scroll has been followed properly again, or a region with a film playing in it would be pasted under itself for as long as it ran. A frame that is half or more exactly where it was is a page standing still with something going on in it, not one that has scrolled out of reach. The picture stops at 20,000 rows. The stitcher knows nothing about where frames come from and has no clock, which is what lets it be tried on made-up pages.

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
├── document.json     the canvas rectangle, the bands cut out, and the layers: pictures and annotations
├── original.png      the capture as taken, never modified, and absent once deleted or from a snapshot started blank
├── images/           pictures pasted in, each named after a hash of its bytes, never modified
└── meta.json         when it was taken, the source screen size, the region within it
```

The point of the format is that editing stays non-destructive. `original.png` is never touched, and every arrow, callout or blur is an object with coordinates rather than pixels burned into the image, so anything drawn today can be moved or deleted next week. Exporting to PNG, JPEG or WebP renders the document rather than being the document.

The canvas is recorded as a rectangle rather than a size, because it is not obliged to match the capture: it carries an offset saying where its top-left corner sits relative to the capture's. A document written before that existed has no offset, and zero is exactly what it meant.

Snapshots are numbered, `snapshot-01.ssk` upwards, because they are working documents a person refers to by name. Straight captures that skip the editor are timestamped instead, since nobody refers to those by number.

## A picture that was not captured here

There are two ways in, depending on where the picture is. One on disk is opened with **Open image**, and becomes a snapshot with that picture as its capture. One on the clipboard goes onto a blank canvas: **New** is an empty canvas, and the first picture pasted onto it decides its size. Ctrl+V with nothing open, or **New from clipboard** in the library, does both at once. New used to take a capture, which Print and the panel menu already do; a second way to do the same thing had left no way at all to start from a picture somebody already had.

A blank snapshot has no capture at all, and is no less a snapshot for it: a document, pictures under `images/`, and no `original.png`. Anything that used to reach for the capture asks instead what an empty canvas measures, which is the capture where there is one and 800 by 600 where there is not. It is not written anywhere until it is saved, so a blank opened and abandoned leaves no empty `untitled.ssk` behind; its name is chosen when it is made, and the first save takes the next free one if another blank has claimed it in the meantime.

A screenshot somebody sent, a diagram exported from something else, a photograph: any of these can be annotated, and none of them arrive as a snapshot. Opening one wraps it in a new `.ssk` beside the captures, and everything after that is the ordinary path. The alternative, an editor that sometimes has a document and sometimes only a picture, would mean every command asking which kind it was looking at; there is one kind of open document, and this is what makes that true.

Opening the same file a second time goes back to the snapshot made the first time rather than making `diagram-2.ssk`. Somebody opening `diagram.png` from the file manager again is going back to their diagram, arrows and all, and a bare copy would both hide that work and leave another snapshot behind every time. The import records where the file was and a hash of what it held, in `meta.json`, and the same path with the same contents is the same picture; a file changed since is a different one and is brought in afresh. Only snapshots still carrying the name the import gave them are looked at, so one renamed since costs an extra import and nothing more, and imports from before the hash was recorded are not recognised.

The one picture that is not kept is one being converted: `snapshotkit-editor picture.jpg --export picture.webp` wraps it in a snapshot in memory, renders that and exits, and nothing is written to the library. It is still a snapshot as far as the export is concerned, which is what keeps that the ordinary path too, but a script run a hundred times should not leave a hundred snapshots nobody will open.

On the way in a JPEG is turned the way its EXIF orientation says, since a PNG is drawn as its pixels lie and the turn has to be made in them once; and an animation or a multi-page file is decoded as its first frame only, because the PNG encoder writes every frame it is handed.

The file the user pointed at is only read. Annotations have nowhere to live inside a PNG, so editing one in place would mean either flattening the drawing into somebody's original or writing a sidecar next to it, and both are things a screenshot tool should not do to a folder it was invited into once.

A PNG keeps its bytes exactly as they were, since it is already in the format the container holds and re-encoding it could only lose a colour profile or a bit depth for nothing. Anything else is decoded and written as PNG once, on the way in, and never again. What decides is what the file turns out to be rather than what it is called: a JPEG named `.png` is common enough, and passing its bytes through unread would leave a JPEG in an entry called `original.png`.

The snapshot is named after the file it came from rather than given the next capture number, because an imported picture already has a name its owner chose and `diagram.ssk` beside `diagram.png` is the connection numbering would throw away. Colliding names take a suffix.

Reaching it is deliberately two ways round. **File ▸ Open image** is for somebody already in the editor; the desktop entry lists the image types it decodes, so the file manager offers SnapShotKit under **Open With** for somebody who is not. Listing the types there is what puts it in that menu, and it does not make SnapShotKit the default handler for images: that stays whatever the user has already chosen.

## The editor

`snapshotkit-editor` is a standalone tool. Editing a snapshot has nothing to do with taking one, and the two are wanted at different times, so the editor is never on the capture path and the daemon merely launches it and forgets about it.

It draws through the same renderer used for export, so what lands in the exported file is what was on screen. Two rendering paths would drift.

Blur is done by keeping a blurred copy of a whole picture per radius in use. A blur region is then every picture beneath it drawn again from its blurred copy, placed and mirrored exactly as the picture is and clipped to the region, which costs the same as drawing any other bitmap; the alternative is a gaussian blur on every repaint. It is every picture beneath rather than the capture alone because the capture can be moved and others pasted beside it, and a blur has to hide whatever it is actually lying over.

Annotations are drawn in the order they are in, and that order can be changed: forward, backward, to the front, to the back. An earlier version drew every blur first regardless of order, so that a blur could never hide an arrow, but a rule like that quietly overrides the choice the user is now able to make. The default is kept instead by where a new blur is filed rather than by how it is drawn, and documents written under the old rule are reordered as they are opened so they still look exactly as they did. That is what the format version is for.

The window has a menu bar, the drawing tools across the top, the capture on a mat with a sidebar of styles and settings down its right, and the recent captures and a status line along the bottom, with the canvas size and the zoom at the status line's end. Each tool is named under its icon, because a row of icons is quick once learned and slow to learn. Nothing floats over the picture. Commands live in the menus and settings live in the sidebar, so saving and exporting do not spend permanent screen space on things pressed once at the end of a session.

**The settings are in a sidebar, which the design handoff ruled out.** An earlier version kept them in one band with the tools, following the handoff's "no right-hand properties panel", and it ran out of width: every setting a tool gained came out of the room the other tools' settings had, and a band that scrolls sideways hides exactly the control somebody is looking for. A sidebar grows downward, where there is room to spare, and it is where Snagit keeps the same things. It costs width the picture could have had, which is the real price, and it stops above the recent captures so those keep the whole width of the window.

What the sidebar shows follows the selection when there is one and the tool otherwise: selecting an object is a statement about what you mean to work on, whichever tool is in hand. It shows only the settings that apply, but always in the same order: colour always above weight. The hand learns each position once. A tool with nothing to set says so rather than leaving an empty column that looks as if something failed to load. A selection overrides the tool when deciding what to show, because acting on a selected object is what the user is doing.

The sidebar leads with ready-made looks, one click each, and the settings under them are what refine them. A look is the unit anyone actually works in: a red box with no fill, or white words on a black plate, is one decision, and reaching either through three controls in a row is three decisions where one was meant. Colour is not what they are for, since the palette beside them already changes a colour in one click; a style earns its place by combining things, and is offered in the few colours that combination is wanted in.

All of a tool's styles are on show, as a grid. When they shared a band with the settings only the three used last fitted, with the rest in a gallery a click behind and the three remembered between sessions; with the room to show them all, a choice made on sight beats one that has to be remembered to exist.

The styles are prototype annotations rather than a table of values, and their previews are drawn by the renderer the canvas and the export use, so a preview cannot promise something the tool then does not do. A style is a complete look and sets everything it covers, including turning a fill or a plate off; changing any setting afterwards unmarks it, since the honest answer to "which of these am I wearing" is then none of them. Blur has no styles, because a style is a combination and blur has one thing to set.

Settings lead with presets rather than sliders. The values worth having are mostly discrete, the choice is visible without being dragged, and a click is naturally one undo step where a drag is a hundred. Every setting still reaches any value: colours through a picker, numbers through a slider and a box behind a trailing segment. That segment shows the value whenever it is off the preset scale, so a custom choice never disappears from the sidebar.

The colour picker and the slider are drawn rather than taken from the toolkit. Avalonia's colour picker is a set of rounded pill controls and its slider thumb is a filled circle in the toolkit's own blue, both of which read as foreign objects in a design whose grammar is square and hairline. The same applies to buttons: they carry a template that binds the content presenter to the button and nothing else, because the stock theme repaints the presenter on hover with translucent brushes that win over whatever the button itself carries.

Text is typed on the picture rather than in a field somewhere else. Placing it opens a text box positioned and styled to match exactly what the renderer would draw, and the annotation underneath is left undrawn while that box is open, so the words never appear twice and committing never moves them. Enter finishes, Shift and Enter make a new line, Escape puts the words back as they were. Text left empty is dropped rather than left invisible on the canvas.

While it is open the words sit on a plate, backed light for dark text and dark for light text, with a two-toned outline around it. Typing over a screenshot means typing over anything at all, and against a busy photograph neither a thin box nor a one pixel caret can be found; the plate guarantees contrast for both. The caret and the selection take their colours from it for the same reason. All of it is chrome and goes the moment the edit is finished.

Selection outlines are two-toned everywhere for the same reason: a dark line under a light one, which is the trick the capture overlay already uses to stay visible over an unknown desktop.

It is a real text box rather than a caret painted by hand: editing text means selection, arrow keys, home and end, backspace across a line break, the clipboard and input methods, and reimplementing that on a drawing surface produces a worse version of what the toolkit already has.

Unsaved changes are a three-way question: keep editing, discard, or save. The honest answer is usually the third, and a dialog offering only the first two leaves the user to dismiss it and save by hand. Saving carries on only if it actually succeeded, so a failed write cannot lose the work it was meant to protect.

A blurred region shows a hairline edge on the canvas and nothing else. The strength is in the sidebar whenever the region is selected, and a permanent readout painted over the picture is noise on every other glance.

A numbered marker is a disc with a number in it, and the number is an ordinary field rather than a position in a sequence. New markers take the next number up so a walkthrough numbers itself, but nothing stops two of them saying the same thing, because a picture with two separate first steps is a real thing to want. The number takes whichever of black or white reads on the disc rather than being another colour to choose and get wrong.

Text can sit on a plate. No single ink colour is legible over a photograph or a gradient, so a background is the only thing that reliably makes text usable on a screenshot.

Annotations that are defined by a rectangle share a `RectAnnotation` base, so the canvas moves and resizes a blur or a box without knowing which it has. Ellipse, highlight and step numbers would all fit the same way.

## Tools that are settings

A line, an ellipse, a highlighter and the two stronger ways of hiding something are not tools of their own. A line is an arrow with no heads, an ellipse is a box with a round outline, a highlighter is a box with no border and a fill that shows through, and squares or a solid bar are what a blurred region can be told to use instead of a blur. Each is a setting on the object it is a kind of, and a style in that tool's grid.

That is less to learn and less to build, but the reason is that it is what they are. A line is dragged out, picked up, moved, restyled and nudged exactly as an arrow is, and somebody who drew an arrow and wanted a line should be able to say so without drawing it again. A tool per shape would have made that a delete and a redraw, and would have given six near-identical buttons to a toolbar that names every tool under its icon. The new properties are additive, with defaults that mean what documents meant before they existed, so the format version does not move.

**Hiding has three strengths because a blur is the weakest.** A light gaussian over text set in a known typeface can be worked backwards, and passwords have been read out of screenshots that way. Squares throw the detail away instead of smearing it; they are made by averaging the picture down and blowing it back up, because ImageSharp's own pixelate takes each square from the single pixel at its middle, which on black text on white is nearly always white, so the text vanishes instead of turning into squares. A solid bar takes nothing from what is under it, so nothing can be got back out, and the tooltip says to use it for anything that must not be read.

**The spotlight is a tool, because it is not a kind of anything else.** It leaves a region at full brightness and dims the rest of the canvas, which suits the screenshot where the thing to look at is a whole panel and an outline would be one more rectangle among the dozen the interface already has. Every spotlight on a picture shares one dimming, drawn where the first of them stands in the stack, with a hole cut for each and as dark as the darkest of them asks: two spotlights are two things to look at, not the second dimming the first. Anything stacked above them stays bright, so an arrow pointing into a spotlight is not dimmed along with the background. Its layer type is new, so a build from before it cannot open a document that has one.

**A callout is text with a tail**, a setting on the text tool for the same reason a line is a setting on the arrow: it is typed, sized, coloured and backed like any text, and a note that turns out to need pointing at something should not have to be typed again. The tail is a triangle from the middle of the plate to the tip, with the plate painted over it afterwards, so only the part outside shows and there is no working out which edge it leaves by: it is the same in every direction. It is drawn in the plate's colour, so asking for a tail asks for a plate. The tip is the text's one handle, and it stays put when the words are moved, because what it points at has not moved. It is kept out of styles, since a style has nowhere for a tail to point.

**The pen and the lens are tools.** The pen keeps the points the pointer passed through rather than a picture of them, so a drawing can be moved, nudged, recoloured and made thicker afterwards. It is not resized: a drawing stretched by a corner is a different drawing. The lens magnifies the pictures under its own middle, in place, the way a glass held over a page does. It draws them again through the ordinary picture routine with the origin moved so that the point under its middle stays still while everything grows away from it, which means a flipped or stretched picture is magnified flipped and stretched without the lens knowing, and the result is as sharp as the capture because it comes from the capture and not from a copy taken when the lens was made. Like a blur it shows pictures and not what is drawn on them.

**The eyedropper takes a colour off the picture**, on I or from under the swatches, for matching an annotation to the application it is drawn over. One pixel is rendered for it through the same renderer as everything else, rather than looked up in the capture's bitmap: what somebody points at is what they can see, which may be a pasted picture, a filled box or the blurred version of either. It ends in the same place a click on a swatch does, so the colour goes to the selection or to the tool in hand, and one click ends it whether or not there was a colour there to take.

The arrow keys nudge the selection a pixel at a time, ten with shift. A run of presses is one undo step, since a held key repeats thirty times a second and thirty steps to take back one movement would make undo useless for whatever came before. A picture nudged takes its cuts and the canvas along, as one dragged does.

## Pictures on the canvas

The capture is a layer. It used to be a backdrop painted before everything else, which made it the one thing on the canvas nothing could touch, and pasting a second picture beside it would then have given two kinds of picture that behaved differently. Now the capture and anything pasted are the same kind of layer: a rectangle saying where the picture stands and how large it is, which way round it faces, and the entry in the snapshot holding its pixels. The pixels are never touched, so a picture shrunk today can be put back to its actual size next week. A document from before this has its capture given a layer at the bottom of the stack, at the origin and at its own size, which is exactly where it was always drawn.

Ctrl+V pastes the picture on the clipboard, read through wl-paste so a PNG arrives as the bytes that were offered. A file copied in the file manager is on the clipboard as a path rather than pixels, and pastes as the picture it names. A pasted picture is stored under `images/` named after a hash of its bytes, so the same picture pasted twice is kept once, and a save writes only the pictures some layer still uses. That goes for the capture as well: deleted, it is left out of the file like any other picture, because somebody who deleted a capture for what was in it is not expecting to hand it over inside the `.ssk` anyway. Every picture pasted in a session is kept in memory until the document closes, because undo holds layers rather than pixels and an undone delete has to find its picture again.

Pictures are picked up with the select tool and only with it. With a drawing tool in hand the whole of the capture answering to a press would turn every arrow into a dragged screenshot. A corner keeps the proportions and shift lets it stretch; everything lands on whole pixels, because a picture placed between them is resampled and a resampled screenshot has soft text.

**The canvas follows the pictures until it is sized by hand.** Left alone it is exactly what the pictures cover: paste a large picture and it grows, shrink that picture and it shrinks back, since room nothing stands in is not something anybody asked for. It changes as a picture moves rather than when it is let go, with the scale held and the picture kept still on screen the same way a canvas edge is dragged, so the part going out stays visible.

Sizing the canvas by hand makes that size the least it will be. A picture pushed past it still takes the canvas along, and brought back in, the canvas returns to the size that was set rather than shrinking to the pictures. What was set is measured against where each picture stood at the moment it was set, which is what lets a crop survive: a canvas pulled in over the capture has the capture reaching past it on purpose, and nudging the capture a few pixels is not a change of mind about the crop. An edge a picture already reached past when the canvas was set is therefore one that picture never moves, however much further it goes: growing by the difference would bring back, a pixel for every pixel dragged, the part the crop had taken off. A picture pasted afterwards has no such standing, so the canvas grows to take it in. Fit to pictures hands the canvas back to the pictures. The rule is worked out from the document alone, in `CanvasFit`, so a drag, a paste, a delete and an undo all come to the same answer without anything remembering which of them happened. Only pictures count: an arrow or a marker near the edge leaves the canvas alone.

A document from before this whose canvas is not exactly its capture had been cropped or padded by somebody, and opens as sized by hand, so it stays as they left it.

Nothing else is positioned against a picture, with one exception given below. Coordinates are measured from where the capture's corner was when it was taken, and they stay measured from there when the capture moves, so moving or resizing a picture leaves every arrow, text and blur exactly where it was drawn. That keeps moving a picture the same cheap, predictable operation as moving anything else, at a cost worth knowing: a blur laid over something private hides whatever is under it now, not what was under it when it was drawn.

The exception is a cut. A band cut out of a picture goes with it: moved with the picture, scaled when the picture is stretched or put back to its own size, and mirrored when it is flipped. A cut takes a stretch of a picture out, often because of what was in it, and a band left behind while the picture moved on would put that stretch straight back into the export and take out some other part nobody chose. Only the bands crossing the picture follow it, and they are placed from where they were when the change began, so dragging a picture back to where it started puts its cuts back exactly as well. A band crossing two pictures follows whichever one is moved.

The stacking order has one floor: nothing drawn goes underneath every picture. While the capture was a backdrop, "send to back" could not get behind it, and now the bottom of the layer list is below it. An arrow sent there would never be seen again, and a blur sent there would have no picture under it left to blur, so the export would show what it was hiding. Whatever a move would leave below the lowest picture is lifted to just above it instead. Between two pictures is still a place to be, which is how a blur hides one picture and not the one pasted over it.

## Resizing the canvas

It is reached from the canvas size along the foot of the window, beside the zoom, rather than from a button among the tools. Resizing draws nothing; it changes the document rather than what stands on it, and in a row of drawing tools it was the one that was not one. The size is worth having on screen anyway, since it is what the file will come out as, and it says "fixed" when it was set by hand, because a canvas that has stopped shrinking to the pictures looks exactly like one that never needed to. C still opens it, and so does the Edit menu.

It stays a mode rather than grips that are always there. The canvas usually fits the pictures exactly, so the capture's own handles and the canvas's would sit on the same pixels on almost every snapshot, and the same drag would mean two things depending on what happened to be selected.

The canvas is the rectangle that gets exported, and it does not have to match the capture. Dragging an edge in crops the picture, dragging one out adds space that is transparent, and dragging the middle aims the canvas at the part worth keeping. Nothing touches `original.png`: a crop is geometry, so an edge pulled in can be pulled back out and the pixels are still there, which is the same promise the annotations get.

Coordinates stay measured from where the capture was taken rather than from the canvas. That is what makes resizing cheap: every annotation is positioned against the picture it was drawn on, so moving the canvas moves nothing else, and cropping never rewrites a document to say where everything is now. Annotations that fall outside the canvas are clipped rather than deleted, on the editing canvas exactly as in the export.

**It is a mode, and while it lasts the editor shows more than the canvas.** This is the whole of the idea. A canvas clipped to itself gives no way to see what an edge is about to cut away, so the mode lays the picture out on a working surface covering both the canvas and the capture, and dims what falls outside the canvas rather than hiding it. What is being cropped stays on screen, greyed, until it is actually cropped.

The surface is that pair and nothing more: no room is kept back around it. Opening the mode would otherwise shrink the picture to make space that is not needed yet, which reads as the editor having done something when all that happened was a tool being picked. The room appears when it is called for, which is when an edge is dragged outward, and the canvas grows into it.

The boundary is drawn as hairlines with the thirds marked inside it, not as the heavy two-toned outline a selected annotation gets. A thick line over the boundary hides the very pixels being decided about, and the dimmed surround already says which side of the line is which.

Nothing reaches the document until the resize is applied, so the whole negotiation is one undo step or none, and a resize abandoned costs nothing. Applying and abandoning are offered on a small bar that sits on the mat beside the picture, under it where there is room and above or to one side of it where there is not. It is never on the picture: the mat is the part of the window where nothing happens, which is exactly where a question about the picture belongs, and the rule that nothing floats over the capture holds here as everywhere else. The bar floats on a layer that asks for no size of its own, since anything that hands its extent up the tree becomes a size the window has to satisfy, and a bar that widened the window would move the picture and so move itself. Enter and Escape answer the question too.

The surface follows the canvas exactly, both ways. Letting it keep the largest extent a drag had reached would leave grey where the canvas has been but no longer is, which says "something was cropped here" about a place where nothing was. The picture still does not move while an edge is dragged: the scale is frozen for the length of the drag, and the window places the surface by hand so that the capture stays exactly where it is on screen whichever way the boundary is going. A surface that refits as the canvas grows would take the picture out from under the pointer that is sizing it, and the drag would chase its own tail. Letting go returns the scale to whatever shows all of it, which is the one moment where moving the picture costs nothing.

Transparency is drawn as a chequerboard on the editing canvas and as nothing at all in an export, which is the same split as a blurred region's hairline edge. The affordance belongs to editing; the picture is the picture. PNG and WebP both carry alpha and keep it unless asked not to. JPEG has none, so what would have been transparent is filled with white rather than arriving black.

Flattening happens in the renderer, by painting the ground before anything stands on it, rather than by dropping the alpha channel afterwards. The renderer is the one thing that knows how a half-covered pixel should meet what is under it, and asking it means a soft edge over the margin lands on white the way it does on screen instead of being composited a second time by hand.

Everything is written by one encoder. Avalonia writes PNG and nothing else, so the moment a second format existed there were two encoders producing files that agreed only by inspection, and they did not: the renderer hands back premultiplied pixels, which the second encoder read as straight ones. Every fully opaque pixel is identical either way, so the pictures looked perfect and only their soft edges were wrong, which is exactly the kind of fault that survives being tested. The colour is divided back out by its alpha now, on one path, and the clipboard is the single exception because it is offered no settings to get wrong.

## Asking before exporting

Export opens a dialog before the file picker, not after. The format decides the extension, and a picker told the name before anyone has said what kind of file it is can only offer to rename it afterwards; this way it arrives already filtered, already named and already pointed at a folder. The portal's own picker can carry extra controls, but they are a fixed list of combo boxes drawn in the file chooser's style, with no way to show a quality setting only when the format that has one is chosen.

What the dialog offers is what the format can actually do, and nothing else. PNG and WebP are asked whether to keep transparency; JPEG is not, because it has no answer to give. WebP is asked lossless or lossy, and the number underneath changes its meaning with that answer: lossy reads it as picture quality, lossless as how hard to work for a smaller file, where every setting returns the same pixels. It is one number rather than two so that switching between them cannot silently move the other one, and the label changes with it, because a slider that keeps its name while changing its meaning is lying about what it does.

WebP defaults to lossless, which makes it a PNG at roughly half the size rather than a smaller JPEG. Lossy is what the format is usually reached for and it is the wrong default here, for the same reason JPEG is not the default: a screenshot is text and hairlines, which is what lossy encoding smears. It stays on offer because a screenshot of a photograph is still a photograph.

Exports go to `~/Pictures/snapshotkit` unless the snapshot came from somewhere. An imported one knows the folder its picture was read from, and somebody working through a folder of images wants the results to land back in it rather than in a second folder they then have to reconcile. The choice is only offered when there is one to make: a capture came off a screen, and a screen is not a folder. The path is checked when the snapshot is opened, so a picture read off a memory stick last month does not leave the dialog offering to start somewhere that no longer exists.

Every setting is remembered between sessions, in a small file in the state directory. Exporting is rarely done once, and a dialog that opens on the defaults every time is one that has to be corrected every time. They are remembered as soon as they are chosen rather than once the file is written, so a picker closed by mistake does not also lose the format and quality just set.

The command line has no dialog and takes the format from the extension, with defaults for the rest. A flag per setting would be a lot of surface for something whose whole job is to render a file in a script. A name asking for a format not on the list is refused rather than quietly written as something else.

## Printing

Ctrl+P opens the application's own print dialog rather than the desktop's. The desktop's dialog knows about printers and nothing about the picture: it cannot show where on the sheet it will land, let it be dragged to the middle, or say how sharp it will be at the size chosen. Those are the questions somebody printing a screenshot has, so they are asked beside a drawing of the sheet that shows the answer. Going through the print portal would bring that dialog along anyway, asking for the copies, the paper and the orientation a second time, which is why the page goes straight to CUPS: `lpstat` says what printers there are, `lp` takes the job, and both are reached the way wl-paste is, as commands. `cups-client` is recommended rather than required, and without it the dialog still opens and can only save a PDF.

**The page is a PDF, one sheet of exactly the paper chosen, with the picture placed on it.** `PrintLayout` holds where the picture stands and how wide it is, in millimetres, and nothing else holds it: the preview drags it and the fields type into it, so the two cannot disagree. The picture always keeps its proportions, which leaves one size to set rather than two to keep in step. SkiaSharp, which Avalonia already ships, writes the PDF with the picture embedded losslessly at every pixel it has, so how sharp it comes out is the printer's doing rather than a resampling done here. The job is sent with `print-scaling=none`, since a driver that shrinks the page to fit its own margins undoes the placement, and a landscape page is not announced as one: the PDF is wider than it is tall and CUPS turns it to suit the sheet by itself.

The picture is rendered once, flattened onto white because paper is, and that same rendering is what the preview shows and what is printed. It starts at the size it was on screen if the page has room and fitted to the page if not. Margins are a guide rather than a wall: they are what "page width" is measured within, since almost no printer reaches the edge of the sheet, but the picture can be dragged over them, and what hangs off the sheet is drawn faint so its corner can still be found. A picture asked to fill the width goes on filling it when the paper is turned round; one placed by hand is placed afresh on a different sheet, because where it stood was a place on the old one. Dragging pulls to the middle of the page, the one spot that is wanted most and is hardest to hit by eye, and Alt lets go of it.

What is remembered is what belongs to the printer on the desk: which printer, the paper, the orientation and the margins. Where the picture stood belongs to the picture, and the number of copies is asked every time, because twenty of last week's page is not a default anybody wants.

## Cutting a band out

A screenshot of a phone or a long page often has a stretch in the middle that nobody needs: a gap, a repeated header, half a screen of nothing. Dragging down the picture with the cut tool marks a band of rows and dragging across it marks a band of columns, and what is left closes up. Which way it runs can also be said outright, because working it out from the drag is right nearly always and useless for a band a few pixels across, where the answer changes with every twitch of the hand. Left to itself it takes the first direction the drag commits to and holds it until the other is clearly meant, rather than swapping on every movement.

Letting go takes the band. Escape abandons it, and it is the only thing that does: a drag ends down two paths, the release and the loss of capture, and the two cannot be allowed to disagree about what a finished drag means.

It is geometry, not a pixel edit, for the same reason a crop is. `original.png` is never touched; the band goes into the document, and from then on the picture is drawn in pieces with that band skipped and everything after it shifted up or left by what the band took. Taking the cut back out puts the picture back exactly as it was.

That leaves two coordinate systems, and the split is what keeps the cost down. Capture coordinates are what the document is written in and what every annotation is positioned against, and they never renumber, so making a cut moves nothing that was drawn. Laid coordinates are what ends up on screen and in the file, with the bands closed. Everything drawn goes one way through the mapping and everything pointed at comes back the other.

Drawing a piece at a time is what makes a cut cost nothing anywhere else. Each piece is the whole picture drawn shifted and clipped to its own band, so a blur, an arrow or a line of text that happens to straddle a cut comes out as its two halves in the right places without any of them knowing that cuts exist. With nothing cut it is one piece with no shift, which is the same drawing as before any of this was added.

A cut that has been made leaves no mark, on the canvas or in the export. It is simply a shorter picture: a line drawn where the join is would be the editor pointing at its own work, when there is nothing wrong with the picture at that spot and nothing there to do anything about. How many cuts a snapshot has is on the status line for the times that matters.

## Zoom and panning

The wheel zooms rather than scrolls, which is the opposite of the toolkit's default and is deliberate: a screenshot at fit is the normal state, and the reason to reach for the wheel over a picture is almost always to look closer at one part of it. It zooms about the pointer, since the thing being looked at is under the pointer and should still be there afterwards, which means a scroll offset worked out after the layout has caught up rather than a scale set and left. Shift and the wheel are left to the scroll viewer, so a picture too big for the window still has a wheel gesture that pans it.

Zoom moves along a ladder rather than by a percentage a notch, for the buttons, the keys and the wheel alike. The sizes worth having are few, and landing on 100% exactly matters more than being able to reach 87%. It stops at 400%, which is close enough to aim an arrow's tip or a blur's edge at one particular pixel; past that the screen is showing magnified pixels rather than the picture.

Holding space turns whatever tool is in hand into a hand, and dragging then moves the picture rather than drawing on it. Every editor with a canvas larger than its window has this gesture, for the same reason: a scroll bar is a poor way to nudge a picture along, and a tool that has to be switched to and back is worse. The movement is measured against the window rather than against the canvas, since the canvas is being scrolled by the very movement being measured, and it is applied step by step so that a pan run into the edge of the picture and back does not have to work off a distance the picture never travelled. A window that loses focus with the key still down drops the mode, because a key held by a window that has gone away is never reported as being let go.

Blur strength is stored 1 to 100 and squared into a gaussian sigma, not stored as sigma. Sigma is only interesting between roughly 0.5 and 8, so a linear slider spends its bottom on invisible changes and its top on a region that is already flat grey. Squaring puts fine control where small differences are visible and still reaches a full redaction at the end.

A blurred region carries a hairline edge and a level caption on the canvas but not in an export. Both say where the object is and how hard it is blurred, which is editing chrome: an exported screenshot must not come out with a label printed across the thing being hidden. This is the one place where canvas and export deliberately differ, and it is limited to affordances, never to the picture.

Anything already drawn can be selected whatever tool is active. Requiring a switch to the select tool before touching an existing arrow is the kind of friction that makes an editor feel stiff, and the drawing tools lose nothing: a new annotation starts from empty canvas, which is where you would start one anyway.

## Where things live

| Path | Contents |
|---|---|
| `~/Pictures/snapshotkit/` | Exports. Images the user deliberately kept, and the only directory they are expected to browse. |
| `~/.local/share/snapshotkit/snapshots/` | `.ssk` working documents. |
| `~/.local/state/snapshotkit/` | Restore token, keybinding backup, what the editor was last doing, and eventually the library index. |
| `$XDG_RUNTIME_DIR/snapshotkit/` | The shared frame. tmpfs, so RAM, cleared on logout. |

Snapshots sit in `XDG_DATA_HOME` rather than Pictures because they are application data, not photographs, and a folder full of them would bury the images the user actually wants. They are not cache either: a snapshot cannot be regenerated, so losing one loses work. Keeping them in an ordinary folder means anyone can open it and delete from it, and backups already cover it.

The consequence is that they are out of sight, which is right for files you should rarely think about and wrong if there is no way to see them at all. `snapshotkit snapshots` lists them from a terminal, and the editor's library window shows them as a grid grouped by day, with what is drawn on each one read from its document in the background.

## The design system

The interface follows the Industry design system, whose handoff and token sheet live in `docs/design/`. Its tokens, the registration-mark frame every framed object wears, and the Lucide glyphs live in `SnapShotKit.Ui`, which the editor and the overlay both reference. They are separate processes and would otherwise drift apart a shade at a time.

Three deliberate departures from the handoff, each for a reason worth keeping:

- **Annotations default to red, not the steel accent.** The single-accent rule governs the application's own surfaces. An annotation is a mark on somebody else's screenshot: it has to read as deliberate against arbitrary pixels underneath, and red is the convention every reader of a screenshot already knows. Their palette leaves the tonal ramps behind for the same reason: it offers true black and true white, which is what a fill or a plate over a light or a dark screenshot usually wants, and then the few colours a screenshot is actually marked up in. Two swatches a few points apart from each other and from black are a choice nobody can make on sight.
- **Eight resize handles, not six.** The design's corners and top/bottom mid-points leave no way to change one horizontal edge without also moving a corner. The two extra handles resize width alone.
- **Arrows are straight.** The mock draws a curved arrow. Curvature needs a third control point in the document and a way to drag it, which is a feature rather than a finish, so it is not built.

Searching and tagging in the library are deferred rather than dropped. A search worth having looks inside documents for text drawn on a capture, which wants an index rather than several hundred archives opened per keystroke.

The library index, when it exists, is a SQLite cache for search and thumbnails, rebuildable from the files and never the source of truth.

That index is the only thing here that wants a database. What the editor remembers between sessions, which today is how the last export was written, is a small JSON file in the state directory: there is nothing in it to query and nothing relational, it is read once when a window opens and rewritten when a setting changes, and a database would buy indexes and transactions it has no use for at the price of a dependency, a schema and its migrations. Nothing in it is important enough to interrupt anyone over either, so a file that cannot be read leaves the editor with its defaults and one that cannot be written leaves the session as it was.
