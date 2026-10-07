'use client';

import { useEffect, useRef } from 'react';

/** Blob palettes (r,g,b) — the console's accent / accent-2 plus a violet. */
const LIGHT = { a: '0 122 255', b: '54 195 255', c: '175 82 222', veil: 'rgb(255 255 255 / 0.3)', alpha: 0.5 };
const DARK = { a: '10 132 255', b: '100 210 255', c: '191 90 242', veil: 'rgb(17 18 20 / 0.3)', alpha: 0.42 };

/** Canvas resolution relative to the viewport: the field is pure gradients, so it upscales losslessly. */
const SCALE = 1 / 8;

/**
 * Fluid mesh-gradient background for the landing page: a few large color fields drifting on slow
 * Lissajous paths, rendered into a tiny fixed canvas that CSS stretches over the viewport (cheap at any
 * page length). Static under prefers-reduced-motion, paused while the tab is hidden.
 */
export function MeshBackground() {
  const ref = useRef<HTMLCanvasElement>(null);

  useEffect(() => {
    const canvas = ref.current;
    const ctx = canvas?.getContext('2d');
    if (!canvas || !ctx) return;

    const reduceMotion = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    let dark = document.documentElement.classList.contains('dark');
    let w = 0;
    let h = 0;
    let t = 0;
    let raf = 0;
    let running = false;

    function resize() {
      w = Math.max(16, Math.ceil(window.innerWidth * SCALE));
      h = Math.max(16, Math.ceil(window.innerHeight * SCALE));
      canvas!.width = w;
      canvas!.height = h;
    }

    function draw() {
      const p = dark ? DARK : LIGHT;
      ctx!.clearRect(0, 0, w, h);
      const blobs: [string, number, number, number][] = [
        [p.a, 0.2 + 0.18 * Math.sin(t), 0.25 + 0.2 * Math.cos(t * 1.3), 0.8],
        [p.b, 0.8 + 0.15 * Math.cos(t * 0.8), 0.3 + 0.25 * Math.sin(t * 1.1), 0.7],
        [p.c, 0.5 + 0.3 * Math.sin(t * 0.6 + 2), 0.9 + 0.12 * Math.cos(t), 0.65],
        [p.a, 0.9 + 0.08 * Math.sin(t * 1.4), 0.95 + 0.08 * Math.cos(t * 0.9), 0.5],
      ];
      const span = Math.max(w, h);
      for (const [rgb, x, y, r] of blobs) {
        const g = ctx!.createRadialGradient(x * w, y * h, 0, x * w, y * h, span * r);
        g.addColorStop(0, `rgb(${rgb} / ${p.alpha})`);
        g.addColorStop(1, `rgb(${rgb} / 0)`);
        ctx!.fillStyle = g;
        ctx!.fillRect(0, 0, w, h);
      }
      ctx!.fillStyle = p.veil;
      ctx!.fillRect(0, 0, w, h);
    }

    function step() {
      t += 0.0025;
      draw();
      raf = requestAnimationFrame(step);
    }

    function start() {
      if (running || reduceMotion) return;
      running = true;
      raf = requestAnimationFrame(step);
    }

    function stop() {
      running = false;
      cancelAnimationFrame(raf);
    }

    const onResize = () => {
      resize();
      draw();
    };
    const onVisibility = () => (document.hidden ? stop() : start());
    // fumadocs toggles `.dark` on <html> when the theme changes.
    const themeObserver = new MutationObserver(() => {
      dark = document.documentElement.classList.contains('dark');
      draw();
    });

    resize();
    draw();
    start();
    window.addEventListener('resize', onResize);
    document.addEventListener('visibilitychange', onVisibility);
    themeObserver.observe(document.documentElement, { attributes: true, attributeFilter: ['class'] });

    return () => {
      stop();
      window.removeEventListener('resize', onResize);
      document.removeEventListener('visibilitychange', onVisibility);
      themeObserver.disconnect();
    };
  }, []);

  return <canvas ref={ref} aria-hidden className="pointer-events-none fixed inset-0 -z-20 size-full" />;
}
