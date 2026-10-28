namespace Astra.Desktop.Core.Privacy;

/// <summary>A ready-made custom rule offered in the add-rule dialog; users can edit the pattern after picking it.</summary>
/// <param name="Id">Stable id of the template.</param>
/// <param name="NameKey">Catalog key of its name (also the default rule name).</param>
/// <param name="HintKey">Catalog key of its explanation.</param>
/// <param name="Pattern">The regular expression (the server compiles it with .NET, the same engine that validates it here).</param>
public sealed record PrivacyRuleTemplate(string Id, string NameKey, string HintKey, string Pattern);

/// <summary>The templates of web/src/lib/privacyTemplates.ts (<c>PRIVACY_RULE_TEMPLATES</c>), patterns byte for byte.</summary>
public static class PrivacyRuleTemplates
{
    /// <summary>All templates, in the web's order.</summary>
    public static readonly IReadOnlyList<PrivacyRuleTemplate> All =
    [
        new("ipv4-all", "settings.privacy.template.ipv4All", "settings.privacy.template.ipv4AllHint", """(?<![\d.])(?:\d{1,3}\.){3}\d{1,3}(?![\d.])"""),
        new("landline", "settings.privacy.template.landline", "settings.privacy.template.genericHint", """(?<!\d)0\d{2,3}[- ]?\d{7,8}(?!\d)"""),
        new("license-plate", "settings.privacy.template.licensePlate", "settings.privacy.template.genericHint", """[京津沪渝冀豫云辽黑湘皖鲁新苏浙赣鄂桂甘晋蒙陕吉闽贵粤青藏川宁琼使领][A-HJ-NP-Z][A-HJ-NP-Z0-9]{4,5}[A-HJ-NP-Z0-9挂学警港澳]"""),
        new("passport-cn", "settings.privacy.template.passportCn", "settings.privacy.template.genericHint", """(?<![A-Za-z0-9])[EG]\d{8}(?!\d)"""),
        new("uscc", "settings.privacy.template.uscc", "settings.privacy.template.genericHint", """(?<![0-9A-HJ-NPQRTUWXY])[0-9A-HJ-NPQRTUWXY]{18}(?![0-9A-HJ-NPQRTUWXY])"""),
        new("long-token", "settings.privacy.template.longToken", "settings.privacy.template.longTokenHint", """(?<![A-Za-z0-9])[A-Za-z0-9_\-]{32,}(?![A-Za-z0-9])"""),
        new("mac-address", "settings.privacy.template.macAddress", "settings.privacy.template.genericHint", """(?<![0-9A-Fa-f:])(?:[0-9A-Fa-f]{2}:){5}[0-9A-Fa-f]{2}(?![0-9A-Fa-f:])"""),
        new("unix-home", "settings.privacy.template.unixHome", "settings.privacy.template.unixHomeHint", """/(?:Users|home)/[A-Za-z0-9_.\-]+"""),
        new("windows-profile", "settings.privacy.template.windowsProfile", "settings.privacy.template.unixHomeHint", """[A-Za-z]:\\+Users\\+[A-Za-z0-9_.\-]+"""),
        new("cloud-accesskey", "settings.privacy.template.cloudAccessKey", "settings.privacy.template.genericHint", """(?<![A-Za-z0-9])(?:LTAI[A-Za-z0-9]{12,20}|AKID[A-Za-z0-9]{13,40})(?![A-Za-z0-9])"""),
        new("aws-secret-key", "settings.privacy.template.awsSecretKey", "settings.privacy.template.genericHint", """(?<![A-Za-z0-9/+=])[A-Za-z0-9/+=]{40}(?![A-Za-z0-9/+=])"""),
        new("im-webhook", "settings.privacy.template.imWebhook", "settings.privacy.template.imWebhookHint", """oapi\.dingtalk\.com/robot/send\?access_token=[0-9a-f]{64}|qyapi\.weixin\.qq\.com/cgi-bin/webhook/send\?key=[0-9a-f]{32}|open\.feishu\.cn/open-apis/bot/v2/hook/[0-9a-f\-]{20,}|hooks\.slack\.com/services/T[A-Za-z0-9]+/B[A-Za-z0-9]+/[A-Za-z0-9]+"""),
        new("internal-service", "settings.privacy.template.internalService", "settings.privacy.template.genericHint", """(?:gitlab|jenkins|jira|confluence|wiki|nexus|harbor|grafana|kibana|sonarqube|oa|sso)\.[A-Za-z0-9\-]+(?:\.[A-Za-z0-9\-]+)+"""),
        new("jdbc-conn", "settings.privacy.template.jdbcConn", "settings.privacy.template.jdbcHint", """jdbc:[A-Za-z0-9]+:[^\s"']*(?:[Uu]ser(?:[ _]?[Ii][Dd])?|[Pp]assword)=[^\s"&';]+"""),
        new("smtp-conn", "settings.privacy.template.smtpConn", "settings.privacy.template.jdbcHint", """(?:smtp|imap|pop3)s?://[^\s:@/]+:[^\s@]+@[^\s]+"""),
        new("key-file-ref", "settings.privacy.template.keyFileRef", "settings.privacy.template.genericHint", """[A-Za-z0-9_\-./\\]+\.(?:pem|key|pfx|p12|jks|keystore)\b"""),
        new("confidential-marker", "settings.privacy.template.confidentialMarker", "settings.privacy.template.confidentialHint", """(?:绝密|机密|商业秘密|内部资料|内部公开|仅限内部|请勿外传|Confidential|Internal Use Only|Internal Only|Company Private)"""),
        new("employee-id", "settings.privacy.template.employeeId", "settings.privacy.template.genericHint", """(?<![A-Za-z0-9])(?:[Ee][Mm][Pp]|工号)[:：\- ]?\d{4,10}(?!\d)"""),
        new("labeled-number", "settings.privacy.template.labeledNumber", "settings.privacy.template.genericHint", """(?:合同|订单|采购单|工单|项目)编号[:：]?\s*[A-Za-z0-9_\-]{4,32}"""),
    ];

    /// <summary>The template with an id, or null.</summary>
    public static PrivacyRuleTemplate? Find(string id) => All.FirstOrDefault(t => t.Id == id);
}
