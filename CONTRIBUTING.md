# Contributing

Contributions are welcome, and the source is published partly so that they are possible.

## The short version

Fork, branch, open a pull request. There is no contributor licence agreement to sign. SnapShotKit is under the GPL, version 3 or later, and a contribution is accepted under the same terms: you keep the copyright in what you wrote, and it is licensed to everyone else exactly as the rest of the project is.

## Before you start on something large

Open an issue first. The architecture has opinions, most of them written down in [docs/architecture.md](docs/architecture.md), and it is no fun to write a few hundred lines and then discover they cut against the grain of something. Small fixes need no ceremony.

## What the code expects

- Read [docs/architecture.md](docs/architecture.md) first. It explains why things are put together the way they are, which is usually the part that is hard to guess.
- Comments explain **why**, not what. The code says what it does; the comment says why it is worth doing that way, and what went wrong when it was done otherwise.
- Match the surrounding style rather than your own. The codebase is consistent, and consistency is worth more than any individual preference.
- One logical change per commit.

## Building and running

```bash
./src/native/snapshotkit-capture/build.sh && dotnet build src/snapshotkit.slnx
```

`make` produces the packaged layout instead; `make install DESTDIR=/tmp/root` stages it without touching the system. Note that a development build shadows an installed one: see [docs/packaging.md](docs/packaging.md).

## Reporting bugs

A capture tool is hard to describe in words, so a screenshot of the problem is worth a great deal, and you have a tool for that. Please say which Fedora and GNOME version, and whether you are on Wayland or X11.
