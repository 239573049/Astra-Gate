using System.ComponentModel;
using Astra.Api.Contracts;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Components.ProviderDetail;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.Clients;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Shell;
using Avalonia;
using Avalonia.Controls;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;
using SettingsGroup = Astra.Desktop.Components.Kit.SettingsGroup;
using SettingsRow = Astra.Desktop.Components.Kit.SettingsRow;
using Tone = Astra.Desktop.Components.Kit.Tone;

namespace Astra.Desktop.Components.Clients;

/// <summary>What the profile sheets need: the profiles API and toasts / confirm.</summary>
public sealed record ProfileSheetContext(ProfilesApi Api, IFeedback Feedback);

/// <summary>
/// The profiles of the Clients page (web <c>ProfilesBar</c>): a sheet that lists the saved profiles (active / drifted marks) with apply,
/// overwrite, rename and delete, and saves the clients' current setup under a new name; applying first shows a preview of what each client
/// would do. A profile is switched through the regular enable / disable path on the server, so the original configs are backed up and
/// restored as for any client switch.
/// </summary>
public static class ProfileSheets
{
    private static string ErrorText(Exception e) => e.Message;

    private static bool IsApiError(Exception e) => e is ApiException or HttpRequestException or TaskCanceledException;

    /// <summary>The profile list sheet; observes the list only while it is open.</summary>
    public static Sheet Show(Visual owner, ProfileSheetContext ctx)
    {
        var query = ctx.Api.Profiles();
        var body = new ContentControl();
        var done = ClientUi.Button(Loc.T("common.done"), null);
        var sheet = new Sheet { Title = Loc.T("profiles.title"), Description = Loc.T("profiles.subtitle"), SheetWidth = 600, Content = body, Footer = [done] };
        done.Click += (_, _) => sheet.Close();
        var name = "";
        string? renaming = null; // the profile being renamed; null = saving a new one
        var busy = false;
        var nameBox = PdUi.TextBox("", v => name = v, name: Loc.T("profiles.name"), placeholder: Loc.T("profiles.name"));

        async Task Run(Func<Task> action)
        {
            busy = true;
            Render();
            try { await action(); }
            catch (Exception e) when (IsApiError(e)) { ctx.Feedback.Error(ErrorText(e)); }
            finally
            {
                busy = false;
                Render();
            }
        }

        void Render()
        {
            var stack = new StackPanel { Spacing = 16 };
            if (query.IsLoading) stack.Children.Add(new LoadingState(3));
            if (query.IsError && !query.HasData) stack.Children.Add(ClientUi.Alert(AlertTone.Danger, Loc.T("common.error"), query.Error?.Message));
            if (query.Data is { Count: 0 }) stack.Children.Add(ClientUi.Text(Loc.T("profiles.none"), 12, ArcDesignTokenKind.TextSecondary));
            if (query.Data is { Count: > 0 } list)
            {
                var group = new SettingsGroup();
                foreach (var p in list)
                {
                    var profile = p;
                    var label = ClientUi.HRow(8, KitStyle.Text(p.Name, ArcDesignTokenKind.TypeSM, ArcDesignTokenKind.Foreground));
                    if (p.Active) label.Children.Add(KitBadge.Create(p.Drifted ? Loc.T("profiles.active") + " · " + Loc.T("profiles.drifted") : Loc.T("profiles.active"),
                        p.Drifted ? Tone.Orange : Tone.Green));
                    var row = new SettingsRow { Label = label, Detail = Loc.T("profiles.clientCount", ("count", p.Clients.Count)) };
                    var apply = ClientUi.Button(Loc.T("profiles.apply"), () => _ = ShowApply(owner, ctx, profile), ButtonVariant.Primary, small: true);
                    var overwrite = ClientUi.Button(null, () => _ = Overwrite(profile), small: true, icon: ClientUi.Icon(LucideIconKind.Save, 12));
                    var rename = ClientUi.Button(null, () =>
                    {
                        renaming = profile.Id;
                        nameBox.Text = profile.Name;
                        Render();
                    }, small: true, icon: ClientUi.Icon(LucideIconKind.Pencil, 12));
                    var delete = ClientUi.Button(null, () => _ = Delete(profile), small: true, icon: ClientUi.Icon(LucideIconKind.Trash2, 12));
                    ToolTip.SetTip(overwrite, Loc.T("profiles.overwrite"));
                    ToolTip.SetTip(rename, Loc.T("profiles.rename"));
                    ToolTip.SetTip(delete, Loc.T("profiles.delete"));
                    foreach (var c in new Control[] { apply, overwrite, rename, delete }) row.Trailing.Add(c);
                    group.Rows.Add(row);
                }
                stack.Children.Add(group);
            }
            var save = ClientUi.Button(renaming is null ? Loc.T("profiles.save") : Loc.T("profiles.rename"), () => _ = Save(), ButtonVariant.Primary);
            save.IsLoading = busy;
            var entry = ClientUi.HRow(8, nameBox, save);
            stack.Children.Add(entry);
            body.Content = stack;
        }

        async Task Save()
        {
            var n = name.Trim();
            if (n.Length == 0) return;
            await Run(async () =>
            {
                if (renaming is { } id) await ctx.Api.UpdateAsync(id, n, overwrite: false);
                else await ctx.Api.SaveAsync(n);
                ctx.Feedback.Success(Loc.T("profiles.saved", ("name", n)));
                renaming = null;
                nameBox.Text = "";
            });
        }

        async Task Overwrite(ProfileDto profile)
        {
            if (!await ctx.Feedback.ConfirmAsync(new ConfirmRequest(Loc.T("profiles.overwriteConfirm", ("name", profile.Name)), null, Loc.T("profiles.overwrite")))) return;
            await Run(async () =>
            {
                await ctx.Api.UpdateAsync(profile.Id, null, overwrite: true);
                ctx.Feedback.Success(Loc.T("profiles.saved", ("name", profile.Name)));
            });
        }

        async Task Delete(ProfileDto profile)
        {
            if (!await ctx.Feedback.ConfirmAsync(new ConfirmRequest(Loc.T("profiles.deleteConfirm", ("name", profile.Name)), null, Loc.T("profiles.delete"), Destructive: true))) return;
            await Run(() => ctx.Api.DeleteAsync(profile.Id));
        }

        void OnChanged(object? s, PropertyChangedEventArgs e) => Avalonia.Threading.Dispatcher.UIThread.Post(Render);
        query.PropertyChanged += OnChanged;
        var token = query.Activate();
        Render();
        _ = WaitClosedAsync();
        return sheet;

        async Task WaitClosedAsync()
        {
            try { await sheet.ShowAsync(owner); }
            finally
            {
                query.PropertyChanged -= OnChanged;
                token.Dispose();
            }
        }
    }

    /// <summary>The preview of applying <paramref name="profile"/> (per client: what it would do), and the apply on confirm.</summary>
    public static Sheet ShowApply(Visual owner, ProfileSheetContext ctx, ProfileDto profile)
    {
        var body = new StackPanel { Spacing = 16 };
        var cancel = ClientUi.Button(Loc.T("common.cancel"), null);
        var confirm = ClientUi.Button(Loc.T("profiles.apply"), null, ButtonVariant.Primary, enabled: false);
        var sheet = new Sheet
        {
            Title = Loc.T("profiles.previewTitle", ("name", profile.Name)),
            Description = Loc.T("profiles.previewDetail"),
            SheetWidth = 620,
            Content = body,
            Footer = [cancel, confirm],
        };
        cancel.Click += (_, _) => sheet.Close();
        body.Children.Add(new LoadingState(4));

        confirm.Click += async (_, _) =>
        {
            confirm.IsLoading = true;
            try
            {
                var result = await ctx.Api.ApplyAsync(profile.Id);
                sheet.Close();
                var skipped = result.Steps.Count(s => s.Action is "skipped" or "failed");
                ctx.Feedback.Success(skipped > 0
                    ? Loc.T("profiles.appliedPartial", ("name", profile.Name), ("count", skipped))
                    : Loc.T("profiles.applied", ("name", profile.Name)));
            }
            catch (Exception e) when (IsApiError(e))
            {
                ctx.Feedback.Error(ErrorText(e));
            }
            finally
            {
                confirm.IsLoading = false;
            }
        };

        _ = Load();
        _ = sheet.ShowAsync(owner);
        return sheet;

        async Task Load()
        {
            try
            {
                var plan = await ctx.Api.PreviewAsync(profile.Id);
                body.Children.Clear();
                body.Children.Add(PlanView(plan));
                confirm.IsEnabled = true;
            }
            catch (Exception e) when (IsApiError(e))
            {
                body.Children.Clear();
                body.Children.Add(ClientUi.Alert(AlertTone.Danger, Loc.T("common.error"), ErrorText(e)));
            }
        }
    }

    /// <summary>One row per client that would change, then what the profile named that no longer exists.</summary>
    public static Control PlanView(ProfilePlanDto plan)
    {
        var stack = new StackPanel { Spacing = 16 };
        var changing = plan.Steps.Where(s => s.Action != "unchanged").ToList();
        if (changing.Count == 0) stack.Children.Add(ClientUi.Alert(AlertTone.Info, Loc.T("profiles.nothing")));
        else
        {
            var group = new SettingsGroup();
            foreach (var s in changing)
            {
                var row = new SettingsRow { Label = s.Name, Detail = StepDetail(s) };
                row.Trailing.Add(KitBadge.Create(Loc.T("profiles.action." + s.Action), ToneOf(s.Action)));
                group.Rows.Add(row);
            }
            stack.Children.Add(group);
        }
        if (plan.Dropped.Count > 0)
            stack.Children.Add(ClientUi.Alert(AlertTone.Warning, Loc.T("profiles.dropped"), string.Join("\n", plan.Dropped.Select(DropText))));
        return stack;
    }

    private static Tone ToneOf(string action) => action switch
    {
        "enable" => Tone.Green,
        "update" => Tone.Accent,
        "disable" or "skipped" => Tone.Orange,
        "failed" => Tone.Red,
        _ => Tone.Neutral,
    };

    private static string? StepDetail(ProfileStepDto s)
    {
        var parts = new List<string>();
        if (s.Reason is { } reason) parts.Add(s.Action == "failed" ? reason : Loc.T("profiles.reason." + reason));
        if (s.Changes.Count > 0) parts.Add(string.Join(", ", s.Changes.Select(c => c.KeyPath)));
        foreach (var n in s.Notes)
            parts.Add(n.StartsWith("drifted:", StringComparison.Ordinal) ? Loc.T("profiles.drift", ("key", n["drifted:".Length..])) : n);
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    private static string DropText(ProfileDropDto d)
    {
        var text = d.What == "provider" ? Loc.T($"profiles.drop.provider.{d.Reason}", ("id", d.Id)) : Loc.T($"profiles.drop.{d.What}", ("id", d.Id));
        return $"{d.Kind}: {text}";
    }
}
