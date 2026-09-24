# Builds SnapShotKit and installs it the way a package expects.
#
# Everything the application runs lands in one directory, libexec/snapshotkit, with the two commands
# a person types symlinked into bin. That is deliberate: the daemon finds the overlay, the editor and
# the capture helper by looking beside itself, so keeping them together is what makes an installed
# copy work without a single configured path.

PREFIX     ?= /usr
DESTDIR    ?=
CONFIG     ?= Release
RUNTIME    ?= linux-x64

# Empty means MinVer reads it from the nearest tag. A source tarball has no history to read, so a
# build from one (COPR, rpmbuild) has to say which version it is.
VERSION    ?=

UUID       := snapshotkit@dvdstelt.github.io
STAGE      := build/stage
LIBEXEC    := $(DESTDIR)$(PREFIX)/lib/snapshotkit
BIN        := $(DESTDIR)$(PREFIX)/bin
SHARE      := $(DESTDIR)$(PREFIX)/share
UNITDIR    := $(DESTDIR)$(PREFIX)/lib/systemd/user

ICON_SIZES := 16 22 24 32 48 64 128 256 512

# Published without symbols and against one runtime identifier. Without the identifier the build
# keeps native libraries for every platform Avalonia supports, which is half a gigabyte of Windows
# and macOS binaries in a Linux package.
PUBLISH := dotnet publish -c $(CONFIG) -r $(RUNTIME) --nologo \
	-p:DebugType=none -p:DebugSymbols=false \
	$(if $(VERSION),-p:MinVerVersionOverride=$(VERSION))

.PHONY: all build native clean install uninstall

all: build

# The capture helper is C and links libpipewire, so it is built on its own terms.
native:
	./src/native/snapshotkit-capture/build.sh

build: native
	@rm -rf $(STAGE)
	@mkdir -p $(STAGE)
	$(PUBLISH) src/SnapShotKit.Cli     -o $(STAGE)
	$(PUBLISH) src/SnapShotKit.Overlay -o $(STAGE) -p:StripSymbols=true
	$(PUBLISH) src/SnapShotKit.Daemon  --self-contained false -o $(STAGE)
	$(PUBLISH) src/SnapShotKit.Editor  --self-contained false -o $(STAGE)
	@cp src/native/snapshotkit-capture/snapshotkit-capture $(STAGE)/
	@rm -f $(STAGE)/*.dbg
	@echo "staged in $(STAGE)"

# Installs what is already staged, and does not build it.
#
# Not because the dependency would be wrong, but because installing needs root and building must
# not have it. The SDK is very often a user-local install under ~/.dotnet, which root cannot reach
# through its own PATH, and reaching it anyway by preserving the caller's leaves a working tree
# full of root-owned build output that the next ordinary build cannot write to.
#
# The spec file already builds and installs as two steps, so this only makes the Makefile say what
# the packaging always assumed.
install:
	@test -d $(STAGE) || { \
		echo "Nothing staged in $(STAGE). Run 'make build' as yourself first, then 'sudo make install'."; \
		exit 1; \
	}
	install -d $(LIBEXEC) $(BIN) $(UNITDIR)
	cp -r $(STAGE)/. $(LIBEXEC)/
	chmod 755 $(LIBEXEC)/snapshotkit $(LIBEXEC)/snapshotkitd \
		$(LIBEXEC)/snapshotkit-editor $(LIBEXEC)/snapshotkit-overlay $(LIBEXEC)/snapshotkit-capture

	# Only the two commands a person types are on the path. The rest are launched by the daemon,
	# which finds them beside itself. Relative links, so they resolve inside a staged DESTDIR as
	# well as on the installed system.
	ln -sf ../lib/snapshotkit/snapshotkit        $(BIN)/snapshotkit
	ln -sf ../lib/snapshotkit/snapshotkit-editor $(BIN)/snapshotkit-editor

	install -Dm644 packaging/snapshotkitd.service $(UNITDIR)/snapshotkitd.service

	# D-Bus activation, so asking the daemon for a capture starts it if it is not already up.
	# Without this, the launcher entry and the CLI both fail silently on a fresh login until
	# something has enabled the unit.
	install -Dm644 packaging/org.snapshotkit.Daemon.service \
		$(SHARE)/dbus-1/services/org.snapshotkit.Daemon.service

	install -Dm644 packaging/snapshotkit-editor.desktop  $(SHARE)/applications/snapshotkit-editor.desktop
	install -Dm644 packaging/snapshotkit-capture.desktop $(SHARE)/applications/snapshotkit-capture.desktop
	install -Dm644 packaging/snapshotkit.xml             $(SHARE)/mime/packages/snapshotkit.xml

	# What a software centre reads. Without it the package installs perfectly well and never
	# appears in GNOME Software as an application.
	install -Dm644 packaging/io.github.dvdstelt.SnapShotKit.metainfo.xml \
		$(SHARE)/metainfo/io.github.dvdstelt.SnapShotKit.metainfo.xml

	for size in $(ICON_SIZES); do \
		install -Dm644 packaging/icons/$$size.png \
			$(SHARE)/icons/hicolor/$${size}x$${size}/apps/snapshotkit.png; \
	done

	# The shell extension ships with the application rather than being downloaded separately: the
	# two speak a private D-Bus interface and have to move together.
	install -d $(SHARE)/gnome-shell/extensions/$(UUID)/schemas
	install -Dm644 src/extension/$(UUID)/extension.js  $(SHARE)/gnome-shell/extensions/$(UUID)/extension.js
	install -Dm644 src/extension/$(UUID)/metadata.json $(SHARE)/gnome-shell/extensions/$(UUID)/metadata.json
	install -Dm644 src/extension/$(UUID)/schemas/*.gschema.xml \
		$(SHARE)/gnome-shell/extensions/$(UUID)/schemas/
	glib-compile-schemas $(SHARE)/gnome-shell/extensions/$(UUID)/schemas/

	install -Dm644 src/SnapShotKit.Ui/Assets/Fonts/OFL.txt $(SHARE)/licenses/snapshotkit/OFL.txt

	# The caches that decide which application a file manager offers for a file. Without these an
	# installed-by-hand copy is on disk and invisible: the .ssk association and the image types
	# under "Open With" both come from here rather than from the files themselves.
	#
	# Only for a real installation. Under DESTDIR the files are in a buildroot nobody has installed
	# yet, and rebuilding the running system's caches from one would be a package build editing the
	# machine it happens to be running on. RPM has its own file triggers for this.
	@if [ -z "$(DESTDIR)" ]; then \
		update-desktop-database $(SHARE)/applications || true; \
		update-mime-database    $(SHARE)/mime         || true; \
	fi

	@echo "installed under $(DESTDIR)$(PREFIX)"

uninstall:
	rm -rf $(LIBEXEC) $(SHARE)/gnome-shell/extensions/$(UUID) $(SHARE)/licenses/snapshotkit
	rm -f $(BIN)/snapshotkit $(BIN)/snapshotkit-editor
	rm -f $(UNITDIR)/snapshotkitd.service
	rm -f $(SHARE)/applications/snapshotkit-editor.desktop $(SHARE)/applications/snapshotkit-capture.desktop
	rm -f $(SHARE)/mime/packages/snapshotkit.xml
	rm -f $(SHARE)/metainfo/io.github.dvdstelt.SnapShotKit.metainfo.xml
	rm -f $(SHARE)/dbus-1/services/org.snapshotkit.Daemon.service
	for size in $(ICON_SIZES); do \
		rm -f $(SHARE)/icons/hicolor/$${size}x$${size}/apps/snapshotkit.png; \
	done

clean:
	rm -rf build
	dotnet clean src/snapshotkit.slnx --nologo -v q || true
