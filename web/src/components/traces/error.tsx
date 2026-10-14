import { ApiError } from '../../api/client';
import { useI18n } from '../../i18n';
import { Alert } from '../arc/alert/alert';
import { errorText } from '../ui/overlays';

/**
 * Why a trace view has nothing to show. A 404 on /api/traces means the connected server predates tracing (an older
 * astra-server still holding the port), which an empty canvas would hide.
 */
export function TraceError({ error }: { error: unknown }) {
  const { t } = useI18n();
  const unsupported = error instanceof ApiError && error.status === 404 && /\/api\/traces/.test(error.message);
  return (
    <Alert tone="warning" title={unsupported ? t('traces.error.unsupportedTitle') : t('traces.error.title')}>
      {unsupported ? t('traces.error.unsupported') : errorText(error)}
    </Alert>
  );
}
