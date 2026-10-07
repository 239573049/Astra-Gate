import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MotionConfig } from 'motion/react';
import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';

import { ApiError } from './api/client';
import { App } from './App';
import { FeedbackProvider } from './components/ui/overlays';
import { I18nProvider } from './i18n';
import { AppearanceProvider, applyInitialTheme } from './shell/appearance';
import { applyShellAttributes } from './shell/bridge';
// Arc design tokens first; index.css maps them onto Astra's palette and must load after.
import './components/arc/foundation.css';
import './styles/index.css';

applyShellAttributes();
applyInitialTheme();

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      staleTime: 5_000,
      // Don't hammer the server on 4xx; retry network blips once.
      retry: (count, err) => count < 1 && !(err instanceof ApiError && err.status >= 400 && err.status < 500),
    },
  },
});

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <QueryClientProvider client={queryClient}>
      <I18nProvider>
        <AppearanceProvider>
          <MotionConfig reducedMotion="user">
            <FeedbackProvider>
              <App />
            </FeedbackProvider>
          </MotionConfig>
        </AppearanceProvider>
      </I18nProvider>
    </QueryClientProvider>
  </StrictMode>,
);
