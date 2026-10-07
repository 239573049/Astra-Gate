'use client';

import { useEffect, useRef } from 'react';

interface Stream {
  /** Polar position around the target (radians, CSS px). */
  a: number;
  r: number;
  v: number;
  spin: number;
  color: number;
  /** Recent positions, oldest first; drawn as a tail fading toward its end. */
  trail: [number, number][];
}

/** Light mode mixes in white: pure blue streams disappear against the blue mesh. */
const LIGHT = ['255 255 255', '0 122 255', '175 82 222'];
const DARK = ['10 132 255', '100 210 255', '191 90 242'];

/** Streams are squashed vertically so they read as converging from the sides more than from below. */
const Y_SCALE = 0.72;
/** Points kept per tail. */
const TRAIL = 14;
/** Radius (CSS px) at which a stream vanishes behind the logo. */
const CORE_R = 48;

/**
 * Light streams flowing in from every edge toward the element marked `data-converge-target` inside the
 * same parent (the hero logo) — the "汇于一处" motif. Each stream keeps its own short tail and the canvas
 * is fully cleared every frame, so it composites cleanly over the mesh behind it. Not rendered under
 * prefers-reduced-motion; paused while the tab is hidden.
 */
export function ConvergeStreams() {
  const ref = useRef<HTMLCanvasElement>(null);

  useEffect(() => {
    const canvas = ref.current;
    const parent = canvas?.parentElement;
    const ctx = canvas?.getContext('2d');
    if (!canvas || !parent || !ctx) return;
    if (window.matchMedia('(prefers-reduced-motion: reduce)').matches) return;

    let dark = document.documentElement.classList.contains('dark');
    let w = 0;
    let h = 0;
    let cx = 0;
    let cy = 0;
    let maxR = 0;
    let max = 0;
    let streams: Stream[] = [];
    let raf = 0;
    let running = false;

    function measure() {
      const dpr = Math.min(window.devicePixelRatio || 1, 2);
      const box = parent!.getBoundingClientRect();
      w = box.width;
      h = box.height;
      canvas!.width = Math.round(w * dpr);
      canvas!.height = Math.round(h * dpr);
      ctx!.setTransform(dpr, 0, 0, dpr, 0, 0);
      const target = parent!.querySelector('[data-converge-target]')?.getBoundingClientRect();
      cx = target ? target.left + target.width / 2 - box.left : w / 2;
      cy = target ? target.top + target.height / 2 - box.top : h / 3;
      // Farthest corner in the squashed space, so streams always enter from off-canvas.
      maxR = Math.max(
        ...[[0, 0], [w, 0], [0, h], [w, h]].map(([x, y]) => Math.hypot(x! - cx, (y! - cy) / Y_SCALE)),
      );
      max = w < 640 ? 36 : 72;
      // Pre-fill across the whole field so the motion is visible from the first frame.
      streams = Array.from({ length: max }, () => spawn(true));
    }

    function spawn(anywhere: boolean): Stream {
      return {
        a: Math.random() * Math.PI * 2,
        r: anywhere ? 80 + Math.random() * maxR : maxR + 10,
        v: 1.4 + Math.random() * 2,
        spin: (0.002 + Math.random() * 0.005) * (Math.random() < 0.5 ? -1 : 1),
        color: Math.floor(Math.random() * 3),
        trail: [],
      };
    }

    function step() {
      ctx!.clearRect(0, 0, w, h);
      ctx!.globalCompositeOperation = dark ? 'lighter' : 'source-over';
      ctx!.lineWidth = 1.4;
      ctx!.lineCap = 'round';
      if (streams.length < max && Math.random() < 0.6) streams.push(spawn(false));
      const colors = dark ? DARK : LIGHT;
      const strength = dark ? 0.85 : 0.75;

      for (const s of streams) {
        // Accelerate toward the core, like being pulled in.
        s.r -= s.v * (1 + 220 / (s.r + 40));
        s.a += s.spin;
        s.trail.push([cx + Math.cos(s.a) * s.r, cy + Math.sin(s.a) * s.r * Y_SCALE]);
        if (s.trail.length > TRAIL) s.trail.shift();
        if (s.trail.length < 2) continue;

        // Fade in from the edge, fade out as it disappears behind the logo.
        const alpha =
          strength * Math.min(1, (maxR - s.r) / 140) * Math.min(1, Math.max(0, (s.r - CORE_R) / 90));
        if (alpha <= 0.01) continue;
        const [tx, ty] = s.trail[0]!;
        const [hx, hy] = s.trail[s.trail.length - 1]!;
        const rgb = colors[s.color]!;
        const grad = ctx!.createLinearGradient(tx, ty, hx, hy);
        grad.addColorStop(0, `rgb(${rgb} / 0)`);
        grad.addColorStop(1, `rgb(${rgb} / ${alpha})`);
        ctx!.strokeStyle = grad;
        ctx!.beginPath();
        ctx!.moveTo(tx, ty);
        for (let i = 1; i < s.trail.length; i++) ctx!.lineTo(s.trail[i]![0], s.trail[i]![1]);
        ctx!.stroke();
      }
      streams = streams.filter((s) => s.r > CORE_R);
      raf = requestAnimationFrame(step);
    }

    function start() {
      if (running) return;
      running = true;
      raf = requestAnimationFrame(step);
    }

    function stop() {
      running = false;
      cancelAnimationFrame(raf);
    }

    const resizeObserver = new ResizeObserver(() => measure());
    const onVisibility = () => (document.hidden ? stop() : start());
    const themeObserver = new MutationObserver(() => {
      dark = document.documentElement.classList.contains('dark');
    });

    measure();
    start();
    resizeObserver.observe(parent);
    document.addEventListener('visibilitychange', onVisibility);
    themeObserver.observe(document.documentElement, { attributes: true, attributeFilter: ['class'] });

    return () => {
      stop();
      resizeObserver.disconnect();
      document.removeEventListener('visibilitychange', onVisibility);
      themeObserver.disconnect();
    };
  }, []);

  return (
    <canvas
      ref={ref}
      aria-hidden
      className="pointer-events-none absolute inset-0 -z-10 size-full [mask-image:linear-gradient(to_bottom,black_70%,transparent)]"
    />
  );
}
