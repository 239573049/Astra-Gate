import type { Metadata } from 'next';
import { PackageOpen } from 'lucide-react';

import { DownloadPanel } from '@/components/download/download-panel';
import { Markdown } from '@/components/download/markdown';
import { DEFAULT_CHANNEL, PREVIEW_CHANNEL, compareVersions, latestWith, readFeed } from '@/lib/releases';

export const dynamic = 'force-dynamic';

export const metadata: Metadata = {
  title: '下载 Astra Pet 桌面宠物',
  description: '下载 Astra Pet：一只住在桌面上的宠物，会走动、会回应点击、接入大模型陪你聊天。支持 macOS 和 Windows，有新版本时会在宠物头顶提醒。',
  alternates: {
    canonical: '/pet',
  },
};

const code = 'mx-1 rounded bg-fd-muted px-1.5 py-0.5 font-mono text-xs';

export default async function PetDownloadPage() {
  const latest = latestWith(await readFeed(DEFAULT_CHANNEL, 'pet'));
  // Previews stay out of the way: offered only while one is newer than the current stable, folded by default.
  const newestPreview = latestWith((await readFeed(PREVIEW_CHANNEL, 'pet')).filter((v) => v.channel === PREVIEW_CHANNEL));
  const preview =
    newestPreview && (!latest || compareVersions(newestPreview.version, latest.version) > 0) ? newestPreview : null;

  return (
    <div className="flex flex-1 flex-col justify-center px-6 py-24">
      <div className="mx-auto w-full max-w-4xl">
        <p className="text-sm font-medium text-fd-muted-foreground">Astra — 桌面宠物</p>
        <h1 className="mt-3 text-4xl font-bold">下载 Astra Pet</h1>
        <p className="mt-3 text-fd-muted-foreground">
          一只住在桌面上的宠物：会走动、会回应点击和摸头、可以接入大模型陪你聊天，也会提醒你喝水、起身活动。
        </p>
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
            <DownloadPanel
              variant="pet"
              version={latest.version}
              files={latest.files}
              hrefBase={`/api/pet-releases/${latest.channel}/v/${latest.version}`}
            />
          </div>
        )}

        {latest?.notes && (
          <div className="mt-8 rounded-xl border border-fd-border p-5">
            <h2 className="text-sm font-medium">版本说明</h2>
            <div className="prose mt-2">
              <Markdown text={latest.notes} />
            </div>
          </div>
        )}

        {preview && (
          <details className="mt-8 rounded-xl border border-dashed border-fd-border p-5">
            <summary className="cursor-pointer text-sm font-medium">
              预览版 v{preview.version}
              <span className="ml-2 font-normal text-fd-muted-foreground">
                {new Date(preview.releasedAt).toLocaleDateString('zh-CN')} · 尚未正式发布，可能不稳定
              </span>
            </summary>
            <p className="mt-3 text-sm text-fd-muted-foreground">
              预览版提前提供下一个正式版的新功能。在控制台「常规 → 更新」里把更新渠道切到「预览版」，之后会收到预览版和正式版的更新提醒。
            </p>
            <div className="mt-6">
              <DownloadPanel
                variant="pet"
                version={preview.version}
                files={preview.files}
                hrefBase={`/api/pet-releases/${PREVIEW_CHANNEL}/v/${preview.version}`}
              />
            </div>
            {preview.notes && (
              <div className="prose mt-6">
                <Markdown text={preview.notes} />
              </div>
            )}
          </details>
        )}

        <div className="mt-10 flex items-start gap-3 rounded-xl border border-fd-border bg-fd-muted/40 p-5">
          <PackageOpen className="mt-0.5 size-5 shrink-0 text-fd-muted-foreground" />
          <div className="text-sm text-fd-muted-foreground">
            <p>
              <strong>需要 ffmpeg。</strong>宠物的动画是带透明通道的 WebM（VP9），由 ffmpeg 解码，请先安装并确认带
              <code className={code}>libvpx-vp9</code>
              解码器。macOS：
              <code className={code}>brew install ffmpeg</code>
              ；Windows：下载 ffmpeg 并加入 PATH，或设置环境变量
              <code className={code}>FFMPEG</code>
              指向 ffmpeg.exe。
            </p>
            <p className="mt-1">
              <strong>macOS：</strong>解压得到 <code className={code}>Astra Pet.app</code>，拖进「应用程序」。应用没有经过 Apple 公证，首次打开请
              右键 → 打开；如果提示「已损坏」，在终端运行
              <code className={code}>xattr -dr com.apple.quarantine &quot;/Applications/Astra Pet.app&quot;</code>
              。
            </p>
            <p className="mt-1">
              <strong>Windows：</strong>解压后运行 <code className={code}>AstraPet.exe</code>。首次运行 SmartScreen 可能拦截，点「更多信息 → 仍要运行」。
            </p>
            <p className="mt-1">
              <strong>更新：</strong>宠物启动后和每隔几小时会自动检测新版本，有更新时在宠物头顶冒出气泡提醒；回到本页下载新的压缩包，覆盖旧版本即可，设置和记忆会保留。
            </p>
          </div>
        </div>
      </div>
    </div>
  );
}
