# snapshotkitd design

The daemon exists for one reason: capture has to be instant, and nothing about establishing a capture pipeline is instant. Setting up a ScreenCast session costs about 100 ms, negotiating a PipeWire format costs another 70 ms, and starting a GUI process costs a few hundred more. Paying all of that at login rather than at the keypress is the entire point.

Numbers quoted here come from [spike 001](spikes/001-portal-capture.md), [spike 002](spikes/002-screencast-capture.md) and [spike 003](spikes/003-pipewire-shim.md).

## Process model

The daemon is headless. It starts at login, establishes the capture pipeline, and idles at roughly 25 to 41 MB. The overlay and editor live in a separate NativeAOT GUI process spawned per capture, which exits afterwards so its memory is fully reclaimed.

[Spike 004](spikes/004-process-model.md) settled this. A resident Avalonia process costs 98 MB before showing anything and 157 MB after a single capture, and closing the window gives back only a quarter of what the capture added. A headless daemon with a parked stream costs 41 MB, because PipeWire itself adds only 13.6 MB.

Spawning the GUI costs roughly 200 ms with NativeAOT. That lands entirely off the hot path: the frame is already captured by then, so what the user waits for is the overlay drawing, not the shutter.

An earlier version of this document argued the opposite, on the grounds that spawning per capture defeats the point of a daemon. That was wrong. The point of the daemon is the capture pipeline, which costs about 170 ms to establish and must not be on the hot path. Window creation never was.

## Ownership

The daemon owns everything with setup cost or shared state:

- the ScreenCast session and its restore token
- the capture helper process and its PipeWire stream
- the fallback Screenshot portal client
- the capture library and its index

The GUI process owns the overlay, the editor and nothing with setup cost. It receives a frame and returns a decision.

The client owns nothing. It exists only to turn a keypress into a D-Bus call.

Frames reach the GUI as a memfd file descriptor passed over D-Bus, which is zero-copy. The daemon releases its own buffer once the GUI has taken the frame, so it returns to its idle footprint rather than holding a screenshot indefinitely.

## Interface

`org.snapshotkit.Daemon` on `/org/snapshotkit/Daemon`:

| Member | Purpose |
|---|---|
| `Capture()` | Grab a frame and show the selection overlay |
| `CaptureFullScreen()` | Grab a frame and skip straight to the editor |
| `ShowLibrary()` | Open the library window |
| `Status() -> a{sv}` | Which backend is active, session health, last error |

`Status` is not decoration. When capture silently degrades to the slow path, the user needs a way to find out why, and `snapshotkit status` reading this is that way.

## Startup

1. Acquire the D-Bus name. If it is taken, another daemon is running; exit.
2. Load the restore token from `~/.local/state/snapshotkit/screencast.token`.
3. Establish the ScreenCast session. Without a token this shows a consent dialog, so first run is interactive by nature.
4. Persist any token returned. If none comes back, the user did not tick remember; record that in `Status` and plan to fall back.
5. Connect the PipeWire stream, take one throwaway frame to force format negotiation, park the stream inactive.
6. Idle.

Step 5 matters. The first grab costs about 70 ms because it pays for negotiation, and every grab after costs about 35 ms. Spending that 70 ms at login means the user never sees it.

## Capture path

This is the only latency-critical path in the product.

| Step | Budget |
|---|---|
| GNOME runs the keybinding command | - |
| Client process starts | 3 to 8 ms with NativeAOT |
| D-Bus call to the daemon | ~1 ms |
| Helper connects a stream and takes one frame | ~90 ms, worst observed 220 ms |
| **Pixels captured** | **~50 ms after the keypress** |
| Spawn the GUI process and show the overlay | ~200 ms with NativeAOT, off the critical path |

The distinction in the last two rows is the one that matters. The captured pixels are the screen as it was roughly 50 ms after the press, which is imperceptible. How long the overlay then takes to appear affects how responsive the tool feels, but not what it captured.

The client must be NativeAOT. A JIT-started .NET process costs 50 to 70 ms, which would more than double the time to capture for no benefit.

## Hotkey

GNOME will not let an application grab Print, so it has to be handed over. `snapshotkit setup` clears the shell's binding and installs a custom one:

```
org.gnome.shell.keybindings show-screenshot-ui   ->  []
org.gnome.settings-daemon.plugins.media-keys custom-keybindings  ->  append ours
```

Two rules. The existing custom keybinding list must be appended to, never replaced, and `snapshotkit setup --revert` must put both settings back. Silently eating someone else's shortcuts is not acceptable.

### The shortcut does not fire while a shell menu is open

A custom media-keys binding is handled by gnome-settings-daemon, a separate process. While GNOME Shell has a menu open, from the panel, quick settings, or an extension, it holds a keyboard grab and gnome-settings-daemon never sees the key. Nothing reaches the daemon, which is why the journal shows no capture at all rather than a failed one.

GNOME's own Print works in that situation because `org.gnome.shell.keybindings` is handled inside the shell, on the other side of the grab. That route is not open to third-party applications.

Three ways out, in increasing order of cost:

- **A delay.** `snapshotkit capture --after 5` gives you time to open the menu after pressing the key. This is what the delay exists for in every other screenshot tool, and it works today.
- **The GlobalShortcuts portal**, present here at version 1. GNOME's implementation may register with mutter directly, which would put it on the right side of the grab. Untested, and it is unclear whether it will accept Print as a trigger.
- **A GNOME Shell extension** registering the binding with `Main.wm.addKeybinding` and `Shell.ActionMode.POPUP`. That runs inside the shell, so it fires during grabs by construction. It is also what window geometry and click-to-select-a-window need, so the two wants point at the same piece of work. This is what SnapShotKit ships.

### Shipping the extension ourselves

The extension is installed by `snapshotkit setup`, not downloaded from extensions.gnome.org. An extension is only a directory under `~/.local/share/gnome-shell/extensions/<uuid>/`, so installing it is copying files, compiling its schema, and adding the uuid to `org.gnome.shell enabled-extensions`. Users get one thing to install rather than two.

There is one constraint, and it was measured rather than assumed: **a running GNOME Shell does not rescan its extension directories.** After installing, the shell listed nine extensions while ten sat on disk, and `gnome-extensions enable` reported that the extension did not exist. On X11 the shell can be restarted in place; on Wayland it cannot, so a newly installed extension stays dormant until the next login.

That is why SnapShotKit installs the settings-daemon binding as well. It works immediately, on the same key, and covers everything except an open shell menu. The extension takes the shortcut over the first time it runs, removing the settings-daemon entry itself, so the two never both fire.

## Failure modes

Every one of these degrades rather than breaks, and every one of them is visible in `Status`.

| Situation | Behaviour |
|---|---|
| User declines ScreenCast consent | Fall back to the Screenshot portal. Notify once, explaining the speed cost. |
| Consent given, remember not ticked | Same fallback. This is the likely case and must not turn into a dialog on every capture. |
| Session or node dies after suspend, idle, or a display change | Detected on grab failure. The session is rebuilt once, in about 400 ms, and the capture proceeds on the fast path. Only a failed rebuild falls back. |
| Grab times out | Fall back for that capture, mark the session suspect, re-establish in the background. |
| Daemon not running when Print is pressed | The client reports it rather than silently doing nothing. |

The Screenshot portal fallback carries its own problem: it always writes into `~/Pictures` and there is no option to stop it. The daemon moves that file into `$XDG_RUNTIME_DIR/snapshotkit/`, which is tmpfs and therefore RAM, immediately on receipt, and deletes it if the capture is discarded. The user's Pictures folder only ever gains files they deliberately saved.

## On disk

| Path | Contents |
|---|---|
| `~/Pictures/snapshotkit/` | Saved captures and exports. Nothing else writes here. |
| `~/.local/state/snapshotkit/screencast.token` | Restore token |
| `~/.local/state/snapshotkit/index.db` | Library index. A cache, rebuildable from the files. |
| `$XDG_RUNTIME_DIR/snapshotkit/` | Transient frames from the fallback path. RAM, cleared on logout. |

Nothing reaches disk on the primary path. Frames go from PipeWire into memory, cropping is a memory slice, and only an explicit save encodes anything.

## Delivery

**Phase 1, capture pipeline end to end.** Daemon skeleton, systemd user unit, D-Bus name, startup sequence, `Capture()` writing a PNG straight to `~/Pictures/snapshotkit/`, NativeAOT client, `snapshotkit setup` and `--revert`. No UI at all. Done when pressing Print produces a correct full-screen PNG and `snapshotkit status` reports the fast backend.

**Phase 2, the overlay. Built.** `snapshotkit-overlay` maps the shared frame and shows it fullscreen with a crosshair spanning the whole screen.

A drag produces a *preview*, not a capture. The box has a marching-ants border and eight grips: four corners resize both axes, four side midpoints resize one, and dragging the middle moves the whole thing. Nothing is captured until the toolbar says so.

The toolbar sits against the preview, below it when there is room and above it otherwise:

| Button | Effect |
|---|---|
| Capture | Crops and saves a PNG |
| Edit | Writes a `.ssk` snapshot for the editor |
| Redraw | Discards the preview and starts over |
| Cancel | Saves nothing |

Escape backs out one step, clearing the selection first and closing the overlay second. Enter with a selection captures it; Enter with none offers the whole screen as a preview, so it can still be nudged.

While a drag is in progress the arrow keys shift the drag point a pixel at a time, ten at a time with Shift. A mouse cannot reliably land on an exact pixel, and this places an edge without moving the hand. It is an offset applied on top of the pointer rather than an absolute position, so moving the mouse afterwards keeps the correction instead of discarding it.

The magnifier appears only while adjusting. At rest it covers the picture the user is trying to look at; the moment it earns its place is when an edge is being placed. The toolbar does the opposite and hides during a drag, since for a small selection it sits exactly where the magnifier lands.

Remaining before it is usable daily: multi-monitor correctness, remembering the last region, and click-to-select-a-window.

**Phase 3, resilience.** Session recovery is done: a stale session is rebuilt on the next capture rather than degrading permanently. Left: the user-facing notification when capture is degraded, and multi-monitor correctness. Done when suspending, changing displays, or declining consent all degrade visibly instead of breaking.

Note that the overlay is shown on every path. An earlier version skipped it whenever capture had degraded, on the grounds that the fallback returns an encoded PNG with no raw frame to map. That saved about a second and cost the user the entire selection step without saying so. The fallback now decodes into the same shared frame the fast path produces, so there is one shape of capture downstream and no path where the overlay silently disappears.

The editor, the library and the `.snapshotkit` format are deliberately outside this document. They depend on none of the above beyond receiving a bitmap and a rectangle, and scoping them now would be guessing.

## Decisions still open

**Whether GlobalShortcuts can bind Print.** Worth a spike. It would remove the client process from the hot path.

**What happens on a multi-monitor change.** The ScreenCast grant names a specific monitor, so the recovery path in phase 3 needs real testing against docking and undocking rather than assumption.
