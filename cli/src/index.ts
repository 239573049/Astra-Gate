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
  runClientShortlist,
  runClientUse,
  runProfileList,
  runProfileRemove,
  runProfileSave,
  runProfileUse,
  runProviderAdd,
  runProviderExport,
  runProviderImport,
  runProviderImportFile,
  runProviderList,
  runProviderRemove,
  runProviderKeysAdd,
  runProviderKeysList,
  runProviderKeysRemove,
  runProviderKeysSwitch,
  runTokenList,
} from './commands/admin.js';
import {
  runBackupAuto,
  runBackupCreate,
  runBackupList,
  runBackupRestore,
  runSyncClearConflicts,
  runSyncEnable,
  runSyncNow,
  runSyncSetup,
  runSyncStatus,
  runSyncTest,
  type SyncSetupOptions,
} from './commands/backup.js';
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
  runPluginGrant,
  runPluginRun,
  runPluginSettings,
  runPluginUpgrade,
  runPluginRollback,
  runPluginDev,
  runPluginTest,
  runPluginV2List,
} from './commands/plugin.js';
import { runToolList, runToolRun } from './commands/tool.js';
import { runGroupAdd, runGroupList, runGroupRemove, runGroupSet, runGroupShow } from './commands/group.js';
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
  .command('use')
  .description('Switch a client to <provider>/<model> in one step (e.g. `astra client use claude-code deepseek/deepseek-v4-pro`)')
  .addArgument(clientKindArgument())
  .argument('<target>', "provider/model, or a model of the client's current provider")
  .action((kind: ClientKind, target: string) => runClientUse(kind, target));
client
  .command('shortlist')
  .description('Limit the models a client sees to the ones listed (none = no limit)')
  .addArgument(clientKindArgument())
  .argument('[models...]', 'Model ids the client should see')
  .action((kind: ClientKind, models: string[]) => runClientShortlist(kind, models));
client
  .command('disable')
  .description('Disable a client and restore its original configuration')
  .addArgument(clientKindArgument())
  .action((kind: ClientKind) => runClientDisable(kind));

const profile = program
  .command('profile')
  .description("Profiles: save every client's setup under a name and switch them all at once");
profile
  .command('list')
  .description('List profiles')
  .action(() => runProfileList());
profile
  .command('save')
  .description("Save the clients' current setup as a profile (overwrites a profile of the same name)")
  .argument('<name>')
  .action((name: string) => runProfileSave(name));
profile
  .command('use')
  .description('Apply a profile: switch every client in it through the normal enable / disable path')
  .argument('<name>')
  .option('--dry-run', 'Only show what would change')
  .action((name: string, opts: { dryRun?: boolean }) => runProfileUse(name, opts));
profile
  .command('rm')
  .description("Delete a profile (the clients' configurations do not change)")
  .argument('<name>')
  .action((name: string) => runProfileRemove(name));

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
  .command('export')
  .description('Export providers and their models to a backup file (no API keys unless asked)')
  .option('--out <file>', 'File to write (default: ./astra-providers-<date>.json)')
  .option('--force', 'Replace the file when it already exists')
  .option('--include-keys', 'Write API keys in plain text (this machine only)')
  .option('--passphrase-stdin', 'Encrypt API keys with a passphrase read from stdin (or ASTRA_BACKUP_PASSPHRASE)')
  .option('--id <id...>', 'Only these provider ids')
  .action((opts: { out?: string; force?: boolean; includeKeys?: boolean; passphraseStdin?: boolean; id?: string[] }) =>
    runProviderExport(opts),
  );
provider
  .command('import-file <file>')
  .description('Preview a backup file made by `provider export`, or with --yes import its providers')
  .option('--yes', 'Import the new entries (or those named with --only)')
  .option('--only <row...>', 'With --yes: import only these preview rows')
  .action((file: string, opts: { yes?: boolean; only?: string[] }) => runProviderImportFile(file, opts));
const providerKeys = provider.command('keys').description('Several API keys on one provider');
providerKeys
  .command('list')
  .description("List a provider's API keys")
  .argument('<provider>', 'Provider id or name')
  .action((id: string) => runProviderKeysList(id));
providerKeys
  .command('add')
  .description('Add an API key to a provider (the key is read from --key, ASTRA_API_KEY or a hidden prompt)')
  .argument('<provider>', 'Provider id or name')
  .option('--key <apiKey>', 'The API key')
  .option('--name <name>', 'Name shown in the list')
  .option('--protocol <protocol>', 'Use the key for this protocol only: openai-chat | openai-responses | anthropic | gemini')
  .option('--weight <n>', "Share of balanced traffic, 1-1000 (default 1)")
  .option('--proxy <url>', "The key's own HTTP proxy")
  .option('--notes <text>', 'A note')
  .action((id: string, opts: { key?: string; name?: string; protocol?: string; weight?: string; proxy?: string; notes?: string }) =>
    runProviderKeysAdd(id, opts),
  );
providerKeys
  .command('remove')
  .description('Remove an API key')
  .argument('<provider>', 'Provider id or name')
  .argument('<key>', 'Key id, name or its number in the list')
  .action((id: string, key: string) => runProviderKeysRemove(id, key));
providerKeys
  .command('enable')
  .description('Switch an API key on')
  .argument('<provider>', 'Provider id or name')
  .argument('<key>', 'Key id, name or its number in the list')
  .action((id: string, key: string) => runProviderKeysSwitch(id, key, true));
providerKeys
  .command('disable')
  .description('Switch an API key off (it stays in the list)')
  .argument('<provider>', 'Provider id or name')
  .argument('<key>', 'Key id, name or its number in the list')
  .action((id: string, key: string) => runProviderKeysSwitch(id, key, false));
provider
  .command('remove')
  .description('Remove a provider')
  .argument('<id>', 'Provider id')
  .action((id: string) => runProviderRemove(id));

const tool = program
  .command('tool')
  .description("Run Astra's built-in tools: web search, web fetch, image generation and plugin tools (this machine only)");
tool.command('list').alias('ls').description('List the tools').option('--json', 'Print JSON').action((opts: { json?: boolean }) => runToolList(opts));
tool
  .command('run', { isDefault: true })
  .description('Run a tool, e.g. astra tool generate_image "a magpie, watercolor" -o hero.png')
  .argument('<name>', 'web_search, web_fetch, generate_image or a plugin tool (see `astra tool list`)')
  .argument('[words...]', "The tool's main argument (query, url or prompt)")
  .option('-o, --output <file>', 'Save an image (or the text) here')
  .option('-a, --arg <key=value...>', 'Another argument; repeat for more')
  .option('--json <object>', 'Arguments as a JSON object')
  .option('--token <key>', 'The gateway key an image is generated as (default: $ASTRA_API_KEY)')
  .option('--raw', 'Print the tool result as JSON')
  .action((name: string, words: string[], opts: { output?: string; arg?: string[]; json?: string; token?: string; raw?: boolean }) =>
    runToolRun(name, words, opts),
  );

const group = program
  .command('group')
  .alias('groups')
  .description('Routing groups: virtual models group/<slug> over several providers (smart, order, rotate, usage, pace, weight)');
group
  .command('list')
  .description('List the routing groups')
  .action(() => runGroupList());
group
  .command('show')
  .description("Show a group's members with their live state")
  .argument('<group>', 'Group id, slug or name')
  .action((ref: string) => runGroupShow(ref));
group
  .command('add')
  .description('Create a group: astra group add "Opus anywhere" models=claude/claude-opus-5-5,copilot/claude-opus-5.5 routing=smart')
  .argument('<name>', 'Display name; the model id is group/<slug of the name>')
  .argument('[settings...]', 'models=provider/model,… routing=smart|order|rotate|usage|pace|weight stays=auto|session|turn|off|strict weight=member=n,… listed=yes|no slug=…')
  .action((name: string, words: string[]) => runGroupAdd(name, words));
group
  .command('set')
  .description('Change a group: models=… (replace), models+=… (add), models-=… (remove), routing=…, stays=…, weight=…, listed=…, enabled=…, name=…')
  .argument('<group>', 'Group id, slug or name')
  .argument('<settings...>', 'key=value words')
  .action((ref: string, words: string[]) => runGroupSet(ref, words));
group
  .command('rm')
  .alias('remove')
  .description('Delete a group (one another group contains cannot be deleted)')
  .argument('<group>', 'Group id, slug or name')
  .action((ref: string) => runGroupRemove(ref));

const plugin = program
  .command('plugin')
  .alias('plugins')
  .description('Manage plugins (magpie-compatible provider plugins, gateway middleware and clients)');
plugin.command('v2').description('List v2 plugins: state, permissions waiting for a grant, commands').option('--json', 'Print JSON').action((opts: { json?: boolean }) => runPluginV2List(opts));
plugin
  .command('grant')
  .description("Grant a v2 plugin the permissions it asks for (after showing them)")
  .argument('<id>')
  .option('-p, --permission <permission...>', 'Only these permissions (e.g. one of its optional ones)')
  .option('-y, --yes', "Don't ask")
  .action((id: string, opts: { yes?: boolean; permission?: string[] }) => runPluginGrant(id, opts));
plugin
  .command('run')
  .description("Run a v2 plugin's command")
  .argument('<id>')
  .argument('<command>')
  .argument('[args]', 'Arguments as JSON')
  .action((id: string, command: string, args?: string) => runPluginRun(id, command, args));
plugin
  .command('settings')
  .description("Show a v2 plugin's settings, or set them from JSON")
  .argument('<id>')
  .argument('[values]', 'Settings as a JSON object')
  .option('-s, --secret <name=value...>', 'Set a secret setting (empty value clears it)')
  .action((id: string, values: string | undefined, opts: { secret?: string[] }) => runPluginSettings(id, values, opts));
plugin
  .command('upgrade')
  .description('List v1 plugins that have a v2 version, or upgrade one')
  .argument('[name]')
  .option('-y, --yes', "Don't ask")
  .action((name: string | undefined, opts: { yes?: boolean }) => runPluginUpgrade(name, opts));
plugin
  .command('dev')
  .description('Develop a v2 plugin from a folder: reloads on save, prints its log, unmounts on Ctrl+C (this machine only)')
  .argument('[dir]', 'The plugin folder (with astra.plugin.json)', '.')
  .option('-y, --yes', 'Grant the permissions it asks for without asking')
  .option('--keep', 'Leave it mounted after Ctrl+C')
  .action((dir: string, opts: { yes?: boolean; keep?: boolean }) => runPluginDev(dir, opts));
plugin
  .command('test')
  .description("Run a v2 plugin folder's tests in Astra's own sandbox (the real astra module and host calls; this machine only)")
  .argument('[dir]', 'The plugin folder (with astra.plugin.json)', '.')
  .option('--json', 'Print JSON')
  .action((dir: string, opts: { json?: boolean }) => runPluginTest(dir, opts));
plugin
  .command('rollback')
  .description('Go back to the v1 plugin a v2 one replaced (within 30 days of the upgrade)')
  .argument('<id>')
  .option('-y, --yes', "Don't ask")
  .action((id: string, opts: { yes?: boolean }) => runPluginRollback(id, opts));
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

const backup = program.command('backup').description('Back up and restore the whole Astra setup (providers, tokens, clients, settings ...)');
backup
  .command('create')
  .description('Write a backup file; API keys are included, encrypted with a passphrase, unless --no-keys')
  .option('--no-keys', 'No API keys and no passphrase: a plain file without secrets')
  .option('-o, --out <file>', 'File to write (default: the name the server suggests, in the current directory)')
  .option('--sections <list>', 'Comma-separated sections to include (default: all)')
  .option('--include-usage', 'Also include usage history')
  .option('--passphrase-stdin', 'Read the passphrase from stdin (or ASTRA_BACKUP_PASSPHRASE)')
  .action((opts: { keys?: boolean; out?: string; sections?: string; includeUsage?: boolean; passphraseStdin?: boolean }) =>
    runBackupCreate(opts),
  );
backup
  .command('restore <file>')
  .description('Preview a backup file, then restore it (asks first; a safety copy is kept)')
  .option('--sections <list>', 'Comma-separated sections to restore (default: all in the file)')
  .option('--replace', 'Make this Astra match the backup: remove what the backup does not have')
  .option('--dry-run', 'Only show what would change')
  .option('--yes', 'Do not ask for confirmation')
  .action((file: string, opts: { sections?: string; replace?: boolean; dryRun?: boolean; yes?: boolean }) =>
    runBackupRestore(file, opts),
  );
backup
  .command('list')
  .description('List the automatic backups')
  .action(() => runBackupList());
backup
  .command('auto')
  .description('Show or change the automatic backups')
  .option('--on', 'Turn them on')
  .option('--off', 'Turn them off')
  .option('--hours <n>', 'Hours between backups')
  .option('--keep <n>', 'How many to keep')
  .option('--run', 'Make a backup now')
  .action((opts: { on?: boolean; off?: boolean; hours?: string; keep?: string; run?: boolean }) => runBackupAuto(opts));

const sync = program.command('sync').description('Keep several machines in step through a folder, WebDAV, S3 or GitHub');
sync
  .command('status')
  .description('Show the sync configuration, state and conflicts')
  .action(() => runSyncStatus());
sync
  .command('now')
  .description('Sync now')
  .option('--dry-run', 'Only show what would be pushed and pulled')
  .action((opts: { dryRun?: boolean }) => runSyncNow(opts));
sync
  .command('enable')
  .description('Turn periodic sync on')
  .action(() => runSyncEnable(true));
sync
  .command('disable')
  .description('Turn periodic sync off')
  .action(() => runSyncEnable(false));
sync
  .command('test')
  .description('Check that the sync target is reachable')
  .action(() => runSyncTest());
sync
  .command('clear-conflicts')
  .description('Clear the list of settled conflicts')
  .action(() => runSyncClearConflicts());
sync
  .command('setup')
  .description('Set the sync target (secret and passphrase: ASTRA_SYNC_SECRET / ASTRA_SYNC_PASSPHRASE, --...-stdin, or a prompt; never argv)')
  .requiredOption('--kind <kind>', 'folder | webdav | s3 | github')
  .option('--url <url>', 'Folder path, WebDAV address, S3 endpoint or GitHub repository')
  .option('--username <name>', 'User name (WebDAV) or access key id (S3)')
  .option('--folder <name>', 'Folder inside the target (default astra)')
  .option('--bucket <name>', 'S3 bucket')
  .option('--region <region>', 'S3 region')
  .option('--path-style', 'S3 path-style addressing')
  .option('--branch <name>', 'GitHub branch')
  .option('--interval <minutes>', 'Minutes between syncs')
  .option('--sections <list>', 'Comma-separated sections to sync')
  .option('--no-keys', 'Do not sync API keys')
  .option('--signins', 'Also sync plugin sign-ins')
  .option('--secret-stdin', 'Read the target password / token from stdin')
  .option('--passphrase-stdin', 'Read the sync passphrase from stdin (the line after the secret when both)')
  .action((opts: SyncSetupOptions) => runSyncSetup(opts));

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
