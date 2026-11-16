import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { I18nProvider } from '../i18n';

// jsdom has no ResizeObserver (the Select measures itself); a noop one never fires.
class NoopResizeObserver {
  observe() {}
  unobserve() {}
  disconnect() {}
}

const { AwsKeyFields, ServiceAccountField, awsKeyDocument, awsRegionOf, isCloudScheme, readServiceAccount } = await import('../components/CloudCredentials');

afterEach(cleanup);

describe('cloud credential helpers', () => {
  it('assembles the IAM key document only once both required parts are there', () => {
    expect(awsKeyDocument('', 'secret', '')).toBe('');
    expect(awsKeyDocument('AKIA1', '', '')).toBe('');
    expect(JSON.parse(awsKeyDocument(' AKIA1 ', ' secret ', ''))).toEqual({ accessKeyId: 'AKIA1', secretAccessKey: 'secret' });
    expect(JSON.parse(awsKeyDocument('AKIA1', 'secret', 'tok'))).toEqual({ accessKeyId: 'AKIA1', secretAccessKey: 'secret', sessionToken: 'tok' });
  });

  it('reads the region an AWS address names', () => {
    expect(awsRegionOf('https://bedrock-runtime.eu-west-1.amazonaws.com/openai/v1')).toBe('eu-west-1');
    expect(awsRegionOf('https://bedrock-mantle.ap-southeast-4.api.aws/v1')).toBe('ap-southeast-4');
    expect(awsRegionOf('https://api.openai.com/v1')).toBeNull();
    expect(awsRegionOf('not a url')).toBeNull();
    expect(awsRegionOf(undefined)).toBeNull();
  });

  it('recognises a service account key file and nothing else, without keeping the private key', () => {
    const key = JSON.stringify({ type: 'service_account', client_email: 'a@p.iam.gserviceaccount.com', private_key: 'x', project_id: 'p' });
    expect(readServiceAccount(key)).toEqual({ email: 'a@p.iam.gserviceaccount.com', projectId: 'p' });
    expect(readServiceAccount(JSON.stringify({ type: 'authorized_user', client_email: 'a', private_key: 'x' }))).toBeNull();
    expect(readServiceAccount('{"client_email":"a"}')).toBeNull();
    expect(readServiceAccount('nope')).toBeNull();
  });

  it('knows the cloud schemes', () => {
    expect(isCloudScheme('aws-sigv4')).toBe(true);
    expect(isCloudScheme('gcp-service-account')).toBe(true);
    expect(isCloudScheme('bearer')).toBe(false);
    expect(isCloudScheme(undefined)).toBe(false);
  });
});

describe('cloud credential fields', () => {
  it('reports the IAM document as the fields fill in', () => {
    vi.stubGlobal('ResizeObserver', NoopResizeObserver);
    const onChange = vi.fn();
    render(
      <I18nProvider>
        <AwsKeyFields onChange={onChange} />
      </I18nProvider>,
    );
    expect(onChange).toHaveBeenLastCalledWith('');
    fireEvent.change(screen.getByLabelText(/access key id/i), { target: { value: 'AKIA1' } });
    fireEvent.change(screen.getByLabelText(/secret access key/i), { target: { value: 'sec' } });
    expect(JSON.parse(onChange.mock.lastCall![0] as string)).toEqual({ accessKeyId: 'AKIA1', secretAccessKey: 'sec' });
  });

  it('accepts a pasted key file, names its account, and refuses other JSON', () => {
    vi.stubGlobal('ResizeObserver', NoopResizeObserver);
    const onChange = vi.fn();
    const onFacts = vi.fn();
    render(
      <I18nProvider>
        <ServiceAccountField onChange={onChange} onFacts={onFacts} />
      </I18nProvider>,
    );
    const box = screen.getByPlaceholderText(/service_account/);
    fireEvent.change(box, { target: { value: '{"foo":1}' } });
    expect(onChange).toHaveBeenLastCalledWith('');
    const key = JSON.stringify({ type: 'service_account', client_email: 'a@p.iam.gserviceaccount.com', private_key: 'x', project_id: 'demo' });
    fireEvent.change(box, { target: { value: key } });
    expect(onChange).toHaveBeenLastCalledWith(key);
    expect(onFacts).toHaveBeenLastCalledWith({ email: 'a@p.iam.gserviceaccount.com', projectId: 'demo' });
    expect(screen.getByText(/Service account: a@p\.iam\.gserviceaccount\.com/)).toBeTruthy();
  });
});
