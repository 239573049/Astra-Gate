using System.Text;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Components.ProviderDetail;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Providers;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;
using ArcButton = TKCode.Arc.Button;

namespace Astra.Desktop.Components.Providers;

/// <summary>
/// The fields of the two cloud auth schemes (web <c>components/CloudCredentials.tsx</c>): an Amazon Bedrock IAM access key (id, secret, optional
/// session token) and a Google Vertex AI service account key file (pasted or chosen, with project and location). Each assembles the JSON document
/// the provider's one encrypted secret keeps (rules in <see cref="CloudCredentials"/>) and hands it to <c>apiKey</c>. Used by the add-provider
/// wizard and the API-key tab.
/// </summary>
internal static class CloudCredentialFields
{
    /// <summary>Access key id, secret and optional session token; <paramref name="document"/> gets the assembled JSON ("" until it is complete).</summary>
    public static Control Aws(Action<string> document, string seed = "")
    {
        var (id, secret, token) = CloudCredentials.ReadAwsParts(seed);
        void Report() => document(CloudCredentials.AwsKeyDocument(id, secret, token));
        var idBox = new Input { Label = Loc.T("providers.cloud.aws.accessKeyId"), Placeholder = "AKIA…", Text = id };
        idBox.Token(TextBlock.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily);
        idBox.TextChanged += (_, _) => { id = idBox.Text ?? ""; Report(); };
        var secretBox = new PasswordField { Label = Loc.T("providers.cloud.aws.secretAccessKey"), Description = Loc.T("providers.apiKeyHint"), Text = secret };
        secretBox.Token(TextBlock.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily);
        secretBox.TextChanged += (_, _) => { secret = secretBox.Text ?? ""; Report(); };
        var tokenBox = new PasswordField { Label = Loc.T("providers.cloud.aws.sessionToken"), Description = Loc.T("providers.cloud.aws.sessionTokenHint"), Text = token };
        tokenBox.Token(TextBlock.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily);
        tokenBox.TextChanged += (_, _) => { token = tokenBox.Text ?? ""; Report(); };
        return new StackPanel { Spacing = 12, Children = { idBox, secretBox, tokenBox } };
    }

    /// <summary>The region the first endpoint names (the signature is scoped to it; another variant changes it).</summary>
    public static Control RegionHint(IEnumerable<string> baseUrls)
    {
        var region = CloudCredentials.AwsRegionOf(baseUrls.FirstOrDefault());
        return ProviderUi.Hint(region is null ? Loc.T("providers.cloud.aws.regionUnknown") : Loc.T("providers.cloud.aws.region", ("region", region)));
    }

    /// <summary>
    /// A service account key file: pasted into the box or chosen from disk (read here, sent only to Astra). <paramref name="document"/> gets the
    /// key ("" while the text is no service account key); <paramref name="facts"/> its account and project.
    /// </summary>
    public static Control ServiceAccount(Action<string> document, Action<ServiceAccountFacts?> facts, string seed = "")
    {
        var note = ProviderUi.Hint(Loc.T("providers.cloud.gcp.keyFileHint"));
        var area = PdUi.Area(seed, null, Loc.T("providers.cloud.gcp.keyFile"), 120, mono: true, placeholder: "{ \"type\": \"service_account\", … }");
        void Changed()
        {
            var text = area.Text ?? "";
            var found = text.Trim().Length == 0 ? null : CloudCredentials.ReadServiceAccount(text);
            document(found is null ? "" : text.Trim());
            facts(found);
            note.Text = found is not null ? Loc.T("providers.cloud.gcp.detected", ("email", found.Email))
                : text.Trim().Length > 0 ? Loc.T("providers.cloud.gcp.invalid") : Loc.T("providers.cloud.gcp.keyFileHint");
        }
        area.TextChanged += (_, _) => Changed();
        ArcButton? choose = null;
        async Task ChooseAsync()
        {
            if (TopLevel.GetTopLevel(choose)?.StorageProvider is not { } storage) return;
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                AllowMultiple = false, FileTypeFilter = [new FilePickerFileType("JSON") { Patterns = ["*.json"], MimeTypes = ["application/json"] }],
            });
            if (files.Count == 0) return;
            await using var stream = await files[0].OpenReadAsync();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            area.Text = await reader.ReadToEndAsync();
        }
        choose = ProviderUi.Button(Loc.T("providers.cloud.gcp.chooseFile"), LucideIconKind.FileUp, ButtonVariant.Secondary, ControlSize.Small, () => _ = ChooseAsync());
        choose.HorizontalAlignment = HorizontalAlignment.Left;
        return new StackPanel { Spacing = 8, Children = { area, note, choose } };
    }

    /// <summary>Project and location, which Vertex puts in every request path (stored in settings.vertex).</summary>
    public static Control VertexTarget(string project, string location, string? projectPlaceholder, Action<string, string> changed)
    {
        var projectBox = new Input
        {
            Label = Loc.T("providers.cloud.gcp.project"), Description = Loc.T("providers.cloud.gcp.projectHint"),
            Placeholder = string.IsNullOrEmpty(projectPlaceholder) ? "my-project-123" : projectPlaceholder, Text = project,
        };
        projectBox.Token(TextBlock.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily);
        var current = location.Length == 0 ? "global" : location;
        var place = PdUi.Choice(CloudCredentials.LocationChoices(current).Select(l => (l, l, (string?)null)), current, l => changed(projectBox.Text ?? "", l),
            name: Loc.T("providers.cloud.gcp.location"));
        projectBox.TextChanged += (_, _) => changed(projectBox.Text ?? "", place.SelectedValue as string ?? current);
        return new StackPanel
        {
            Spacing = 12,
            Children =
            {
                projectBox,
                ProviderUi.Labeled(Loc.T("providers.cloud.gcp.location"), place, Loc.T("providers.cloud.gcp.locationHint")),
            },
        };
    }
}
