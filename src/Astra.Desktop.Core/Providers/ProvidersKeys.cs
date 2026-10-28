namespace Astra.Desktop.Core.Providers;

/// <summary>
/// Every catalog key the providers page, its wizard and the import sheet read (web <c>t(...)</c> calls of ProvidersPage.tsx and
/// ImportProvidersSheet.tsx), including the keys built from data (categories, skip reasons, statuses, quota error codes). A test checks
/// them against the generated catalog, so a key renamed in <c>messages.ts</c> fails here and not as a raw key on screen.
/// </summary>
public static class ProvidersKeys
{
    private static readonly string[] Fixed =
    [
        "common.back", "common.cancel", "common.continue", "common.delete", "common.enabled", "common.error", "plugins.browseMarket",
        "import.detail", "import.done", "import.doneSkipped", "import.existing", "import.footnote", "import.ignored", "import.models", "import.noItems",
        "import.noKey", "import.noMatch", "import.notFound", "import.path", "import.rescan", "import.sameHostHint", "import.search", "import.selected",
        "import.sourceError", "import.template", "import.title",
        "providers.add", "providers.addDetail", "providers.addEndpoint", "providers.addHeader", "providers.addTitle", "providers.addTitleDetails",
        "providers.apiKey", "providers.apiKeyHint", "providers.authScheme", "providers.baseUrl", "providers.baseUrlHint", "providers.card.active",
        "providers.card.disabled", "providers.card.noAuth", "providers.card.noRequests", "providers.card.plugin", "providers.card.requests7d",
        "providers.card.subscription", "providers.category.plugin", "providers.count", "providers.create", "providers.created", "providers.custom",
        "providers.customDetail", "providers.empty", "providers.emptyDetail", "providers.endpointsFooter", "providers.fullUrl", "providers.fullUrlHint",
        "providers.getKey", "providers.headerName", "providers.headers", "providers.headerValue", "providers.import", "providers.initialModels",
        "providers.initialModelsCustomHint", "providers.initialModelsTemplateHint", "providers.keyAlternative.pick", "providers.keyAlternative.wizard",
        "providers.modelCount", "providers.moreOptions", "providers.moreOptionsDetail", "providers.multiplier", "providers.multiplierHint", "providers.name",
        "providers.noKey", "providers.notes", "providers.pluginHint", "providers.pluginProvided", "providers.preferred", "providers.preferredHint",
        "providers.priceKey", "providers.priceKeyHint", "providers.priceKeyNone", "providers.priceKeyShort", "providers.protocol", "providers.proxy",
        "providers.proxyHint", "providers.quota.balance", "providers.quota.failed", "providers.quota.invalid", "providers.quota.never",
        "providers.quota.refresh", "providers.quota.totalUsed", "providers.quota.usedPercent", "providers.quota.window", "providers.searchTemplates",
        "providers.section.endpoints", "providers.step.details", "providers.step.template", "providers.subscriptionAdded",
        "providers.subscriptionWizardDetail", "providers.subscriptionWizardHint", "providers.templateModels", "providers.templateUpdate", "providers.test",
        "providers.variant", "providers.variantDefault",
    ];

    private static readonly string[] Statuses = ["new", "same", "sameHost", "off", "skip"];

    private static readonly string[] SkipReasons = ["official-login", "no-base-url", "unsupported-protocol", "points-to-astra"];

    private static readonly string[] QuotaErrors =
        ["config", "no_key", "unauthorized", "no_endpoint", "rate_limited", "upstream", "timeout", "network", "not_json", "too_large", "empty"];

    /// <summary>All keys, the data-built families expanded.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        .. Fixed,
        .. ProviderTemplates.CategoryOrder.Select(c => "providers.category." + c),
        .. Statuses.Select(s => "import.status." + s),
        .. SkipReasons.Select(r => "import.skip." + r),
        .. QuotaErrors.Select(e => "providers.quota.error." + e),
    ];
}
