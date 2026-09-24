// SnapShotKit's GNOME Shell extension.
//
// It does two things that cannot be done from outside the shell.
//
// The first is the keybinding. A shortcut registered by gnome-settings-daemon is not delivered
// while GNOME Shell holds a keyboard grab, which it does whenever a panel, quick settings or
// extension menu is open. Registered here, with Shell.ActionMode.POPUP among its modes, the same
// shortcut fires in exactly those situations.
//
// The second is the panel menu. A delayed capture is for photographing something that disappears
// the moment you touch the keyboard, which includes menus that close on a keypress; starting it
// from a menu item rather than a shortcut is the only way to catch some of them. The same menu is
// the obvious place to reach the editor and the folder the captures land in.
//
// The third is knowing where the windows are. Wayland tells a client nothing about any window but
// its own, so picking a window to capture is only possible if something inside the compositor says
// where they are. The extension answers that one question, for the daemon and nobody else.
//
// Everything is asked of the daemon over D-Bus. The extension deliberately knows no paths and
// launches no processes: it runs inside the compositor, where a mistake takes the desktop with it,
// and the daemon already knows where everything lives in a way that survives being packaged.

import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
import GObject from 'gi://GObject';
import Meta from 'gi://Meta';
import Shell from 'gi://Shell';
import St from 'gi://St';

import * as Main from 'resource:///org/gnome/shell/ui/main.js';
import * as PanelMenu from 'resource:///org/gnome/shell/ui/panelMenu.js';
import * as PopupMenu from 'resource:///org/gnome/shell/ui/popupMenu.js';
import {Extension} from 'resource:///org/gnome/shell/extensions/extension.js';

const SERVICE = 'org.snapshotkit.Daemon';
const OBJECT = '/org/snapshotkit/Daemon';

// What the daemon asks of the shell. Exported on the shell's own connection, since an extension
// has no bus name of its own.
const SHELL_OBJECT = '/org/snapshotkit/Shell';

const SHELL_INTERFACE = `
<node>
  <interface name="org.snapshotkit.Shell">
    <method name="Windows">
      <arg type="a(iiii)" direction="out" name="windows"/>
      <arg type="a(iiii)" direction="out" name="monitors"/>
    </method>
  </interface>
</node>`;

const Indicator = GObject.registerClass(
class SnapShotKitIndicator extends PanelMenu.Button {
    _init(delaySeconds, call) {
        super._init(0.0, 'SnapShotKit');

        this.add_child(new St.Icon({
            icon_name: 'camera-photo-symbolic',
            style_class: 'system-status-icon',
        }));

        this._add('Capture', () => call('Capture', null));

        this._add(`Capture in ${delaySeconds} seconds`, () =>
            call('CaptureDelayed', new GLib.Variant('(u)', [delaySeconds])));

        this.menu.addMenuItem(new PopupMenu.PopupSeparatorMenuItem());

        this._add('Open editor', () => call('OpenEditor', null));
        this._add('Open snapshots folder', () => call('OpenSnapshots', null));
    }

    _add(label, action) {
        const item = new PopupMenu.PopupMenuItem(label);
        item.connect('activate', () => action());
        this.menu.addMenuItem(item);
    }
});

export default class SnapShotKitExtension extends Extension {
    enable() {
        this._settings = this.getSettings();
        this._delayed = null;

        this._takeOverFromSettingsDaemon();

        this._bind('capture', () => this._call('Capture', null));
        this._bind('capture-delayed', () => this._captureDelayed());

        this._indicator = new Indicator(
            this._delaySeconds(),
            (method, args) => this._call(method, args));

        Main.panel.addToStatusArea(this.uuid, this._indicator);

        this._exportWindows();

        console.log('snapshotkit: keybindings and panel menu registered');
    }

    // Where the windows are is nobody's business but the capture's. GNOME closed its own window
    // introspection to outside callers for that reason, and an extension that handed the same
    // thing to anything on the session bus would be reopening it. So the daemon's bus name is
    // watched, and only whoever owns it gets an answer. Watching is asynchronous on purpose: a
    // blocking bus call made from inside the compositor stalls the desktop while it waits.
    _exportWindows() {
        this._daemonOwner = null;

        this._daemonWatch = Gio.bus_watch_name(
            Gio.BusType.SESSION, SERVICE, Gio.BusNameWatcherFlags.NONE,
            (connection, name, owner) => { this._daemonOwner = owner; },
            () => { this._daemonOwner = null; });

        this._shell = Gio.DBusExportedObject.wrapJSObject(SHELL_INTERFACE, this);
        this._shell.export(Gio.DBus.session, SHELL_OBJECT);
    }

    // Called by the bus for org.snapshotkit.Shell.Windows. The Async suffix is how GJS hands over
    // the invocation, which is the only place the caller's name can be read.
    WindowsAsync(parameters, invocation) {
        if (!this._daemonOwner || invocation.get_sender() !== this._daemonOwner) {
            invocation.return_dbus_error('org.snapshotkit.Shell.NotAllowed', 'Only the SnapShotKit daemon may ask.');
            return;
        }

        try {
            invocation.return_value(new GLib.Variant('(a(iiii)a(iiii))', [this._windows(), this._monitors()]));
        } catch (error) {
            console.error(`snapshotkit: could not list the windows: ${error.message}`);
            invocation.return_dbus_error('org.snapshotkit.Shell.Failed', error.message);
        }
    }

    // Topmost first, which is the order a press on the frozen screen has to be tried in. Only what
    // is actually showing on this workspace: a minimised window has a rectangle too, and offering
    // it would be offering a piece of whatever is on screen where it used to be.
    _windows() {
        const workspace = global.workspace_manager.get_active_workspace();

        return global.get_window_actors()
            .map(actor => actor.meta_window)
            .filter(window => window
                && !window.minimized
                && window.showing_on_its_workspace()
                && window.located_on_workspace(workspace)
                && [
                    Meta.WindowType.NORMAL,
                    Meta.WindowType.DIALOG,
                    Meta.WindowType.MODAL_DIALOG,
                    Meta.WindowType.UTILITY,
                ].includes(window.get_window_type()))
            .reverse()
            .map(window => {
                // The frame rather than the buffer: the buffer includes the shadow, and a captured
                // window with a margin of somebody else's desktop round it is not the window.
                const frame = window.get_frame_rect();
                return [frame.x, frame.y, frame.width, frame.height];
            });
    }

    // Primary first, because the daemon has to guess which monitor its frame shows and the primary
    // is the better guess.
    _monitors() {
        const monitors = Main.layoutManager.monitors.map(monitor => [monitor.x, monitor.y, monitor.width, monitor.height]);
        const primary = Main.layoutManager.primaryIndex;

        if (primary > 0 && primary < monitors.length) {
            monitors.unshift(...monitors.splice(primary, 1));
        }

        return monitors;
    }

    // SnapShotKit binds Print through gnome-settings-daemon as well, because a freshly installed
    // extension does not load until the session restarts and Print has to work before then. Once
    // this extension is running that binding is redundant, and leaving both registered on the same
    // key would mean two captures per press. Removing our own entry hands the shortcut over.
    _takeOverFromSettingsDaemon() {
        const OURS = '/org/gnome/settings-daemon/plugins/media-keys/custom-keybindings/snapshotkit/';

        try {
            const mediaKeys = new Gio.Settings({schema_id: 'org.gnome.settings-daemon.plugins.media-keys'});
            const bindings = mediaKeys.get_strv('custom-keybindings');

            if (!bindings.includes(OURS)) {
                return;
            }

            mediaKeys.set_strv('custom-keybindings', bindings.filter(binding => binding !== OURS));
            console.log('snapshotkit: took Print over from gnome-settings-daemon');
        } catch (error) {
            // Not fatal. At worst the old binding stays and the user sees a duplicate capture,
            // which is annoying rather than broken.
            console.error(`snapshotkit: could not take over the settings-daemon binding: ${error.message}`);
        }
    }

    disable() {
        // Timers outlive disable() unless removed, and a capture firing after the extension is gone
        // would be baffling to debug.
        if (this._delayed) {
            GLib.Source.remove(this._delayed);
            this._delayed = null;
        }

        Main.wm.removeKeybinding('capture');
        Main.wm.removeKeybinding('capture-delayed');

        this._indicator?.destroy();
        this._indicator = null;

        this._shell?.unexport();
        this._shell = null;

        if (this._daemonWatch) {
            Gio.bus_unwatch_name(this._daemonWatch);
            this._daemonWatch = null;
        }

        this._daemonOwner = null;

        this._settings = null;
        console.log('snapshotkit: keybindings and panel menu removed');
    }

    _delaySeconds() {
        return Math.max(1, this._settings.get_int('delay-seconds'));
    }

    _bind(key, handler) {
        Main.wm.addKeybinding(
            key,
            this._settings,
            Meta.KeyBindingFlags.NONE,
            // POPUP is the point of the whole extension: it is the mode that covers an open shell
            // menu, which is where a media-keys binding goes silent.
            Shell.ActionMode.NORMAL | Shell.ActionMode.OVERVIEW | Shell.ActionMode.POPUP,
            handler);
    }

    // The shortcut counts down in the shell so the notification can say what is happening. The
    // menu item hands the wait to the daemon instead, because the menu has to be gone before the
    // capture, and a shell timer would keep the extension alive across it for no reason.
    _captureDelayed() {
        if (this._delayed) {
            return;
        }

        const seconds = this._delaySeconds();

        Main.notify('SnapShotKit', `Capturing in ${seconds} seconds`);

        this._delayed = GLib.timeout_add_seconds(GLib.PRIORITY_DEFAULT, seconds, () => {
            this._delayed = null;
            this._call('Capture', null);
            return GLib.SOURCE_REMOVE;
        });
    }

    _call(method, args) {
        // Fire and forget. The daemon answers when the user has finished choosing a region, which
        // can be a long time, and the shell must not wait on that.
        Gio.DBus.session.call(
            SERVICE, OBJECT, SERVICE, method,
            args, null, Gio.DBusCallFlags.NONE, -1, null,
            (connection, result) => {
                try {
                    connection.call_finish(result);
                } catch (error) {
                    if (error.matches(Gio.DBusError, Gio.DBusError.SERVICE_UNKNOWN)) {
                        Main.notify('SnapShotKit', 'The SnapShotKit daemon is not running.');
                    } else {
                        console.error(`snapshotkit: ${method} failed: ${error.message}`);
                    }
                }
            });
    }
}
