using Astra.Core.Models;
using Astra.Core.Routing;
using Astra.Gateway.Routing;

namespace Astra.Gateway.Tests;

/// <summary>The smart-routing settings (plan §5.7): what can be saved, what can be switched on, and that it all sticks.</summary>
public class SmartRoutingServiceTests
{
    private static readonly FakeProtector Protector = new();

    private static string[] Codes(RoutingSettingsException e) => e.Problems.Select(p => p.Code).ToArray();

    [Fact]
    public async Task Smart_Routing_Is_Off_By_Default_With_Three_Grades_And_No_Models()
    {
        var store = await RoutingStore.StartAsync();
        var service = store.NewService();
        await service.LoadAsync();

        var s = service.Current;
        Assert.False(s.Enabled);
        Assert.Equal("astra-auto", s.VirtualModel);
        Assert.Equal(RoutingModes.Session, s.Rules.Mode); // once per session unless told otherwise
        Assert.Equal(["light", "standard", "heavy"], s.Grades.Select(g => g.Id));
        Assert.All(s.Grades, g => Assert.Equal("", g.Model));
        Assert.Equal(JudgeProtocols.TypeSafe, s.Judge.Protocol);
        Assert.Equal("https://api.typesafe.ai", s.Judge.EffectiveBaseUrl);
        Assert.Equal("jev-latest", s.Judge.Model);
        Assert.Equal("standard", s.Rules.FallbackGrade);
        Assert.Equal(0, service.Snapshot.Generation);
    }

    [Fact]
    public async Task A_Setup_Can_Be_Saved_Half_Finished_While_It_Is_Off()
    {
        var store = await RoutingStore.StartAsync();
        var service = store.NewService();
        await service.LoadAsync();

        await service.UpdateAsync(s => s.Judge.Model = "jev-1.13.0");

        Assert.Equal("jev-1.13.0", service.Current.Judge.Model);
        Assert.False(service.Current.Enabled);
    }

    [Fact]
    public async Task It_Cannot_Be_Switched_On_Until_The_Judge_And_Every_Grade_Are_Set_And_All_Gaps_Are_Reported_At_Once()
    {
        var store = await RoutingStore.StartAsync();
        var service = store.NewService();
        await service.LoadAsync();

        var e = await Assert.ThrowsAsync<RoutingSettingsException>(() => service.UpdateAsync(s => s.Enabled = true));

        Assert.Contains(SmartRoutingValidator.Codes.JudgeKeyMissing, Codes(e));
        Assert.Equal(3, e.Problems.Count(p => p.Code == SmartRoutingValidator.Codes.GradeModelMissing));
        Assert.Equal(["grades[0].model", "grades[1].model", "grades[2].model"], e.Problems.Where(p => p.Code == SmartRoutingValidator.Codes.GradeModelMissing).Select(p => p.Field));
        Assert.False(service.Current.Enabled);
    }

    [Fact]
    public async Task A_Complete_Setup_Can_Be_Switched_On()
    {
        var store = await RoutingStore.StartAsync();
        var service = store.NewService();
        await service.LoadAsync();

        var saved = await service.UpdateAsync(s =>
        {
            RoutingStore.Complete(s, Protector);
            s.Enabled = true;
        });

        Assert.True(saved.Enabled);
        Assert.True(service.Current.Enabled);
        Assert.Equal("big-model", service.Current.Grades[2].Model);
    }

    [Fact]
    public async Task A_Grade_Model_No_Provider_Serves_Is_Refused()
    {
        var store = await RoutingStore.StartAsync();
        var service = store.NewService();
        await service.LoadAsync();

        var e = await Assert.ThrowsAsync<RoutingSettingsException>(() => service.UpdateAsync(s =>
        {
            RoutingStore.Complete(s, Protector);
            s.Grades[0].Model = "ghost-model";
            s.Enabled = true;
        }));

        var problem = Assert.Single(e.Problems);
        Assert.Equal(SmartRoutingValidator.Codes.GradeModelUnroutable, problem.Code);
        Assert.Equal("grades[0].model", problem.Field);
    }

    [Fact]
    public async Task A_Model_That_Is_Switched_Off_Or_Lives_On_A_Disabled_Provider_Does_Not_Count()
    {
        var store = await RoutingStore.StartAsync();
        var model = (await store.Db.Providers.GetModelAsync(store.Provider.Id, "small-model"))!;
        model.Enabled = false;
        await store.Db.Providers.UpdateModelAsync(model);
        var service = store.NewService();
        await service.LoadAsync();

        var e = await Assert.ThrowsAsync<RoutingSettingsException>(() => service.UpdateAsync(s =>
        {
            RoutingStore.Complete(s, Protector);
            s.Enabled = true;
        }));
        Assert.Equal("grades[0].model", Assert.Single(e.Problems).Field);

        // And the whole provider off: nothing is routable.
        model.Enabled = true;
        await store.Db.Providers.UpdateModelAsync(model);
        store.Provider.Enabled = false;
        await store.Db.Providers.UpdateAsync(store.Provider);
        Assert.Empty(await service.RoutableModelsAsync());
    }

    [Fact]
    public async Task The_Virtual_Model_Must_Not_Be_A_Model_A_Provider_Serves_Even_While_It_Is_Off()
    {
        var store = await RoutingStore.StartAsync();
        var service = store.NewService();
        await service.LoadAsync();

        var e = await Assert.ThrowsAsync<RoutingSettingsException>(() => service.UpdateAsync(s => s.VirtualModel = "mid-model"));

        var problem = Assert.Single(e.Problems);
        Assert.Equal(SmartRoutingValidator.Codes.VirtualModelConflict, problem.Code);
        Assert.Equal("virtualModel", problem.Field);
        await service.UpdateAsync(s => s.VirtualModel = "smart-auto");
        Assert.True(service.IsVirtual("smart-auto"));
        Assert.False(service.IsVirtual("astra-auto"));
    }

    [Fact]
    public async Task Shape_Problems_Are_Reported_Whether_Or_Not_It_Is_On()
    {
        var store = await RoutingStore.StartAsync();
        var service = store.NewService();
        await service.LoadAsync();

        var e = await Assert.ThrowsAsync<RoutingSettingsException>(() => service.UpdateAsync(s =>
        {
            s.Grades[1].Id = "light"; // twice
            s.Rules.Mode = "sometimes";
            s.Rules.MinConfidence = 3;
        }));

        Assert.Contains(SmartRoutingValidator.Codes.GradeIdDuplicate, Codes(e));
        Assert.Contains(SmartRoutingValidator.Codes.ModeInvalid, Codes(e));
        Assert.Contains(SmartRoutingValidator.Codes.OutOfRange, Codes(e));
        Assert.Contains(SmartRoutingValidator.Codes.FallbackGradeUnknown, Codes(e)); // "standard" is no longer a grade
    }

    [Fact]
    public async Task A_Refused_Change_Leaves_The_Settings_And_The_Generation_As_They_Were()
    {
        var store = await RoutingStore.StartAsync();
        var service = store.NewService();
        await service.LoadAsync();
        await service.UpdateAsync(s => s.Rules.SessionTtlMinutes = 30);
        var generation = service.Snapshot.Generation;

        await Assert.ThrowsAsync<RoutingSettingsException>(() => service.UpdateAsync(s => s.Rules.SessionTtlMinutes = 99_999));

        Assert.Equal(30, service.Current.Rules.SessionTtlMinutes);
        Assert.Equal(generation, service.Snapshot.Generation);
    }

    [Fact]
    public async Task Every_Saved_Change_Raises_The_Generation_So_Old_Decisions_Are_Dropped()
    {
        var store = await RoutingStore.StartAsync();
        var service = store.NewService();
        await service.LoadAsync();

        await service.UpdateAsync(s => s.Rules.ContextTurns = 1);
        await service.UpdateAsync(s => s.Rules.ContextTurns = 3);

        Assert.Equal(2, service.Snapshot.Generation);
    }

    [Fact]
    public async Task The_Settings_Survive_A_Restart_And_The_Key_Stays_Protected()
    {
        var store = await RoutingStore.StartAsync();
        var first = store.NewService();
        await first.LoadAsync();
        await first.UpdateAsync(s =>
        {
            RoutingStore.Complete(s, Protector);
            s.Enabled = true;
            s.Rules.Mode = RoutingModes.Turn;
        });

        var second = store.NewService();
        await second.LoadAsync();

        Assert.True(second.Current.Enabled);
        Assert.Equal(RoutingModes.Turn, second.Current.Rules.Mode);
        Assert.Equal("enc:sk-judge", second.Current.Judge.ApiKeyProtected);
        Assert.Equal("big-model", second.Current.Grades[2].Model);
        Assert.Equal(0, second.Snapshot.Generation);
        // What is stored holds no plain key.
        var stored = await store.Db.Settings.GetAsync<SmartRoutingSettings>(SmartRoutingSettings.StorageKey);
        Assert.DoesNotContain("sk-judge", Astra.Core.Json.Serialize(stored)!.Replace("enc:sk-judge", ""));
    }

    [Fact]
    public async Task What_A_Person_Typed_Is_Trimmed_And_An_Empty_Address_Means_The_Default()
    {
        var store = await RoutingStore.StartAsync();
        var service = store.NewService();
        await service.LoadAsync();

        await service.UpdateAsync(s =>
        {
            s.Judge.BaseUrl = "  https://openrouter.ai/api/  ";
            s.Judge.Model = "  ~typesafe/jev-latest ";
            s.Judge.Instructions = "   ";
            s.Grades[0].Name = " Quick ";
            s.Grades[0].Model = " small-model ";
        });
        Assert.Equal("https://openrouter.ai/api", service.Current.Judge.BaseUrl);
        Assert.Equal("~typesafe/jev-latest", service.Current.Judge.Model);
        Assert.Null(service.Current.Judge.Instructions);
        Assert.Equal("Quick", service.Current.Grades[0].Name);
        Assert.Equal("small-model", service.Current.Grades[0].Model);

        await service.UpdateAsync(s => s.Judge.BaseUrl = "  ");
        Assert.Null(service.Current.Judge.BaseUrl);
        Assert.Equal("https://api.typesafe.ai", service.Current.Judge.EffectiveBaseUrl);
    }

    [Fact]
    public async Task Routable_Models_Are_The_Enabled_Models_Of_Usable_Providers_Each_Once()
    {
        var store = await RoutingStore.StartAsync("a-model", "b-model");
        var other = new Provider
        {
            Id = Astra.Core.Ulid.NewUlid(), Name = "Other", AuthScheme = AuthSchemes.Bearer,
            Endpoints = [new ProviderEndpoint { Protocol = Astra.Core.ApiProtocol.OpenAIChat, BaseUrl = "https://other.test/v1" }],
        };
        await store.Db.Providers.InsertAsync(other);
        await store.Db.Providers.InsertModelAsync(new ProviderModel { ProviderId = other.Id, ModelId = "b-model" }); // also served here
        await store.Db.Providers.InsertModelAsync(new ProviderModel { ProviderId = other.Id, ModelId = "c-model" });
        var service = store.NewService();

        var ids = (await service.RoutableModelsAsync()).Select(m => m.Id).Order().ToArray();

        Assert.Equal(["a-model", "b-model", "c-model"], ids);
    }

    [Fact]
    public async Task Routing_Options_List_A_Model_Once_For_Every_Provider_That_Serves_It()
    {
        var store = await RoutingStore.StartAsync("a-model", "b-model");
        var other = await store.AddProviderAsync("Other", "b-model", "c-model");
        var service = store.NewService();

        var options = (await service.RoutingOptionsAsync()).Select(o => (o.Target.Provider.Id, o.Id)).Order().ToArray();

        Assert.Equal(new[] { (store.Provider.Id, "a-model"), (store.Provider.Id, "b-model"), (other.Id, "b-model"), (other.Id, "c-model") }.Order(), options);
    }

    [Fact]
    public async Task A_Grade_Without_A_Provider_Gets_The_First_One_Serving_Its_Model_On_Save_And_On_Load()
    {
        var store = await RoutingStore.StartAsync();
        // Providers are listed by sort order, then name: "Zeta" comes after the store's "Models", as in the console.
        await store.AddProviderAsync("Zeta", "small-model");
        var service = store.NewService();
        await service.LoadAsync();

        await service.UpdateAsync(s => RoutingStore.Complete(s, Protector));
        Assert.All(service.Current.Grades, g => Assert.Equal(store.Provider.Id, g.ProviderId));

        // Settings saved before grades named a provider: filled in as they are loaded.
        var legacy = service.Current.Clone();
        foreach (var grade in legacy.Grades) grade.ProviderId = null;
        await store.Db.Settings.SetAsync(SmartRoutingSettings.StorageKey, legacy);
        var restarted = store.NewService();
        await restarted.LoadAsync();
        Assert.All(restarted.Current.Grades, g => Assert.Equal(store.Provider.Id, g.ProviderId));
    }

    [Fact]
    public async Task A_Grade_Whose_Provider_Does_Not_Serve_Its_Model_Cannot_Be_Switched_On()
    {
        var store = await RoutingStore.StartAsync();
        var other = await store.AddProviderAsync("Other", "tiny-model");
        var service = store.NewService();
        await service.LoadAsync();

        var e = await Assert.ThrowsAsync<RoutingSettingsException>(() => service.UpdateAsync(s =>
        {
            RoutingStore.Complete(s, Protector);
            s.Grades[0].ProviderId = other.Id; // serves tiny-model, not small-model
            s.Enabled = true;
        }));

        var problem = Assert.Single(e.Problems);
        Assert.Equal((SmartRoutingValidator.Codes.GradeModelUnroutable, "grades[0].model"), (problem.Code, problem.Field));
    }

    [Fact]
    public async Task Concurrent_Changes_Do_Not_Overwrite_Each_Other()
    {
        var store = await RoutingStore.StartAsync();
        var service = store.NewService();
        await service.LoadAsync();

        // Each change reads the current value and adds one: any lost write would leave the total short.
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => service.UpdateAsync(s => s.Rules.SessionTtlMinutes += 1)));

        Assert.Equal(80, service.Current.Rules.SessionTtlMinutes);
        Assert.Equal(20, service.Snapshot.Generation);
    }
}
