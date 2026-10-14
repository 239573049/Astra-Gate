import '@xyflow/react/dist/style.css';
import './flow.css';

import {
  Background,
  BackgroundVariant,
  ConnectionMode,
  MiniMap,
  Panel,
  ReactFlow,
  ReactFlowProvider,
  useNodesState,
  useReactFlow,
  useViewport,
  type XYPosition,
} from '@xyflow/react';
import { Maximize, Minus, Plus, X } from 'lucide-react';
import { useEffect, useRef, useSyncExternalStore, type ReactNode } from 'react';

import { useI18n } from '../../i18n';
import { cn } from '../../lib/cn';
import { useAppearance } from '../../shell/appearance';
import { Switch } from '../ui/controls';
import type { Graph, TraceNode } from './model';
import { EDGE_TYPES, NODE_TYPES } from './nodes';
import { TraceSelectContext } from './select';

// ------------------------------------------------------------------ the 详情 switch (shared, remembered locally)

const INSPECTOR_KEY = 'astra.traces.inspector';
const listeners = new Set<() => void>();
let inspectorOn = readInspector();

function readInspector(): boolean {
  try {
    return localStorage.getItem(INSPECTOR_KEY) === '1';
  } catch {
    return false;
  }
}

/** Whether clicking a node opens its details panel. Off by default; one setting for every trace canvas. */
export function useInspectorSwitch(): [boolean, (on: boolean) => void] {
  const value = useSyncExternalStore(
    (cb) => {
      listeners.add(cb);
      return () => listeners.delete(cb);
    },
    () => inspectorOn,
  );
  return [value, setInspector];
}

function setInspector(on: boolean) {
  inspectorOn = on;
  try {
    localStorage.setItem(INSPECTOR_KEY, on ? '1' : '0');
  } catch {
    // remembered for this page only
  }
  listeners.forEach((cb) => cb());
}

// ------------------------------------------------------------------ dragged positions

const layoutKey = (key: string) => `astra.traces.layout.${key}`;

function readLayout(key: string | undefined): Map<string, XYPosition> {
  if (!key) return new Map();
  try {
    const raw = JSON.parse(localStorage.getItem(layoutKey(key)) ?? '{}') as Record<string, XYPosition>;
    return new Map(Object.entries(raw).filter(([, p]) => Number.isFinite(p?.x) && Number.isFinite(p?.y)));
  } catch {
    return new Map();
  }
}

function writeLayout(key: string | undefined, positions: Map<string, XYPosition>) {
  if (!key) return;
  try {
    if (positions.size === 0) localStorage.removeItem(layoutKey(key));
    else localStorage.setItem(layoutKey(key), JSON.stringify(Object.fromEntries(positions)));
  } catch {
    // layout is a convenience
  }
}

// ------------------------------------------------------------------ canvas

export interface TraceCanvasProps {
  graph: Graph;
  /** Remember dragged positions under this key (the topology); otherwise they last while the view is open. */
  layoutKey?: string;
  selected: string | null;
  onSelect: (id: string | null) => void;
  /** Details of the selected node, shown while the 详情 switch is on. */
  inspector: ReactNode | null;
  className?: string;
  /** Centre the view on this node (a new seq moves again to the same node). */
  focus?: { id: string; seq: number } | null;
}

/** A draggable, zoomable React Flow canvas with Astra's controls, minimap and details panel. */
export function TraceCanvas(props: TraceCanvasProps) {
  return (
    <ReactFlowProvider>
      <Canvas {...props} />
    </ReactFlowProvider>
  );
}

function Canvas({ graph, layoutKey: storeKey, selected, onSelect, inspector, className, focus }: TraceCanvasProps) {
  const { t } = useI18n();
  const { resolvedTheme } = useAppearance();
  const [details, setDetails] = useInspectorSwitch();
  const flow = useReactFlow();
  const { zoom } = useViewport();
  const moved = useRef<Map<string, XYPosition> | null>(null);
  moved.current ??= readLayout(storeKey);
  const [nodes, setNodes, onNodesChange] = useNodesState<TraceNode>([]);

  // New data keeps the positions the user dragged nodes to.
  useEffect(() => {
    const positions = moved.current!;
    setNodes(graph.nodes.map((n) => ({ ...n, position: positions.get(n.id) ?? n.position, selected: n.id === selected })));
  }, [graph, selected, setNodes]);

  const fitted = useRef(false);
  useEffect(() => {
    if (fitted.current || graph.nodes.length === 0) return;
    fitted.current = true;
    requestAnimationFrame(() => void flow.fitView({ padding: 0.18, maxZoom: 1 }));
  }, [graph.nodes.length, flow]);

  useEffect(() => {
    if (!focus) return;
    const node = flow.getNode(focus.id);
    if (!node) return;
    const w = node.measured?.width ?? 200;
    const h = node.measured?.height ?? 64;
    void flow.setCenter(node.position.x + w / 2, node.position.y + h / 2, { zoom: Math.max(flow.getZoom(), 0.9), duration: 300 });
  }, [focus, flow]);

  const resetLayout = () => {
    moved.current = new Map();
    writeLayout(storeKey, moved.current);
    setNodes(graph.nodes.map((n) => ({ ...n, selected: n.id === selected })));
    requestAnimationFrame(() => void flow.fitView({ padding: 0.18, maxZoom: 1, duration: 200 }));
  };

  const showInspector = details && selected !== null && inspector !== null;
  return (
    <div className={cn('astra-flow relative min-h-[420px] overflow-hidden rounded-[14px] border border-[var(--separator)] bg-[var(--surface)]', className)}>
      <TraceSelectContext.Provider value={onSelect}>
      <ReactFlow
        nodes={nodes}
        edges={graph.edges}
        nodeTypes={NODE_TYPES}
        edgeTypes={EDGE_TYPES}
        onNodesChange={onNodesChange}
        onNodeDragStop={(_, node) => {
          moved.current!.set(node.id, node.position);
          writeLayout(storeKey, moved.current!);
        }}
        onNodeClick={(_, node) => onSelect(node.id)}
        onEdgeClick={(_, edge) => {
          const id = (edge.data as { selectId?: string } | undefined)?.selectId;
          if (id) onSelect(id);
        }}
        connectionMode={ConnectionMode.Loose}
        nodesConnectable={false}
        elementsSelectable={false}
        edgesFocusable={false}
        colorMode={resolvedTheme}
        minZoom={0.25}
        maxZoom={2.5}
        // trackpad / wheel pans; ⌘ or Ctrl + wheel (and pinch) zooms
        panOnScroll
        zoomOnScroll={false}
        zoomActivationKeyCode={['Meta', 'Control']}
        proOptions={{ hideAttribution: true }}
      >
        <Background variant={BackgroundVariant.Dots} gap={20} size={1} />
        <MiniMap pannable zoomable position="bottom-right" nodeBorderRadius={10} ariaLabel={t('traces.canvas.minimap')} />
        <Panel position="top-left" className="glass rounded-full px-2.5 py-0.5 text-[11.5px] text-[var(--text-secondary)]">
          {t('traces.canvas.hint')}
        </Panel>
        <Panel position="bottom-left" className="glass flex items-center gap-0.5 rounded-[10px] p-1">
          <ToolbarButton label={t('traces.canvas.zoomOut')} onClick={() => void flow.zoomOut({ duration: 150 })}>
            <Minus className="size-4" />
          </ToolbarButton>
          <ToolbarButton label={t('traces.canvas.fit')} onClick={() => void flow.fitView({ padding: 0.18, maxZoom: 1, duration: 200 })} wide>
            <span className="num text-[12px] text-[var(--text-secondary)]">{Math.round(zoom * 100)}%</span>
          </ToolbarButton>
          <ToolbarButton label={t('traces.canvas.zoomIn')} onClick={() => void flow.zoomIn({ duration: 150 })}>
            <Plus className="size-4" />
          </ToolbarButton>
          <span className="mx-0.5 h-4 w-px bg-[var(--separator)]" />
          <ToolbarButton label={t('traces.canvas.fit')} onClick={() => void flow.fitView({ padding: 0.18, maxZoom: 1, duration: 200 })} wide>
            <Maximize className="size-3.5" />
            <span className="text-[12px]">{t('traces.canvas.fit')}</span>
          </ToolbarButton>
          <ToolbarButton label={t('traces.canvas.resetLayout')} onClick={resetLayout} wide>
            <span className="text-[12px]">{t('traces.canvas.resetLayout')}</span>
          </ToolbarButton>
          <span className="mx-0.5 h-4 w-px bg-[var(--separator)]" />
          <label className="flex cursor-pointer items-center gap-2 px-2 text-[12px]">
            <Switch checked={details} onChange={setDetails} label={t('traces.canvas.details')} />
            <span aria-hidden>{t('traces.canvas.details')}</span>
          </label>
        </Panel>
        {showInspector ? (
          <Panel
            position="top-right"
            className="m-3! max-h-[calc(100%-170px)] w-[300px] overflow-auto rounded-[14px] border border-[var(--separator)] bg-[var(--surface-solid)] p-3.5 shadow-[0_12px_32px_rgb(0_0_0/0.28)]"
          >
            <button
              type="button"
              aria-label={t('traces.canvas.close')}
              onClick={() => setDetails(false)}
              className="absolute top-2.5 right-2.5 inline-flex size-6 items-center justify-center rounded-md text-[var(--text-secondary)] hover:bg-[var(--surface-muted)]"
            >
              <X className="size-4" />
            </button>
            {inspector}
          </Panel>
        ) : null}
      </ReactFlow>
      </TraceSelectContext.Provider>
    </div>
  );
}

function ToolbarButton({ label, onClick, children, wide }: { label: string; onClick: () => void; children: ReactNode; wide?: boolean }) {
  return (
    <button
      type="button"
      aria-label={label}
      title={label}
      onClick={onClick}
      className={cn('inline-flex h-7 items-center justify-center gap-1.5 rounded-[7px] text-[var(--foreground)] hover:bg-[var(--surface-muted)]', wide ? 'px-2' : 'w-7')}
    >
      {children}
    </button>
  );
}
