using System.Text.Json;
using Astra.Api.Contracts;
using Astra.Core;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Shell;
using Astra.Desktop.Core.SmartRouting;
using Astra.Desktop.Tests.Plugins;

namespace Astra.Desktop.Tests.SmartRouting;

[Collection("Loc")]
public sealed class SmartRoutingFormModelTests : IDisposable
{
    private readonly string _saved = Loc.Language;
    private readonly StubHttp _http = new();
    private readonly QueryClient _queries = new();
    private readonly ApiClient _api;
    private readonly Recorder _feedback = new();

    public SmartRoutingFormModelTests()
    {
        Loc.Language = "en";
        _api = new ApiClient("http://stub", _http);
    }

    public void Dispose()
    {
        Loc.Language = _saved;
        _queries.Dispose();
        _api.Dispose();
    }

    private sealed class Recorder : IFeedback
    {
        public List<string> Successes { get; } = [];
        public List<string> Errors { get; } = [];
        public void Success(string title, string? description = null) => Successes.Add(title);
        public void Error(string title, string? description = null) => Errors.Add(title);
        public void Info(string title, string? description = null) { }
        public Task<bool> ConfirmAsync(ConfirmRequest request, CancellationToken ct = default) => Task.FromResult(true);
    }

    private static string Json(SmartRoutingDto dto) => JsonSerializer.Serialize(dto, JsonContexts.Info<SmartRoutingDto>(Astra.Core.Json.Api));

    private SmartRoutingFormModel Form(SmartRoutingDto? saved = null) => new(new SmartRoutingApi(_api, _queries), _feedback, saved ?? SmartRoutingLogicTests.Saved());

    [Fact]
    public void Editing_makes_the_form_dirty_and_discard_makes_it_clean()
    {
        var f = Form();
        var areas = new List<DraftArea>();
        f.Changed += areas.Add;
        Assert.False(f.IsDirty);
        f.Edit(DraftArea.None, d => d.Model = "other");
        Assert.True(f.IsDirty);
        f.Edit(DraftArea.Grades, d => d.AddGrade());
        Assert.Equal([DraftArea.None, DraftArea.Grades], areas);
        f.Discard();
        Assert.False(f.IsDirty);
        Assert.Equal(DraftArea.All, areas[^1]);
        Assert.Equal("jev-latest", f.Draft.Model);
    }

    [Fact]
    public void An_empty_number_blocks_saving()
    {
        var f = Form();
        f.Edit(DraftArea.None, d => d.TimeoutMs = null);
        Assert.False(f.IsComplete);
        f.Edit(DraftArea.None, d => d.TimeoutMs = 1500);
        Assert.True(f.IsComplete);
    }

    [Fact]
    public async Task Saving_sends_the_draft_replaces_it_with_the_answer_and_toasts()
    {
        var after = SmartRoutingLogicTests.Saved(baseUrl: "https://mine.example");
        _http.On("PUT /api/smart-routing", Json(after));
        var f = Form();
        f.Edit(DraftArea.Judge, d => d.BaseUrl = "https://mine.example");
        await f.SaveAsync();
        var put = Assert.Single(_http.Requests, r => r.Method == "PUT");
        Assert.Contains("\"baseUrl\":\"https://mine.example\"", put.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"enabled\"", put.Body, StringComparison.Ordinal);
        Assert.False(f.IsDirty);
        Assert.Empty(f.Problems);
        Assert.Equal(["Saved"], _feedback.Successes);
    }

    [Fact]
    public async Task The_switch_saves_the_unsaved_changes_with_it_and_toasts_on_or_off()
    {
        _http.On("PUT /api/smart-routing", Json(SmartRoutingLogicTests.Saved(enabled: true)));
        var f = Form(SmartRoutingLogicTests.Saved(enabled: false));
        f.Edit(DraftArea.None, d => d.Model = "m2");
        await f.SaveAsync(true);
        var body = _http.Requests.Single(r => r.Method == "PUT").Body;
        Assert.Contains("\"enabled\":true", body, StringComparison.Ordinal);
        Assert.Contains("\"model\":\"m2\"", body, StringComparison.Ordinal);
        Assert.Equal([Loc.T("smartRouting.toast.on")], _feedback.Successes);
    }

    [Fact]
    public async Task A_refused_save_lists_the_problems_and_keeps_the_draft()
    {
        _http.On("PUT /api/smart-routing", """{"error":"invalid","details":{"problems":[{"code":"judge_model_missing","message":"m"},{"code":"grade_model_missing","message":"g","field":"grades[0].model"}]}}""", 400);
        var f = Form();
        f.Edit(DraftArea.None, d => d.Model = "");
        await f.SaveAsync(true);
        Assert.Equal(["The judge model has not been entered yet.", "Grade “Simple” has no model yet."], f.Problems);
        Assert.True(f.IsDirty);
        Assert.Equal("", f.Draft.Model);
        Assert.Empty(_feedback.Successes);
        Assert.False(f.IsSaving);
    }

    [Fact]
    public async Task The_tester_judges_the_unsaved_draft_and_shows_the_answer_or_why_it_could_not_ask()
    {
        _http.On("POST /api/smart-routing/test", req => req.Content!.ReadAsStringAsync().Result.Contains("hard problem", StringComparison.Ordinal)
            ? (200, """{"ok":true,"grade":"hard","gradeName":"Hard","model":"gpt-5","confidence":0.91,"confident":true,"judgeModel":"jev","latencyMs":3000,"inputTokens":10,"outputTokens":2,"limitMs":2000,"newConnection":false}""")
            : (400, """{"error":"invalid","details":{"problems":[{"code":"judge_key_missing","message":"k"}]}}"""));
        var f = Form();
        f.Edit(DraftArea.None, d => d.Model = "unsaved-model");
        var t = f.Tester;
        Assert.False(t.CanRun);
        t.Text = "  ";
        await t.RunAsync();
        Assert.Equal(0, _http.Count("POST", "/api/smart-routing/test"));

        t.Text = "a hard problem";
        await t.RunAsync();
        Assert.True(t.Result!.Ok);
        Assert.True(t.IsSlow);
        var body = _http.Requests.Single(r => r.PathAndQuery == "/api/smart-routing/test").Body;
        Assert.Contains("unsaved-model", body, StringComparison.Ordinal);
        Assert.Contains("\"grades\":[", body, StringComparison.Ordinal);

        t.Text = "an easy one";
        await t.RunAsync();
        Assert.Null(t.Result);
        Assert.Equal(["The judge service’s key has not been entered yet."], t.Errors);
    }

    [Fact]
    public async Task The_page_model_creates_the_form_with_the_first_answer_and_later_answers_only_refresh_the_status()
    {
        var first = SmartRoutingLogicTests.Saved();
        _http.On("GET /api/smart-routing", _ => (200, Json(first)));
        var api = new SmartRoutingApi(_api, _queries);
        var page = new SmartRoutingPageModel(api, _feedback);
        Assert.Null(page.Form);
        await api.Settings.RefetchAsync();
        var form = page.Form!;
        form.Edit(DraftArea.None, d => d.Model = "typed");
        var refreshed = SmartRoutingLogicTests.Saved(status: new SmartRoutingLogicTests.StatusOverride(Pins: 5));
        _http.On("GET /api/smart-routing", _ => (200, Json(refreshed)));
        await api.Settings.RefetchAsync();
        Assert.Same(form, page.Form);
        Assert.Equal("typed", form.Draft.Model);
        Assert.Equal(5, form.Saved.Status.Pins);
        Assert.True(form.IsDirty);
    }
}
