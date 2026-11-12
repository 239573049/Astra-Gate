using System.Text.Json.Nodes;
using Astra.Desktop.Core.Plugins.V2;

namespace Astra.Desktop.Tests.PluginUi;

/// <summary>The desktop Table's local behaviour (as the web's TableView) and the SVG subset plugin images draw with.</summary>
public class TableLogicTests
{
    private static JsonObject Row(string id, JsonNode? v) => new() { ["id"] = id, ["v"] = v };

    [Fact]
    public void A_header_press_sorts_ascending_then_flips()
    {
        var s = TableLogic.NextSort(null, "name");
        Assert.Equal(new TableSort("name", "asc"), s);
        s = TableLogic.NextSort(s, "name");
        Assert.Equal("desc", s.Dir);
        Assert.Equal("asc", TableLogic.NextSort(s, "other").Dir);
        Assert.Equal("""{"key":"name","dir":"desc"}""", s.ToJson().ToJsonString());
    }

    [Fact]
    public void Numbers_sort_numerically_text_as_text_and_ties_keep_their_order()
    {
        var rows = new List<JsonObject> { Row("a", 10), Row("b", 9), Row("c", 100), Row("d", 9) };
        Assert.Equal(["b", "d", "a", "c"], TableLogic.Sort(rows, new TableSort("v", "asc")).Select(r => r["id"]!.ToString()));
        Assert.Equal(["c", "a", "b", "d"], TableLogic.Sort(rows, new TableSort("v", "desc")).Select(r => r["id"]!.ToString()));
        var text = new List<JsonObject> { Row("x", "pear"), Row("y", "apple"), Row("z", null) };
        Assert.Equal(["z", "y", "x"], TableLogic.Sort(text, new TableSort("v", "asc")).Select(r => r["id"]!.ToString()));
        Assert.Equal(["x", "y", "z"], TableLogic.Sort(text, null).Select(r => r["id"]!.ToString()));
    }

    [Fact]
    public void Selection_toggles_and_the_header_selects_all_or_none()
    {
        Assert.Equal(["b"], TableLogic.Toggle(["a"], "b", multiple: false));
        Assert.Equal(["a", "b"], TableLogic.Toggle(["a"], "b", multiple: true));
        Assert.Equal(["b"], TableLogic.Toggle(["a", "b"], "a", multiple: true));
        Assert.Equal(["a", "b"], TableLogic.ToggleAll(["a"], ["a", "b"]));
        Assert.Empty(TableLogic.ToggleAll(["a", "b"], ["a", "b"]));
    }

    [Fact]
    public void Row_keys_and_page_counts()
    {
        Assert.Equal("k1", TableLogic.KeyOf(new JsonObject { ["id"] = "k1" }, "id", 3));
        Assert.Equal("42", TableLogic.KeyOf(new JsonObject { ["id"] = 42 }, "id", 3));
        Assert.Equal("3", TableLogic.KeyOf(new JsonObject(), "id", 3));
        Assert.Equal(3, TableLogic.Pages(41, 20));
        Assert.Equal(1, TableLogic.Pages(0, 20));
        Assert.Equal(5, TableLogic.Pages(100, 0)); // no page size: 20
        Assert.Equal(new TableSort("a", "desc"), TableSort.From(JsonNode.Parse("""{"key":"a","dir":"desc"}""")));
        Assert.Null(TableSort.From(JsonNode.Parse("""{"dir":"desc"}""")));
    }
}

public class SvgDocumentTests
{
    [Fact]
    public void Shapes_become_paths_with_inherited_paint_and_transforms()
    {
        var doc = SvgDocument.TryParse("""
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
              <g transform="translate(2 3)" opacity="0.5">
                <rect x="1" y="1" width="10" height="6" rx="2"/>
                <circle cx="5" cy="5" r="3" fill="#f00" style="stroke:none"/>
              </g>
              <path d="M0 0L10 10" stroke-linecap="round"/>
              <polygon points="0,0 4,0 2,3" fill="rgb(0,128,0)"/>
              <line x1="0" y1="0" x2="5" y2="5"/>
            </svg>
            """);
        Assert.NotNull(doc);
        Assert.Equal((0, 0, 24, 24), (doc!.X, doc.Y, doc.Width, doc.Height));
        Assert.Equal(5, doc.Shapes.Count);
        var rect = doc.Shapes[0];
        Assert.StartsWith("M3,1", rect.PathData);
        Assert.Contains("A2,2", rect.PathData);
        Assert.Null(rect.Fill);
        Assert.Equal("currentColor", rect.Stroke);
        Assert.Equal([1, 0, 0, 1, 2, 3], rect.Transform);
        Assert.Equal(0.5, rect.Opacity);
        var circle = doc.Shapes[1];
        Assert.Equal("#f00", circle.Fill);
        Assert.Null(circle.Stroke);
        Assert.Equal("round", doc.Shapes[2].LineCap);
        Assert.Equal("rgb(0,128,0)", doc.Shapes[3].Fill);
        Assert.EndsWith("Z", doc.Shapes[3].PathData);
        Assert.Null(doc.Shapes[4].Fill);
    }

    [Fact]
    public void Nothing_external_or_scripted_is_read()
    {
        // a DTD (entity expansion, external files) is refused outright
        Assert.Null(SvgDocument.TryParse("""<?xml version="1.0"?><!DOCTYPE svg [<!ENTITY x SYSTEM "file:///etc/passwd">]><svg><path d="M0 0L1 1"/></svg>"""));
        var doc = SvgDocument.TryParse("""
            <svg viewBox="0 0 10 10">
              <script>alert(1)</script>
              <defs><path id="p" d="M0 0L9 9"/></defs>
              <use href="#p"/>
              <image href="https://evil.example/x.png"/>
              <foreignObject><div>hi</div></foreignObject>
              <path d="M0 0 L5 5 Z"/>
              <path d="M0 0 url(javascript:x)"/>
              <g display="none"><path d="M1 1L2 2"/></g>
            </svg>
            """);
        Assert.NotNull(doc);
        Assert.Equal("M0 0 L5 5 Z", Assert.Single(doc!.Shapes).PathData);
        Assert.Null(SvgDocument.TryParse("<html/>"));
        Assert.Null(SvgDocument.TryParse("not xml"));
    }

    [Fact]
    public void Transforms_compose_like_svg()
    {
        Assert.Equal([2, 0, 0, 2, 10, 0], SvgDocument.ParseTransform("translate(10) scale(2)"));
        var r = SvgDocument.ParseTransform("rotate(90 5 5)");
        Assert.Equal(0, r[0], 6);
        Assert.Equal(1, r[1], 6);
        Assert.Equal(10, r[4], 6);
        Assert.Equal(0, r[5], 6);
    }

    [Fact]
    public void Sources_and_sizes()
    {
        Assert.True(SvgDocument.IsSvg("img/logo.svg?v=2"));
        Assert.True(SvgDocument.IsSvg("data:image/svg+xml;base64,AAAA"));
        Assert.False(SvgDocument.IsSvg("img/logo.png"));
        var b64 = "data:image/svg+xml;base64," + Convert.ToBase64String("<svg width=\"32\" height=\"16\"><path d=\"M0 0H1\"/></svg>"u8.ToArray());
        var doc = SvgDocument.TryParse(SvgDocument.TextOf(b64, null)!);
        Assert.Equal((32, 16), (doc!.Width, doc.Height)); // no viewBox: width / height
        Assert.Equal("<svg/>", SvgDocument.TextOf("data:image/svg+xml,%3Csvg%2F%3E", null));
    }
}
