# Packaging

## What a package has to contain

Five binaries, and they must stay together. The daemon finds the overlay, the editor and the capture helper by looking beside itself, which is what lets an installed copy work without a single configured path. `make install` therefore puts all of them in `/usr/lib/snapshotkit/` and symlinks only the two commands a person types into `/usr/bin`.

Beyond the binaries: a systemd user unit, a D-Bus activation file, two desktop entries, the `.ssk` MIME type, icons at nine sizes, and the GNOME Shell extension.

## The D-Bus activation file is not optional

`/usr/share/dbus-1/services/org.snapshotkit.Daemon.service` is what makes the first capture after a login work. Without it, `snapshotkit capture` and the launcher entry both fail until something has started the daemon, and they fail quietly, which is the worst way to fail.

This is also why the daemon registers its method handler *before* claiming its bus name. The name appearing is what tells the world it is ready, and with activation the call that started it arrives the instant it appears; claiming the name first loses exactly that call.

## The extension ships with the application

It is installed to `/usr/share/gnome-shell/extensions/`, not downloaded from extensions.gnome.org. The extension and the daemon speak a private D-Bus interface and have to move together: a version skew between them shows up as a menu item that does nothing.

This is the strongest argument for an RPM over a Flatpak. A Flatpak cannot install a shell extension, so a Flathub release means two separate installs that can drift apart.

## Build requirements

```
dotnet-sdk-10.0 gcc make pipewire-devel glib2-devel
```

`global.json` asks for SDK 10.0.100 with `latestFeature`, which Fedora's 10.0.111 satisfies. It used to ask for 10.0.200, which it does not: a higher feature band than the distro ships means the build cannot find an SDK at all.

## Publishing flags that matter

Both are in the Makefile, and both were mistakes worth recording.

**A runtime identifier is required.** Published without `-r linux-x64`, Avalonia keeps native libraries for every platform it supports: the editor came to 564 MB, almost all of it Windows and macOS binaries. With it, 25 MB.

**Symbols have to be turned off.** The ahead-of-time compiled overlay ships a 51 MB `.dbg` file otherwise, which is larger than everything else in the package put together.

## RPM

`packaging/snapshotkit.spec` builds through the Makefile. To build locally:

```bash
rpmbuild -ba packaging/snapshotkit.spec
```

For COPR, the project needs **network access enabled in the build settings**. NuGet restore needs the network and Fedora's mock disables it by default. This is the usual arrangement for .NET packages; the alternative is vendoring every dependency into the source tarball.

The package deliberately does not enable the daemon in `%post`. A capture daemon is a per-user, per-session decision, and a system package has no business making it for every account on the machine. `snapshotkit setup` does it for the user who runs it.

## Releasing

`.github/workflows/release.yml` builds the RPM on a clean Fedora on every push, installs it, and checks the commands landed where they should. Pushing a version tag additionally attaches the package to a GitHub release:

```bash
git tag v0.1.0
git push origin v0.1.0
```

Nothing else is needed. The version comes from the tag and is written into the spec by the workflow, so the spec's own `Version:` only matters for untagged builds.

Building in a Fedora container rather than on the Ubuntu runner is deliberate: the package is built against Fedora's .NET SDK and pipewire, and building it against anything else would prove something other than what is shipped. It is also what caught the `global.json` feature band problem, which only appears when the SDK is the distro's rather than a hand-installed one.

## COPR

COPR gives users `dnf install` and automatic updates, which a GitHub release does not. It needs no code: create a project at [copr.fedorainfracloud.org](https://copr.fedorainfracloud.org), set the source to **SCM** pointing at this repository with `packaging/snapshotkit.spec`, and turn on **internet access during builds** — NuGet restore needs the network and mock disables it by default. COPR provides a webhook URL to paste into the repository settings so a push rebuilds.

## What is not packaged yet

- **Flatpak.** Wants the capture shortcut moved to the GlobalShortcuts portal first, since a sandbox cannot write gsettings keybindings or install a shell extension. GNOME 50 does implement that portal, so it is a real option rather than a dead end.
- **Architectures other than x86_64.** Nothing about the code is x86-specific, but nothing has been built or tested elsewhere, so the spec says `ExclusiveArch: x86_64` rather than claiming otherwise.
