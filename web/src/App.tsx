import type { ReactNode } from 'react';
import { BrowserRouter, HashRouter, Navigate, Route, Routes } from 'react-router';

import { useAuthStatus } from './api/hooks';
import { AppShell } from './components/layout/AppShell';
import { Spinner } from './components/ui/controls';
import { ClientsPage } from './pages/ClientsPage';
import { LoginPage } from './pages/LoginPage';
import { ModelDetailPage } from './pages/ModelDetailPage';
import { ModelsPage } from './pages/ModelsPage';
import { OverviewPage } from './pages/OverviewPage';
import { PrivacyPage } from './pages/PrivacyPage';
import { ProviderDetailPage } from './pages/ProviderDetailPage';
import { ProvidersPage } from './pages/ProvidersPage';
import { RequestsPage } from './pages/RequestsPage';
import { SettingsPage } from './pages/SettingsPage';
import { TokensPage } from './pages/TokensPage';
import { TrayPanelPage } from './pages/TrayPanelPage';
import { isDesktop } from './shell/bridge';

// app:// has no server-side fallback for deep links, so the desktop uses hash routing.
const Router = isDesktop ? HashRouter : BrowserRouter;

export function App() {
  return (
    <Router>
      <AuthGate>
        <Routes>
          {/* The desktop tray popover window: no app shell around it. */}
          <Route path="tray" element={<TrayPanelPage />} />
          <Route element={<AppShell />}>
            <Route index element={<OverviewPage />} />
            <Route path="requests" element={<RequestsPage />} />
            <Route path="tokens" element={<TokensPage />} />
            <Route path="clients" element={<ClientsPage />} />
            <Route path="clients/:kind" element={<ClientsPage />} />
            <Route path="providers" element={<ProvidersPage />} />
            <Route path="providers/:id" element={<ProviderDetailPage />} />
            <Route path="models" element={<ModelsPage />} />
            <Route path="models/:id" element={<ModelDetailPage />} />
            <Route path="privacy" element={<PrivacyPage />} />
            <Route path="privacy/:tab" element={<PrivacyPage />} />
            <Route path="settings" element={<SettingsPage />} />
            <Route path="settings/:tab" element={<SettingsPage />} />
            <Route path="*" element={<Navigate to="/" replace />} />
          </Route>
        </Routes>
      </AuthGate>
    </Router>
  );
}

/** Remote (non-loopback) servers require a password; loopback access is always signed in. */
function AuthGate({ children }: { children: ReactNode }) {
  const auth = useAuthStatus();
  if (auth.isLoading) {
    return (
      <div className="wallpaper flex h-full items-center justify-center">
        <Spinner />
      </div>
    );
  }
  if (auth.data?.required && !auth.data.signedIn) return <LoginPage />;
  return <>{children}</>;
}
