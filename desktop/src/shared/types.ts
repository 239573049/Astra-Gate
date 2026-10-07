/** Shape of ~/.astra/runtime.json as written by a running server. */
export interface RuntimeInfo {
  pid: number;
  port: number;
  host?: string;
  version?: string;
  apiVersion?: string;
  startedBy?: string;
  startedAt?: string;
  runtimeToken?: string;
}

/** Shape of ~/.astra/install.json as written by the CLI. */
export interface InstallInfo {
  serverPath?: string;
  serverVersion?: string;
  desktopPath?: string;
  desktopVersion?: string;
  updatedAt?: string;
}

/** Subset of GET /api/clients items the desktop app relies on. */
export interface GatewayClient {
  kind: string;
  name: string;
  enabled: boolean;
  providerId: string | null;
}

/** Subset of GET /api/providers items the desktop app relies on. */
export interface GatewayProvider {
  id: string;
  name: string;
  enabled: boolean;
}

/** Value the desktop app passes as `--started-by`. */
export const STARTED_BY = 'desktop' as const;
