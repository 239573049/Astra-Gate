import { describe, expect, it } from 'vitest';

import { PRIVACY_RULE_TEMPLATES } from '../lib/privacyTemplates';

describe('privacy rule templates', () => {
  it('every pattern compiles in JavaScript', () => {
    for (const tpl of PRIVACY_RULE_TEMPLATES) {
      expect(() => new RegExp(tpl.pattern), tpl.id).not.toThrow();
    }
  });

  it('template ids are unique', () => {
    expect(new Set(PRIVACY_RULE_TEMPLATES.map((t) => t.id)).size).toBe(PRIVACY_RULE_TEMPLATES.length);
  });

  it('matches the samples each template claims to catch', () => {
    const patternOf = (id: string) => new RegExp(PRIVACY_RULE_TEMPLATES.find((t) => t.id === id)!.pattern);
    expect(patternOf('ipv4-all').test('server 203.0.113.9 down')).toBe(true);
    expect(patternOf('ipv4-all').test('version 10.5.1')).toBe(false);
    expect(patternOf('landline').test('call 010-88886666 now')).toBe(true);
    expect(patternOf('license-plate').test('plate 京A12345 parked')).toBe(true);
    expect(patternOf('passport-cn').test('passport E12345678')).toBe(true);
    expect(patternOf('uscc').test('code 91330106K2H8Y3X5X6')).toBe(true);
    expect(patternOf('long-token').test('token abcd1234abcd1234abcd1234abcd1234')).toBe(true);
    expect(patternOf('mac-address').test('mac aa:bb:cc:dd:ee:ff here')).toBe(true);
    expect(patternOf('unix-home').test('log at /Users/alice/app.log')).toBe(true);
    expect(patternOf('windows-profile').test('saved in C:\\Users\\alice\\AppData')).toBe(true);
  });

  it('enterprise templates match their typical leaks', () => {
    const patternOf = (id: string) => new RegExp(PRIVACY_RULE_TEMPLATES.find((t) => t.id === id)!.pattern);
    expect(patternOf('cloud-accesskey').test('key LTAI5tABCdefGHIjklMNOpqr')).toBe(true);
    expect(patternOf('cloud-accesskey').test('AKIDz8krbsJ5yKBZQpn74WFkLPw')).toBe(true);
    expect(patternOf('cloud-accesskey').test('container BASE64DATA')).toBe(false);
    // AWS's own documented example secret (exactly 40 chars of base64).
    expect(patternOf('aws-secret-key').test('wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY')).toBe(true);
    expect(patternOf('aws-secret-key').test('wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKE')).toBe(false); // 39 chars
    expect(patternOf('im-webhook').test('https://oapi.dingtalk.com/robot/send?access_token=abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789')).toBe(true);
    expect(patternOf('im-webhook').test('https://qyapi.weixin.qq.com/cgi-bin/webhook/send?key=abcdef0123456789abcdef0123456789')).toBe(true);
    expect(patternOf('internal-service').test('check http://gitlab.corp.example.com/ci')).toBe(true);
    expect(patternOf('internal-service').test('see example.com/docs')).toBe(false);
    expect(patternOf('jdbc-conn').test('jdbc:mysql://localhost:3306/db?user=root&password=s3cr3t')).toBe(true);
    expect(patternOf('smtp-conn').test('smtp://user:mypass@smtp.corp.com:587')).toBe(true);
    expect(patternOf('key-file-ref').test('loaded from /etc/ssl/server.pem')).toBe(true);
    expect(patternOf('key-file-ref').test(String.raw`C:\ssl\server.key`)).toBe(true);
    expect(patternOf('confidential-marker').test('此文档为内部资料，请勿外传')).toBe(true);
    expect(patternOf('employee-id').test('EMP-123456 onboarded')).toBe(true);
    expect(patternOf('employee-id').test('工号：123456 已离职')).toBe(true);
    expect(patternOf('labeled-number').test('合同编号：HT-2026-001')).toBe(true);
    expect(patternOf('labeled-number').test('工单编号 AB12CD')).toBe(true);
  });
});
