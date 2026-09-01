# Contributing

Contributions are welcome, and the source is published partly so that they are possible.

## The short version

Fork, branch, open a pull request. That is the normal GitHub flow and it is explicitly permitted by [LICENSE](LICENSE) section 1e, which otherwise forbids copying the project around.

Anyone whose contribution is merged gets a free perpetual licence to SnapShotKit, including 1.0.0 and everything after it. That is not a token: the person who fixes a bug should not then be asked to pay for the fix.

## The contributor licence agreement

Before the first pull request can be merged you will be asked to sign a contributor licence agreement. It is short, and it says two things:

- **You keep the copyright in what you wrote.** It is yours. Signing does not hand it over.
- **You grant a licence broad enough to ship it and to relicense the project later.**

The second point is the one that matters, and it is worth explaining rather than burying. SnapShotKit is source-available rather than open source, and one day it may be sold under other terms as well. If every contributor kept an unlicensed veto over their patch, none of that would be possible without tracking down every contributor who ever touched the file. Asking once, up front, is the honest version of that problem.

If you would rather not sign, that is a perfectly reasonable position. Open an issue describing the fix instead, and it will get credited in the commit that implements it.

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
