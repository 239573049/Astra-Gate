import pkg from '../package.json';

export const VERSION: string = pkg.version;

/**
 * npm name this build is published under: `astragate`, or `@aidotnet/astra-gate`
 * (release.yml rebuilds the same CLI under the scoped name). Self-update and
 * reinstall hints use it so they target the package the user actually installed.
 */
export const PACKAGE_NAME: string = pkg.name;
