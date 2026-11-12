// Page patterns made of UI 1.0 components (design/plugin-v2 06 §1). Event props take the action names of the view
// that renders them.
import type { ChartProps, Format, MetricCardProps, Tone, UiAction, UiNode } from './index.js';

export interface SettingsRow {
  key?: string;
  label: string;
  detail?: string;
  hint?: string;
  icon?: string;
  control?: UiNode;
}
export interface SettingsGroup {
  key?: string;
  title?: string;
  description?: string;
  footer?: string;
  rows: SettingsRow[];
}
export function SettingsPage(o: { title: string; subtitle?: string; groups: SettingsGroup[]; saveAction?: UiAction; dirtyBar?: boolean; width?: 'narrow' | 'default' | 'wide' | 'full' }): UiNode;

export interface ListItem {
  key: string | number;
  label: string;
  detail?: string;
  icon?: string;
}
/** Two panes; on `width: 'compact'` (pass ctx.client.width) the list or the detail alone, with a back button. */
export function ListDetail(o: {
  title?: string;
  items: ListItem[];
  selected?: string | number | null;
  /** gets { key } */
  selectAction: string;
  backAction?: UiAction;
  detail?: UiNode | null;
  empty?: UiNode;
  width?: 'compact' | 'regular' | 'wide' | string;
  backLabel?: string;
  ratio?: number;
}): UiNode;

export interface WizardStep {
  title: string;
  description?: string;
  content?: UiNode;
}
export function Wizard(o: {
  steps: WizardStep[];
  current: number;
  backAction?: UiAction;
  nextAction: UiAction;
  finishAction?: UiAction;
  canNext?: boolean;
  busy?: boolean;
  labels?: { back?: string; next?: string; finish?: string };
}): UiNode;

export interface DashboardMetric {
  key?: string;
  label: string;
  value: unknown;
  format?: Format;
  delta?: number;
  trend?: number[];
  tone?: Tone;
  icon?: string;
}
export interface DashboardChart extends Omit<ChartProps, 'key'> {
  key?: string;
  title?: string;
  description?: string;
}
export function Dashboard(o: { title?: string; subtitle?: string; metrics?: DashboardMetric[]; charts?: DashboardChart[]; columns?: unknown; actions?: UiNode[] }): UiNode;

export interface CrudSheet {
  open: boolean;
  mode?: 'create' | 'edit';
  /** the edited row's key, sent to deleteAction as { key } */
  key?: string | number;
  title?: string;
  content?: UiNode | UiNode[];
}
export function CrudTable(o: {
  title?: string;
  description?: string;
  columns: unknown[];
  rows?: unknown[];
  rowKey?: string;
  source?: { collection: string };
  empty?: UiNode;
  loading?: boolean;
  createAction?: UiAction;
  editAction?: UiAction;
  saveAction?: UiAction;
  closeAction?: UiAction;
  /** gets { key } */
  deleteAction?: string;
  sheet?: CrudSheet;
  labels?: Partial<Record<'create' | 'edit' | 'delete' | 'confirmDelete' | 'cancel' | 'save' | 'empty', string>>;
}): UiNode;

export type { MetricCardProps };
