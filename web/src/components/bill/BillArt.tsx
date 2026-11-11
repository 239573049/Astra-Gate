// The two printable looks of the daily bill (styles in bill.css). Both take the shaped bill (lib/bill.ts) and only lay it
// out; the Avalonia client draws the same two pictures (src/Astra.Desktop/Components/Overview/ReceiptBillView.cs,
// MangaBillView.cs).

import { forwardRef } from 'react';

import logo from '../../assets/logo.png';
import { useI18n } from '../../i18n';
import type { BillView } from '../../lib/bill';
import './bill.css';

/** A · thermal receipt: monospace, torn edges, a barcode of the bill number. */
export const ReceiptBill = forwardRef<HTMLDivElement, { view: BillView }>(function ReceiptBill({ view: v }, ref) {
  const { t } = useI18n();
  const kv = (k: string, val: string) => (
    <div className="kv">
      <span>{k}</span>
      <span>{val}</span>
    </div>
  );
  return (
    <div ref={ref} className="bill-rc" data-bill-style="receipt">
      <div className="zz-t" />
      <div className="paper">
        <div className="head">
          <img className="logo" src={logo} alt="Astra Gate" draggable={false} />
          <div className="brand">ASTRA GATE</div>
          <div className="sub">{t('bill.subtitle')}</div>
        </div>
        <div className="dash" />
        <div>
          {kv(t('bill.field.date'), `${v.date} ${v.weekday}`)}
          {kv(t('bill.field.window'), v.window)}
          {kv(t('bill.field.number'), v.number)}
          {kv(t('bill.field.currency'), 'USD')}
        </div>
        <div className="dash" />
        {v.providers.length === 0 && <div className="none">{t('bill.empty')}</div>}
        {v.providers.map((p) => (
          <div key={p.id} className="prov">
            <div className="prov-h">
              <span>■ {p.upper}</span>
              <span>{t('bill.count', { count: p.requests })}</span>
            </div>
            {p.models.map((m) => (
              <div key={m.name} className="m">
                <div className="m-l">
                  <span>{m.name}</span>
                  <b>{m.cost}</b>
                </div>
                <div className="m-s">{t('bill.modelLine', { requests: m.requests, input: m.input, output: m.output })}</div>
              </div>
            ))}
            <div className="sum">
              <span>{t('bill.subtotal')}</span>
              <i />
              <span>{p.cost}</span>
            </div>
          </div>
        ))}
        <div className="dbl" />
        <div>
          {kv(t('bill.field.requests'), t('bill.count', { count: v.requests }))}
          {kv(t('bill.field.successRate'), v.successRate)}
          {kv(t('bill.field.input'), v.inputTokens)}
          {kv(t('bill.field.output'), v.outputTokens)}
        </div>
        <div className="dash" />
        <div className="total">
          <span>{t('bill.total')}</span>
          <strong>{v.total}</strong>
        </div>
        <div className="note">{t('bill.estimate')}</div>
        <div className="barcode" aria-hidden />
        <div className="code">{v.number.replaceAll('-', ' ')}</div>
        <div className="thanks">{t('bill.thanks')}</div>
        <div className="note site">astra-gate.si</div>
      </div>
      <div className="zz-b" />
    </div>
  );
});

/** E · comic page: the Astra line-art face with a speech bubble, inked provider panels and a red "settled" seal. */
export const MangaBill = forwardRef<HTMLDivElement, { view: BillView }>(function MangaBill({ view: v }, ref) {
  const { t } = useI18n();
  return (
    <div ref={ref} className="bill-mg-frame" data-bill-style="manga">
      <div className="bill-mg">
        <div className="top">
          <div className="face">
            <img src={logo} alt="Astra Gate" draggable={false} />
          </div>
          <div className="say">
            <div className="kicker">
              <span>{t('bill.manga.kicker')}</span>
              <span>No.{v.date.replaceAll('-', '')}</span>
            </div>
            <div className="bubble">
              {v.top ? (
                <>
                  <SayTotal template={t('bill.manga.say', { total: '\u0000' })} total={v.total} />
                  <br />
                  <span>{t('bill.manga.top', { provider: v.top.name, share: v.top.share })}</span>
                </>
              ) : (
                t('bill.manga.sayEmpty')
              )}
            </div>
            <div className="kicker">
              <span>
                {v.dateShort} {v.weekday}
              </span>
              <span>{t('bill.manga.until', { time: v.until })}</span>
            </div>
          </div>
        </div>
        <div className="strip">
          <h3>{t('bill.title')}</h3>
          <span>{t('bill.manga.strip', { requests: v.requests, tokens: v.tokensCompact })}</span>
        </div>
        {v.providers.length === 0 ? (
          <div className="none">{t('bill.empty')}</div>
        ) : (
          <div className="grid">
            {v.providers.map((p) => (
              <div key={p.id} className="panel">
                <div className="panel-h">
                  <span>{p.name}</span>
                  <span>{p.cost}</span>
                </div>
                {p.models.map((m) => (
                  <div key={m.name} className="mr">
                    <div>
                      <span>{m.name}</span>
                      <span>{m.cost}</span>
                    </div>
                    <small>{t('bill.modelLine', { requests: m.requests, input: m.input, output: m.output })}</small>
                  </div>
                ))}
              </div>
            ))}
          </div>
        )}
        <div className="tot tone">
          <div className="tot-in">
            <span>{t('bill.total')}</span>
            <strong>{v.total}</strong>
            <small>{t('bill.manga.facts', { requests: v.requests, rate: v.successRate })}</small>
          </div>
          <div className="seal" aria-hidden>
            <b>ASTRA</b>
            <b>GATE</b>
            <span>{t('bill.manga.seal')}</span>
          </div>
        </div>
        <div className="ft">
          <span>{t('bill.estimate')}</span>
          <span>astra-gate.si</span>
        </div>
      </div>
    </div>
  );
});

/** The bubble's first line with the amount set in red: the translation is split around its {total} slot. */
function SayTotal({ template, total }: { template: string; total: string }) {
  const [before, after = ''] = template.split('\u0000');
  return (
    <>
      {before}
      <em>{total}</em>
      {after}
    </>
  );
}
