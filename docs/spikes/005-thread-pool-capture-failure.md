# Spike 005: the fast path fails inside the daemon

Status: **root cause not identified, workable design found**, 2026-08-26
Impact: phase 1 works, but every user-triggered capture falls back to the slow Screenshot portal.

## Symptom

`snapshotkitd` establishes the ScreenCast session and takes its warm-up frame successfully, reporting the fast path. Every capture requested afterwards times out and falls back:

```
last error : Fast capture failed, using the fallback: No frame arrived within 5000 ms.
```

The failing grab shows nothing wrong. The stream reaches STREAMING and stays there for the entire timeout, `on_process` is never called once, and no error is reported by PipeWire, by the portal, or by the stream state callback. The compositor simply delivers no buffer.

The fallback works, so captures are still produced, at 1 to 8 seconds instead of 35 ms.

## Reproducing it

`SnapShotKit.Spike.PipeWire` carries flags that add one daemon-like property at a time. Control runs pass every time and can be interleaved with failing runs against the same session, so this is not drift:

| Configuration | Flag | Result |
|---|---|---|
| Main thread, foreground | none | **works** |
| Detached with nohup | run under `nohup` | works |
| Grabs on a dedicated foreground thread | `--own-thread` | works |
| Grabs via the thread pool | `--off-thread` | **fails, every grab** |
| A second D-Bus connection, opened after PipeWire | `--second-connection` | **fails, every grab** |
| A second D-Bus connection, opened before PipeWire | `--second-connection --early` | **libpipewire aborts** |
| Serving a bus name | `--request-name`, `--serve` | fails |
| Grabs arriving as D-Bus method calls | `--via-dbus` | fails |

Opening the second connection first does not merely fail, it kills the process:

```
'source->loop == &impl->loop' failed at ../spa/plugins/support/loop.c:189 remove_from_poll()
```

That is libpipewire tripping an internal invariant, which points at descriptor or epoll state being corrupted rather than at anything SnapShotKit does with the stream.

## Ruled out

**Idle time.** Gaps of 10, 20 and 30 seconds between grabs are fine.

**Thread identity of the native calls.** `PipeWireCapture` marshals every libpipewire call onto one dedicated thread. Thread ids traced from inside the shim confirm all native calls land on that thread, and the loop thread is the same, in both working and failing runs. The failure persists.

**Parking the stream.** The shim was temporarily changed to destroy and rebuild the stream per grab rather than toggling `pw_stream_set_active`. The failure persists, so it is not about reactivating a parked stream. That change has been reverted.

**Detachment.** Running under `nohup` with no controlling terminal, exactly as the daemon runs, works fine.

**Descriptor ownership.** `OpenPipeWireRemote` now duplicates the descriptor before handing it to libpipewire, since `ReadHandleRaw` returns one the D-Bus message still owns. This is correct on its own merits and has been kept, but it does not fix the failure.

**Two D-Bus connections.** The daemon was changed to share a single connection between the portal and its own bus name. Also correct on its own merits, also kept, and it does **not** fix the daemon.

## What the evidence points at

Every failing configuration has one thing in common: **.NET thread pool or async I/O activity in the process** after PipeWire is connected. Task.Run, a second connection's reader loop, and serving D-Bus all qualify. Every passing configuration keeps the process on the main thread or a dedicated foreground thread.

That is a correlation, not a mechanism, and it is unsatisfying: the native calls demonstrably happen on the same thread either way, and libpipewire has its own loop thread that .NET knows nothing about. Something about the two runtimes coexisting is breaking buffer delivery, and the `remove_from_poll` abort suggests it is at the epoll or descriptor layer rather than anything higher.

## Fixes attempted from research, and what they did

Searching turned up one genuine bug and two plausible mechanisms. Only the bug was real; neither mechanism was the cause.

**Incomplete buffer negotiation (a real bug, fixed, kept).** The shim parsed the negotiated format and stopped. It never called `pw_stream_update_params`, so it never declared which buffers it could accept. [OBS](https://github.com/obsproject/obs-studio/blob/master/plugins/linux-pipewire/pipewire.c) answers with `SPA_PARAM_Buffers` naming `SPA_DATA_MemPtr` plus `SPA_PARAM_Meta` for the header, and the [PipeWire streams documentation](https://docs.pipewire.org/page_streams.html) is explicit that the client must complete negotiation this way. Without it, `SPA_DATA_DmaBuf` buffers we cannot map may be offered. This was a protocol violation on our side and is now fixed. **It did not fix the failure.**

**CoreCLR activation signals.** CoreCLR uses `SIGRTMIN` to interrupt threads, and a signal delivered to a thread in `epoll_wait` returns EINTR, which an event loop that does not retry would lose. The shim was changed to block all realtime signals while creating the loop, so libpipewire's thread inherits a mask deaf to them. **No effect. Reverted.**

**Descriptor ownership and connection count.** Both covered above, both kept, neither fixed it.

## What does work: a separate process

Run under the exact configuration that breaks in-process capture, a child process capturing the same node succeeds every time:

| Capture location | Same session, same `--second-connection` condition |
|---|---|
| In-process, through the shim | fails, every grab |
| Child process (`gst-launch-1.0 pipewiresrc`) | **works, every grab, 95 to 162 ms** |

That 95 ms includes about 25 ms of `gst-launch` startup, so a purpose-built helper would be faster, and a resident helper asked for frames over a pipe would remove the startup cost entirely.

The root cause is still unknown. What is now established is that libpipewire and the .NET runtime cannot reliably share an address space for this workload, and that the boundary of a process is enough to make the problem disappear.

## Recommended design

Move capture into a small resident helper process:

- **`snapshotkitd`** keeps the portal handshake, since that is D-Bus work .NET does well, and passes the PipeWire descriptor to the helper.
- **`snapshotkit-capture`**, written in C, owns the libpipewire connection and the stream, and answers frame requests over a pipe, returning frames as a memfd.
- The daemon never links libpipewire at all.

This also removes the need for the `PipeWireCapture` P/Invoke layer, and makes the earlier packaging question moot: the native component stops being optional, so the pure P/Invoke alternative from spike 003 is off the table.

## Reproducing this again

The bisect harness was `src/SnapShotKit.Spike.PipeWire`, removed once the design moved out of process because it depended on the in-process shim. Restore it from commit 606db9f if the root cause is ever worth chasing.

## Still worth checking

Whether mutter logs anything when a stream reaches STREAMING and receives no buffers, since the silence on our side is total. That is the one remaining avenue for an actual root cause, and it would be worth reporting upstream if reproducible outside SnapShotKit.

## Meanwhile

The fallback is doing exactly what it was designed for: capture degrades in speed rather than breaking, and `snapshotkit status` says why. That is the only reason phase 1 is usable despite this.
