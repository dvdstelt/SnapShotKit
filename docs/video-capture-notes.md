# Notes on video and GIF capture

Not implemented, and deliberately not scheduled. This records what is already known so the question can be answered quickly when it comes up.

## We are most of the way there already

ScreenCast is a *video* portal. `snapshotkit-capture` currently connects a stream, takes one frame, and disconnects. Recording is the same stream left running, with frames going to an encoder instead of a shared file. None of the hard parts, portal consent, restore tokens, session recovery, would need to change.

## What is already measured

An active stream costs gnome-shell about 17 points of CPU ([spike 002](spikes/002-screencast-capture.md)). That was the reason the session is released when idle. During a deliberate recording it is entirely acceptable: the user asked for it and can see it happening.

Frames arrive at up to display refresh, roughly 13 ms apart when the screen is busy, and on damage only when it is still ([spike 003](spikes/003-pipewire-shim.md)). Damage-driven delivery is a gift for screen recording, since a static screen produces almost no data.

## The real question is encoding, not capture

Raw frames are 28 MB each at 5120x1440. A minute at 30 fps is roughly 50 GB, so encoding has to happen live rather than by buffering and converting afterwards.

The obvious route is GStreamer inside the capture helper, which already proved out during spike 002: `pipewiresrc` feeding `videoconvert` and an encoder. That keeps encoding out of the .NET process, which matters given what spike 005 found about the two runtimes coexisting.

## GIF is the wrong target

GIF is capped at 256 colours per frame, which mangles screenshots of text and UI, and the files are large for what they are. Better outputs, in order:

- **Animated WebP** for sharing. Good compression, real colour, and it works everywhere GIF does except in the very oldest software.
- **MP4 (H.264)** for anything longer than a few seconds.
- **GIF** last, produced by conversion when a destination genuinely accepts nothing else. Worth generating a palette per clip rather than using a fixed one.

Practically that means encoding once to a good format and converting on export, rather than treating GIF as the native format.

## Format

A recording is not a snapshot with extra frames. Annotations on video need timing, which `document.json` has no concept of. Either a separate `.skv` with its own document shape, or a snapshot format that grows a timeline, and that decision should wait until there is a real recording to annotate.

## What would need proving first

- Whether an encoder can keep up at 5120x1440 without dropping frames, and what to do when it cannot.
- Whether the cursor should be embedded, which needs `cursor_mode` set to embedded or metadata rather than hidden as it is now.
- How a recording is stopped, given the overlay is a modal fullscreen window and a recording needs an unobtrusive control.
