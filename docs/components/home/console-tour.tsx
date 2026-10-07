'use client';

import { useState } from 'react';
import { cn } from '@/lib/cn';
import { WindowFrame } from './window-frame';

export interface TourShot {
  id: string;
  label: string;
  src: string;
}

/** Console screenshots behind a macOS-style segmented control (landing page). */
export function ConsoleTour({ shots }: { shots: TourShot[] }) {
  const [active, setActive] = useState(0);
  const current = shots[active] ?? shots[0];

  return (
    <div>
      <div className="flex justify-center">
        <div
          role="tablist"
          className="glass inline-flex max-w-full gap-1 overflow-x-auto rounded-full p-1"
        >
          {shots.map((shot, i) => (
            <button
              key={shot.id}
              type="button"
              role="tab"
              aria-selected={i === active}
              onClick={() => setActive(i)}
              className={cn(
                'shrink-0 rounded-full px-4 py-1.5 text-sm font-medium transition-colors',
                i === active
                  ? 'bg-[var(--accent)] text-white shadow-sm'
                  : 'text-fd-muted-foreground hover:text-fd-foreground',
              )}
            >
              {shot.label}
            </button>
          ))}
        </div>
      </div>
      {current ? (
        <WindowFrame title={`Astra Gate — ${current.label}`} className="mt-8">
          {/* eslint-disable-next-line @next/next/no-img-element */}
          <img
            key={current.src}
            src={current.src}
            alt={current.label}
            width={1440}
            height={900}
            loading="lazy"
            className="block h-auto w-full"
          />
        </WindowFrame>
      ) : null}
    </div>
  );
}
