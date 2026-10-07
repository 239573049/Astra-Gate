// Astra's control kit. Every interactive control is an Arc component (src/components/arc, installed from
// the @uiarc registry); this file only adapts Arc's props to the call sites and adds layout containers.

import {
  createContext,
  forwardRef,
  useContext,
  type CSSProperties,
  type InputHTMLAttributes,
  type ReactNode,
  type TextareaHTMLAttributes,
} from 'react';

import { cn } from '../../lib/cn';
import { Accordion } from '../arc/accordion/accordion';
import { Badge as ArcBadge, type BadgeTone } from '../arc/badge/badge';
import { Button as ArcButton, type ButtonProps as ArcButtonProps } from '../arc/button/button';
import { CodeBlock as ArcCodeBlock } from '../arc/code-block/code-block';
import { EmptyState as ArcEmptyState } from '../arc/empty-state/empty-state';
import { Input as ArcInput } from '../arc/input/input';
import { SearchField as ArcSearchField } from '../arc/search-field/search-field';
import SegmentedControl from '../arc/segmented-control/segmented-control';
import { Skeleton } from '../arc/skeleton/skeleton';
import { Switch as ArcSwitch } from '../arc/switch/switch';
import { Textarea as ArcTextarea } from '../arc/textarea/textarea';

// ---------- Field: hands its label / hint / error to the Arc control inside it ----------

interface FieldInfo {
  label: string;
  description?: string;
  error?: string;
}

const FieldContext = createContext<FieldInfo | null>(null);

/** Resolves the accessible label of a control: explicit label, enclosing Field, or a fallback (visually hidden). */
export function useFieldLabel(explicit: string | undefined, fallback: string | undefined) {
  const field = useContext(FieldContext);
  const label = explicit ?? field?.label ?? fallback ?? '';
  return { field, label, hidden: explicit === undefined && field === null };
}

export function Field({ label, hint, error, children, className }: { label: string; hint?: ReactNode; error?: string; children: ReactNode; className?: string }) {
  const description = typeof hint === 'string' ? hint : undefined;
  return (
    <div className={cn('flex min-w-0 flex-col', className)}>
      <FieldContext.Provider value={{ label, description, error }}>{children}</FieldContext.Provider>
      {hint !== undefined && description === undefined && !error && <div className="mt-2 text-[11px] text-[var(--text-muted)]">{hint}</div>}
    </div>
  );
}

// ---------- Button ----------

type ButtonVariant = 'primary' | 'glass' | 'plain' | 'destructive';

export interface ButtonProps extends Omit<ArcButtonProps, 'variant' | 'size'> {
  variant?: ButtonVariant;
  size?: 'sm' | 'md';
  icon?: ReactNode;
}

const ARC_VARIANT = { primary: 'primary', glass: 'secondary', plain: 'ghost', destructive: 'danger' } as const;

export const Button = forwardRef<HTMLButtonElement, ButtonProps>(function Button(
  { variant = 'glass', size = 'md', icon, children, style, type = 'button', ...rest },
  ref,
) {
  const iconOnly = children === undefined || children === null || children === false;
  const extra: CSSProperties = {
    // Primary actions use the system accent (plan §9.1) instead of Arc's neutral foreground fill.
    ...(variant === 'primary' && { background: 'var(--accent)', borderColor: 'var(--accent)', color: 'var(--on-accent)' }),
    // Icon-only buttons are square.
    ...(iconOnly && { paddingInline: 0, width: size === 'sm' ? 'var(--control-height-sm)' : 'var(--control-height-md)' }),
    ...(style as CSSProperties | undefined),
  };
  return (
    <ArcButton ref={ref} type={type} variant={ARC_VARIANT[variant]} size={size} style={extra} {...rest}>
      {icon}
      {children}
    </ArcButton>
  );
});

/** Lays out related toolbar buttons side by side. */
export function ButtonGroup({ children, className }: { children: ReactNode; className?: string }) {
  return <div className={cn('inline-flex items-center gap-1', className)}>{children}</div>;
}

// ---------- Segmented control ----------

export interface SegmentItem<T extends string> {
  value: T;
  label: string;
  accessory?: ReactNode;
}

export function Segmented<T extends string>({
  items,
  value,
  onChange,
  className,
  ariaLabel,
}: {
  items: SegmentItem<T>[];
  value: T;
  onChange: (v: T) => void;
  /** Kept for call-site compatibility; Arc's segmented control has one size. */
  size?: 'sm' | 'md';
  className?: string;
  ariaLabel?: string;
}) {
  return (
    <SegmentedControl
      className={className}
      label={ariaLabel}
      value={value}
      onValueChange={(v) => onChange(v as T)}
      options={items.map((it) => ({ value: it.value, label: it.label, accessory: it.accessory }))}
    />
  );
}

// ---------- Switch (settings that apply immediately) ----------

export function Switch({ checked, onChange, disabled, label }: { checked: boolean; onChange: (v: boolean) => void; disabled?: boolean; label?: string }) {
  return (
    <ArcSwitch
      checked={checked}
      onCheckedChange={onChange}
      disabled={disabled}
      // Rows already show the setting name, so the label is the accessible name only (not rendered text).
      aria-label={label}
      // Switches often sit inside clickable rows.
      onClick={(e) => e.stopPropagation()}
    />
  );
}

// ---------- grouped list (System Settings style layout) ----------

export function Group({ title, footer, children, className }: { title?: ReactNode; footer?: ReactNode; children: ReactNode; className?: string }) {
  return (
    <section className={cn('mb-5', className)}>
      {title && <h3 className="mb-2 px-1 text-[12px] font-medium text-[var(--text-secondary)]">{title}</h3>}
      {/* No overflow class here: rows' rounded hover corners are clipped by the `.card` rule, which releases the
          clip while a row's Combobox popover is open (its popover is positioned inside this card, not portaled). */}
      <div className="card divide-y divide-[var(--border)]">{children}</div>
      {footer && <p className="mt-2 px-1 text-[11px] text-[var(--text-muted)]">{footer}</p>}
    </section>
  );
}

export function Row({
  label,
  detail,
  icon,
  children,
  onClick,
  selected,
  className,
}: {
  label: ReactNode;
  detail?: ReactNode;
  icon?: ReactNode;
  children?: ReactNode;
  onClick?: () => void;
  selected?: boolean;
  className?: string;
}) {
  // A clickable row may contain its own controls (switches, buttons), so it cannot be a <button>.
  const interactive = onClick
    ? {
        role: 'button',
        tabIndex: 0,
        onClick,
        onKeyDown: (e: React.KeyboardEvent<HTMLDivElement>) => {
          if (e.target === e.currentTarget && (e.key === 'Enter' || e.key === ' ')) {
            e.preventDefault();
            onClick();
          }
        },
      }
    : {};
  return (
    <div
      {...interactive}
      className={cn(
        'flex min-h-11 w-full items-center gap-3 px-4 py-2 text-left',
        onClick && 'cursor-default transition-colors hover:bg-[var(--surface-muted)]',
        selected && 'bg-[var(--accent-subtle)]',
        className,
      )}
    >
      {icon}
      <div className="min-w-0 flex-1">
        <div className="truncate text-[13px]">{label}</div>
        {detail && <div className="truncate text-[11px] text-[var(--text-secondary)]">{detail}</div>}
      </div>
      {children !== undefined && <div className="flex shrink-0 items-center gap-2">{children}</div>}
    </div>
  );
}

// ---------- inputs ----------

type InputProps = InputHTMLAttributes<HTMLInputElement> & {
  invalid?: boolean;
  /** Visible label when the input is not inside a Field (use aria-label for a hidden one). */
  label?: string;
  /** Monospaced value (ids, URLs, keys). */
  mono?: boolean;
};

const MONO: CSSProperties = { fontFamily: 'var(--font-mono)', fontSize: '12px' };

export const Input = forwardRef<HTMLInputElement, InputProps>(function Input({ className, invalid, label, mono, style, ...rest }, ref) {
  const f = useFieldLabel(label, rest['aria-label'] ?? rest.placeholder);
  return (
    <div className={cn('min-w-0', f.hidden && 'astra-label-hidden', className)}>
      <ArcInput
        ref={ref}
        label={f.label}
        description={f.field?.description}
        error={f.field?.error}
        aria-invalid={invalid || undefined}
        style={mono ? { ...MONO, ...style } : style}
        {...rest}
      />
    </div>
  );
});

export const TextArea = forwardRef<HTMLTextAreaElement, TextareaHTMLAttributes<HTMLTextAreaElement> & { invalid?: boolean; label?: string; mono?: boolean }>(
  function TextArea({ className, invalid, label, mono, style, ...rest }, ref) {
    const f = useFieldLabel(label, rest['aria-label'] ?? rest.placeholder);
    return (
      <div className={cn('min-w-0', f.hidden && 'astra-label-hidden', className)}>
        <ArcTextarea
          ref={ref}
          label={f.label}
          description={f.field?.description}
          error={f.field?.error}
          aria-invalid={invalid || undefined}
          style={mono ? { ...MONO, ...style } : style}
          {...rest}
        />
      </div>
    );
  },
);

/**
 * Optional number (prices, limits): "" maps to null, which Arc's NumberField cannot express, so this is
 * Arc's Input with a decimal keyboard.
 */
export function NumberInput({
  value,
  onChange,
  className,
  placeholder,
  step,
  min,
  label,
}: {
  value: number | null | undefined;
  onChange: (v: number | null) => void;
  className?: string;
  placeholder?: string;
  step?: number | string;
  min?: number;
  label?: string;
}) {
  return (
    <Input
      type="number"
      inputMode="decimal"
      step={step ?? 'any'}
      min={min}
      aria-label={label}
      placeholder={placeholder}
      className={className}
      style={{ fontVariantNumeric: 'tabular-nums', textAlign: 'right' }}
      value={value ?? ''}
      onChange={(e) => {
        const raw = e.target.value;
        if (raw === '') return onChange(null);
        const n = Number(raw);
        if (!Number.isNaN(n)) onChange(n);
      }}
    />
  );
}

export function SearchField({
  value,
  onChange,
  placeholder,
  inputRef,
  className,
  clearLabel,
}: {
  value: string;
  onChange: (v: string) => void;
  placeholder?: string;
  inputRef?: React.Ref<HTMLInputElement>;
  className?: string;
  clearLabel?: string;
}) {
  return (
    <div className={cn('astra-label-hidden min-w-0', className)}>
      <ArcSearchField ref={inputRef} label={placeholder ?? 'Search'} placeholder={placeholder} value={value} onValueChange={onChange} clearLabel={clearLabel} />
    </div>
  );
}

// ---------- display ----------

type Tone = 'neutral' | 'accent' | 'green' | 'orange' | 'red';
const BADGE_TONE: Record<Tone, BadgeTone> = { neutral: 'neutral', accent: 'info', green: 'success', orange: 'warning', red: 'danger' };

export function Badge({ tone = 'neutral', children, className, title }: { tone?: Tone; children: ReactNode; className?: string; title?: string }) {
  return (
    <ArcBadge tone={BADGE_TONE[tone]} size="sm" className={cn('shrink-0', className)} title={title}>
      {children}
    </ArcBadge>
  );
}

/** Status dot (service state, provider health). */
export function Dot({ tone }: { tone: 'green' | 'orange' | 'red' | 'neutral' }) {
  const color = { green: 'var(--success)', orange: 'var(--warning)', red: 'var(--danger)', neutral: 'var(--text-muted)' }[tone];
  return <span className="inline-block size-2 shrink-0 rounded-full" style={{ background: color }} />;
}

/**
 * Icon slot. Arc never puts icons in colored rounded tiles, so this renders the glyph alone; `color` is kept
 * for call sites that pass an identity color and is ignored.
 */
export function Tile({ children, size = 20, className }: { color?: string; children: ReactNode; size?: number; className?: string }) {
  return (
    <span className={cn('inline-flex shrink-0 items-center justify-center text-[var(--text-secondary)]', className)} style={{ width: size, height: size }}>
      {children}
    </span>
  );
}

export function KV({ label, children, mono }: { label: ReactNode; children: ReactNode; mono?: boolean }) {
  return (
    <div className="flex items-baseline justify-between gap-3 py-1 text-[12px]">
      <span className="shrink-0 text-[var(--text-secondary)]">{label}</span>
      <span className={cn('min-w-0 truncate text-right selectable', mono && 'font-mono text-[11px]')}>{children}</span>
    </div>
  );
}

export function CodeBlock({ children, maxHeight = 320, language = 'json', filename }: { children: string; className?: string; maxHeight?: number; language?: string; filename?: string }) {
  return <ArcCodeBlock code={children} language={language} filename={filename} maxLines={Math.max(4, Math.round(maxHeight / 22))} />;
}

/** Loading placeholder (Arc skeleton). */
export function Spinner({ className, lines = 2 }: { className?: string; lines?: number }) {
  return <Skeleton className={className} lines={lines} />;
}

export function EmptyState({ icon, title, detail, action }: { icon?: ReactNode; title: string; detail?: string; action?: ReactNode }) {
  return (
    <div className="px-6 py-12">
      <ArcEmptyState icon={icon} title={title} description={detail ?? ''} action={action} />
    </div>
  );
}

export function SectionTitle({ children, action }: { children: ReactNode; action?: ReactNode }) {
  return (
    <div className="mb-2 flex items-center justify-between gap-2 px-1">
      <h2 className="text-[13px] font-medium">{children}</h2>
      {action}
    </div>
  );
}

/** Titled block inside dialogs (label row + content). */
export function DetailSection({ title, action, children, className }: { title: ReactNode; action?: ReactNode; children: ReactNode; className?: string }) {
  return (
    <section className={className}>
      <div className="mb-2 flex items-center justify-between gap-2">
        <h3 className="text-[12px] font-medium text-[var(--text-secondary)]">{title}</h3>
        {action}
      </div>
      {children}
    </section>
  );
}

/** Collapsible section (raw JSON, bodies), closed by default. */
export function Disclosure({ title, children }: { title: string; children: ReactNode }) {
  return <Accordion items={[{ title, content: children }]} defaultOpen={-1} />;
}
