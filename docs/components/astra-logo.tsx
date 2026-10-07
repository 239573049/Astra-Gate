import { cn } from '@/lib/cn';

/**
 * The Astra logo — the same artwork as the admin UI (`web/src/assets/logo.png`) and the desktop
 * icon, served from `public/logo.png`. Sized by the caller (e.g. `className="size-6"`).
 */
export function AstraLogo({ className }: { className?: string }) {
  return (
    // eslint-disable-next-line @next/next/no-img-element
    <img
      src="/logo.png"
      alt=""
      aria-hidden
      draggable={false}
      width={568}
      height={572}
      className={cn('shrink-0 bg-white object-cover', className)}
    />
  );
}
