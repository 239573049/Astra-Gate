import type { MessageKey } from '../i18n';

/** Ready-made custom rule patterns offered in the add-rule dialog; users can edit after picking. */
export interface PrivacyRuleTemplate {
  id: string;
  nameKey: MessageKey;
  hintKey: MessageKey;
  pattern: string;
}

export const PRIVACY_RULE_TEMPLATES: PrivacyRuleTemplate[] = [
  {
    id: 'ipv4-all',
    nameKey: 'settings.privacy.template.ipv4All',
    hintKey: 'settings.privacy.template.ipv4AllHint',
    pattern: String.raw`(?<![\d.])(?:\d{1,3}\.){3}\d{1,3}(?![\d.])`,
  },
  {
    id: 'landline',
    nameKey: 'settings.privacy.template.landline',
    hintKey: 'settings.privacy.template.genericHint',
    pattern: String.raw`(?<!\d)0\d{2,3}[- ]?\d{7,8}(?!\d)`,
  },
  {
    id: 'license-plate',
    nameKey: 'settings.privacy.template.licensePlate',
    hintKey: 'settings.privacy.template.genericHint',
    pattern: String.raw`[京津沪渝冀豫云辽黑湘皖鲁新苏浙赣鄂桂甘晋蒙陕吉闽贵粤青藏川宁琼使领][A-HJ-NP-Z][A-HJ-NP-Z0-9]{4,5}[A-HJ-NP-Z0-9挂学警港澳]`,
  },
  {
    id: 'passport-cn',
    nameKey: 'settings.privacy.template.passportCn',
    hintKey: 'settings.privacy.template.genericHint',
    pattern: String.raw`(?<![A-Za-z0-9])[EG]\d{8}(?!\d)`,
  },
  {
    id: 'uscc',
    nameKey: 'settings.privacy.template.uscc',
    hintKey: 'settings.privacy.template.genericHint',
    pattern: String.raw`(?<![0-9A-HJ-NPQRTUWXY])[0-9A-HJ-NPQRTUWXY]{18}(?![0-9A-HJ-NPQRTUWXY])`,
  },
  {
    id: 'long-token',
    nameKey: 'settings.privacy.template.longToken',
    hintKey: 'settings.privacy.template.longTokenHint',
    pattern: String.raw`(?<![A-Za-z0-9])[A-Za-z0-9_\-]{32,}(?![A-Za-z0-9])`,
  },
  {
    id: 'mac-address',
    nameKey: 'settings.privacy.template.macAddress',
    hintKey: 'settings.privacy.template.genericHint',
    pattern: String.raw`(?<![0-9A-Fa-f:])(?:[0-9A-Fa-f]{2}:){5}[0-9A-Fa-f]{2}(?![0-9A-Fa-f:])`,
  },
  {
    id: 'unix-home',
    nameKey: 'settings.privacy.template.unixHome',
    hintKey: 'settings.privacy.template.unixHomeHint',
    pattern: String.raw`/(?:Users|home)/[A-Za-z0-9_.\-]+`,
  },
  {
    id: 'windows-profile',
    nameKey: 'settings.privacy.template.windowsProfile',
    hintKey: 'settings.privacy.template.unixHomeHint',
    pattern: String.raw`[A-Za-z]:\\+Users\\+[A-Za-z0-9_.\-]+`,
  },
  // ---------- enterprise ----------
  {
    id: 'cloud-accesskey',
    nameKey: 'settings.privacy.template.cloudAccessKey',
    hintKey: 'settings.privacy.template.genericHint',
    pattern: String.raw`(?<![A-Za-z0-9])(?:LTAI[A-Za-z0-9]{12,20}|AKID[A-Za-z0-9]{13,40})(?![A-Za-z0-9])`,
  },
  {
    id: 'aws-secret-key',
    nameKey: 'settings.privacy.template.awsSecretKey',
    hintKey: 'settings.privacy.template.genericHint',
    pattern: String.raw`(?<![A-Za-z0-9/+=])[A-Za-z0-9/+=]{40}(?![A-Za-z0-9/+=])`,
  },
  {
    id: 'im-webhook',
    nameKey: 'settings.privacy.template.imWebhook',
    hintKey: 'settings.privacy.template.imWebhookHint',
    pattern: String.raw`oapi\.dingtalk\.com/robot/send\?access_token=[0-9a-f]{64}|qyapi\.weixin\.qq\.com/cgi-bin/webhook/send\?key=[0-9a-f]{32}|open\.feishu\.cn/open-apis/bot/v2/hook/[0-9a-f\-]{20,}|hooks\.slack\.com/services/T[A-Za-z0-9]+/B[A-Za-z0-9]+/[A-Za-z0-9]+`,
  },
  {
    id: 'internal-service',
    nameKey: 'settings.privacy.template.internalService',
    hintKey: 'settings.privacy.template.genericHint',
    pattern: String.raw`(?:gitlab|jenkins|jira|confluence|wiki|nexus|harbor|grafana|kibana|sonarqube|oa|sso)\.[A-Za-z0-9\-]+(?:\.[A-Za-z0-9\-]+)+`,
  },
  {
    id: 'jdbc-conn',
    nameKey: 'settings.privacy.template.jdbcConn',
    hintKey: 'settings.privacy.template.jdbcHint',
    pattern: String.raw`jdbc:[A-Za-z0-9]+:[^\s"']*(?:[Uu]ser(?:[ _]?[Ii][Dd])?|[Pp]assword)=[^\s"&';]+`,
  },
  {
    id: 'smtp-conn',
    nameKey: 'settings.privacy.template.smtpConn',
    hintKey: 'settings.privacy.template.jdbcHint',
    pattern: String.raw`(?:smtp|imap|pop3)s?://[^\s:@/]+:[^\s@]+@[^\s]+`,
  },
  {
    id: 'key-file-ref',
    nameKey: 'settings.privacy.template.keyFileRef',
    hintKey: 'settings.privacy.template.genericHint',
    pattern: String.raw`[A-Za-z0-9_\-./\\]+\.(?:pem|key|pfx|p12|jks|keystore)\b`,
  },
  {
    id: 'confidential-marker',
    nameKey: 'settings.privacy.template.confidentialMarker',
    hintKey: 'settings.privacy.template.confidentialHint',
    pattern: String.raw`(?:绝密|机密|商业秘密|内部资料|内部公开|仅限内部|请勿外传|Confidential|Internal Use Only|Internal Only|Company Private)`,
  },
  {
    id: 'employee-id',
    nameKey: 'settings.privacy.template.employeeId',
    hintKey: 'settings.privacy.template.genericHint',
    pattern: String.raw`(?<![A-Za-z0-9])(?:[Ee][Mm][Pp]|工号)[:：\- ]?\d{4,10}(?!\d)`,
  },
  {
    id: 'labeled-number',
    nameKey: 'settings.privacy.template.labeledNumber',
    hintKey: 'settings.privacy.template.genericHint',
    pattern: String.raw`(?:合同|订单|采购单|工单|项目)编号[:：]?\s*[A-Za-z0-9_\-]{4,32}`,
  },
];
