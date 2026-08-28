# Spike 002: raw frame capture through the XDG ScreenCast portal

Status: complete, 2026-08-26
Outcome: ScreenCast is roughly three times faster than the Screenshot portal and needs one consent dialog, ever. It should be the primary capture path, with the Screenshot portal kept as the fallback.

## The question

[Spike 001](001-portal-capture.md) showed the Screenshot portal costs about 700 ms per capture, most of it PNG encoding and a disk round trip. That delay lands exactly where the user is waiting for the selection overlay to appear.

ScreenCast is the alternative: a session that streams the screen over PipeWire, so frames arrive as raw pixels with no encode and no file. Two things had to be true for it to be worth the extra complexity. Consent must be a one-time cost rather than per capture, and a frame has to arrive meaningfully faster than 700 ms.

## Method

`SnapShotKit.Spike.ScreenCast` runs the full session handshake, timing each step, and persists the restore token to a state file so subsequent runs can present it:

```bash
dotnet run --project src/SnapShotKit.Spike.ScreenCast -- --frame
```

`--forget` deletes the stored token to replay the first-run experience. `SNAPSHOTKIT_TRACE=1` dumps the D-Bus exchange, including the keys the portal returns from `Start`.

Frames are pulled with `gst-launch-1.0 pipewiresrc`, which avoids writing a PipeWire interop layer just to answer a timing question. Process startup is measured separately with a trivial `fakesrc` pipeline and subtracted, so the reported figure is not inflated by spawning GStreamer.

## Results

Session handshake, with a restore token, across four runs:

| Step | Time |
|---|---|
| CreateSession | 39 ms |
| SelectSources | 4 to 15 ms |
| Start | 37 to 43 ms |
| OpenPipeWireRemote | 6 ms |
| **Total handshake** | **98 to 114 ms** |

No dialog appeared on any run presenting a token.

Frame pull, 5120x1440:

| Run | Total | Net of GStreamer startup | Size |
|---|---|---|---|
| 1 | 124 ms | 100 ms | 28.12 MB |
| 2 | 175 ms | 150 ms | 28.12 MB |
| 3 | 168 ms | 141 ms | 28.12 MB |
| 4 | 151 ms | 126 ms | 28.12 MB |

28.12 MB is exactly 5120 x 1440 x 4, so frames arrive as raw 32-bit pixels with no encoding in the path.

Cold cost, handshake plus frame, is roughly 230 to 260 ms against the Screenshot portal's 700 ms. A daemon holding the session open pays only the frame pull.

## Findings

**Consent is once, not per capture.** The first `Start` shows GNOME's screen-share dialog. Every later `Start` that presents a restore token completes in about 40 ms with no dialog at all.

**The restore token is only issued if the user ticks the remember box.** This is the sharp edge. Declining to tick it means `Start` succeeds normally and simply omits `restore_token` from its results, so the next run prompts again. There is no error and nothing to catch. SnapShotKit must notice the missing token and tell the user why it keeps asking, rather than silently prompting forever.

**Unsandboxed apps do get persistent grants.** The token lands in the permission store under table `screencast`, keyed to the empty app id that unsandboxed callers are given:

```
$ gdbus call --session --dest org.freedesktop.impl.portal.PermissionStore \
    --object-path /org/freedesktop/impl/portal/PermissionStore \
    --method org.freedesktop.impl.portal.PermissionStore.Lookup screencast _rfeJa3rraVDhxGCxQVxNg
({'': ['yes']}, <('GNOME', uint32 1, <(int64 ..., int64 ..., [(uint32 0, uint32 1, <'SAM:C49RG9x:H1AK500000'>)])>)>)
```

The grant is pinned to a specific monitor, so a display change likely invalidates it. Untested.

**`OpenPipeWireRemote` works and returns a usable fd** in about 6 ms. The spike pulled frames using the node id against the default PipeWire daemon instead, which also works, but production should use the fd since that is the access path the portal actually intends.

**The stream reports its size**, 5120x1440, matching the Screenshot portal's composite. Multi-monitor still arrives as one canvas.

## Gotchas

**`Start` returns `size` as a bare struct, not a variant-wrapped one.** Calling `GetVariantValue()` on it throws `Type Struct can not be retrieved as Variant`. Tmds already unwraps the variant in an `a{sv}`.

**Read the restore token before anything that can throw.** The first run of this spike crashed on the `size` parse after `Start` had already succeeded, which threw away a token the user had just granted consent for and forced them to accept the dialog a second time. `StartAsync` now reads the token first, and `PortalStreamException` carries it so even a failed parse cannot lose it.

## Conclusion

Make ScreenCast the primary capture path. One dialog ever, roughly 100 ms to establish a session, roughly 100 to 150 ms for a raw frame, and no encode anywhere in the hot path.

Keep the Screenshot portal as the fallback for the case that actually matters: a user who declines consent, or declines to tick remember. That path never prompts, so SnapShotKit always has a way to capture.

## Open questions

**What does holding a session warm actually cost?** A live ScreenCast session means the compositor is capturing continuously, which is not free in CPU, GPU or battery. A daemon that holds one open permanently may be a bad neighbour. The likely answer is a lazily started session with an idle timeout, but the cost needs measuring before designing around it.

**Does a display change invalidate the grant?** The permission store entry names a specific monitor. Docking, undocking or changing resolution may force fresh consent, which the fallback path would need to cover.

**In-process PipeWire.** The 100 to 150 ms measured here includes GStreamer connecting to PipeWire and negotiating a format on every pull. A warm in-process stream should be substantially faster, at the cost of either P/Invoke against libpipewire or a GStreamer dependency.
