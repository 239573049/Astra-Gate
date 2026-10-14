import { lazy, Suspense, type ReactNode } from 'react';
import { BrowserRouter, HashRouter, Navigate, Route, Routes } from 'react-router';

import { useAuthStatus } from './api/hooks';
import { AppShell } from './components/layout/AppShell';
import { Spinner } from './components/ui/controls';
import { LoginPage } from './pages/LoginPage';
import { OverviewPage } from './pages/OverviewPage';
import { isDesktop } from './shell/bridge';

// The overview (the window's first screen) and the login page ship in the entry chunk; every other page is
// its own chunk, so the first paint — and the tray panel's hidden window — parse only what they show.
const ClientsPage = lazy(() => import('./pages/ClientsPage').then((m) => ({ default: m.ClientsPage })));
const ModelDetailPage = lazy(() => import('./pages/ModelDetailPage').then((m) => ({ default: m.ModelDetailPage })));
const ModelsPage = lazy(() => import('./pages/ModelsPage').then((m) => ({ default: m.ModelsPage })));
const PluginsPage = lazy(() => import('./pages/PluginsPage').then((m) => ({ default: m.PluginsPage })));
const PrivacyPage = lazy(() => import('./pages/PrivacyPage').then((m) => ({ default: m.PrivacyPage })));
const ProviderDetailPage = lazy(() =>
  import('./pages/ProviderDetailPage').then((m) => ({ default: m.ProviderDetailPage })),
);
const ProvidersPage = lazy(() => import('./pages/ProvidersPage').then((m) => ({ default: m.ProvidersPage })));
const RequestsPage = lazy(() => import('./pages/RequestsPage').then((m) => ({ default: m.RequestsPage })));
const SettingsPage = lazy(() => import('./pages/SettingsPage').then((m) => ({ default: m.SettingsPage })));
const TokensPage = lazy(() => import('./pages/TokensPage').then((m) => ({ default: m.TokensPage })));
const TracesPage = lazy(() => import('./pages/TracesPage').then((m) => ({ default: m.TracesPage })));
// The token self-service page lives outside the admin login (the token itself authenticates; plan: alerts-webhooks-token-limits §2.2).
const KeyPage = lazy(() => import('./pages/KeyPage').then((m) => ({ default: m.KeyPage })));
const TrayPanelPage = lazy(() => import('./pages/TrayPanelPage').then((m) => ({ default: m.TrayPanelPage })));

// app:// has no server-side fallback for deep links, so the desktop uses hash routing.
const Router = isDesktop ? HashRouter : BrowserRouter;

export function App() {
  return (
    <Router>
      <Routes>
        <Route
          path="key"
          element={
            <Suspense fallback={null}>
              <KeyPage />
            </Suspense>
          }
        />
        <Route path="*" element={<AdminApp />} />
      </Routes>
    </Router>
  );
}

function AdminApp() {
  return (
    <AuthGate>
      {/* Pages inside the shell suspend at its <Outlet />; this boundary only covers the tray route. */}
      <Suspense fallback={null}>
        <Routes>
          {/* The desktop tray popover window: no app shell around it. */}
          <Route path="tray" element={<TrayPanelPage />} />
          <Route element={<AppShell />}>
            <Route index element={<OverviewPage />} />
            <Route path="requests" element={<RequestsPage />} />
            <Route path="traces" element={<TracesPage />} />
            <Route path="traces/sessions" element={<TracesPage />} />
            <Route path="traces/sessions/:key" element={<TracesPage />} />
            <Route path="traces/requests" element={<TracesPage />} />
            <Route path="traces/requests/:id" element={<TracesPage />} />
            <Route path="tokens" element={<TokensPage />} />
            <Route path="clients" element={<ClientsPage />} />
            <Route path="clients/:kind" element={<ClientsPage />} />
            <Route path="providers" element={<ProvidersPage />} />
            <Route path="providers/:id" element={<ProviderDetailPage />} />
            <Route path="models" element={<ModelsPage />} />
            <Route path="models/:id" element={<ModelDetailPage />} />
            <Route path="plugins" element={<PluginsPage />} />
            <Route path="plugins/:tab" element={<PluginsPage />} />
            <Route path="privacy" element={<PrivacyPage />} />
            <Route path="privacy/:tab" element={<PrivacyPage />} />
            <Route path="settings" element={<SettingsPage />} />
            <Route path="settings/:tab" element={<SettingsPage />} />
            <Route path="*" element={<Navigate to="/" replace />} />
          </Route>
        </Routes>
      </Suspense>
    </AuthGate>
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
