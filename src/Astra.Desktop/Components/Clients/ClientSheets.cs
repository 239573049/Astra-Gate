using System.ComponentModel;
using System.Text.Json.Nodes;
using Astra.Api.Contracts;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.Clients;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Overview;
using Astra.Desktop.Core.Shell;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using TKCode.Arc;
using SettingsGroup = Astra.Desktop.Components.Kit.SettingsGroup;
using SettingsRow = Astra.Desktop.Components.Kit.SettingsRow;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;
using ArcButton = TKCode.Arc.Button;
using Tone = Astra.Desktop.Components.Kit.Tone;

namespace Astra.Desktop.Components.Clients;

/// <summary>What the client sheets need from the page: the API, toasts / confirm, the clipboard and the zone the times are shown in.</summary>
public sealed record ClientSheetContext(ClientsApi Api, IFeedback Feedback, Func<string, Task<bool>> Copy, TimeSpan Zone);

/// <summary>
/// The sheets of the client panel (web/src/pages/ClientsPage.tsx): <c>EnableSheet</c> (the config edits as a unified diff before anything is
/// written), <c>BackupsSheet</c> (the config backups and their restore) and <c>InstallSheet</c> (confirm the exact command, then follow the
/// run's output). Each is the kit's <see cref="Sheet"/> (the web's centered dialog); a sheet observes its queries only while it is open.
/// </summary>
public static class ClientSheets
{
    private static string Name(ClientInfoDto c) => ClientCatalog.Name(c.Kind, c.Name);

    private static string ErrorText(Exception e) => e.Message;

    // ---------- enable ----------

    /// <summary>
    /// Previews enabling (or re-applying) <paramref name="client"/> with <paramref name="request"/> and enables it on confirm. Nothing is
    /// written until the user confirms; closing the sheet discards the preview.
    /// </summary>
    public static Sheet ShowEnable(Visual owner, ClientSheetContext ctx, ClientInfoDto client, JsonObject request)
    {
        var name = Name(client);
        var body = new StackPanel { Spacing = 16 };
        var cancel = ClientUi.Button(Loc.T("common.cancel"), null);
        var confirm = ClientUi.Button(client.Enabled ? Loc.T("common.apply") : Loc.T("clients.enable"), null, ButtonVariant.Primary, enabled: false);
        var sheet = new Sheet
        {
            Title = Loc.T("clients.enableTitle", ("name", name)),
            Description = Loc.T("clients.enableDetail"),
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
                var enabled = await ctx.Api.EnableAsync(client.Kind, (JsonObject)request.DeepClone());
                sheet.Close();
                ctx.Feedback.Success(enabled.RequiresRestart ? Loc.T("clients.enabledRestart", ("name", name)) : Loc.T("clients.enabledToast", ("name", name)));
            }
            catch (Exception e) when (e is ApiException or HttpRequestException or TaskCanceledException)
            {
                ctx.Feedback.Error(ErrorText(e));
            }
            finally
            {
                confirm.IsLoading = false;
            }
        };

        _ = LoadPreviewAsync();
        _ = sheet.ShowAsync(owner);
        return sheet;

        async Task LoadPreviewAsync()
        {
            try
            {
                var preview = await ctx.Api.PreviewEnableAsync(client.Kind, (JsonObject)request.DeepClone());
                body.Children.Clear();
                body.Children.Add(PreviewView(preview));
                confirm.IsEnabled = true;
            }
            catch (Exception e) when (e is ApiException or HttpRequestException or TaskCanceledException)
            {
                body.Children.Clear();
                body.Children.Add(ClientUi.Alert(AlertTone.Danger, Loc.T("common.error"), ErrorText(e)));
            }
        }
    }

    /// <summary>The preview: warnings, "no changes", then one diff per file under its path (web <c>PreviewView</c>).</summary>
    public static Control PreviewView(ClientPreviewDto preview)
    {
        var stack = new StackPanel { Spacing = 16 };
        foreach (var w in preview.Warnings) stack.Children.Add(ClientUi.Alert(AlertTone.Warning, w));
        if (preview.Changes.Count == 0) stack.Children.Add(ClientUi.Alert(AlertTone.Info, Loc.T("clients.noChanges")));
        foreach (var d in preview.Diffs)
        {
            var file = ClientUi.Text(d.File, 11, ArcDesignTokenKind.TextSecondary);
            file.Token(TextBlock.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily);
            file.Margin = new Thickness(0, 0, 0, 8);
            stack.Children.Add(new StackPanel { Children = { file, new DiffView(d.UnifiedDiff) } });
        }
        return stack;
    }

    // ---------- backups ----------

    /// <summary>The client's config backups, newest first as the server lists them, each restorable after a confirmation.</summary>
    public static Sheet ShowBackups(Visual owner, ClientSheetContext ctx, ClientInfoDto client)
    {
        var query = ctx.Api.Backups(client.Kind);
        var body = new ContentControl();
        var done = ClientUi.Button(Loc.T("common.done"), null);
        var sheet = new Sheet
        {
            Title = Loc.T("clients.backups"),
            Description = Loc.T("clients.backupsDetail"),
            SheetWidth = 520,
            Content = body,
            Footer = [done],
        };
        done.Click += (_, _) => sheet.Close();
        string? restoring = null;

        void Render()
        {
            var stack = new StackPanel { Spacing = 12 };
            if (query.IsLoading) stack.Children.Add(new LoadingState(3));
            if (query.IsError && !query.HasData) stack.Children.Add(ClientUi.Alert(AlertTone.Danger, Loc.T("common.error"), query.Error?.Message));
            if (query.HasData && query.Data is { Count: 0 })
                stack.Children.Add(new EmptyStateView(Loc.T("clients.noBackups"), icon: new LucideIcon { Kind = LucideIconKind.Archive, Size = 24 }));
            if (query.Data is { Count: > 0 } list)
            {
                var rows = new List<Control>();
                foreach (var b in list)
                {
                    var label = ClientUi.HRow(8, KitStyle.Text(StatsFormat.DateTime(b.CreatedAt, ctx.Zone, Loc.Language), ArcDesignTokenKind.TypeSM, ArcDesignTokenKind.Foreground));
                    if (b.FirstWrite) label.Children.Add(KitBadge.Create(Loc.T("clients.firstWrite"), Tone.Accent));
                    var row = new SettingsRow { Label = label, Detail = string.Join(", ", b.Files) };
                    var restore = ClientUi.Button(Loc.T("clients.restore"), null, small: true, icon: ClientUi.Icon(LucideIconKind.RotateCcw, 12));
                    restore.IsLoading = restoring == b.Id;
                    var backup = b;
                    restore.Click += async (_, _) =>
                    {
                        if (!await ctx.Feedback.ConfirmAsync(new ConfirmRequest(Loc.T("clients.restoreConfirm"), string.Join("\n", backup.Files), Loc.T("clients.restore"), Destructive: true))) return;
                        restoring = backup.Id;
                        Render();
                        try
                        {
                            await ctx.Api.RestoreBackupAsync(client.Kind, backup.Id);
                            ctx.Feedback.Success(Loc.T("clients.restored"));
                        }
                        catch (Exception e) when (e is ApiException or HttpRequestException or TaskCanceledException)
                        {
                            ctx.Feedback.Error(ErrorText(e));
                        }
                        finally
                        {
                            restoring = null;
                            Render();
                        }
                    };
                    row.Trailing.Add(restore);
                    rows.Add(row);
                }
                var group = new SettingsGroup();
                foreach (var r in rows) group.Rows.Add(r);
                group.Margin = new Thickness(0, 0, 0, -20);
                stack.Children.Add(group);
            }
            body.Content = stack;
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

    // ---------- install / update ----------

    /// <summary>
    /// Runs an install / update / uninstall after showing the exact command, then follows its output (polled). <see cref="InstallSheetMode.Log"/>
    /// only shows the current or last run. Closing the sheet does not stop a run; "Stop" does. Confirming an uninstall of a client Astra has
    /// taken over says its original config is restored first (the server does it through the disable path before the command runs).
    /// </summary>
    public static Sheet ShowInstall(Visual owner, ClientSheetContext ctx, ClientInfoDto client, InstallSheetMode mode)
    {
        var name = Name(client);
        var inst = client.Install;
        var query = ctx.Api.InstallJob(client.Kind);
        var started = false;
        var starting = false;
        var cancelling = false;
        var body = new ContentControl();
        var sheet = new Sheet { SheetWidth = 620, Content = body };
        ScrollViewer? log = null;

        async Task Run(string action, bool elevated)
        {
            starting = true;
            Render();
            try
            {
                await ctx.Api.StartInstallAsync(client.Kind, action, elevated);
                started = true;
            }
            catch (Exception e) when (e is ApiException or HttpRequestException or TaskCanceledException)
            {
                ctx.Feedback.Error(ErrorText(e));
            }
            finally
            {
                starting = false;
                Render();
            }
        }

        async Task Copy(string text)
        {
            if (await ctx.Copy(text)) ctx.Feedback.Info(Loc.T("common.copied"));
        }

        Control? Fix()
        {
            if (inst?.AdminFixCommand is not { } fix) return null;
            var tip = ClientUi.Wrap(Loc.T("clients.admin.fixTip"), ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary, 11);
            var copy = ClientUi.Button(null, () => _ = Copy(fix), small: true, icon: ClientUi.Icon(LucideIconKind.Copy, 12));
            Avalonia.Automation.AutomationProperties.SetName(copy, Loc.T("common.copy"));
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
            var box = ClientUi.CodeBox(fix);
            row.Children.Add(box);
            Grid.SetColumn(copy, 1);
            copy.VerticalAlignment = VerticalAlignment.Top;
            row.Children.Add(copy);
            return new StackPanel { Spacing = 8, Children = { tip, row } };
        }

        void Render()
        {
            var job = query.HasData ? query.Data : null;
            var confirming = InstallLogic.Confirming(mode, started, job);
            var command = InstallLogic.CommandFor(mode, inst);
            var needsAdmin = inst?.NeedsAdmin == true;
            var canElevate = inst?.AdminAvailable == true;
            var retry = InstallLogic.RetryAsAdmin(job, inst);

            sheet.Title = Loc.T(InstallLogic.TitleKey(mode, job), ("name", name));
            var uninstalling = mode == InstallSheetMode.Uninstall;
            sheet.Description = confirming ? Loc.T(uninstalling ? "clients.uninstallRunDetail" : "clients.installRunDetail") : null;

            // ----- footer -----
            var footer = new List<Control>();
            switch (InstallLogic.FooterFor(confirming, job))
            {
                case InstallFooter.Confirm:
                    footer.Add(ClientUi.Button(Loc.T("common.cancel"), () => sheet.Close()));
                    if (needsAdmin && !canElevate)
                        // No system prompt here (e.g. Linux without pkexec): the user runs it in a terminal.
                        footer.Add(ClientUi.Button(Loc.T("clients.admin.copyCommand"), () => _ = Copy("sudo " + command), ButtonVariant.Primary, icon: ClientUi.Icon(LucideIconKind.Copy), enabled: command is not null));
                    else
                    {
                        var start = ClientUi.Button(needsAdmin ? Loc.T("clients.admin.start") : uninstalling ? Loc.T("clients.uninstall") : Loc.T("clients.installStart"),
                            () => _ = Run(InstallLogic.ActionName(mode), needsAdmin), uninstalling ? ButtonVariant.Danger : ButtonVariant.Primary,
                            icon: needsAdmin ? ClientUi.Icon(LucideIconKind.ShieldAlert) : null, enabled: command is not null);
                        start.IsLoading = starting;
                        footer.Add(start);
                    }
                    break;
                case InstallFooter.Running:
                    // An elevated command runs as root under the system prompt; it cannot be stopped from here.
                    if (job?.Elevated != true)
                    {
                        var stop = ClientUi.Button(Loc.T("clients.installStop"), null, ButtonVariant.Danger);
                        stop.IsLoading = cancelling;
                        stop.Click += async (_, _) =>
                        {
                            cancelling = true;
                            Render();
                            try { await ctx.Api.CancelInstallAsync(client.Kind); }
                            catch (Exception e) when (e is ApiException or HttpRequestException or TaskCanceledException) { ctx.Feedback.Error(ErrorText(e)); }
                            finally
                            {
                                cancelling = false;
                                Render();
                            }
                        };
                        footer.Add(stop);
                    }
                    footer.Add(ClientUi.Button(Loc.T("common.close"), () => sheet.Close()));
                    break;
                default:
                    footer.Add(ClientUi.Button(Loc.T("common.done"), () => sheet.Close()));
                    if (retry && job is not null)
                    {
                        var again = ClientUi.Button(Loc.T("clients.admin.retry"), () => _ = Run(job.Action, true), ButtonVariant.Primary, icon: ClientUi.Icon(LucideIconKind.ShieldAlert));
                        again.IsLoading = starting;
                        footer.Add(again);
                    }
                    break;
            }
            sheet.Footer = footer;

            // ----- body -----
            var stack = new StackPanel { Spacing = 12 };
            if (confirming)
            {
                if (InstallLogic.RestoresFirst(mode, client.Enabled))
                    stack.Children.Add(ClientUi.Alert(AlertTone.Warning, Loc.T("clients.uninstallRestoreTitle"), Loc.T("clients.uninstallRestoreDetail", ("name", name))));
                if (needsAdmin) stack.Children.Add(ClientUi.Alert(AlertTone.Warning, Loc.T("clients.admin.title"), Loc.T(canElevate ? "clients.admin.prompt" : "clients.admin.manual")));
                stack.Children.Add(command is not null ? ClientUi.CodeBox(needsAdmin ? "sudo " + command : command) : ClientUi.Alert(AlertTone.Warning, Loc.T("clients.installUnavailable")));
                if (uninstalling) stack.Children.Add(ClientUi.Wrap(Loc.T("clients.uninstallKeepsData"), ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextMuted, 11));
                else if (inst?.Method == "npm") stack.Children.Add(ClientUi.Wrap(Loc.T("clients.installNpmNote"), ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextMuted, 11));
                if (needsAdmin && Fix() is { } fix) stack.Children.Add(fix);
            }
            else if (job is not null)
            {
                var tone = InstallLogic.NotEffective(job) ? AlertTone.Warning : job.State switch
                {
                    "running" => AlertTone.Info,
                    "succeeded" => AlertTone.Success,
                    "failed" => AlertTone.Danger,
                    _ => AlertTone.Warning,
                };
                stack.Children.Add(ClientUi.Alert(tone, Loc.T(InstallLogic.StatusTitleKey(job)), InstallLogic.StatusBody(job, retry, (k, a) => Loc.T(k, a))));
                var box = ClientUi.CodeBox(string.Join("\n", job.Log), 11, maxHeight: 340, minHeight: 160, relaxed: true);
                log = box.Child as ScrollViewer;
                stack.Children.Add(box);
                if (job.Hint == "permission-denied" && Fix() is { } fix) stack.Children.Add(fix);
                // Follow the output (web: scroll the log to its end whenever it grows).
                Avalonia.Threading.Dispatcher.UIThread.Post(() => log?.ScrollToEnd(), Avalonia.Threading.DispatcherPriority.Background);
            }
            else if (query.IsLoading || (!query.HasData && !query.IsError)) stack.Children.Add(new LoadingState(4));
            else stack.Children.Add(new EmptyStateView(Loc.T("clients.noInstallLog"), icon: new LucideIcon { Kind = LucideIconKind.ScrollText, Size = 24 }));
            body.Content = stack;
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
}
