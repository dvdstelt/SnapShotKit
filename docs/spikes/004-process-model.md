# Spike 004: what an idle GUI toolkit costs

Status: complete, 2026-08-26
Outcome: the daemon must be headless. Keeping Avalonia resident costs 98 to 139 MB before anything is captured and never gives it back. The design in `daemon-design.md` was wrong and has been corrected.

## The question

The daemon design assumed it was acceptable to keep a GUI toolkit resident all day so the overlay could appear without paying startup. That assumption was written down as "if it turns out to be 200 MB, the split-process design deserves reconsideration" and then never checked. The figure was invented, not measured.

## Method

A minimal Avalonia 12.1.1 app that reports its own `VmRSS` at four points: before Avalonia is touched, after initialisation with no window ever shown, with a fullscreen window displaying a 5120x1440 frame, and after closing that window and forcing a collection. Measured as Debug JIT, Release ReadyToRun, and NativeAOT, because comparing a Debug build against a resident process would be dishonest.

Separately, the headless capture path was instrumented to report RSS at each stage, to find out what a daemon without a toolkit actually weighs.

## Results

Avalonia, by build configuration:

| | Debug JIT | Release R2R | NativeAOT |
|---|---|---|---|
| Before Avalonia | 26.7 MB | 27.4 MB | 11.1 MB |
| Initialised, no window ever shown | 135 MB | 139.6 MB | 98.3 MB |
| Fullscreen window with the frame | 230 MB | 233.2 MB | 184.5 MB |
| Window closed and collected | 203 MB | 206.0 MB | 157.0 MB |
| Process start to Avalonia initialised | ~1100 ms | 381 to 431 ms | 183 to 221 ms |

The headless capture path, Debug JIT:

| Stage | RSS |
|---|---|
| Bare process | 27.7 MB |
| Portal session up, stream connected and parked | 41.3 MB |
| After one grab | 97.7 MB |

## Findings

**A resident toolkit costs 98 MB minimum and never returns to it.** Even NativeAOT, the best case, sits at 98 MB having shown nothing, and settles at 157 MB after a single capture. Closing the window reclaims only about 27 MB of the 86 MB the capture added. Over a day of screenshots that is memory the user never gets back, in a process that spends almost all its time doing nothing.

**PipeWire is not the expensive part.** A parked stream adds only 13.6 MB over a bare process. The jump to 97.7 MB comes from taking a frame, and is the 28 MB destination buffer plus PipeWire's mapped buffers becoming resident. That cost is inherent to holding a screenshot in memory and is paid under any process model.

**NativeAOT changes the trade-off entirely.** It cuts startup roughly in half against ReadyToRun and by a factor of five against Debug, putting process start to initialised at under 220 ms. That is the number that makes spawning the UI per capture viable.

**The idle daemon is cheap without a toolkit.** 41 MB as a Debug JIT build, and the bare-process figures suggest roughly 25 MB compiled with NativeAOT.

## Conclusion

Split the process. A headless daemon owns the portal session, the parked PipeWire stream and the library, and idles at roughly 25 to 41 MB. The overlay and editor live in a separate NativeAOT GUI process spawned per capture, which exits afterwards so every byte it used is reclaimed.

The cost is roughly 200 ms before the overlay appears. That lands on the one part of the path that was explicitly identified as not latency-sensitive: the capture itself still happens about 50 ms after the keypress, and what the user waits for afterwards is only the UI drawing.

The earlier argument for a single process was that spawning per capture "defeats the point of having a daemon". That was wrong. The point of the daemon is the capture pipeline, which costs 170 ms to establish and must not be on the hot path. Window creation was never on the hot path.

## Follow-ups

**Frame handoff.** The daemon holds a 28 MB frame the GUI needs. Copying it over D-Bus would be wasteful; passing a memfd file descriptor is zero-copy and D-Bus carries descriptors natively.

**Releasing the frame buffer.** The daemon should drop its buffer once the GUI has taken the frame, returning to its 41 MB baseline rather than holding a screenshot indefinitely.

**PipeWire buffer count.** `SPA_PARAM_Buffers` allows requesting a specific number of buffers. The default pool is a meaningful share of the post-grab RSS and is worth tuning once the daemon exists.
