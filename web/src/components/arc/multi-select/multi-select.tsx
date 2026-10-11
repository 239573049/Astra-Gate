"use client";

import { memo } from "react";
import type { ReactNode } from "react";
import * as DropdownPrimitive from "@radix-ui/react-dropdown-menu";
import { Check, ChevronDown, X } from "lucide-react";
import styles from "./multi-select.module.css";

export interface MultiSelectOption { value: string; label: string; icon?: ReactNode; }

export interface MultiSelectProps {
  /** Accessible field name; the trigger reads "label: picked labels". */
  label: string;
  /** Shown while nothing is picked (e.g. "All clients"). */
  placeholder: string;
  options: MultiSelectOption[];
  value: string[];
  onValueChange: (next: string[]) => void;
  /** Menu row that empties the selection; omitted means no such row. */
  clearLabel?: string;
  /** Shown in the menu when there are no options. */
  emptyLabel?: string;
  className?: string;
}

/**
 * A select whose menu toggles several values: picking a row keeps the menu open, so a few values can be
 * checked in one pass. The trigger shows the first pick (icon + label) and a "+N" count for the rest.
 * Memoized: a page re-rendering on every live tick leaves untouched selects alone.
 */
export const MultiSelect = memo(function MultiSelect({ label, placeholder, options, value, onValueChange, clearLabel, emptyLabel, className }: MultiSelectProps) {
  const picked = new Set(value);
  const pickedOptions = options.filter((o) => picked.has(o.value));
  // A picked value missing from options (a deleted provider, options still loading) still counts and shows its raw id.
  const first = pickedOptions[0] ?? (value[0] !== undefined ? { value: value[0], label: value[0] } : undefined);
  const names = value.map((v) => options.find((o) => o.value === v)?.label ?? v);
  const toggle = (v: string) => onValueChange(picked.has(v) ? value.filter((x) => x !== v) : [...value, v]);

  return <DropdownPrimitive.Root modal={false}>
    <DropdownPrimitive.Trigger type="button" className={[styles.trigger, className].filter(Boolean).join(" ")} data-active={value.length > 0 ? "" : undefined} aria-label={names.length ? `${label}: ${names.join(", ")}` : `${label}: ${placeholder}`} title={names.length ? names.join(", ") : undefined}>
      {first ? <>
        {first.icon && <span className={styles.icon} aria-hidden="true">{first.icon}</span>}
        <span className={styles.value}>{first.label}</span>
        {value.length > 1 && <span className={styles.count} aria-hidden="true">+{value.length - 1}</span>}
      </> : <span className={styles.placeholder}>{placeholder}</span>}
      <ChevronDown className={styles.chevron} size={15} strokeWidth={1.8} aria-hidden="true" />
    </DropdownPrimitive.Trigger>
    <DropdownPrimitive.Portal>
      <DropdownPrimitive.Content className={styles.menu} sideOffset={6} align="start" collisionPadding={12} loop aria-label={label}>
        {options.length === 0 && <div className={styles.empty}>{emptyLabel}</div>}
        {options.map((o) => (
          <DropdownPrimitive.CheckboxItem
            key={o.value}
            data-row={o.value}
            className={styles.item}
            checked={picked.has(o.value)}
            onCheckedChange={() => toggle(o.value)}
            // Keep the menu open so several values can be checked in a row.
            onSelect={(e) => e.preventDefault()}
          >
            {o.icon && <span className={styles.icon} aria-hidden="true">{o.icon}</span>}
            <span className={styles.value}>{o.label}</span>
            <DropdownPrimitive.ItemIndicator className={styles.indicator}><Check size={15} strokeWidth={1.9} aria-hidden="true" /></DropdownPrimitive.ItemIndicator>
          </DropdownPrimitive.CheckboxItem>
        ))}
        {clearLabel && value.length > 0 && <>
          <DropdownPrimitive.Separator className={styles.separator} />
          <DropdownPrimitive.Item className={styles.item} onSelect={() => onValueChange([])}>
            <span className={styles.icon} aria-hidden="true"><X size={15} strokeWidth={1.8} /></span>
            <span className={styles.value}>{clearLabel}</span>
          </DropdownPrimitive.Item>
        </>}
      </DropdownPrimitive.Content>
    </DropdownPrimitive.Portal>
  </DropdownPrimitive.Root>;
});

export default MultiSelect;
