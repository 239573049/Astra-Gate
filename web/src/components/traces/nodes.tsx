import { BaseEdge, EdgeLabelRenderer, getBezierPath, Handle, Position, type Edge, type EdgeProps, type NodeProps } from '@xyflow/react';

import { useContext } from 'react';

import type { ClientKind } from '../../api/types';
import logoMark from '../../assets/logo-mark.png';
import { useI18n } from '../../i18n';
import { cn } from '../../lib/cn';
import { ClientIcon, ProviderIcon } from '../icons';
import { FailedDot, LiveDot, PendingLine } from './live';
import type { AccountRow, CardNode, FlowData, HubNode, ProviderNode, StepData } from './model';
import { TraceSelectContext } from './select';

// Every side carries a handle; the canvas runs in loose mode, so an edge names the sides it leaves and enters by.
const SIDES = [
  ['left', Position.Left],
  ['right', Position.Right],
  ['top', Position.Top],
  ['bottom', Position.Bottom],
] as const;

function Handles() {
  return (
    <>
      {SIDES.map(([id, position]) => (
        <Handle key={id} id={id} type="source" position={position} isConnectable={false} />
      ))}
    </>
  );
}

/** A client, provider, gateway stage, attempt, model call or tool: brand icon + title + secondary lines. */
export function CardNodeView({ data, selected }: NodeProps<CardNode>) {
  const { t } = useI18n();
  const icon = data.client ? (
    <ClientIcon kind={data.client as ClientKind} size={data.stacked ? 28 : 26} />
  ) : data.provider ? (
    <ProviderIcon name={data.provider.name} icon={data.provider.icon} colorKey={data.provider.templateId} size={data.stacked ? 28 : 26} />
  ) : null;
  return (
    <div
      className={cn(
        'trace-node relative',
        data.tone && `tone-${data.tone}`,
        data.pill && 'is-pill',
        data.group && 'is-group',
        data.pending && 'is-pending',
        selected && 'is-selected',
        data.dim && 'is-dim',
      )}
    >
      <Handles />
      {data.flag ? <span className={cn('trace-flag', `tone-${data.flag.tone}`)}>{data.flag.text}</span> : null}
      {data.live ? (
        <span className="trace-live-badge" aria-label={t('traces.live.badge', { count: data.live })}>
          <span className="trace-live-pulse" />
          {t('traces.live.badge', { count: data.live })}
        </span>
      ) : null}
      <div className={cn('flex h-full min-w-0', data.stacked ? 'flex-col justify-center gap-2' : 'items-center gap-2.5')}>
        {icon}
        <div className="min-w-0">
          <div className="truncate text-[12.5px] font-semibold">{data.title}</div>
          {data.lines.map((line, i) => (
            <div key={i} className="truncate text-[11.5px] text-[var(--text-secondary)]">
              {line}
            </div>
          ))}
          {data.status ? <div className={cn('trace-status truncate', `tone-${data.status.tone}`)}>{data.status.text}</div> : null}
          {data.pending ? <PendingLine id={data.pending} /> : null}
        </div>
      </div>
    </div>
  );
}

/** The Astra hub: only the logo — with, above it, the switch a request just made or where one was just sent. */
export function HubNodeView({ data, selected }: NodeProps<HubNode>) {
  return (
    <div className="relative">
      {data.note ? (
        <div key={data.note.text} className={cn('trace-hub-note', `tone-${data.note.tone}`)}>
          {data.note.text}
        </div>
      ) : null}
      <div className={cn('trace-hub', selected && 'is-selected', data.busy && 'is-busy')}>
        <img src={logoMark} alt="" draggable={false} />
      </div>
      <Handles />
    </div>
  );
}

/**
 * A provider on the topology: brand, name and a status line, then one row per account (or its API key). Each row carries
 * the handle its line from the hub ends on, so a request is drawn on the account that serves it.
 */
export function ProviderNodeView({ data, selected }: NodeProps<ProviderNode>) {
  const { t } = useI18n();
  return (
    <div className={cn('trace-node trace-provider relative', `tone-${data.tone}`, selected && 'is-selected', data.dim && 'is-dim')}>
      <Handle id="left" type="source" position={Position.Left} isConnectable={false} />
      {data.live ? (
        <span className="trace-live-badge" aria-label={t('traces.live.badge', { count: data.live })}>
          <span className="trace-live-pulse" />
          {t('traces.live.badge', { count: data.live })}
        </span>
      ) : null}
      <div className="flex min-w-0 items-center gap-2.5">
        <ProviderIcon name={data.provider.name} icon={data.provider.icon} colorKey={data.provider.templateId} size={26} />
        <div className="min-w-0">
          <div className="truncate text-[12.5px] font-semibold">{data.title}</div>
          {data.lines.map((line, i) => (
            <div key={i} className="truncate text-[11.5px] text-[var(--text-secondary)]">
              {line}
            </div>
          ))}
        </div>
      </div>
      <div className="trace-rows">
        {data.rows.map((row) => (
          <AccountRowView key={row.id} row={row} />
        ))}
      </div>
    </div>
  );
}

function AccountRowView({ row }: { row: AccountRow }) {
  const tone = row.status === 'ok' ? 'ok' : row.status === 'cooldown' ? 'warn' : 'off';
  return (
    <div className={cn('trace-row', row.hit && `is-${row.hit}`, row.dim && 'is-dim')} title={`${row.name} · ${row.detail}`}>
      <Handle id={row.id} type="target" position={Position.Left} isConnectable={false} className="trace-row-handle" />
      <span className={cn('trace-row-dot', `tone-${tone}`)} />
      <span className="min-w-0 flex-1 truncate">{row.name}</span>
      {row.live > 0 ? <span className="trace-row-live">{row.live}</span> : null}
      <span className={cn('shrink-0 truncate text-[11px]', tone === 'ok' ? 'text-[var(--text-secondary)]' : 'text-[var(--orange)]')}>{row.detail}</span>
    </div>
  );
}

/** A traffic line of the topology: its width carries the volume; every request in flight rides it as a dot. */
export function FlowEdgeView({ id, sourceX, sourceY, targetX, targetY, sourcePosition, targetPosition, style, label, data }: EdgeProps<Edge<FlowData>>) {
  const [path, labelX, labelY] = getBezierPath({ sourceX, sourceY, sourcePosition, targetX, targetY, targetPosition });
  const role = data?.role ?? 'client';
  const end = role === 'client' ? { x: sourceX, y: sourceY } : { x: targetX, y: targetY };
  const failing = (data?.failed?.length ?? 0) > 0;
  return (
    <>
      <BaseEdge id={id} path={path} style={style} className={cn(data?.retry && 'trace-edge-retry', failing && 'trace-edge-failed')} />
      {(data?.requests ?? []).map((request) => (
        <LiveDot key={request} id={request} path={path} role={role} end={end} />
      ))}
      {(data?.failed ?? []).map((request) => (
        <FailedDot key={`failed:${request}`} id={request} path={path} end={end} />
      ))}
      {/* in the HTML label layer, above the dots riding the line */}
      {label ? (
        <EdgeLabelRenderer>
          <div className="trace-edge-label nodrag nopan" style={{ transform: `translate(-50%, -50%) translate(${labelX}px, ${labelY}px)` }}>
            {label}
          </div>
        </EdgeLabelRenderer>
      ) : null}
    </>
  );
}

/** A rounded orthogonal path through <paramref>points</paramref>. */
function rounded(points: [number, number][], r = 12): string {
  let d = `M ${points[0][0]} ${points[0][1]}`;
  for (let i = 1; i < points.length - 1; i++) {
    const [px, py] = points[i - 1];
    const [x, y] = points[i];
    const [nx, ny] = points[i + 1];
    const inLen = Math.hypot(x - px, y - py);
    const outLen = Math.hypot(nx - x, ny - y);
    const k = Math.min(r, inLen / 2, outLen / 2);
    const ax = x - ((x - px) / (inLen || 1)) * k;
    const ay = y - ((y - py) / (inLen || 1)) * k;
    const bx = x + ((nx - x) / (outLen || 1)) * k;
    const by = y + ((ny - y) / (outLen || 1)) * k;
    d += ` L ${ax} ${ay} Q ${x} ${y} ${bx} ${by}`;
  }
  const last = points[points.length - 1];
  return `${d} L ${last[0]} ${last[1]}`;
}

/**
 * A step of the agent loop: the tools run between two calls are its label (click it for the details). A step that
 * wraps to the next row runs round the right edge, back along the gap above that row, into its first step.
 */
export function StepEdgeView({ id, sourceX, sourceY, targetX, targetY, sourcePosition, targetPosition, style, markerEnd, data }: EdgeProps<Edge<StepData>>) {
  const select = useContext(TraceSelectContext);
  let path: string;
  let labelX: number;
  let labelY: number;
  const wrap = data?.wrap;
  if (wrap && targetY > sourceY + 60) {
    const rx = Math.max(wrap.rightX, sourceX + 24);
    const lx = Math.min(wrap.leftX, targetX - 24);
    const gy = Math.min(Math.max(wrap.gapY, sourceY + 30), targetY - 30);
    path = rounded([[sourceX, sourceY], [rx, sourceY], [rx, gy], [lx, gy], [lx, targetY], [targetX, targetY]]);
    labelX = (rx + lx) / 2;
    labelY = gy;
  } else {
    [path, labelX, labelY] = getBezierPath({ sourceX, sourceY, sourcePosition, targetX, targetY, targetPosition });
  }
  return (
    <>
      <BaseEdge id={id} path={path} style={style} markerEnd={markerEnd} />
      {data?.label ? (
        <EdgeLabelRenderer>
          <button
            type="button"
            title={data.label}
            aria-label={data.label}
            onClick={() => data.selectId && select(data.selectId)}
            className={cn('trace-step-label nodrag nopan', data.selectId && 'is-clickable', data.tools && 'has-tools')}
            style={{ transform: `translate(-50%, -50%) translate(${labelX}px, ${labelY}px)` }}
          >
            {data.tools ? <span className="trace-step-tools">{data.tools}</span> : null}
            {data.gap ? <span className="trace-step-gap">{data.gap}</span> : null}
          </button>
        </EdgeLabelRenderer>
      ) : null}
    </>
  );
}

export const NODE_TYPES = { card: CardNodeView, hub: HubNodeView, provider: ProviderNodeView };
export const EDGE_TYPES = { flow: FlowEdgeView, step: StepEdgeView };
