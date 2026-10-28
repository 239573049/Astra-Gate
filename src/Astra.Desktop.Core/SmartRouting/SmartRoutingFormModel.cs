using Astra.Api.Contracts;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Plugins;
using Astra.Desktop.Core.Shell;

namespace Astra.Desktop.Core.SmartRouting;

/// <summary>Which part of the form an edit changes the shape of (the view rebuilds that part; plain typing changes none).</summary>
[Flags]
public enum DraftArea
{
    /// <summary>Only values changed: no rows appear or vanish.</summary>
    None = 0,

    /// <summary>The status group (after a save or a refresh).</summary>
    Status = 1,

    /// <summary>The judge group (dialect, service, key state).</summary>
    Judge = 2,

    /// <summary>The grades group (add, remove, move, fallback badge).</summary>
    Grades = 4,

    /// <summary>The rules group (mode shows or hides the session length; grade names feed the fallback list).</summary>
    Rules = 8,

    /// <summary>Everything (a discard, a save).</summary>
    All = Status | Judge | Grades | Rules,
}

/// <summary>
/// The page's form (web <c>SmartRoutingForm</c>): the draft being edited against the saved settings, the save and the switch, and the
/// reasons a save was refused. Created once the settings first load; <see cref="Saved"/> follows every refresh (the status polls)
/// without touching the draft.
/// </summary>
public sealed class SmartRoutingFormModel : NotifyingModel
{
    private readonly SmartRoutingApi _api;
    private readonly IFeedback _feedback;
    private SmartRoutingDto _saved;
    private IReadOnlyList<string> _problems = [];
    private bool _saving;

    /// <summary>Creates the form from the settings as saved.</summary>
    public SmartRoutingFormModel(SmartRoutingApi api, IFeedback feedback, SmartRoutingDto saved)
    {
        _api = api;
        _feedback = feedback;
        _saved = saved;
        Draft = SmartRoutingDraft.Of(saved);
        Tester = new SmartRoutingTester(api, () => Draft);
    }

    /// <summary>Raised after the draft or the saved settings changed, with the parts whose shape changed.</summary>
    public event Action<DraftArea>? Changed;

    /// <summary>Toasts (the copy button confirms with one).</summary>
    public IFeedback Feedback => _feedback;

    /// <summary>What is on screen.</summary>
    public SmartRoutingDraft Draft { get; private set; }

    /// <summary>The settings as saved, with their live status.</summary>
    public SmartRoutingDto Saved => _saved;

    /// <summary>The sample-text tester of the judge group.</summary>
    public SmartRoutingTester Tester { get; }

    /// <summary>Why the last save was refused, one line each; empty when it was not.</summary>
    public IReadOnlyList<string> Problems => _problems;

    /// <summary>A save is running.</summary>
    public bool IsSaving => _saving;

    /// <summary>The draft differs from what is saved.</summary>
    public bool IsDirty => !Draft.SameAs(SmartRoutingDraft.Of(_saved));

    /// <summary>No number field is empty: the switch and Save are allowed.</summary>
    public bool IsComplete => Draft.IsComplete;

    /// <summary>The settings were refreshed (the status polls): the status group and the dirty state follow, the draft stays.</summary>
    public void Refresh(SmartRoutingDto saved)
    {
        if (ReferenceEquals(saved, _saved)) return;
        _saved = saved;
        Raise(nameof(Saved));
        Raise(nameof(IsDirty));
        // Only the status group has anything new to show; the inputs keep what the user is typing (web: the form keeps its own state).
        Changed?.Invoke(DraftArea.Status);
    }

    /// <summary>Edits the draft; <paramref name="area"/> names what changed shape (None for typing).</summary>
    public void Edit(DraftArea area, Action<SmartRoutingDraft> change)
    {
        change(Draft);
        Raise(nameof(IsDirty));
        Raise(nameof(IsComplete));
        Changed?.Invoke(area);
    }

    /// <summary>"Discard": the draft goes back to what is saved.</summary>
    public void Discard()
    {
        Draft = SmartRoutingDraft.Of(_saved);
        Raise(nameof(Draft));
        Raise(nameof(IsDirty));
        Raise(nameof(IsComplete));
        Changed?.Invoke(DraftArea.All);
    }

    /// <summary>
    /// Saves the draft; with <paramref name="enabled"/> it also flips the switch (the switch saves the page's unsaved changes along with
    /// itself). A refusal fills <see cref="Problems"/> instead of closing anything.
    /// </summary>
    public async Task SaveAsync(bool? enabled = null)
    {
        if (_saving) return;
        _saving = true;
        Raise(nameof(IsSaving));
        Changed?.Invoke(DraftArea.Status);
        try
        {
            var next = await _api.SaveAsync(Draft.ToUpdate(enabled)).ConfigureAwait(true);
            _problems = [];
            Raise(nameof(Problems));
            _saved = next;
            Draft = SmartRoutingDraft.Of(next);
            Raise(nameof(Saved));
            Raise(nameof(Draft));
            _feedback.Success(enabled is null ? Loc.T("common.saved") : enabled.Value ? Loc.T("smartRouting.toast.on") : Loc.T("smartRouting.toast.off"));
        }
        catch (Exception e)
        {
            _problems = RoutingTexts.Explain(e, Draft);
            Raise(nameof(Problems));
        }
        finally
        {
            _saving = false;
            Raise(nameof(IsSaving));
            Raise(nameof(IsDirty));
            Raise(nameof(IsComplete));
            Changed?.Invoke(DraftArea.All);
        }
    }
}

/// <summary>
/// Judges a sample text with the settings as they are on screen (unsaved), to see what the judge would choose (web <c>Tester</c>).
/// </summary>
public sealed class SmartRoutingTester : NotifyingModel
{
    private readonly SmartRoutingApi _api;
    private readonly Func<SmartRoutingDraft> _draft;
    private string _text = "";
    private SmartRoutingTestDto? _result;
    private IReadOnlyList<string> _errors = [];
    private bool _running;

    internal SmartRoutingTester(SmartRoutingApi api, Func<SmartRoutingDraft> draft)
    {
        _api = api;
        _draft = draft;
    }

    /// <summary>The sample text.</summary>
    public string Text
    {
        get => _text;
        set
        {
            if (Set(ref _text, value)) Raise(nameof(CanRun));
        }
    }

    /// <summary>The last answer, null before one (and while a new one is awaited).</summary>
    public SmartRoutingTestDto? Result => _result;

    /// <summary>Why the last test failed to run, one line each (a refused request, not a judge that failed).</summary>
    public IReadOnlyList<string> Errors => _errors;

    /// <summary>A test is running.</summary>
    public bool IsRunning => _running;

    /// <summary>The text is not blank.</summary>
    public bool CanRun => _text.Trim().Length > 0;

    /// <summary>An answer slower than a request would have waited: shown, but a request would have fallen back (web <c>slow</c>).</summary>
    public bool IsSlow => _result is { } r && r.LatencyMs > r.LimitMs;

    /// <summary>Runs the test with the current draft.</summary>
    public async Task RunAsync()
    {
        if (_running || !CanRun) return;
        _result = null;
        _errors = [];
        _running = true;
        Raise(nameof(Result));
        Raise(nameof(Errors));
        Raise(nameof(IsRunning));
        var draft = _draft();
        try
        {
            _result = await _api.TestAsync(new SmartRoutingTestRequest(_text, draft.ToJudge(), draft.GradeUpdates())).ConfigureAwait(true);
        }
        catch (Exception e)
        {
            _errors = RoutingTexts.Explain(e, draft);
        }
        finally
        {
            _running = false;
            Raise(nameof(Result));
            Raise(nameof(Errors));
            Raise(nameof(IsRunning));
        }
    }
}

/// <summary>The settings page's top level: waits for the settings, then keeps one <see cref="SmartRoutingFormModel"/> over them.</summary>
public sealed class SmartRoutingPageModel : NotifyingModel
{
    private readonly IFeedback _feedback;
    private SmartRoutingFormModel? _form;

    /// <summary>Creates the page model; the form appears with the first answer.</summary>
    public SmartRoutingPageModel(SmartRoutingApi api, IFeedback feedback)
    {
        Api = api;
        _feedback = feedback;
        api.Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Api.Settings.Data)) OnSettings();
        };
        OnSettings();
    }

    /// <summary>The API.</summary>
    public SmartRoutingApi Api { get; }

    /// <summary>The form; null until the settings loaded.</summary>
    public SmartRoutingFormModel? Form => _form;

    private void OnSettings()
    {
        if (Api.Settings.Data is not { } data) return;
        if (_form is null)
        {
            _form = new SmartRoutingFormModel(Api, _feedback, data);
            Raise(nameof(Form));
        }
        else _form.Refresh(data);
    }
}
