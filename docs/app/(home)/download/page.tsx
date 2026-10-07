import { Download, PackageOpen } from 'lucide-react';

import { DEFAULT_CHANNEL, latestVersion, readIndex, type ReleaseFile } from '@/lib/releases';

export const dynamic = 'force-dynamic';

const PLATFORM_LABELS: Record<string, string> = {
  'darwin-arm64': 'macOS · Apple 芯片 (M 系列及以上)',
  'darwin-x64': 'macOS · Intel 芯片',
  'win32-x64': 'Windows · x64',
  'win32-arm64': 'Windows · ARM',
  'linux-x64': 'Linux · x64',
  'linux-arm64': 'Linux · ARM',
};

/** Downloadable kinds, in display order. Anything else (e.g. the server-update
 * manifest, kind "json") is feed plumbing and stays off the page. */
const KIND_LABELS: Record<string, string> = {
  dmg: '安装镜像',
  exe: '安装程序',
  appimage: 'AppImage（免安装，直接运行）',
  zip: '压缩包（自动更新用）',
};

const PLATFORM_ORDER = Object.keys(PLATFORM_LABELS);
const KIND_ORDER = Object.keys(KIND_LABELS);

function rank(order: string[], value: string): number {
  const i = order.indexOf(value);
  return i === -1 ? order.length : i;
}

/** Installer files only, grouped by platform (table order) then kind. */
function downloadableFiles(files: ReleaseFile[]): ReleaseFile[] {
  return files
    .filter((f) => f.kind in KIND_LABELS)
    .sort(
      (a, b) =>
        rank(PLATFORM_ORDER, a.platform) - rank(PLATFORM_ORDER, b.platform) ||
        rank(KIND_ORDER, a.kind) - rank(KIND_ORDER, b.kind),
    );
}

function formatSize(bytes: number): string {
  if (bytes >= 1024 * 1024 * 1024) return `${(bytes / 1024 / 1024 / 1024).toFixed(2)} GB`;
  return `${(bytes / 1024 / 1024).toFixed(1)} MB`;
}

function platformLabel(platform: string): string {
  return PLATFORM_LABELS[platform] ?? platform;
}

function fileBadge(file: ReleaseFile): string {
  return KIND_LABELS[file.kind] ?? file.kind.toUpperCase();
}

export default async function DownloadPage() {
  const index = await readIndex(DEFAULT_CHANNEL);
  const latest = latestVersion(index);

  return (
    <div className="flex flex-1 flex-col justify-center px-6 py-24">
      <div className="mx-auto w-full max-w-4xl">
        <p className="text-sm font-medium text-fd-muted-foreground">Astra — 桌面客户端</p>
        <h1 className="mt-3 text-4xl font-bold">下载桌面客户端</h1>
        {latest ? (
          <p className="mt-3 text-fd-muted-foreground">
            最新版本 <span className="font-medium text-fd-foreground">v{latest.version}</span>
            <span className="mx-2">·</span>
            {new Date(latest.releasedAt).toLocaleDateString('zh-CN')}
          </p>
        ) : (
          <p className="mt-3 text-fd-muted-foreground">暂无发布版本，请稍后再来。</p>
        )}

        {latest && (
          <div className="mt-8 grid gap-4 sm:grid-cols-2">
            {downloadableFiles(latest.files).map((file) => (
              <a
                key={file.name}
                href={`/api/client-releases/stable/v/${latest.version}/${file.name}`}
                className="group rounded-xl border border-fd-border bg-fd-card p-5 transition-colors hover:border-fd-primary"
              >
                <div className="flex items-center justify-between">
                  <span className="font-medium">{platformLabel(file.platform)}</span>
                  <Download className="size-4 text-fd-muted-foreground transition-colors group-hover:text-fd-primary" />
                </div>
                <p className="mt-1 text-sm text-fd-muted-foreground">
                  {fileBadge(file)} · {formatSize(file.size)}
                </p>
                <p className="mt-3 truncate font-mono text-xs text-fd-muted-foreground" title={file.name}>
                  {file.name}
                </p>
              </a>
            ))}
          </div>
        )}

        {latest?.notes && (
          <div className="mt-8 rounded-xl border border-fd-border p-5">
            <h2 className="text-sm font-medium">版本说明</h2>
            <pre className="mt-2 whitespace-pre-wrap font-sans text-sm text-fd-muted-foreground">{latest.notes}</pre>
          </div>
        )}

        <div className="mt-10 flex items-start gap-3 rounded-xl border border-fd-border bg-fd-muted/40 p-5">
          <PackageOpen className="mt-0.5 size-5 shrink-0 text-fd-muted-foreground" />
          <div className="text-sm text-fd-muted-foreground">
            <p>
              安装包内置服务端，无需 Node.js。macOS 版通过应用内自动更新；Windows / Linux 版有新版本时，从本页下载新的安装包覆盖安装即可。
            </p>
            <p className="mt-1">
              服务端与命令行通过 npm 安装：
              <code className="mx-1 rounded bg-fd-muted px-1.5 py-0.5 font-mono text-xs">npm install -g @aidotnet/astra-gate</code>
              安装后运行
              <code className="mx-1 rounded bg-fd-muted px-1.5 py-0.5 font-mono text-xs">astra install --desktop</code>
              即可装好桌面端并接管本地客户端配置。也可以不全局安装，直接运行
              <code className="mx-1 rounded bg-fd-muted px-1.5 py-0.5 font-mono text-xs">npx @aidotnet/astra-gate install --desktop</code>
              。
            </p>
          </div>
        </div>
      </div>
    </div>
  );
}
