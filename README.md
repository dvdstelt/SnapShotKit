# SnapShotKit

A screenshot and annotation tool for Linux, in the spirit of Snagit.

Press Print and the whole screen is captured immediately, so timing a capture is easy. An overlay then appears on the frozen image, where you either keep the whole screen or drag out a region; nothing moves under the cursor while you aim, because what you are aiming at is already a photograph.

The result opens in an editor for arrows, boxes, blur, text and numbered markers, where the canvas can also be cropped or given transparent space around it, and is filed in a library grouped by day.

Annotations are kept as objects alongside the untouched capture, so anything drawn can be moved, restyled or removed next week, and the picture underneath is never modified. Exporting to PNG, JPEG or WebP renders the document rather than being the document, and asks first: transparency kept or flattened, WebP lossless or lossy and how hard to compress, JPEG quality, and whether the file lands in the exports folder or beside the picture it came from.

An image that was never captured here can be annotated too. Open one from the File menu, or right-click it in the file manager and open it with SnapShotKit, and it is wrapped in a snapshot of its own; the file you pointed at is only read.

Target platform is Fedora on GNOME Wayland.

## Installing

Download the RPM from the [latest release](https://github.com/dvdstelt/SnapShotKit/releases) and install it:

```bash
sudo dnf install ./snapshotkit-*.rpm
```

Or build it yourself:

```bash
sudo dnf install dotnet-sdk-10.0 gcc make pipewire-devel glib2-devel wl-clipboard
make
sudo make install
```

Then set it up for your account. This is per-user rather than something the install does, because a capture daemon is a decision each account makes for itself:

```bash
snapshotkit setup
```

That enables the daemon and binds Print. To get the panel menu and a Print key that works even while a GNOME menu is open, enable the shell extension and log out and back in:

```bash
gnome-extensions enable snapshotkit@dvdstelt.github.io
```

## Using it

Press **Print** to capture, then drag a region or take the whole screen.

Both halves also stand on their own:

```bash
snapshotkit capture              # capture, and choose a region
snapshotkit capture --after 5    # wait first, for menus that close on a keypress
snapshotkit-editor               # the editor, with nothing open
snapshotkit-editor --library     # the library
snapshotkit-editor shot.ssk      # one snapshot
snapshotkit-editor diagram.png   # any image, brought in as a snapshot
```

Both appear in the applications list as **SnapShotKit** and **Take a Screenshot**. `.ssk` files open in the editor from the file manager, and any PNG, JPEG, WebP, BMP, GIF or TIFF can be sent to it through **Open With**.

The panel menu offers a delayed capture, the editor and the snapshots folder.

## Where things are kept

| Path | Contents |
|---|---|
| `~/Pictures/snapshotkit/` | Exported images. The only folder you are meant to browse. |
| `~/.local/share/snapshotkit/snapshots/` | `.ssk` working documents. |
| `~/.local/state/snapshotkit/` | Screen sharing consent token, keybinding backup. |

## Building for development

```bash
./src/native/snapshotkit-capture/build.sh && dotnet build src/snapshotkit.slnx
```

`make` produces the packaged layout instead; `make install DESTDIR=/tmp/root` stages it without touching the system.

## Documentation

- [docs/architecture.md](docs/architecture.md) — how it is put together and why
- [docs/packaging.md](docs/packaging.md) — building a package
- [docs/spikes/](docs/spikes/) — what was actually measured, including the things that did not work

## Licence and price

SnapShotKit is free to use while it is pre-1.0. From version 1.0.0 it will cost 15 euro, paid once, with updates included. There is no subscription and there will not be one.

The source stays visible either way, and you are welcome to read it, build it and run what you have built. What you may not do is publish your own build or ship a modified version. Contributors with a merged pull request get a free perpetual licence, so nobody is charged for a bug they fixed themselves.

See [LICENSE](LICENSE) for the terms and [CONTRIBUTING.md](CONTRIBUTING.md) for how contributions work. This is a source-available licence rather than an open source one, and it does not pretend otherwise. Version 0.1.0 was released under the GPL and remains available under those terms.

The bundled Barlow and Barlow Condensed fonts are under the SIL Open Font License, which travels with them in `src/SnapShotKit.Ui/Assets/Fonts/OFL.txt`.
