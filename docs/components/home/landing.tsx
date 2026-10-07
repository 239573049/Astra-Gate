import type { ReactNode } from 'react';
import Link from 'next/link';
import {
  ArrowLeftRight,
  ArrowRight,
  BookOpen,
  ChartColumn,
  Download,
  KeyRound,
  MousePointerClick,
  ShieldCheck,
} from 'lucide-react';
import { AstraLogo } from '@/components/astra-logo';
import { appName, githubUrl, releasesUrl } from '@/lib/shared';
import { cn } from '@/lib/cn';
import { ConsoleTour } from './console-tour';
import { CopyCommand } from './copy-command';
import { homeMessages, type HomeLang } from './messages';
import { ConvergeStreams } from './converge-streams';
import { MeshBackground } from './mesh-background';
import { WindowFrame } from './window-frame';

const installCommand = 'npm install -g @aidotnet/astra-gate';

const clients = ['Codex', 'Claude Code', 'Gemini CLI', 'OpenCode', 'Claude Desktop', 'Grok Build'];

const providers = [
  'OpenAI',
  'Anthropic',
  'Google Gemini',
  'xAI',
  'DeepSeek',
  'Moonshot Kimi',
  'Zhipu GLM',
  'Alibaba Qwen',
  'MiniMax',
  'Volcengine Ark',
  'OpenRouter',
  'SiliconFlow',
  'Ollama',
  'LM Studio',
];

const protocols = ['OpenAI Chat', 'OpenAI Responses', 'Anthropic Messages', 'Gemini'];

/**
 * The website landing page (`/` = zh, `/en` = en). The surrounding HomeLayout comes from the route's
 * layout (`homeOptions(lang)` in lib/layout.shared.tsx), which localizes the nav.
 */
export function Landing({ lang }: { lang: HomeLang }) {
  return (
    <main className="astra-home relative isolate -mt-14 flex flex-1 flex-col overflow-x-clip pt-14">
      {/* Page-wide fluid mesh; the hero adds converging light streams on top of it. */}
      <MeshBackground />
      <Hero lang={lang} />
      <ClientStrip lang={lang} />
      <Features lang={lang} />
      <Flow lang={lang} />
      <Tour lang={lang} />
      <QuickStart lang={lang} />
      <CallToAction lang={lang} />
      <Footer lang={lang} />
    </main>
  );
}

/* ---------- building blocks ---------- */

function Section({ id, children, className }: { id?: string; children: ReactNode; className?: string }) {
  return (
    <section id={id} className={cn('mx-auto w-full max-w-6xl px-5 sm:px-8', className)}>
      {children}
    </section>
  );
}

function SectionHeading({ eyebrow, title, subtitle }: { eyebrow: string; title: string; subtitle?: string }) {
  return (
    <div className="mx-auto max-w-2xl text-center">
      <p className="text-sm font-semibold text-[var(--accent)]">{eyebrow}</p>
      <h2 className="mt-2 text-3xl font-semibold tracking-tight text-balance sm:text-4xl">{title}</h2>
      {subtitle ? (
        <p className="mt-4 text-base text-pretty text-fd-muted-foreground">{subtitle}</p>
      ) : null}
    </div>
  );
}

function Chip({ children, className }: { children: ReactNode; className?: string }) {
  return (
    <span
      className={cn(
        'glass inline-flex items-center rounded-full px-3.5 py-1.5 text-sm font-medium whitespace-nowrap',
        className,
      )}
    >
      {children}
    </span>
  );
}

function PrimaryButton({ href, children, external }: { href: string; children: ReactNode; external?: boolean }) {
  return (
    <Link
      href={href}
      {...(external ? { target: '_blank', rel: 'noreferrer' } : {})}
      className="inline-flex items-center gap-1.5 rounded-full bg-[var(--accent)] px-5 py-2.5 text-sm font-semibold text-white shadow-[0_6px_20px_color-mix(in_oklab,var(--accent)_35%,transparent)] transition hover:brightness-110 active:scale-[0.98]"
    >
      {children}
    </Link>
  );
}

function SecondaryButton({ href, children, external }: { href: string; children: ReactNode; external?: boolean }) {
  return (
    <Link
      href={href}
      {...(external ? { target: '_blank', rel: 'noreferrer' } : {})}
      className="glass glass-strong inline-flex items-center gap-1.5 rounded-full px-5 py-2.5 text-sm font-semibold text-[var(--accent)] transition hover:brightness-105 active:scale-[0.98]"
    >
      {children}
    </Link>
  );
}

function GitHubMark({ className }: { className?: string }) {
  return (
    <svg viewBox="0 0 16 16" aria-hidden className={className} fill="currentColor">
      <path d="M8 0C3.58 0 0 3.58 0 8c0 3.54 2.29 6.53 5.47 7.59.4.07.55-.17.55-.38 0-.19-.01-.82-.01-1.49-2.01.37-2.53-.49-2.69-.94-.09-.23-.48-.94-.82-1.13-.28-.15-.68-.52-.01-.53.63-.01 1.08.58 1.23.82.72 1.21 1.87.87 2.33.66.07-.52.28-.87.51-1.07-1.78-.2-3.64-.89-3.64-3.95 0-.87.31-1.59.82-2.15-.08-.2-.36-1.02.08-2.12 0 0 .67-.21 2.2.82.64-.18 1.32-.27 2-.27.68 0 1.36.09 2 .27 1.53-1.04 2.2-.82 2.2-.82.44 1.1.16 1.92.08 2.12.51.56.82 1.27.82 2.15 0 3.07-1.87 3.75-3.65 3.95.29.25.54.73.54 1.48 0 1.07-.01 1.93-.01 2.2 0 .21.15.46.55.38A8.013 8.013 0 0 0 16 8c0-4.42-3.58-8-8-8Z" />
    </svg>
  );
}

/* ---------- sections ---------- */

function Hero({ lang }: { lang: HomeLang }) {
  const t = homeMessages[lang].hero;
  return (
    <>
      {/* The streams converge on the element marked data-converge-target (the logo). */}
      <div className="relative isolate">
        <ConvergeStreams />
        <Section className="pt-16 pb-6 text-center sm:pt-24">
          <div className="relative isolate mx-auto mb-7 w-fit" data-converge-target>
            <div aria-hidden className="astra-core absolute -inset-10 -z-10 rounded-full" />
            <div className="glass rounded-[26px] p-2">
              <AstraLogo className="size-20 rounded-[20px] sm:size-24 sm:rounded-[22px]" />
            </div>
          </div>
          <span className="glass inline-flex items-center gap-2 rounded-full px-3.5 py-1 text-xs font-medium text-fd-muted-foreground">
            <span className="size-1.5 rounded-full bg-[#34c759]" />
            {t.badge}
          </span>
          <h1 className="mx-auto mt-6 max-w-4xl text-4xl leading-[1.1] font-semibold tracking-tight text-balance sm:text-6xl">
            {t.titleLead}
            <span className="bg-gradient-to-r from-[var(--accent)] to-[var(--accent-2)] bg-clip-text text-transparent">
              {t.titleAccent}
            </span>
          </h1>
          <p className="mx-auto mt-6 max-w-2xl text-base leading-relaxed text-pretty text-fd-muted-foreground sm:text-lg">
            {t.subtitle}
          </p>
          <div className="mt-8 flex flex-wrap items-center justify-center gap-3">
            <PrimaryButton href="/docs/guide">
              {t.primary} <ArrowRight className="size-4" />
            </PrimaryButton>
            <SecondaryButton href="/download">
              <Download className="size-4" /> {t.secondary}
            </SecondaryButton>
          </div>
          <CopyCommand command={installCommand} copyLabel={t.copy} copiedLabel={t.copied} className="mt-6" />
        </Section>
      </div>

      <Section className="pb-10">
        <div className="relative isolate mx-auto mt-10 max-w-5xl">
          <div
            aria-hidden
            className="absolute inset-x-10 -top-6 bottom-10 -z-10 rounded-[40px] bg-[var(--accent)] opacity-25 blur-3xl"
          />
          <WindowFrame title={`${appName} — Overview`}>
            {/* eslint-disable-next-line @next/next/no-img-element */}
            <img
              src="/screenshots/overview.png"
              alt={appName}
              width={1440}
              height={900}
              fetchPriority="high"
              className="block h-auto w-full"
            />
          </WindowFrame>
        </div>
      </Section>
    </>
  );
}

function ClientStrip({ lang }: { lang: HomeLang }) {
  const t = homeMessages[lang].clients;
  return (
    <Section className="py-12 text-center">
      <p className="text-sm font-medium text-fd-muted-foreground">{t.title}</p>
      <div className="mt-5 flex flex-wrap justify-center gap-2.5">
        {clients.map((c) => (
          <Chip key={c}>{c}</Chip>
        ))}
      </div>
      <p className="mt-8 text-xs font-medium tracking-wide text-fd-muted-foreground uppercase">
        {t.providersTitle}
      </p>
      <div className="mx-auto mt-3 flex max-w-4xl flex-wrap justify-center gap-x-5 gap-y-2 text-sm text-fd-muted-foreground">
        {providers.map((p) => (
          <span key={p}>{p}</span>
        ))}
      </div>
    </Section>
  );
}

function FeatureCard({
  icon,
  title,
  desc,
  children,
  className,
}: {
  icon: ReactNode;
  title: string;
  desc: string;
  children?: ReactNode;
  className?: string;
}) {
  return (
    <div className={cn('glass flex min-w-0 flex-col rounded-[22px] p-6', className)}>
      <div className="flex size-10 items-center justify-center rounded-xl bg-[color-mix(in_oklab,var(--accent)_14%,transparent)] text-[var(--accent)] [&_svg]:size-5">
        {icon}
      </div>
      <h3 className="mt-4 text-lg font-semibold">{title}</h3>
      <p className="mt-2 text-sm leading-relaxed text-fd-muted-foreground">{desc}</p>
      {children ? <div className="mt-6 flex flex-1 items-end">{children}</div> : null}
    </div>
  );
}

function Features({ lang }: { lang: HomeLang }) {
  const t = homeMessages[lang].features;
  const bars = [38, 62, 45, 80, 56, 92, 70];
  return (
    <Section id="features" className="py-20">
      <SectionHeading eyebrow={t.eyebrow} title={t.title} subtitle={t.subtitle} />
      <div className="mt-12 grid gap-5 md:grid-cols-3">
        <FeatureCard
          className="md:col-span-2"
          icon={<ArrowLeftRight />}
          title={t.items.protocol.title}
          desc={t.items.protocol.desc}
        >
          <div className="flex w-full items-center gap-3 sm:gap-5">
            <div className="flex min-w-0 flex-1 flex-col gap-2">
              {protocols.map((p) => (
                <span key={p} className="truncate rounded-lg bg-black/[0.04] px-3 py-1.5 text-xs font-medium dark:bg-white/[0.06]">
                  {p}
                </span>
              ))}
            </div>
            <ArrowRight className="size-4 shrink-0 text-fd-muted-foreground" />
            <span className="flex size-12 shrink-0 items-center sm:size-16 justify-center rounded-2xl bg-[var(--accent)] font-mono text-sm font-bold text-white shadow-[0_6px_20px_color-mix(in_oklab,var(--accent)_35%,transparent)]">
              IR
            </span>
            <ArrowRight className="size-4 shrink-0 text-fd-muted-foreground" />
            <div className="flex min-w-0 flex-1 flex-col gap-2">
              {protocols.map((p) => (
                <span key={p} className="truncate rounded-lg bg-black/[0.04] px-3 py-1.5 text-xs font-medium dark:bg-white/[0.06]">
                  {p}
                </span>
              ))}
            </div>
          </div>
        </FeatureCard>

        <FeatureCard icon={<MousePointerClick />} title={t.items.clients.title} desc={t.items.clients.desc}>
          <div className="flex w-full flex-col gap-2">
            {[
              ['Codex', true],
              ['Claude Code', true],
              ['Gemini CLI', false],
            ].map(([name, on]) => (
              <div
                key={String(name)}
                className="flex items-center justify-between rounded-lg bg-black/[0.04] px-3 py-1.5 text-xs font-medium dark:bg-white/[0.06]"
              >
                {name}
                <span
                  className={cn(
                    'flex h-4 w-7 items-center rounded-full p-0.5 transition-colors',
                    on ? 'justify-end bg-[#34c759]' : 'justify-start bg-black/15 dark:bg-white/20',
                  )}
                >
                  <span className="size-3 rounded-full bg-white shadow-sm" />
                </span>
              </div>
            ))}
          </div>
        </FeatureCard>

        <FeatureCard icon={<ChartColumn />} title={t.items.billing.title} desc={t.items.billing.desc}>
          <div className="flex h-20 w-full items-end gap-2">
            {bars.map((h, i) => (
              <span
                key={i}
                className="flex-1 rounded-md bg-gradient-to-t from-[var(--accent)] to-[var(--accent-2)]"
                style={{ height: `${h}%`, opacity: 0.45 + (h / 100) * 0.55 }}
              />
            ))}
          </div>
        </FeatureCard>

        <FeatureCard icon={<ShieldCheck />} title={t.items.privacy.title} desc={t.items.privacy.desc}>
          <div className="w-full space-y-2 font-mono text-xs">
            <div className="truncate rounded-lg bg-[#ff3b30]/10 px-3 py-1.5 text-[#d70015] line-through decoration-1 dark:text-[#ff6961]">
              key=sk-ant-api03-9fK2…
            </div>
            <div className="truncate rounded-lg bg-[#34c759]/12 px-3 py-1.5 text-[#248a3d] dark:text-[#30d158]">
              key=«SECRET_1»
            </div>
          </div>
        </FeatureCard>

        <FeatureCard icon={<KeyRound />} title={t.items.accounts.title} desc={t.items.accounts.desc}>
          <div className="flex flex-wrap gap-2">
            {['Claude Pro/Max', 'ChatGPT', 'Kimi', 'GLM', 'Grok'].map((s) => (
              <span key={s} className="rounded-full bg-black/[0.04] px-2.5 py-1 text-xs font-medium dark:bg-white/[0.06]">
                {s}
              </span>
            ))}
          </div>
        </FeatureCard>
      </div>
    </Section>
  );
}

function Flow({ lang }: { lang: HomeLang }) {
  const t = homeMessages[lang].flow;
  const left = clients.slice(0, 4);
  const right = ['Anthropic', 'OpenAI', 'DeepSeek', 'OpenRouter'];
  return (
    <Section id="how-it-works" className="py-20">
      <SectionHeading eyebrow={t.eyebrow} title={t.title} subtitle={t.subtitle} />
      <div className="mt-12 grid items-center gap-6 md:grid-cols-[1fr_auto_1.15fr_auto_1fr]">
        <FlowColumn label={t.clients} items={left} />
        <FlowArrow />
        <div className="glass glass-strong rounded-[26px] p-6 text-center">
          <AstraLogo className="mx-auto size-14 rounded-[14px] border border-black/10" />
          <p className="mt-3 text-lg font-semibold">{appName}</p>
          <p className="text-xs text-fd-muted-foreground">{t.gateway}</p>
          <p className="mt-2 inline-block rounded-md bg-black/[0.05] px-2 py-0.5 font-mono text-xs dark:bg-white/[0.08]">
            127.0.0.1:17321
          </p>
          <div className="mt-5 grid gap-2">
            {t.steps.map((s, i) => (
              <div
                key={s}
                className="flex items-center gap-2.5 rounded-xl bg-[color-mix(in_oklab,var(--accent)_10%,transparent)] px-3 py-2 text-left text-sm font-medium"
              >
                <span className="flex size-5 items-center justify-center rounded-full bg-[var(--accent)] text-[11px] font-bold text-white">
                  {i + 1}
                </span>
                {s}
              </div>
            ))}
          </div>
        </div>
        <FlowArrow />
        <FlowColumn label={t.providers} items={right} />
      </div>
    </Section>
  );
}

function FlowColumn({ label, items }: { label: string; items: string[] }) {
  return (
    <div className="text-center">
      <p className="mb-3 text-xs font-semibold tracking-wide text-fd-muted-foreground uppercase">{label}</p>
      <div className="flex flex-wrap justify-center gap-2 md:flex-col">
        {items.map((i) => (
          <Chip key={i} className="justify-center">
            {i}
          </Chip>
        ))}
      </div>
    </div>
  );
}

function FlowArrow() {
  return (
    <div aria-hidden className="flex justify-center text-[var(--accent)]">
      <span className="hidden h-px w-10 bg-gradient-to-r from-transparent to-[var(--accent)] md:block" />
      <ArrowRight className="size-5 rotate-90 md:-ml-1 md:rotate-0" />
    </div>
  );
}

function Tour({ lang }: { lang: HomeLang }) {
  const t = homeMessages[lang].tour;
  const shots = (['overview', 'clients', 'requests', 'privacy', 'providers'] as const).map((id) => ({
    id,
    label: t.tabs[id],
    src: `/screenshots/${id}.png`,
  }));
  return (
    <Section id="console" className="py-20">
      <SectionHeading eyebrow={t.eyebrow} title={t.title} subtitle={t.subtitle} />
      <div className="mt-10">
        <ConsoleTour shots={shots} />
      </div>
    </Section>
  );
}

function QuickStart({ lang }: { lang: HomeLang }) {
  const t = homeMessages[lang].start;
  const commands = [installCommand, 'astra start --open', 'astra client enable codex'];
  return (
    <Section id="quick-start" className="py-20">
      <SectionHeading eyebrow={t.eyebrow} title={t.title} />
      <ol className="mt-12 grid gap-5 md:grid-cols-3">
        {t.steps.map((step, i) => (
          <li key={step.title} className="glass flex flex-col rounded-[22px] p-6">
            <span className="flex size-8 items-center justify-center rounded-full bg-[var(--accent)] text-sm font-bold text-white">
              {i + 1}
            </span>
            <h3 className="mt-4 text-lg font-semibold">{step.title}</h3>
            <p className="mt-1.5 flex-1 text-sm text-fd-muted-foreground">{step.desc}</p>
            <code className="mt-5 block truncate rounded-xl bg-[#1d1d1f] px-4 py-3 font-mono text-[13px] text-white/90">
              <span className="text-[#64d2ff] select-none">$ </span>
              {commands[i]}
            </code>
          </li>
        ))}
      </ol>
      <div className="mt-8 text-center">
        <Link
          href="/docs/guide/first-request"
          className="inline-flex items-center gap-1.5 text-sm font-semibold text-[var(--accent)] hover:underline"
        >
          <BookOpen className="size-4" /> {t.more} <ArrowRight className="size-4" />
        </Link>
      </div>
    </Section>
  );
}

function CallToAction({ lang }: { lang: HomeLang }) {
  const t = homeMessages[lang].cta;
  return (
    <Section className="py-20">
      <div className="glass relative isolate overflow-hidden rounded-[32px] px-6 py-14 text-center sm:px-12">
        <div
          aria-hidden
          className="absolute -top-24 left-1/2 -z-10 h-64 w-[36rem] -translate-x-1/2 rounded-full bg-[var(--accent)] opacity-25 blur-3xl"
        />
        <AstraLogo className="mx-auto size-16 rounded-[16px] border border-black/10" />
        <h2 className="mx-auto mt-6 max-w-2xl text-3xl font-semibold tracking-tight text-balance sm:text-4xl">
          {t.title}
        </h2>
        <p className="mt-4 text-fd-muted-foreground">{t.subtitle}</p>
        <div className="mt-8 flex flex-wrap justify-center gap-3">
          <PrimaryButton href="/docs/guide">
            {t.primary} <ArrowRight className="size-4" />
          </PrimaryButton>
          <SecondaryButton href={githubUrl} external>
            <GitHubMark className="size-4" /> {t.secondary}
          </SecondaryButton>
        </div>
      </div>
    </Section>
  );
}

function Footer({ lang }: { lang: HomeLang }) {
  const t = homeMessages[lang].footer;
  const columns = [
    {
      title: t.product,
      links: [
        { text: t.guide, href: '/docs/guide' },
        { text: t.console, href: '/docs/console' },
        { text: t.desktop, href: '/docs/desktop' },
        { text: t.download, href: '/download' },
      ],
    },
    {
      title: t.resources,
      links: [
        { text: t.cli, href: '/docs/cli' },
        { text: t.advanced, href: '/docs/advanced' },
        { text: t.releases, href: releasesUrl },
        { text: 'GitHub', href: githubUrl },
      ],
    },
  ];
  return (
    <footer className="mt-auto border-t border-black/5 dark:border-white/10">
      <div className="mx-auto flex w-full max-w-6xl flex-col gap-10 px-5 py-12 sm:flex-row sm:justify-between sm:px-8">
        <div>
          <div className="flex items-center gap-2.5">
            <AstraLogo className="size-8 rounded-[8px] border border-black/10" />
            <span className="text-lg font-semibold">{appName}</span>
          </div>
          <p className="mt-2 text-sm text-fd-muted-foreground">{t.tagline}</p>
          <p className="mt-6 text-xs text-fd-muted-foreground">
            © {new Date().getFullYear()} {appName} · {t.license}
          </p>
        </div>
        <div className="flex gap-16">
          {columns.map((col) => (
            <div key={col.title}>
              <p className="text-sm font-semibold">{col.title}</p>
              <ul className="mt-3 space-y-2 text-sm text-fd-muted-foreground">
                {col.links.map((l) => (
                  <li key={l.text}>
                    <Link
                      href={l.href}
                      {...(l.href.startsWith('http') ? { target: '_blank', rel: 'noreferrer' } : {})}
                      className="transition-colors hover:text-fd-foreground"
                    >
                      {l.text}
                    </Link>
                  </li>
                ))}
              </ul>
            </div>
          ))}
        </div>
      </div>
    </footer>
  );
}
