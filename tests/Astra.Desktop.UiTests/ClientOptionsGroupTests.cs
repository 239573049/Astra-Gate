using Astra.Core.Clients;
using Astra.Desktop.Components.Clients;
using Astra.Desktop.Core.Clients;
using Avalonia.Headless.XUnit;
using TKCode.Arc;

namespace Astra.Desktop.UiTests;

/// <summary>Client options preserve numeric storage while presenting preset selectors.</summary>
public sealed class ClientOptionsGroupTests
{
    [AvaloniaFact]
    public void Preset_select_keeps_custom_values_and_clears_the_option()
    {
        var descriptor = new ClientOptionDescriptor("contextWindow", "int", "", "config", ["128000", "400000", "1000000"]);
        OptionValue? edited = new OptionValue.Int(300000);
        var select = Assert.IsType<Select>(ClientOptionsGroup.Control(descriptor, "Context window", edited, (_, value) => edited = value));
        Assert.Equal("300000", select.SelectedValue);
        select.SelectedValue = "1000000";
        Assert.Equal(new OptionValue.Int(1000000), edited);
        select.SelectedValue = "";
        Assert.Null(edited);
    }

    [AvaloniaFact]
    public void Enum_and_switch_edits_use_the_existing_option_value_types()
    {
        OptionValue? edited = null;
        var descriptor = new ClientOptionDescriptor("reasoningEffort", "enum", "default", "config", ["default", "high", "xhigh"]);
        var select = Assert.IsType<Select>(ClientOptionsGroup.Control(descriptor, "Reasoning", null, (_, value) => edited = value));
        select.SelectedValue = "xhigh";
        Assert.Equal(new OptionValue.Choice("xhigh"), edited);
        var toggle = Assert.IsType<Switch>(ClientOptionsGroup.Control(new("supportsWebsockets", "bool", "false", "config"), "WebSocket", null, (_, value) => edited = value));
        toggle.IsChecked = true;
        Assert.Equal(new OptionValue.Bool(true), edited);
    }
}
