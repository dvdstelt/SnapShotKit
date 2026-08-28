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

        console.log('snapshotkit: keybindings and panel menu registered');
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
