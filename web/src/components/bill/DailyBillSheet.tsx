// The overview's "daily bill" (header button): today's spending per provider and model, printed as a receipt or a comic
// page, copied or saved as a PNG. The Avalonia client has the same sheet (src/Astra.Desktop/Components/Overview/DailyBillSheet.cs).

import { Copy, Download } from 'lucide-react';
import { useMemo, useRef, useState } from 'react';

import { useDailyBill } from '../../api/hooks';
import { useI18n } from '../../i18n';
import { BILL_STYLE_KEY, BILL_STYLES, billFileName, shapeBill, type BillStyle } from '../../lib/bill';
import { Alert } from '../arc/alert/alert';
import { Button, Segmented, Spinner } from '../ui/controls';
import { errorText, Sheet, useFeedback } from '../ui/overlays';
import { MangaBill, ReceiptBill } from './BillArt';
import { copyBill, saveBill } from './exportImage';

function storedStyle(): BillStyle {
  try {
    const v = localStorage.getItem(BILL_STYLE_KEY);
    if (v && (BILL_STYLES as readonly string[]).includes(v)) return v as BillStyle;
  } catch {
    // storage unavailable: the default
  }
  return 'receipt';
}

export function DailyBillSheet({ open, onOpenChange }: { open: boolean; onOpenChange: (open: boolean) => void }) {
  const { t, locale } = useI18n();
  const { toast } = useFeedback();
  const bill = useDailyBill(open);
  const [style, setStyleState] = useState<BillStyle>(storedStyle);
  const [busy, setBusy] = useState(false);
  const art = useRef<HTMLDivElement>(null);
  const view = useMemo(() => (bill.data ? shapeBill(bill.data, locale) : null), [bill.data, locale]);

  const setStyle = (s: BillStyle) => {
    setStyleState(s);
    try {
      localStorage.setItem(BILL_STYLE_KEY, s);
    } catch {
      // not remembered
    }
  };

  const run = async (action: 'copy' | 'save') => {
    if (!art.current || !bill.data) return;
    setBusy(true);
    try {
      if (action === 'copy') {
        await copyBill(art.current);
        toast(t('bill.copied'), 'success');
      } else {
        await saveBill(art.current, billFileName(bill.data.date, style));
        toast(t('bill.saved'), 'success');
      }
    } catch (e) {
      toast(t(action === 'copy' ? 'bill.copyFailed' : 'bill.saveFailed', { error: errorText(e) }), 'error');
    } finally {
      setBusy(false);
    }
  };

  return (
    <Sheet
      open={open}
      onOpenChange={onOpenChange}
      title={t('bill.title')}
      description={t('bill.description')}
      width={720}
      footer={
        <>
          <Button icon={<Copy className="size-4" />} disabled={!view || busy} onClick={() => void run('copy')}>
            {t('bill.copy')}
          </Button>
          <Button variant="primary" icon={<Download className="size-4" />} disabled={!view || busy} onClick={() => void run('save')}>
            {t('bill.save')}
          </Button>
        </>
      }
    >
      <div className="flex flex-col gap-3">
        <Segmented
          className="self-start"
          ariaLabel={t('bill.style')}
          value={style}
          onChange={setStyle}
          items={BILL_STYLES.map((s) => ({ value: s, label: t(`bill.style.${s}`) }))}
        />
        <div className="max-h-[62vh] overflow-auto rounded-xl bg-fill-2 p-5" data-testid="bill-preview">
          {bill.error ? (
            <Alert tone="danger" title={t('common.error')}>{errorText(bill.error)}</Alert>
          ) : !view ? (
            <Spinner lines={6} />
          ) : (
            <div className="flex justify-center">
              {style === 'receipt' ? <ReceiptBill ref={art} view={view} /> : <MangaBill ref={art} view={view} />}
            </div>
          )}
        </div>
      </div>
    </Sheet>
  );
}
