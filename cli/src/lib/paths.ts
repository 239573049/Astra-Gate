import os from 'node:os';
import path from 'node:path';

export interface HomePaths {
  home: string;
  configFile: string;
  runtimeFile: string;
  installFile: string;
  logsDir: string;
  serverStdoutLog: string;
  dbFile: string;
  keysDir: string;
  desktopPrefix: string;
}

/** Data directory: ASTRA_HOME if set, else ~/.astra */
export function astraHome(env: NodeJS.ProcessEnv = process.env): string {
  const override = env.ASTRA_HOME?.trim();
  return override ? override : path.join(os.homedir(), '.astra');
}

export function homePaths(home: string): HomePaths {
  return {
    home,
    configFile: path.join(home, 'config.json'),
    runtimeFile: path.join(home, 'runtime.json'),
    installFile: path.join(home, 'install.json'),
    logsDir: path.join(home, 'logs'),
    serverStdoutLog: path.join(home, 'logs', 'server-stdout.log'),
    dbFile: path.join(home, 'astra.db'),
    keysDir: path.join(home, 'keys'),
    desktopPrefix: path.join(home, 'desktop'),
  };
}
