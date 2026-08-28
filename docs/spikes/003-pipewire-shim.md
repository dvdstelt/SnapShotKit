# Spike 003: capture from a parked PipeWire stream

Status: complete, 2026-08-26
Outcome: capture costs about 35 ms and an idle SnapShotKit costs the compositor nothing. This is the capture design.

## The question

[Spike 002](002-screencast-capture.md) left the design stuck between two bad options. A ScreenCast stream held permanently active costs gnome-shell about 17 points of CPU, which is unacceptable for a tool that sits idle all day. Starting a session on demand instead costs roughly 230 ms, which means the pixels reflect the screen a fifth of a second after the key was pressed, and a menu or tooltip may already be gone.

The way out is a third state. PipeWire streams can be connected and format-negotiated but left inactive, producing no frames. If activating such a stream is fast, SnapShotKit can park one, spend nothing while idle, and still capture at the moment of the keypress.

Two things had to be measured: how long activate-to-frame actually takes, and whether a parked stream really is free.

## Method

`src/native/snapshotkit-pw` is a small C shim over libpipewire exposing open, grab and close. It exists to hide SPA rather than PipeWire: format negotiation is built with variadic POD builder macros that have no reasonable P/Invoke equivalent, so it stays in C and C# gets a flat interface.

The stream connects with `PW_STREAM_FLAG_INACTIVE`. A grab flips it active, takes exactly one frame, and parks it again.

```bash
sudo dnf install pipewire-devel
./src/native/snapshotkit-pw/build.sh
dotnet run --project src/SnapShotKit.Spike.PipeWire -- --iterations 10 --parked-cost 10
```

The first grab still pays for format negotiation, so it is reported as a warm-up rather than counted. `--quiet-gap` inserts a pause before each grab so the screen goes still, because mutter delivers frames on damage and a grab taken while the screen is busy would be the easy case.

## Results

Setup, once per session:

| | |
|---|---|
| Shim connect | 6 ms |
| Warm-up grab, including format negotiation | 68 to 74 ms |

Grabs from a parked stream, 5120x1440, stride 20480, 28.12 MB per frame:

| | Fastest | Median | Slowest |
|---|---|---|---|
| Screen busy | 10.3 ms | 34.8 ms | 37.2 ms |
| Screen still for 3s before each grab | 18.7 ms | 38.3 ms | 50.2 ms |

Compositor cost over 10 second windows:

| State | gnome-shell CPU |
|---|---|
| Control, nothing of ours running | 23.3% |
| Stream parked inactive | 24.9% |
| Stream active (spike 002) | +17 points |

## Findings

**Capture costs about 35 ms, worst observed 50 ms.** Against 700 ms for the Screenshot portal, that is a factor of 14 to 20. Against a 230 ms cold session it is still six times better, and more importantly the pixels are the screen as it was when the key was pressed.

**A parked stream is free.** 24.9% against a 23.3% control is within noise, and nowhere near the 17 point penalty an active stream carries. SnapShotKit can hold a stream indefinitely without being a bad neighbour.

**Damage-driven delivery is not a problem.** Letting the screen sit still for three seconds before each grab moved the median from 34.8 ms to 38.3 ms. Activating the stream is enough to make the compositor produce a frame, so a still desktop does not stall the capture.

**Negotiation happens once, on connect.** The warm-up grab costs about 70 ms and every grab after it costs a third of that, which confirms activation is not re-negotiating. SnapShotKit should perform a throwaway grab at startup so the user never pays the warm-up.

## Conclusion

This is the capture design. A daemon opens a ScreenCast session at startup, connects a PipeWire stream, takes one throwaway frame to force negotiation, and parks the stream. Print activates it, takes one frame in roughly 35 ms, and parks it again. Nothing is written to disk, and idle costs nothing.

The Screenshot portal remains the fallback for a user who declines ScreenCast consent, or who does not tick the remember box.

## Open questions

**Session lifetime across suspend and display changes.** The permission store entry names a specific monitor. Docking, undocking or resuming from suspend may invalidate the session or the node, and the daemon needs to notice and re-establish rather than handing back a dead stream.

**Whether the shim is worth keeping.** P/Invoke needs a library at runtime, not headers at build time, so calling `libpipewire-0.3.so.0` directly from C# would remove the native build step entirely and make SnapShotKit a pure .NET artifact. The cost is hand-serialising SPA PODs and pinning ABI constants. Now that the performance case is settled, this is a packaging question rather than a feasibility one.
