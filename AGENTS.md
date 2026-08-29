# SnapShotKit

A screenshot and annotation tool for Linux, in the spirit of Snagit. Press Print, the whole screen is captured, an overlay lets you keep all of it or drag out a region, and the result opens in an editor. Annotations are stored as objects rather than baked into pixels, and captures live in a searchable library.

Target platform is Fedora on GNOME Wayland. C# on .NET 10.

## Layout

```
src/           all code, including snapshotkit.slnx
docs/          all documentation
docs/spikes/   findings from throwaway experiments, kept because the findings are not throwaway
```

## Build and run

The managed solution builds on its own. The native shim needs headers, which are build-time only: users of a packaged SnapShotKit need `pipewire-libs`, which every PipeWire system already has.

```bash
sudo dnf install pipewire-devel
```

```bash
./src/native/snapshotkit-capture/build.sh && dotnet build src/snapshotkit.slnx
```

Then hand Print over and start the daemon. `setup` does both, because a keybinding without a running daemon is a shortcut that fails:

```bash
./src/SnapShotKit.Cli/bin/Debug/net10.0/snapshotkit setup
```

```bash
./src/SnapShotKit.Cli/bin/Debug/net10.0/snapshotkit setup --revert
```

The editor opens a snapshot, and can render one without a window:

```bash
./src/SnapShotKit.Editor/bin/Debug/net10.0/snapshotkit-editor ~/Pictures/snapshotkit/snapshot-01.ssk
```

```bash
./src/SnapShotKit.Editor/bin/Debug/net10.0/snapshotkit-editor snapshot-01.ssk --export out.png
```

```bash
dotnet run --project src/SnapShotKit.Spike.PortalCapture -- --iterations 5 --out ./spike-output
```

```bash
dotnet run --project src/SnapShotKit.Spike.ScreenCast -- --frame
```

Set `SNAPSHOTKIT_TRACE=1` on any SnapShotKit process to get stage-by-stage D-Bus tracing on stderr.

## Read first

[docs/architecture.md](docs/architecture.md) explains why the design looks the way it does. Most of it follows from Wayland constraints rather than preference, so changing the shape usually means one of those constraints was misunderstood.

[docs/spikes/001-portal-capture.md](docs/spikes/001-portal-capture.md) and [docs/spikes/002-screencast-capture.md](docs/spikes/002-screencast-capture.md) record how capture actually behaves on this platform, including several D-Bus bugs that each produced a silent hang or a lost grant rather than an error.

## Three processes, on purpose

| Process | Role |
|---|---|
| `snapshotkitd` | Owns the portal session, the library, and the D-Bus name. Headless. |
| `snapshotkit-capture` | Owns libpipewire. Answers `grab` over a pipe, writes frames into a shared file in `XDG_RUNTIME_DIR`. |
| `snapshotkit-overlay` | Avalonia. Spawned per capture, reports the chosen region on stdout, exits. |
| `snapshotkit` | Thin AOT client. Turns a keypress into a D-Bus call. |
| `snapshotkit-editor` | Avalonia. Opens a `.ssk` snapshot for annotation, or the library when given none. Standalone, not part of the capture path. |

The splits are not stylistic. Capture is separate because libpipewire cannot be driven from inside the .NET process; the overlay is separate because a resident Avalonia costs 98 MB and never gives it back.

## Capture runs in its own process

`src/native/snapshotkit-capture` owns libpipewire and answers frame requests from the daemon over a pipe, returning frames through a shared file in `XDG_RUNTIME_DIR`. The daemon does not link libpipewire at all.

This is not a stylistic choice. In-process, a PipeWire stream driven from .NET reaches STREAMING and then never receives a single buffer, with no error reported anywhere; out of process it works every time. The root cause is unknown. Do not move capture back into the daemon without reading [docs/spikes/005-thread-pool-capture-failure.md](docs/spikes/005-thread-pool-capture-failure.md), which lists everything already ruled out.

## The shell extension is not only about the hotkey

`src/extension/` holds a GNOME Shell extension that does two things nothing outside the shell can. It registers the capture shortcut with `Shell.ActionMode.POPUP`, which is the only way a shortcut fires while a shell menu holds a keyboard grab, and it puts a panel menu in the top bar offering a delayed capture, the editor and the snapshots folder.

Everything the menu does is a D-Bus call to the daemon. The extension knows no paths and launches no processes: it runs inside the compositor, where a mistake takes the desktop down with it, and the daemon already knows where things live in a way that survives being packaged. Add a menu item by adding a daemon method, not by spawning from JavaScript.

GNOME Shell caches extension modules, and on Wayland the shell cannot be restarted without ending the session, so changes to `extension.js` only take effect after a log out and back in. Disabling and re-enabling is not enough.

## The interface follows a design system

The three windows are built to the Industry design system. Its handoff, token sheet and the design file live in `docs/design/`, and `docs/architecture.md` records where the implementation departs from it and why.

Take colours, spacing, type and radius from `SnapShotKit.Ui.Tokens`, never inline. Every framed object — window, canvas, thumbnail, dropdown, primary button — wears the `Blueprint` frame with its corner registration marks; the design system is explicit that these are not optional. Icons are Lucide at stroke width 1.5, in `Lucide`, drawn as geometry rather than shipped as images. Radius is zero on everything except the interiors of icons.

`SnapShotKit.Ui` exists so the editor and the overlay cannot drift apart. They are separate processes; anything either of them draws that the other might also draw belongs there.

Two names avoid collisions rather than being awkward for their own sake: `Tokens` because every Avalonia control inherits a `Theme` property, and `WaylandClipboard` because every window inherits a `Clipboard` one.

Do not reach for a stock Fluent control where the design specifies a shape. Its buttons repaint themselves translucent on hover, its slider thumb is a blue circle and its colour picker is a stack of rounded pills, and each of those wins over anything set on the control itself. `Buttons.Bare`, `TextFields.Bare`, `Slide` and `ColourPicker` exist because of that, and a new control that needs a state the theme also styles should follow them rather than fight it.

The same theme also claims keys. A text box with `AcceptsReturn` marks Enter handled as it inserts a line break, so a handler added with `+=` never sees it; the in-place text editor tunnels its key handler to get in first. Check for this whenever a key seems to do nothing.

## Where things are written

`~/Pictures/snapshotkit/` is for exports only, and nothing else may write there: it is the one directory the user browses. `.ssk` working documents go to `~/.local/share/snapshotkit/snapshots/`, since they are application data rather than pictures. `SnapShotKitPaths` is the only place these are decided.

## Platform rules that are easy to get wrong

- `org.gnome.Shell.Screenshot` is closed to third-party callers by a sender whitelist. It introspects fine and then returns `AccessDenied`. Use the XDG portal.
- The Screenshot portal writes captures into `~/Pictures` with an incrementing filename. The caller owns the file and must move or delete it, or every capture litters the user's Pictures folder.
- Mutter does not implement `wlr-layer-shell`, so nothing can be drawn over the live desktop. The overlay always works on an already-captured bitmap.
- Wayland exposes no window geometry to clients. Anything needing window rectangles requires a GNOME Shell extension.
- A PipeWire client must answer format negotiation with `pw_stream_update_params`, declaring `SPA_PARAM_Buffers` with `SPA_DATA_MemPtr`. A client that stays silent gets a stream that reaches STREAMING and receives nothing, with no error.
- The descriptor from `OpenPipeWireRemote` is duplicated before use. The D-Bus message still owns the original, and `dup` also clears close-on-exec so the helper inherits it.
- The ScreenCast portal only returns a `restore_token` if the user ticked the remember box in the consent dialog. Without it `Start` still succeeds and simply omits the key, and the next run prompts again. Read the token before any parsing that can throw, or a granted consent is thrown away.

## Avalonia rules that are easy to get wrong

None of these fail at the compiler. Each one showed up as a window doing something strange, or as a crash a long way from its cause.

- **A control's desired size becomes a size the window has to satisfy.** Anything placed by margin or alignment hands its extent up the tree, and a window whose content asks for more room than it has grows to fit. Anything meant to float over the picture therefore belongs on a `Canvas` layer, which asks for no size of its own however far out its children are put. The symptom is a window that resizes itself, or `InvalidOperationException: Infinite layout loop detected` when the growth moves the very thing that caused it. The editor window still does this to itself about eleven seconds after opening, with nothing clicked: it jumps from 1180x740 to 2866x1371, and 2812 of that is about what the recent strip's tiles come to laid out end to end. That one is not fixed.
- **`Canvas.GetLeft` and `Canvas.GetTop` return `NaN` until they are set**, and every comparison against `NaN` is false, including the one meant to notice that the value was missing. A guard of the form `Math.Abs(current - wanted) > 0.5` therefore never fires the first time, and the control sits in the corner looking like a layout bug.
- **A `Popup` moved to a new parent stops opening**, and says nothing about it. Build the panel that holds one once, in the constructor, and rebuild whatever is beside it instead.
- **`Bounds` inside `ArrangeOverride` holds the previous pass's answer**, and on the first pass holds nothing at all. Measure against the size being arranged.
- **`LayoutUpdated` fires after every layout pass, on every control.** It is the right hook for keeping something pinned to a control that moves, but whatever it sets must be assigned only when the value has actually changed, or the layout never settles.
- **`ScrollViewer` reads a wheel as a scroll and marks it handled**, so a handler added with `+=` never runs. Tunnel to get in ahead of it, and leave the modifiers it still needs alone.
- **Releasing pointer capture with `Capture(null)` reports capture lost synchronously.** Anything that ends a drag has to be safe to call twice, and in either order.
- **The stock Fluent theme wins over properties set on the control**, because it sets them on the template's presenter instead. That is what `Buttons.Bare`, `TextFields.Bare`, `Slide` and `ColourPicker` exist for; see the design system section above.

## Checking the interface

The editor draws through XWayland, so its window can be found and photographed from a script:

```bash
WID=$(xdotool search --all --pid $PID --name "SnapShotKit" | head -1)
import -window "$WID" shot.png
```

`--all` is not optional. `xdotool search` ORs its criteria without it, so a search by process and name will cheerfully hand back somebody else's window, and the screenshot will look like a bug that is not there.

Input cannot be synthesised at all. `xdotool key` and `xdotool mousemove` are ignored under GNOME Wayland: the pointer does not move and the key never arrives, silently. To check a gesture, drive the handler it ends in behind a temporary environment variable and a dispatcher timer, photograph the result, and take the scaffolding out again.

Anything that ends up in the picture rather than around it is cheaper to check through `--export`, which renders without showing a window and can be compared pixel for pixel against a known-good file.

## Tmds.DBus.Protocol rules that are easy to get wrong

- One D-Bus connection per process. A second one is enough to break PipeWire capture, and opening it before PipeWire makes libpipewire abort outright.


Each of these fails silently, with a hang rather than an exception.

- `MessageWriter` is a mutable ref struct. Pass it by `ref` into any helper that writes a message body, or the writes land in a copy and an empty message goes out. A `using` declaration cannot be passed by `ref`, so use an explicit try/finally.
- Check `Notification<T>.IsCompletion` before reading `Notification<T>.Exception`. Reading it on a value notification throws inside the observer callback and tears the observer down, after which the subscription silently stops delivering.
- Portal calls return a request object path and deliver the real result later as a `Response` signal. Subscribe to the predicted path before making the call, and assert the returned path matches, so a convention change fails loudly instead of hanging.

## Conventions

- Follow SemVer. Nothing is tagged yet.
- Never commit to `main`. Work on a feature branch.
- The `.slnx` solution format is used rather than `.sln`.
- `global.json` pins .NET 10 with `rollForward: latestFeature`.
