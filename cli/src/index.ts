import process from 'node:process';
import { Argument, Command, InvalidArgumentError } from 'commander';
import { ApiError } from './lib/http.js';
import { AstraError } from './errors.js';
import { fail } from './ui.js';
import { CLIENT_KINDS, isClientKind, type ClientKind } from './lib/platform.js';

/** A client kind argument: a built-in one, or plugin-<agent id> for a client a plugin adds. */
function clientKindArgument(): Argument {
  return new Argument('<kind>', `Client kind (${CLIENT_KINDS.join(', ')}, or plugin-<id>)`).argParser(
    (value: string): ClientKind => {
      if (!isClientKind(value)) throw new InvalidArgumentError(`Unknown client kind "${value}".`);
      return value;
    },
  );
}
import { runStart, runStop, runRestart, runStatus, runLogs, runOpen } from './commands/manage.js';
import { runAutostart } from './commands/autostart.js';
import { runInstall, runUninstallClient, runUpdate } from './commands/desktop.js';
import { runUninstall } from './commands/uninstall.js';
import {
  runClientDisable,
  runClientEnable,
  runClientList,
  runClientStatus,
  runProviderAdd,
  runProviderImport,
  runProviderList,
  runProviderRemove,
  runTokenList,
} from './commands/admin.js';
import { runSetPassword } from './commands/config.js';
import {
  runPluginAdd,
  runPluginList,
  runPluginMarket,
  runPluginOptions,
  runPluginRemove,
  runPluginSwitch,
  runPluginUpdate,
  runPluginUse,
} from './commands/plugin.js';
import { runRestoreAll } from './commands/restore-all.js';
import { runClaude } from './commands/claude.js';
import { VERSION } from './version.js';

function addStartOptions(cmd: Command): Command {
  return cmd
    .option('--port <port>', 'TCP port to listen on (default 17321)')
    .option('--host <host>', 'Address to bind (default 127.0.0.1)')
    .option('--foreground', 'Run the server in the foreground instead of detached')
    .option('--open', 'Open the web UI in the default browser');
}

const program = new Command();

program
  .name('astra')
  .description('Astra — a local AI gateway. Manage the astra-server from the terminal.')
  .version(VERSION, '-v, --version', 'Print the CLI version');

// `astra` with no subcommand behaves like `astra start`
addStartOptions(program).action((opts) => runStart(opts));

addStartOptions(
  program.command('start').description('Start the Astra server (background by default)'),
).action((opts) => runStart(opts));

program
  .command('stop')
  .description('Stop the running Astra server')
  .action(() => runStop());

addStartOptions(program.command('restart').description('Restart the Astra server')).action(
  (opts) => runRestart(opts),
);

program
  .command('status')
  .description('Show whether the server is running and its details')
  .action(() => runStatus());

program
  .command('logs')
  .description('Show recent server logs')
  .option('-f, --follow', 'Keep printing new log output')
  .action((opts: { follow?: boolean }) => runLogs(opts));

program
  .command('open')
  .description('Open the Astra web UI in the default browser')
  .action(() => runOpen());

const autostart = program.command('autostart').description('Manage starting Astra at login');
autostart
  .command('enable')
  .description('Enable autostart (LaunchAgent / systemd --user / Run registry key)')
  .action(() => runAutostart('enable'));
autostart
  .command('disable')
  .description('Disable autostart')
  .action(() => runAutostart('disable'));
autostart
  .command('status')
  .description('Show whether autostart is enabled')
  .action(() => runAutostart('status'));

program
  .command('install')
  .description('Install optional Astra components')
  .option('--desktop', 'Install the Astra desktop app for this platform')
  .option('--client', 'Alias of --desktop')
  .action((opts: { desktop?: boolean; client?: boolean }) =>
    runInstall({ client: opts.desktop || opts.client }),
  );

program
  .command('uninstall')
  .description('Uninstall Astra (restore client configs, stop the server)')
  .option('--desktop', 'Only uninstall the desktop app')
  .option('--client', 'Alias of --desktop')
  .option('--purge', 'Also delete the Astra data directory (~/.astra)')
  .action((opts: { desktop?: boolean; client?: boolean; purge?: boolean }) => {
    if (opts.desktop || opts.client) return runUninstallClient();
    return runUninstall({ purge: opts.purge });
  });

program
  .command('update')
  .description('Update astra (and the desktop app when installed), then restart the server')
  .option('--desktop', 'Only update the desktop app')
  .option('--client', 'Alias of --desktop')
  .option('--check', 'Only check for updates, do not install')
  .option('--retry', 'Retry even after repeated update failures')
  .option('--channel <channel>', 'Release channel for this run: stable | preview (default: the server setting)')
  .action((opts: { desktop?: boolean; client?: boolean; check?: boolean; retry?: boolean; channel?: string }) =>
    runUpdate({ client: opts.desktop || opts.client, check: opts.check, retry: opts.retry, channel: opts.channel }),
  );

const client = program.command('client').description('Manage AI clients (Codex, Claude Code, …)');
client
  .command('list')
  .description('List clients and their status')
  .action(() => runClientList());
client
  .command('status')
  .description('Show one client in detail')
  .addArgument(clientKindArgument())
  .action((kind: ClientKind) => runClientStatus(kind));
client
  .command('enable')
  .description('Enable a client and point it at Astra')
  .addArgument(clientKindArgument())
  .requiredOption('--provider <id-or-name>', 'Provider id or name to bind')
  .option('--model <model>', 'Model to write into the client configuration')
  .option(
    '--token <id-or-name>',
    "Token to write into the client configuration (default: the client's current token, else the default token)",
  )
  .action(
    (kind: ClientKind, opts: { provider: string; model?: string; token?: string }) =>
      runClientEnable(kind, opts),
  );
client
  .command('disable')
  .description('Disable a client and restore its original configuration')
  .addArgument(clientKindArgument())
  .action((kind: ClientKind) => runClientDisable(kind));

const token = program.command('token').description('Manage tokens (credentials clients use to reach Astra)');
token
  .command('list')
  .description("List tokens with today's and lifetime usage")
  .action(() => runTokenList());

const provider = program.command('provider').description('Manage providers (upstream AI accounts)');
provider
  .command('list')
  .description('List providers')
  .action(() => runProviderList());
provider
  .command('add')
  .description('Add a provider from a template')
  .requiredOption('--template <id>', 'Provider template id (see `astra provider templates`)')
  .option('--variant <id>', 'Template variant, when the template has variants')
  .requiredOption('--key <apiKey>', 'API key for the provider')
  .option('--name <name>', 'Display name for the provider')
  .action((opts: { template: string; variant?: string; key: string; name?: string }) =>
    runProviderAdd(opts),
  );
provider
  .command('import')
  .description('Import providers from CC Switch, Alma, Claude Code, Codex or Magpie (lists only, unless --yes)')
  .option('--from <source>', 'Only this source: cc-switch | alma | claude-code | codex | magpie')
  .option('--yes', 'Import the new entries (or those named with --only)')
  .option('--only <ref...>', 'With --yes: import only these refs (see the listing)')
  .action((opts: { from?: string; yes?: boolean; only?: string[] }) => runProviderImport(opts));
provider
  .command('remove')
  .description('Remove a provider')
  .argument('<id>', 'Provider id')
  .action((id: string) => runProviderRemove(id));

const plugin = program
  .command('plugin')
  .alias('plugins')
  .description('Manage plugins (magpie-compatible provider plugins, gateway middleware and clients)');
plugin.command('list').alias('ls').description('List installed plugins').option('--json', 'Print JSON').action((opts: { json?: boolean }) => runPluginList(opts));
plugin
  .command('add')
  .alias('install')
  .description('Install a plugin: an npm package, a git repository (github:owner/repo) or a path')
  .argument('<spec>', 'What to install')
  .action((spec: string) => runPluginAdd(spec));
plugin
  .command('remove')
  .aliases(['rm', 'uninstall'])
  .description('Remove a plugin (a client it added gets its configuration back first)')
  .argument('<name>', 'Spec, package name or short name')
  .action((name: string) => runPluginRemove(name));
plugin.command('on').description('Switch a plugin on').argument('<name>').action((name: string) => runPluginSwitch(name, true));
plugin.command('off').description('Switch a plugin off').argument('<name>').action((name: string) => runPluginSwitch(name, false));
plugin
  .command('update')
  .alias('upgrade')
  .description("Update a plugin to npm's newest (every plugin when none is named)")
  .argument('[name]')
  .action((name?: string) => runPluginUpdate(name));
plugin
  .command('options')
  .alias('config')
  .description('Show a plugin\'s options, set them from JSON, or clear them with "off"')
  .argument('<name>')
  .argument('[json]')
  .action((name: string, json?: string) => runPluginOptions(name, json));
plugin
  .command('use')
  .alias('prefer')
  .description('Have a plugin serve a provider another plugin signs in to as well')
  .argument('<name>')
  .argument('<provider>', "The provider's id, as OpenCode knows it")
  .action((name: string, provider: string) => runPluginUse(name, provider));
plugin
  .command('market')
  .description("List the market's plugins, or search npm and GitHub")
  .argument('[query]')
  .action((query?: string) => runPluginMarket(query));

program
  .command('config')
  .description('Manage Astra configuration')
  .command('set-password')
  .description('Set the admin password (required before binding to a non-loopback address)')
  .action(() => runSetPassword());

program
  .command('claude')
  .description('Run the official Claude Code CLI with a Claude subscription account Astra holds')
  .option('--account <id>', 'Subscription account id (default: the account selected in Astra)')
  .option('--status', 'Show the account and token lifetime without starting Claude Code')
  .action((opts: { account?: string; status?: boolean }) => runClaude(opts));

program
  .command('restore-all')
  .description('Restore every managed client configuration offline (no server needed)')
  .option('--purge', 'Also remove Astra entries from client configs entirely')
  .action((opts: { purge?: boolean }) => runRestoreAll(opts));

program
  .parseAsync(process.argv)
  .then(() => {
    /* success — exit code already set by commands when needed */
  })
  .catch((err: unknown) => {
    if (err instanceof ApiError) {
      fail(`error: ${err.message} (HTTP ${err.status})`);
      process.exitCode = 1;
      return;
    }
    if (err instanceof AstraError) {
      fail(`error: ${err.message}`);
      if (err.hint) console.error(`  ${err.hint}`);
      process.exitCode = 1;
      return;
    }
    fail(`error: ${err instanceof Error ? err.message : String(err)}`);
    if (process.env.ASTRA_DEBUG) console.error(err);
    else console.error('Run with ASTRA_DEBUG=1 for a stack trace.');
    process.exitCode = 1;
  });
