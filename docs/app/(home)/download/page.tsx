import { PackageOpen } from 'lucide-react';

import { DownloadPanel } from '@/components/download/download-panel';
import { DEFAULT_CHANNEL, latestVersion, readIndex } from '@/lib/releases';

export const dynamic = 'force-dynamic';

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
          <div className="mt-8">
            <DownloadPanel version={latest.version} files={latest.files} hrefBase={`/api/client-releases/stable/v/${latest.version}`} />
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
