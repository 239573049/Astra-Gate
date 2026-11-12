import { Page } from '../components/layout/Page';
import { useI18n } from '../i18n';
import { GalleryPage } from '../plugin-ui/Gallery';

/** /plugins/dev/gallery: the v2 plugin UI component gallery (design/plugin-v2 06 §2). */
export function PluginGalleryPage() {
  const { t } = useI18n();
  return (
    <Page title={t('pluginUi.gallery.title')} subtitle={t('pluginUi.gallery.subtitle')}>
      <GalleryPage />
    </Page>
  );
}
