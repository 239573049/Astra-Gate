import * as path from 'node:path';

export interface AstraPaths {
  /** Data dir root: ~/.astra, overridable via ASTRA_HOME. */
  home: string;
  configFile: string;
  runtimeFile: string;
  installFile: string;
  logsDir: string;
  /** Path of the server's log file for a given day: astra-YYYYMMDD.log. */
  logFile: (date: Date) => string;
}

export function logFileName(date: Date): string {
  const y = date.getFullYear();
  const m = String(date.getMonth() + 1).padStart(2, '0');
  const d = String(date.getDate()).padStart(2, '0');
  return `astra-${y}${m}${d}.log`;
}

export function astraPaths(
  env: { ASTRA_HOME?: string | undefined },
  homedir: string,
): AstraPaths {
  const override = env.ASTRA_HOME?.trim();
  const home = override ? path.resolve(override) : path.join(homedir, '.astra');
  return {
    home,
    configFile: path.join(home, 'config.json'),
    runtimeFile: path.join(home, 'runtime.json'),
    installFile: path.join(home, 'install.json'),
    logsDir: path.join(home, 'logs'),
    logFile: (date: Date) => path.join(home, 'logs', logFileName(date)),
  };
}
