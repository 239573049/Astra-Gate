import logo from '../assets/logo.png';

/** The Astra logo. Sized by the caller (e.g. `className="size-6"`). */
export function AstraLogo({ className }: { className?: string }) {
  return <img src={logo} alt="" className={className} draggable={false} aria-hidden />;
}
