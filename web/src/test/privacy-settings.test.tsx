import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { DryRunTester, PrivacyPolicyGroup, PrivacyRulesPanel } from '../components/PrivacyGuardSettings';
import type { PrivacyDryRunResult, PrivacyOverview } from '../api/types';
import { FeedbackProvider } from '../components/ui/overlays';
import { I18nProvider } from '../i18n';

const spies = vi.hoisted(() => ({
  update: vi.fn(),
  dryRun: vi.fn(),
}));

const overview: PrivacyOverview = {
  settings: {
    enabled: true,
    dryRun: false,
    restoreResponses: true,
    recordSamples: true,
    defaultAction: 'redact',
    categoryActions: {},
    clientDefaults: {},
    customRules: [{ id: 'custom.employee', name: 'Employee id', pattern: 'EMP-[0-9]{6}', enabled: true }],
  },
  rules: [
    { id: 'builtin.sk-openai', category: 'api_key', description: 'OpenAI-style API keys' },
    { id: 'builtin.email', category: 'email', description: 'Email addresses' },
    { id: 'builtin.aws-access-key', category: 'aws_key', description: 'AWS access keys' },
    { id: 'builtin.intranet-host', category: 'intranet', description: 'Intranet addresses' },
  ],
};

const dryRunResult: PrivacyDryRunResult = {
  blocked: false,
  hits: [{ ruleId: 'builtin.email', category: 'email', action: 'redact', count: 2 }],
  wouldRedact: 2,
  categories: ['email'],
};

vi.mock('../api/hooks', () => ({
  usePrivacy: () => ({ data: overview, isLoading: false, isError: false, error: null }),
  useUpdatePrivacy: () => ({ mutate: spies.update }),
  usePrivacyDryRun: () => ({ mutate: spies.dryRun, isPending: false, data: dryRunResult }),
}));

const show = (ui: React.ReactNode) => {
  localStorage.setItem('astra.locale', 'en');
  return render(
    <I18nProvider>
      <FeedbackProvider>{ui}</FeedbackProvider>
    </I18nProvider>,
  );
};

beforeEach(() => {
  spies.update.mockClear();
  spies.dryRun.mockClear();
});

afterEach(() => {
  cleanup();
  localStorage.clear();
});

/** The page composes these panels side by side; render them together to cover all sections. */
function PrivacyPanels() {
  return (
    <>
      <PrivacyPolicyGroup />
      <PrivacyRulesPanel />
      <DryRunTester />
    </>
  );
}

describe('PrivacyGuardSettings', () => {
  it('renders the policy switches and one action row per detection category', () => {
    show(<PrivacyPanels />);

    expect(screen.getByRole('switch', { name: 'Enable privacy guard' })).toBeChecked();
    expect(screen.getByRole('switch', { name: 'Detect only' })).not.toBeChecked();
    expect(screen.getByRole('switch', { name: 'Record masked samples' })).toBeChecked();
    // Arc selects render a visually hidden label copy, so match "at least one".
    expect(screen.getAllByText('API keys').length).toBeGreaterThan(0);
    expect(screen.getAllByText('AWS keys').length).toBeGreaterThan(0);
    expect(screen.getAllByText('Email addresses').length).toBeGreaterThan(0);
    expect(screen.getByText('Employee id')).toBeInTheDocument(); // custom rule row
  });

  it('turning the master switch off PUTs the whole settings object with enabled=false', async () => {
    show(<PrivacyPanels />);

    fireEvent.click(screen.getByRole('switch', { name: 'Enable privacy guard' }));

    await waitFor(() => expect(spies.update).toHaveBeenCalledTimes(1));
    const sent = spies.update.mock.calls[0][0] as Record<string, unknown>;
    expect(sent.enabled).toBe(false);
    expect(sent.customRules).toHaveLength(1); // the rest of the policy travels unchanged
  });

  it('the dry-run tester sends the pasted text and shows what would happen', async () => {
    show(<PrivacyPanels />);

    fireEvent.change(screen.getByLabelText('Dry run'), { target: { value: 'mail a@b.com' } });
    fireEvent.click(screen.getByRole('button', { name: 'Check' }));

    await waitFor(() =>
      expect(spies.dryRun).toHaveBeenCalledWith({ text: 'mail a@b.com' }, expect.anything()));
    expect(screen.getByText('Would redact 2 place(s)')).toBeInTheDocument();
    expect(screen.getByText('Email addresses · builtin.email × 2')).toBeInTheDocument();
  });
});
