import type { BaseLayoutProps } from 'fumadocs-ui/layouts/shared';
import { appName } from './shared';

export function baseOptions(): BaseLayoutProps {
  return {
    nav: {
      title: (
        <span className="inline-flex items-center gap-2 font-semibold">
          <span className="inline-flex size-6 items-center justify-center rounded-md bg-[var(--color-blue-500)] text-xs font-bold text-white">
            A
          </span>
          {appName}
        </span>
      ),
    },
  };
}
