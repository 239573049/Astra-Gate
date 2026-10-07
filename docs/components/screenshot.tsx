import { cn } from '@/lib/cn';

export interface ScreenshotProps {
  /** Path under /public, e.g. /screenshots/overview.png */
  src: string;
  /** Accessibility text — describe what the screenshot shows. */
  alt: string;
  /** Title shown in the mock window chrome (defaults to 'Astra'). */
  title?: string;
  /** Caption rendered under the frame. */
  caption?: string;
  /** Intrinsic pixel size of the source image (screenshots are 1440×900 @2x). */
  width?: number;
  height?: number;
  className?: string;
}

/**
 * A screenshot inside a macOS-style window frame, used by the console tutorials.
 * Source PNGs live in `public/screenshots/` and are produced by `scripts/snap.mjs`
 * from the HTML mockups in `shots/`.
 */
export function Screenshot({
  src,
  alt,
  title = 'Astra',
  caption,
  width = 1440,
  height = 900,
  className,
}: ScreenshotProps) {
  return (
    <figure className={cn('not-prose my-6', className)}>
      <div className="shot-frame">
        <div className="flex h-9 items-center gap-2 border-b bg-[#f6f6f7] px-3.5 dark:bg-[#242428]">
          <span className="flex gap-1.5">
            <span className="size-3 rounded-full bg-[#ff5f57]" />
            <span className="size-3 rounded-full bg-[#febc2e]" />
            <span className="size-3 rounded-full bg-[#28c840]" />
          </span>
          <span className="pointer-events-none absolute left-1/2 -translate-x-1/2 truncate text-xs font-medium text-fd-muted-foreground">
            {title}
          </span>
        </div>
        {/* eslint-disable-next-line @next/next/no-img-element */}
        <img src={src} alt={alt} width={width} height={height} loading="lazy" />
      </div>
      {caption ? (
        <figcaption className="mt-2 text-center text-sm text-fd-muted-foreground">{caption}</figcaption>
      ) : null}
    </figure>
  );
}
