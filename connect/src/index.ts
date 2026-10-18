import { Command } from 'commander';
import { doctor } from './commands/doctor.js';
import { join } from './commands/join.js';
import { leave } from './commands/leave.js';
import { status } from './commands/status.js';
import { UserError, processIo } from './lib/io.js';
import { createRuntime } from './lib/runtime.js';
import { CancelledError, clackUi, plainUi, silentUi, type Ui } from './lib/ui.js';
import { VERSION } from './version.js';

const program = new Command();
program
  .name('astra-connect')
  .description('把本机的 AI 客户端（Claude Code、Codex、Gemini CLI …）接到企业的 Astra 网关，随时可还原。')
  .version(VERSION);

/** Runs a command with the right Ui and turns failures into a message and an exit code. */
async function run(json: boolean | undefined, body: (ui: Ui, io: ReturnType<typeof processIo>) => Promise<unknown>): Promise<void> {
  const io = processIo();
  const ui = json ? silentUi : io.interactive ? clackUi : plainUi;
  try {
    const result = await body(ui, json ? { ...io, interactive: false } : io);
    if (json) console.log(JSON.stringify(result, null, 2));
  } catch (e) {
    if (e instanceof CancelledError) process.exit(130);
    const message = e instanceof Error ? e.message : String(e);
    if (json) console.log(JSON.stringify({ error: message }));
    else console.error(`✗ ${message}`);
    process.exit(e instanceof UserError ? e.exitCode : 1);
  }
}

program
  .command('join [url]')
  .description('接入企业网关：验证 Key，把选中的客户端指向它')
  .option('--clients <kinds>', '要接入的客户端（逗号分隔，或 all = 所有检测到的）')
  .option('--model <id>', '默认模型')
  .option('-y, --yes', '不再询问，使用默认选择')
  .option('--key-stdin', '从标准输入读取 Key（也可用环境变量 ASTRA_API_KEY）')
  .option('--json', '输出机器可读的结果')
  .action((url: string | undefined, opts) =>
    run(opts.json, (ui, io) => join(createRuntime(), ui, { ...io, interactive: ui.interactive }, url, opts)),
  );

program
  .command('status')
  .description('查看连接状态、额度和各客户端的配置状态')
  .option('--json', '输出机器可读的结果')
  .action((opts) => run(opts.json, (ui) => status(createRuntime(), ui)));

program
  .command('leave')
  .description('还原客户端的原配置')
  .option('--clients <kinds>', '只还原这些客户端（逗号分隔）')
  .option('--force', '连被你改过的配置项也强制还原')
  .option('-y, --yes', '不再询问')
  .option('--keep-profile', '还原后保留已保存的网关地址和 Key')
  .option('--json', '输出机器可读的结果')
  .action((opts) => run(opts.json, (ui, io) => leave(createRuntime(), ui, io, opts)));

program
  .command('doctor')
  .description('检查网关连通性、Key 和各客户端配置')
  .option('--json', '输出机器可读的结果')
  .action((opts) =>
    run(opts.json, async (ui) => {
      const result = await doctor(createRuntime(), ui);
      if (!result.ok) process.exitCode = 1;
      return result;
    }),
  );

await program.parseAsync(process.argv);
