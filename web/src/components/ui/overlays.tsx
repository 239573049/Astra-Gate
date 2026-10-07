// Dialogs, menus, selects and feedback, built on Arc components (src/components/arc).

import { createContext, useCallback, useContext, useRef, useState, type ReactNode } from 'react';

import { useT } from '../../i18n';
import { cn } from '../../lib/cn';
import { Alert } from '../arc/alert/alert';
import { Combobox } from '../arc/combobox/combobox';
import { Dialog, DialogContent } from '../arc/dialog/dialog';
import { DropdownMenu } from '../arc/dropdown-menu/dropdown-menu';
import { Select as ArcSelect } from '../arc/select/select';
import Toast from '../arc/toast/toast';
import { Button, useFieldLabel } from './controls';

// ---------- Sheet (modal dialog) ----------

export function Sheet({
  open,
  onOpenChange,
  title,
  description,
  children,
  footer,
  width = 460,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  title: string;
  description?: string;
  children?: ReactNode;
  footer?: ReactNode;
  width?: number;
  /** Nested dialogs stack by DOM order (each opens in a later portal), so no explicit level is needed. */
  level?: number;
}) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent title={title} description={description} style={{ width: `min(calc(100vw - 2rem), ${width}px)` }}>
        {children}
        {footer && <div className={cn('flex flex-wrap items-center justify-end gap-2', children !== undefined && 'mt-6')}>{footer}</div>}
      </DialogContent>
    </Dialog>
  );
}

// ---------- Menu (actions anchored to a trigger) ----------

export interface MenuItem {
  id: string;
  label: string;
  icon?: ReactNode;
  destructive?: boolean;
  disabled?: boolean;
  separatorBefore?: boolean;
  onSelect: () => void;
}

export function Menu({ label, icon, items }: { label: string; icon?: ReactNode; items: MenuItem[] }) {
  return (
    <DropdownMenu
      label={label}
      icon={icon}
      items={items.map((it) => ({
        label: it.label,
        icon: it.icon,
        destructive: it.destructive,
        disabled: it.disabled,
        separatorBefore: it.separatorBefore,
        onSelect: it.onSelect,
      }))}
    />
  );
}

// ---------- Select / Combobox ----------

export interface SelectOption<T extends string> {
  value: T;
  label: string;
  /** Secondary text; searchable in long lists. */
  detail?: string;
  disabled?: boolean;
}

/** Radix Select reserves "" for "no value", so an option whose value is "" travels under this key. */
const EMPTY = '__astra_empty__';
const toArc = (v: string) => (v === '' ? EMPTY : v);
const fromArc = (v: string) => (v === EMPTY ? '' : v);
/** Long lists become a searchable combobox (Arc guidance: select for a few options, combobox for many). */
const COMBOBOX_THRESHOLD = 12;

export function Select<T extends string>({
  value,
  onChange,
  options,
  placeholder,
  className,
  disabled,
  label,
  ariaLabel,
}: {
  value: T | null | undefined;
  onChange: (v: T) => void;
  options: SelectOption<T>[];
  placeholder?: string;
  className?: string;
  disabled?: boolean;
  /** Visible label (outside a Field). */
  label?: string;
  /** Accessible name only (rows and toolbars that already show the name). */
  ariaLabel?: string;
}) {
  const t = useT();
  const current = options.find((o) => o.value === value);
  const f = useFieldLabel(label, ariaLabel ?? placeholder ?? current?.label);
  const wrap = cn('min-w-0', f.hidden && 'astra-label-hidden', className);

  if (options.length > COMBOBOX_THRESHOLD) {
    return (
      <div className={wrap}>
        <Combobox
          label={f.label}
          description={f.field?.description}
          placeholder={placeholder ?? t('common.search')}
          emptyMessage={t('common.noResults')}
          value={value == null ? '' : toArc(value)}
          onValueChange={(v) => onChange(fromArc(v) as T)}
          options={options.map((o) => ({
            value: toArc(o.value),
            label: o.detail && o.detail !== o.label ? `${o.label} · ${o.detail}` : o.label,
            disabled: o.disabled,
            keywords: o.detail ? [o.detail] : undefined,
          }))}
        />
      </div>
    );
  }

  return (
    <div className={wrap}>
      <ArcSelect
        label={f.label}
        description={f.field?.description}
        placeholder={placeholder ?? t('common.select')}
        disabled={disabled}
        value={value == null || (value === '' && !current) ? '' : toArc(value)}
        onValueChange={(v) => onChange(fromArc(v) as T)}
        options={options.map((o) => ({ value: toArc(o.value), label: o.label, disabled: o.disabled }))}
      />
    </div>
  );
}

// ---------- toasts + confirm ----------

interface ToastItem {
  id: number;
  tone: 'info' | 'success' | 'error';
  text: string;
}

interface ConfirmRequest {
  title: string;
  detail?: string;
  confirmLabel?: string;
  destructive?: boolean;
  resolve: (ok: boolean) => void;
}

interface FeedbackApi {
  toast: (text: string, tone?: ToastItem['tone']) => void;
  confirm: (req: Omit<ConfirmRequest, 'resolve'>) => Promise<boolean>;
}

const FeedbackContext = createContext<FeedbackApi | null>(null);

export function FeedbackProvider({ children }: { children: ReactNode }) {
  const t = useT();
  const [toasts, setToasts] = useState<ToastItem[]>([]);
  const [pending, setPending] = useState<ConfirmRequest | null>(null);
  const seq = useRef(0);

  const dismiss = useCallback((id: number) => setToasts((list) => list.filter((x) => x.id !== id)), []);

  const toast = useCallback(
    (text: string, tone: ToastItem['tone'] = 'info') => {
      const id = ++seq.current;
      setToasts((list) => [...list.slice(-2), { id, tone, text }]);
      // Arc toasts time themselves out; errors are alerts and stay a little longer.
      if (tone === 'error') setTimeout(() => dismiss(id), 8000);
    },
    [dismiss],
  );

  const confirm = useCallback(
    (req: Omit<ConfirmRequest, 'resolve'>) => new Promise<boolean>((resolve) => setPending({ ...req, resolve })),
    [],
  );

  const close = (ok: boolean) => {
    pending?.resolve(ok);
    setPending(null);
  };

  return (
    <FeedbackContext.Provider value={{ toast, confirm }}>
      {children}
      <div className="pointer-events-none fixed inset-x-0 bottom-5 z-[95] flex flex-col items-center gap-2 px-4">
        {toasts.map((x) =>
          x.tone === 'error' ? (
            <div key={x.id} className="pointer-events-auto w-[min(100%,26rem)] selectable">
              <Alert tone="danger" title={t('common.error')} onDismiss={() => dismiss(x.id)}>
                {x.text}
              </Alert>
            </div>
          ) : (
            <div key={x.id} className="pointer-events-auto flex w-full justify-center">
              <Toast title={x.text} open onOpenChange={(o) => !o && dismiss(x.id)} />
            </div>
          ),
        )}
      </div>
      <Sheet
        open={pending !== null}
        onOpenChange={(o) => !o && close(false)}
        title={pending?.title ?? ''}
        description={pending?.detail}
        width={420}
        footer={
          <>
            <Button onClick={() => close(false)}>{t('common.cancel')}</Button>
            <Button variant={pending?.destructive ? 'destructive' : 'primary'} onClick={() => close(true)} autoFocus>
              {pending?.confirmLabel ?? t('common.confirm')}
            </Button>
          </>
        }
      />
    </FeedbackContext.Provider>
  );
}

export function useFeedback(): FeedbackApi {
  const ctx = useContext(FeedbackContext);
  if (!ctx) throw new Error('useFeedback outside FeedbackProvider');
  return ctx;
}

/** Converts any thrown value into a toast-friendly message. */
export function errorText(err: unknown): string {
  if (err instanceof Error) return err.message;
  return String(err);
}
