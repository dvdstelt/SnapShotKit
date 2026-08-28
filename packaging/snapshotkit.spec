%global uuid snapshotkit@dvdstelt.github.io

# The repository's own name, which is not the package's. Both GitHub's archive endpoint and COPR
# name the directory inside the tarball after the repository, so %prep has to unpack that and not
# the lowercase package name.
%global forgename SnapShotKit

# The published output is a mix of native binaries and managed assemblies that are already trimmed
# and stripped, and rpmbuild's own post-processing has nothing useful to do to either.
%global __brp_strip %{nil}
%global __brp_strip_static_archive %{nil}
%global debug_package %{nil}

Name:           snapshotkit
Version:        0.1.0
Release:        1%{?dist}
Summary:        Capture a region of the screen and annotate it

License:        GPL-3.0-or-later
URL:            https://github.com/dvdstelt/snapshotkit
Source0:        %{url}/archive/v%{version}/%{forgename}-%{version}.tar.gz

ExclusiveArch:  x86_64

BuildRequires:  dotnet-sdk-10.0
BuildRequires:  gcc
BuildRequires:  make
BuildRequires:  pipewire-devel
# Named by the programs they provide rather than by package. The schema compiler lives in glib2
# rather than glib2-devel, which the package name got wrong, and a file dependency cannot.
BuildRequires:  /usr/bin/glib-compile-schemas
BuildRequires:  /usr/bin/desktop-file-validate
BuildRequires:  /usr/bin/appstreamcli
# Defines %%{_userunitdir}. A minimal build root does not have it, and an undefined macro in
# %%files fails the build after everything has already been compiled.
BuildRequires:  systemd-rpm-macros

Requires:       dotnet-runtime-10.0
Requires:       pipewire-libs
# Screen capture goes through the desktop portal; without a backend there is no capture at all.
Requires:       xdg-desktop-portal
# A Wayland clipboard offer belongs to the process that made it, and the overlay exits as soon as
# it has an answer. wl-copy forks a holder that outlives it, which is the only reason copy works.
Requires:       wl-clipboard

Recommends:     xdg-desktop-portal-gnome
Recommends:     gnome-shell

%description
SnapShotKit captures the whole screen the moment you press the key, then lets you choose a region
from the frozen image, so nothing moves under the cursor while you aim.

Snapshots are kept as objects rather than pixels: an arrow, a blur or a numbered marker can be
moved or deleted next week, and the capture underneath is never modified. Exporting to PNG or JPEG
renders the document rather than being the document.

It ships a GNOME Shell extension that registers the capture shortcut inside the shell, so the key
still works while a shell menu holds a keyboard grab, and offers a delayed capture, the editor and
the snapshots folder from the top bar.

%prep
%autosetup -n %{forgename}-%{version}

%build
# NuGet restore needs the network. Fedora's mock disables it by default; a COPR project has to have
# "enable network" turned on, which is the usual arrangement for .NET packages.
%make_build build

%install
%make_install PREFIX=%{_prefix}

desktop-file-validate %{buildroot}%{_datadir}/applications/%{name}-editor.desktop
desktop-file-validate %{buildroot}%{_datadir}/applications/%{name}-capture.desktop

# Without the network, so the build does not fail because GitHub was briefly unreachable.
appstreamcli validate --no-net \
    %{buildroot}%{_metainfodir}/io.github.dvdstelt.SnapShotKit.metainfo.xml

%files
%license LICENSE
%license %{_datadir}/licenses/%{name}/OFL.txt
%doc README.md
%{_bindir}/snapshotkit
%{_bindir}/snapshotkit-editor
%{_prefix}/lib/snapshotkit/
%{_userunitdir}/snapshotkitd.service
%{_datadir}/applications/%{name}-editor.desktop
%{_datadir}/applications/%{name}-capture.desktop
%{_datadir}/mime/packages/%{name}.xml
%{_metainfodir}/io.github.dvdstelt.SnapShotKit.metainfo.xml
%{_datadir}/dbus-1/services/org.snapshotkit.Daemon.service
%{_datadir}/icons/hicolor/*/apps/%{name}.png
%{_datadir}/gnome-shell/extensions/%{uuid}/

%post
# The daemon is per-user and per-session, so it is not enabled here: a system package has no
# business deciding that every account on the machine wants a capture daemon. `snapshotkit setup`
# enables it for the user who runs it.
:

%changelog
* Fri Aug 28 2026 Dennis van der Stelt <dvdstelt@gmail.com> - 0.1.0-1
- First release.
