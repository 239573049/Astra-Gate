using Astra.Api.Contracts;
using Astra.Desktop.Core.ProviderDetail;

namespace Astra.Desktop.Tests.ProviderDetail;

public class ProviderKeyLogicTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    private static ProviderAccountDto Key(string id, bool enabled = true, DateTimeOffset? until = null, string? error = null) =>
        new(id, "p", id, null, null, "active", null, null, null, Now, Enabled: enabled, CooldownUntilUtc: until, LastError: error, Kind: "api_key");

    [Fact]
    public void A_Rejection_Is_Told_From_A_Rate_Limit_And_A_Lapsed_Cooldown_From_None()
    {
        Assert.Equal(KeyState.Off, ProviderKeyLogic.StateOf(Key("a", enabled: false), Now));
        Assert.Equal(KeyState.AuthFailed, ProviderKeyLogic.StateOf(Key("a", until: Now.AddHours(1), error: "401 rejected"), Now));
        Assert.Equal(KeyState.AuthFailed, ProviderKeyLogic.StateOf(Key("a", until: Now.AddHours(1), error: "403 rejected"), Now));
        Assert.Equal(KeyState.Cooling, ProviderKeyLogic.StateOf(Key("a", until: Now.AddHours(1), error: "429 limited"), Now));
        Assert.Equal(KeyState.Ok, ProviderKeyLogic.StateOf(Key("a", until: Now.AddHours(-1), error: "401 rejected"), Now));
    }

    [Fact]
    public void Weights_Run_From_One_To_One_Thousand_And_Empty_Means_One()
    {
        Assert.True(ProviderKeyLogic.WeightOk(null));
        Assert.True(ProviderKeyLogic.WeightOk(1));
        Assert.True(ProviderKeyLogic.WeightOk(1000));
        Assert.False(ProviderKeyLogic.WeightOk(0));
        Assert.False(ProviderKeyLogic.WeightOk(1001));
        Assert.False(ProviderKeyLogic.WeightOk(2.5m));
    }

    [Fact]
    public void Moving_A_Key_Swaps_It_With_Its_Neighbour_And_Stops_At_The_Ends()
    {
        List<ProviderAccountDto> keys = [Key("a"), Key("b"), Key("c")];
        Assert.Equal(["b", "a", "c"], ProviderKeyLogic.Moved(keys, 0, 1));
        Assert.Equal(["a", "c", "b"], ProviderKeyLogic.Moved(keys, 2, -1));
        Assert.Null(ProviderKeyLogic.Moved(keys, 0, -1));
        Assert.Null(ProviderKeyLogic.Moved(keys, 2, 1));
    }

    [Fact]
    public void The_Key_List_Flag_Is_Read_From_The_Provider_Settings()
    {
        Assert.False(ProviderKeyLogic.IsPool(PdData.Provider()));
        Assert.True(ProviderKeyLogic.IsPool(PdData.Provider(o => o["settings"] = new System.Text.Json.Nodes.JsonObject { ["key_pool"] = true })));
    }
}
