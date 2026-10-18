import {
  ClientAdapterRegistry,
  ClientConfigApplier,
  ClientEnvironment,
  JsonFileStateStore,
  type IClientConfigStateStore,
} from '@aidotnet/client-config';
import { connectPaths, type ConnectPaths } from './profile.js';

/** Everything a command touches on the machine; tests build one over a fake home. */
export interface Runtime {
  paths: ConnectPaths;
  env: ClientEnvironment;
  store: IClientConfigStateStore;
  registry: ClientAdapterRegistry;
  applier: ClientConfigApplier;
  fetch: typeof fetch;
}

export function createRuntime(
  options: { paths?: ConnectPaths; env?: ClientEnvironment; fetch?: typeof fetch } = {},
): Runtime {
  const paths = options.paths ?? connectPaths();
  const env = options.env ?? ClientEnvironment.real();
  const store = new JsonFileStateStore(paths.stateFile);
  return {
    paths,
    env,
    store,
    registry: ClientAdapterRegistry.createDefault(env, store),
    applier: new ClientConfigApplier(store, env, paths.backupsDir),
    fetch: options.fetch ?? fetch,
  };
}
