# SnapShotKit

A screenshot and annotation tool for Linux, in the spirit of Snagit.

Press Print and the whole screen is captured immediately, so timing a capture is easy. An overlay then appears on the frozen image, where you either keep the whole screen or drag out a region; nothing moves under the cursor while you aim, because what you are aiming at is already a photograph.

The result opens in an editor for arrows, boxes, blur, text and numbered markers, where the canvas can also be cropped or given transparent space around it, and is filed in a library grouped by day.

Annotations are kept as objects alongside the untouched capture, so anything drawn can be moved, restyled or removed next week, and the picture underneath is never modified. Exporting to PNG or JPEG renders the document rather than being the document.

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
snapshotkit-editor               # the library
snapshotkit-editor shot.ssk      # one snapshot
```

Both appear in the applications list as **SnapShotKit** and **Take a Screenshot**, and `.ssk` files open in the editor from the file manager.

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

## Licence

GNU General Public License v3.0 or later. See [LICENSE](LICENSE).

The bundled Barlow and Barlow Condensed fonts are under the SIL Open Font License, which travels with them in `src/SnapShotKit.Ui/Assets/Fonts/OFL.txt`.
