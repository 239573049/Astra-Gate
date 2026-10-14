import { describe, expect, it } from 'vitest';

import {
  HIDDEN_ARG,
  desktopExecArg,
  linuxAutostartEnabled,
  linuxAutostartPath,
  linuxLoginExecutable,
  renderLinuxAutostart,
  retargetLinuxAutostart,
} from '../src/shared/loginItem';

describe('Linux autostart entry', () => {
  it('lives in the XDG autostart folder, under a name the launcher does not use', () => {
    expect(linuxAutostartPath({}, '/home/demo')).toBe('/home/demo/.config/autostart/io.astra.desktop.desktop');
    expect(linuxAutostartPath({ XDG_CONFIG_HOME: '/cfg' }, '/home/demo')).toBe('/cfg/autostart/io.astra.desktop.desktop');
  });

  it('starts the AppImage file, not its temporary mount', () => {
    expect(linuxLoginExecutable({ APPIMAGE: '/home/demo/Apps/Astra.AppImage' }, '/tmp/.mount_Astra/astra')).toBe(
      '/home/demo/Apps/Astra.AppImage',
    );
    expect(linuxLoginExecutable({}, '/home/demo/.astra/desktop/app/astra')).toBe('/home/demo/.astra/desktop/app/astra');
  });

  it('opens the app hidden in the tray', () => {
    const entry = renderLinuxAutostart(['/opt/Astra/astra', HIDDEN_ARG]);
    expect(entry).toContain('[Desktop Entry]\nType=Application\n');
    expect(entry).toContain('\nExec=/opt/Astra/astra --hidden\n');
    expect(entry).toContain('\nX-GNOME-Autostart-enabled=true\n');
  });

  it('counts an entry the desktop switched off as off', () => {
    const entry = renderLinuxAutostart(['/opt/Astra/astra', HIDDEN_ARG]);
    expect(linuxAutostartEnabled(entry)).toBe(true);
    expect(linuxAutostartEnabled(entry.replace('X-GNOME-Autostart-enabled=true', 'X-GNOME-Autostart-enabled=false'))).toBe(false);
    expect(linuxAutostartEnabled(`${entry}Hidden=true\n`)).toBe(false);
  });

  it('follows a moved AppImage without undoing anything else in the entry', () => {
    const entry = `${renderLinuxAutostart(['/old/Astra.AppImage', HIDDEN_ARG])}Hidden=true\n`;
    const next = retargetLinuxAutostart(entry, ['/new/Astra.AppImage', HIDDEN_ARG]);
    expect(next).toContain('\nExec=/new/Astra.AppImage --hidden\n');
    expect(next).toContain('\nHidden=true\n');
    expect(retargetLinuxAutostart(next!, ['/new/Astra.AppImage', HIDDEN_ARG])).toBeNull();
  });

  it('quotes Exec arguments the way the Desktop Entry spec reads them', () => {
    expect(desktopExecArg('/opt/Astra/astra')).toBe('/opt/Astra/astra');
    expect(desktopExecArg('/home/demo/My Apps/Astra.AppImage')).toBe('"/home/demo/My Apps/Astra.AppImage"');
    expect(desktopExecArg('/opt/100%/astra')).toBe('/opt/100%%/astra');
    // Quoting escapes " ` $ \ with a backslash; string escaping then doubles every backslash.
    expect(desktopExecArg('/opt/$HOME/a"b')).toBe('"/opt/\\\\$HOME/a\\\\"b"');
    expect(desktopExecArg('a\\b')).toBe('"a\\\\\\\\b"');
  });
});
