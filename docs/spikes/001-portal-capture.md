# Spike 001: full-screen capture through the XDG Screenshot portal

Status: complete, 2026-08-26
Outcome: the Screenshot portal is viable for v1. It never prompts, but it costs roughly 700 ms per capture and writes into `~/Pictures`.

## The question

SnapShotKit captures the whole screen the instant Print is pressed, then shows a selection overlay on top of that frozen bitmap. That only works if an unsandboxed app can capture the screen repeatedly, silently, and quickly.

GNOME's private `org.gnome.Shell.Screenshot` API is not an option. It introspects fine, but every method is behind a sender whitelist:

```
$ gdbus call --session --dest org.gnome.Shell.Screenshot \
    --object-path /org/gnome/Shell/Screenshot \
    --method org.gnome.Shell.Screenshot.FlashArea 0 0 1 1
Error: GDBus.Error:org.freedesktop.DBus.Error.AccessDenied: FlashArea is not allowed
```

That leaves the XDG portal. The open question was whether `org.freedesktop.portal.Screenshot` prompts for permission on every call for an app that is not Flatpak-packaged, since a dialog per screenshot would make the whole design unusable.

## Method

`SnapShotKit.Spike.PortalCapture` calls `org.freedesktop.portal.Screenshot.Screenshot` with `interactive: false` in a loop, timing each round trip end to end, and reports the returned image dimensions. A capture that needed a dialog cannot come back in under a second, so the timings alone distinguish a silent capture from a prompted one.

```bash
dotnet run --project src/SnapShotKit.Spike.PortalCapture -- --iterations 5 --out ./spike-output
```

Set `SNAPSHOTKIT_TRACE=1` for stage-by-stage tracing of the D-Bus exchange.

## Environment

| | |
|---|---|
| OS | Fedora 44 |
| Desktop | GNOME Shell 50.4, Wayland |
| Display | single 5120x1440, scale 1 |
| Portal | xdg-desktop-portal 1.22.1, xdg-desktop-portal-gnome 50.0 |
| Screenshot portal version | 2 |
| Runtime | .NET 10.0.201, Tmds.DBus.Protocol 0.95.0 |

## Results

| Iteration | Elapsed | Dimensions | PNG size |
|---|---|---|---|
| 1 | 853 ms | 5120x1440 | 1.99 MB |
| 2 | 735 ms | 5120x1440 | 1.99 MB |
| 3 | 696 ms | 5120x1440 | 1.99 MB |
| 4 | 704 ms | 5120x1440 | 1.99 MB |
| 5 | 687 ms | 5120x1440 | 1.99 MB |

5 of 5 succeeded. No permission dialog appeared on any iteration, including the first.

## Findings

**No prompting.** GNOME's portal grants non-interactive screenshots to unsandboxed callers without a dialog and without a permission-store entry to accept first. This is the finding that unblocks the design.

**One capture covers the whole desktop.** The returned image is the full 5120x1440 logical screen, so a multi-monitor layout arrives as a single composite bitmap and the overlay can treat it as one canvas.

**Roughly 700 ms per capture, and that is the real cost.** Most of it is almost certainly PNG encoding of 7.4 megapixels plus the disk round trip, since the portal hands back a file path rather than pixels. It is fast enough to ship, but it is not instant, and the delay lands exactly where the user is waiting for the overlay to appear.

**The portal writes into `~/Pictures`.** Files arrive as `~/Pictures/Screenshot-N.png` with an incrementing counter, not in a temp directory. The calling app owns the file and must move or delete it, otherwise every capture permanently litters the user's Pictures folder. The spike moves each file into its output directory.

## Gotchas found along the way

Three bugs cost most of the spike's time and are all worth remembering, because none of them produced an error message.

**`MessageWriter` is a mutable ref struct, so it must be passed by reference.** Passing it by value into a body-writing delegate means every write lands in a copy. The result is an empty message that is silently never sent, and the caller simply waits forever. Note that a `using` declaration cannot be passed by `ref`, so the writer needs an explicit try/finally.

**A match rule's `Sender` is compared against the message's sender field literally.** Signals arrive stamped with the portal's unique name, for example `:1.77`, not the well-known `org.freedesktop.portal.Desktop`. Resolving the owner with `GetNameOwner` first is the fix. Testing later showed a rule naming the well-known name does match, so this is not strictly required, but resolving is unambiguous and costs one round trip on a local socket.

**`Notification<T>.Exception` throws unless `IsCompletion` is true.** Reading it on a value notification raises `InvalidOperationException` inside the observer callback, which tears the observer down. The symptom is a subscription that silently stops delivering. Always branch on `IsCompletion` first.

## Conclusion

Ship v1 on the Screenshot portal. It is prompt-free, returns the entire desktop in one image, and needs about 40 lines of D-Bus. Move the returned file out of `~/Pictures` immediately on every capture.

Revisit the roughly 700 ms if it feels slow in practice. The escape hatch is spike 002.

## Next: spike 002

Measure `org.freedesktop.portal.ScreenCast` with `persist_mode: 2` against the same baseline. A restore token gives a persistent session that can be held warm in a daemon, and pulling a raw PipeWire frame skips the PNG encode and the disk round trip entirely, which should put capture well under 100 ms. The cost is consuming PipeWire from .NET, which needs either a native interop shim or GStreamer.
