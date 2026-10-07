import type { ReactNode } from 'react';
import { cn } from '@/lib/cn';

/** A glass macOS window around a console screenshot (landing page). */
export function WindowFrame({
  title,
  children,
  className,
}: {
  title: string;
  children: ReactNode;
  className?: string;
}) {
  return (
    <div className={cn('glass overflow-hidden rounded-2xl p-1.5 sm:rounded-[22px] sm:p-2', className)}>
      <div className="relative flex h-8 items-center px-2.5">
        <span className="flex gap-1.5">
          <span className="size-3 rounded-full bg-[#ff5f57]" />
          <span className="size-3 rounded-full bg-[#febc2e]" />
          <span className="size-3 rounded-full bg-[#28c840]" />
        </span>
        <span className="pointer-events-none absolute left-1/2 hidden -translate-x-1/2 truncate text-xs font-medium text-fd-muted-foreground sm:block">
          {title}
        </span>
      </div>
      <div className="overflow-hidden rounded-xl border border-black/5 bg-white sm:rounded-[16px] dark:border-white/10">
        {children}
      </div>
    </div>
  );
}
