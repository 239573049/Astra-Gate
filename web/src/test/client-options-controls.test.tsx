import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { ClientOptionDescriptor } from '../api/types';
import { ClientOptionsGroup } from '../components/ClientOptionsGroup';
import { I18nProvider } from '../i18n';

const descriptors: ClientOptionDescriptor[] = [
  {
    key: 'contextWindow',
    type: 'int',
    default: '',
    scope: 'config',
    choices: ['128000', '400000', '1000000'],
  },
  { key: 'supportsWebsockets', type: 'bool', default: 'false', scope: 'config' },
];

afterEach(cleanup);

describe('client option controls', () => {
  it('renders a selector instead of a text field and keeps a saved custom number visible', () => {
    localStorage.setItem('astra.locale', 'en');
    const onChange = vi.fn();
    render(
      <I18nProvider>
        <ClientOptionsGroup
          descriptors={descriptors}
          values={{ contextWindow: 300000 }}
          onChange={onChange}
          enabled
        />
      </I18nProvider>,
    );
    expect(screen.getByRole('combobox', { name: 'Context window' })).toHaveTextContent('300K');
    expect(screen.queryByRole('textbox')).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole('switch', { name: 'Enable WebSocket' }));
    expect(onChange).toHaveBeenCalledWith({ contextWindow: 300000, supportsWebsockets: true });
  });
});
