import type { ReactNode } from 'react';

export interface InspectorProps {
  kind?: string;
  icon?: ReactNode;
  title: string;
  sub?: string | null;
  rows: [string, ReactNode][];
  /** A monospace block (headers, rewrites, warnings). */
  code?: { title: string; lines: string[] } | null;
  /** Everything the span recorded (attributes and events), folded away under the summary. */
  raw?: { title: string; lines: string[] } | null;
  actions?: ReactNode;
}

/** The details of one trace node: what it did, how long it took, what it changed. */
export function Inspector({ kind, icon, title, sub, rows, code, raw, actions }: InspectorProps) {
  return (
    <div className="flex flex-col gap-3">
      <div className="flex flex-col gap-0.5 pr-6">
        {kind ? <span className="text-[11px] text-[var(--text-secondary)]">{kind}</span> : null}
        <div className="flex min-w-0 items-center gap-2.5">
          {icon}
          <div className="min-w-0">
            <div className="truncate text-[15px] font-semibold">{title}</div>
            {sub ? <div className="truncate text-[12px] text-[var(--text-secondary)]">{sub}</div> : null}
          </div>
        </div>
      </div>
      {rows.length > 0 ? (
        <div className="divide-y divide-[var(--separator)]">
          {rows.map(([label, value]) => (
            // values wrap rather than truncate: ids, retry reasons and timestamps must stay readable
            <div key={label} className="flex items-baseline justify-between gap-3 py-1 text-[12px]">
              <span className="shrink-0 text-[var(--text-secondary)]">{label}</span>
              <span className="selectable min-w-0 text-right break-words">{value}</span>
            </div>
          ))}
        </div>
      ) : null}
      {code && code.lines.length > 0 ? (
        <div className="flex flex-col gap-1.5">
          <span className="text-[11px] text-[var(--text-secondary)]">{code.title}</span>
          <div className="selectable flex flex-col rounded-[10px] bg-[var(--surface-muted)] px-3 py-2 font-mono text-[11.5px] leading-relaxed">
            {code.lines.map((line, i) => (
              <span
                key={i}
                className={line.startsWith('- ') ? 'text-[var(--red)]' : line.startsWith('+ ') ? 'text-[var(--green)]' : 'text-[var(--text-secondary)]'}
                style={{ whiteSpace: 'pre-wrap', wordBreak: 'break-all' }}
              >
                {line}
              </span>
            ))}
          </div>
        </div>
      ) : null}
      {raw && raw.lines.length > 0 ? (
        <details className="group flex flex-col gap-1.5">
          <summary className="cursor-pointer text-[11px] text-[var(--text-secondary)] select-none hover:text-[var(--foreground)]">{raw.title}</summary>
          <div className="selectable mt-1.5 flex flex-col rounded-[10px] bg-[var(--surface-muted)] px-3 py-2 font-mono text-[11px] leading-relaxed text-[var(--text-secondary)]">
            {raw.lines.map((line, i) => (
              <span key={i} style={{ whiteSpace: 'pre-wrap', wordBreak: 'break-all' }}>
                {line}
              </span>
            ))}
          </div>
        </details>
      ) : null}
      {actions ? <div className="flex flex-wrap gap-2">{actions}</div> : null}
    </div>
  );
}

/** A stacked bar of where time went, with its legend. */
export function TimeBar({ title, total, parts }: { title: string; total: string; parts: { label: string; value: number; text: string; color: string; note?: ReactNode }[] }) {
  const sum = parts.reduce((s, p) => s + Math.max(0, p.value), 0) || 1;
  return (
    <section className="card flex flex-col gap-2.5 p-3.5" aria-label={title}>
      <div className="flex justify-between gap-3 text-[12px]">
        <b>{title}</b>
        <span className="num text-[var(--text-secondary)]">{total}</span>
      </div>
      <div className="flex h-3.5 gap-0.5 overflow-hidden rounded-[7px]">
        {parts
          .filter((p) => p.value > 0)
          .map((p) => (
            <div key={p.label} style={{ flex: `${Math.max(p.value / sum, 0.004)} 1 0`, minWidth: 3, background: p.color }} title={`${p.label} ${p.text}`} />
          ))}
      </div>
      <div className="flex flex-wrap gap-x-4 gap-y-1.5 text-[12px] text-[var(--text-secondary)]">
        {parts.map((p) => (
          <span key={p.label} className="inline-flex items-center gap-1.5">
            <span className="inline-block size-2.5 rounded-[3px]" style={{ background: p.color }} />
            {p.label} <b className="num text-[var(--foreground)]">{p.text}</b>
            {p.note}
          </span>
        ))}
      </div>
    </section>
  );
}
