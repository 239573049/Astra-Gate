"use client";

import { useMemo, useRef, useState } from "react";
import { activityGrid, activityLevel } from "../../../lib/stats";
import styles from "./activity-heatmap.module.css";

export interface ActivityHeatmapDay {
  /** Local "yyyy-MM-dd". */
  day: string;
  requests: number;
}

/** Use an activity heatmap for one measure across a year of days, when people look for streaks and quiet stretches rather than exact numbers. */
export interface ActivityHeatmapProps {
  /** Names the grid for assistive technology. */
  label: string;
  /** One entry per day that had activity; days left out are empty. */
  data: ActivityHeatmapDay[];
  /** Days the grid covers, matching the window the data was fetched for. */
  days?: number;
  /** Row labels, Monday first, 7 entries; an empty entry keeps that row quiet. */
  weekdays: string[];
  /** Line under the grid: the window, how many days were active, the busiest day. */
  summary: string;
  /** Legend ends: "less" leads the swatches, "more" follows them. */
  legend: { less: string; more: string };
  /** Bubble of one cell, such as "2026-10-08 · 4 requests". */
  formatDay: (day: string, requests: number) => string;
  /** Month header above a column that opens a month, such as "Oct". */
  formatMonth: (month: number) => string;
  /** Month header above the column the window opens inside of, such as "Oct 2025": that one needs its year to read as a start. */
  formatMonthStart: (month: number, year: number) => string;
}

const LEVELS = [0, 1, 2, 3, 4] as const;
/**
 * A bubble is about 32px tall with an 8px gap above the cell; one whose cell sits nearer the grid's top than that
 * hangs below instead, and it keeps this much clear of either edge of the card.
 */
const BUBBLE_ROOM = 44, BUBBLE_EDGE = 100;
type Hover = { day: string; requests: number; left: number; top: number; above: boolean; align: "start" | "center" | "end" };

/**
 * A year of days as GitHub-style calendar weeks (Monday first, newest day in the last column). The grid is drawn
 * in pixels rather than scaled, so `--cell` keeps the cells crisp and the labels the same size as the rest of
 * the app however wide the card is: the columns spread into the card they are given, and a card too narrow for
 * them scrolls sideways.
 *
 * The pointer gets a bubble over the hovered cell. It is one element placed per hover rather than a tooltip per
 * cell, which 365 of would mount a provider each and still wait out a delay — and it lives inside the plot, which
 * is `position: relative` for it, because the card clips its overflow. Cells carry no `title`: the bubble answers
 * at once, and the browser's own tooltip a second later would only double it. The grid is one image whose
 * `aria-label` is the window's summary.
 */
export function ActivityHeatmap({ label, data, days = 365, weekdays, summary, legend, formatDay, formatMonth, formatMonthStart }: ActivityHeatmapProps) {
  const grid = useMemo(() => activityGrid(days), [days]);
  const counts = useMemo(() => new Map(data.map((d) => [d.day, d.requests])), [data]);
  const peak = useMemo(() => data.reduce((max, d) => Math.max(max, d.requests), 0), [data]);
  // Month names: a column that opens a month takes its name, and the column the window opens inside of names its own month and year.
  const labels = useMemo(() => grid.weeks.map((_, column) => {
    const opens = grid.months.find((month) => month.index === column);
    if (opens) return formatMonth(opens.label);
    if (column !== 0) return undefined;
    const start = grid.monthLabels[0]!;
    return formatMonthStart(start.month, start.year);
  }), [formatMonth, formatMonthStart, grid]);
  const [hover, setHover] = useState<Hover | null>(null);
  const plot = useRef<HTMLDivElement>(null); // the bubble's containing block: offsets are measured from it

  // One handler for the whole grid reads the day under the pointer off `data-day`, so hovering never rebuilds 365 cells.
  const track = (event: React.PointerEvent<HTMLDivElement>) => {
    const cell = (event.target as HTMLElement).dataset.day === undefined ? null : event.target as HTMLElement;
    const frame = plot.current?.getBoundingClientRect();
    if (!cell || !frame) return setHover(null);
    const box = cell.getBoundingClientRect();
    const day = cell.dataset.day!;
    const x = box.left + box.width / 2 - frame.left, y = box.top - frame.top;
    const align = x < BUBBLE_EDGE ? "start" : x > frame.width - BUBBLE_EDGE ? "end" : "center";
    setHover({
      day,
      requests: counts.get(day) ?? 0,
      // A bubble aligned to an edge keeps that edge 8px inside the grid; a centred one rides the cell's own axis.
      left: align === "start" ? 8 : align === "end" ? frame.width - 8 : x,
      top: y,
      // A first-row cell has no room above it inside the plot's scroller, and the bottom row never needs to hang below.
      above: y > BUBBLE_ROOM,
      align,
    });
  };

  return <figure className={styles.figure}>
    <figcaption className={styles.srOnly}>{label}</figcaption>
    <div className={styles.scroll}>
      <div className={styles.plot} ref={plot} role="img" aria-label={`${label}. ${summary}`}>
        <div className={styles.months} data-months aria-hidden="true">
          {labels.map((text, column) => <span key={column} className={styles.month}>{text ?? ""}</span>)}
        </div>
        <div className={styles.weekdays} data-weekdays aria-hidden="true">
          {weekdays.map((text, row) => <span key={row} className={styles.weekday}>{text}</span>)}
        </div>
        <div className={styles.grid} data-grid aria-hidden="true" onPointerOver={track} onPointerLeave={() => setHover(null)}>
          {grid.weeks.map((week, column) => <span key={column} className={styles.week}>
            {week.map((at) => {
              const day = grid.days[at]!;
              const requests = counts.get(day) ?? 0;
              return <span
                key={at}
                className={styles.cell}
                data-day={day}
                data-level={activityLevel(requests, peak)}
              />;
            })}
          </span>)}
        </div>
        {hover && <span className={styles.bubble} data-bubble data-align={hover.align} data-below={!hover.above || undefined} style={{ left: hover.left, top: hover.top }}>
          {formatDay(hover.day, hover.requests)}
        </span>}
      </div>
    </div>
    <div className={styles.foot}>
      <span className={styles.summary}>{summary}</span>
      <span className={styles.legend} aria-hidden="true">
        {legend.less}
        {LEVELS.map((level) => <span key={level} className={styles.swatch} data-level={level} />)}
        {legend.more}
      </span>
    </div>
  </figure>;
}

export default ActivityHeatmap;
