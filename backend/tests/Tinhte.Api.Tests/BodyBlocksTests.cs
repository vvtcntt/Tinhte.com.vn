using System.Text.Json;
using Tinhte.Modules.Ai.Application;
using Tinhte.Platform.Content;

namespace Tinhte.Api.Tests;

/// <summary>
/// Validator thân bài/video dùng chung (BodyBlock / BodyRichText / BodyTextRun, hợp đồng v0.3 CHANGELOG 22): hình dạng loại đã biết,
/// giới hạn kích thước, link an toàn, text = nối runs, loại lạ được giữ; đường dẫn lỗi chính xác.
/// </summary>
public sealed class BodyBlocksTests
{
    private static BodyBlocks.Violation? Validate(string json) => BodyBlocks.Validate(JsonDocument.Parse(json).RootElement);

    [Fact]
    public void A_full_rich_body_is_valid()
    {
        var media = Guid.NewGuid();
        var json = $$"""
            [
              {"type":"heading","level":2,"text":"Thông số","align":"center"},
              {"type":"paragraph","text":"Chip A19 rất mạnh. Xem thêm",
               "runs":[{"text":"Chip A19","marks":["bold","italic"]},{"text":" rất mạnh. "},
                       {"text":"Xem thêm","href":"https://news.example.com/a?b=1#c","color":"#FF0000","highlight":"#00ff00","marks":["underline"]}]},
              {"type":"paragraph","text":"Liên hệ","runs":[{"text":"Liên hệ","href":"mailto:toa-soan@example.com","marks":["strike"]}]},
              {"type":"quote","text":"Một câu trích"},
              {"type":"list","ordered":true,"items":[{"text":"Pin 4000 mAh"},{"text":"Sạc 45 W","runs":[{"text":"Sạc 45 W","marks":["bold"]}]}]},
              {"type":"image","mediaId":"{{media}}","alt":"","caption":"Mặt lưng","align":"left"},
              {"type":"table","headerRow":true,"caption":"Bảng","rows":[[{"text":"Máy"},{"text":"Giá"}],[{"text":"A"},{"text":"10"}]]},
              {"type":"paragraph","text":""}
            ]
            """;
        Assert.Null(Validate(json));
        Assert.Equal([(5, media)], BodyBlocks.ImageMediaIds(JsonDocument.Parse(json).RootElement));
    }

    [Theory]
    [InlineData("""[{"type":"video-embed","provider":"x","anything":[1,2,3]}]""")]   // loại lạ: giữ, renderer bỏ qua
    [InlineData("""[{"type":"paragraph","text":"a","extra":true}]""")]                 // thuộc tính thừa: schema không cấm
    [InlineData("""[]""")]
    public void Unknown_types_and_extra_properties_are_kept(string json) => Assert.Null(Validate(json));

    [Theory]
    [InlineData("""{"type":"paragraph"}""", "bodyBlocks")]
    [InlineData("""[1]""", "bodyBlocks[0]")]
    [InlineData("""[{"text":"x"}]""", "bodyBlocks[0].type")]
    [InlineData("""[{"type":""}]""", "bodyBlocks[0].type")]
    [InlineData("""[{"type":"   "}]""", "bodyBlocks[0].type")]
    [InlineData("""[{"type":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}]""", "bodyBlocks[0].type")]   // 41 ký tự
    [InlineData("""[{"type":"paragraph"}]""", "bodyBlocks[0].text")]
    [InlineData("""[{"type":"paragraph","text":5}]""", "bodyBlocks[0].text")]
    [InlineData("""[{"type":"paragraph","text":"x","align":"justify"}]""", "bodyBlocks[0].align")]
    [InlineData("""[{"type":"heading","text":"x"}]""", "bodyBlocks[0].level")]
    [InlineData("""[{"type":"heading","level":1,"text":"x"}]""", "bodyBlocks[0].level")]
    [InlineData("""[{"type":"quote","text":"ab","runs":[{"text":"a"},{"text":"c"}]}]""", "bodyBlocks[0].text")]   // text ≠ nối runs
    [InlineData("""[{"type":"paragraph","text":"ab","runs":[{"text":"ab","marks":["bold","bold"]}]}]""", "bodyBlocks[0].runs[0].marks")]
    [InlineData("""[{"type":"paragraph","text":"ab","runs":[{"text":"ab","marks":["code"]}]}]""", "bodyBlocks[0].runs[0].marks")]
    [InlineData("""[{"type":"paragraph","text":"","runs":[{"text":""}]}]""", "bodyBlocks[0].runs[0].text")]
    [InlineData("""[{"type":"paragraph","text":"a","runs":[{"text":"a","color":"red"}]}]""", "bodyBlocks[0].runs[0].color")]
    [InlineData("""[{"type":"paragraph","text":"a","runs":[{"text":"a","highlight":"#fff"}]}]""", "bodyBlocks[0].runs[0].highlight")]
    [InlineData("""[{"type":"paragraph","text":"a","runs":"a"}]""", "bodyBlocks[0].runs")]
    [InlineData("""[{"type":"list","items":[]}]""", "bodyBlocks[0].ordered")]
    [InlineData("""[{"type":"list","ordered":false,"items":["Pin"]}]""", "bodyBlocks[0].items[0]")]
    [InlineData("""[{"type":"list","ordered":false,"items":[{"text":"a"},{"runs":[]}]}]""", "bodyBlocks[0].items[1].text")]
    [InlineData("""[{"type":"image","src":"x"}]""", "bodyBlocks[0].mediaId")]
    [InlineData("""[{"type":"image","mediaId":"not-a-uuid","alt":""}]""", "bodyBlocks[0].mediaId")]
    [InlineData("""[{"type":"image","mediaId":"0190f3c4-1111-7000-8000-00000000abcd"}]""", "bodyBlocks[0].alt")]
    [InlineData("""[{"type":"image","mediaId":"0190f3c4-1111-7000-8000-00000000abcd","alt":"","caption":null}]""", "bodyBlocks[0].caption")]
    [InlineData("""[{"type":"table","rows":[[]]}]""", "bodyBlocks[0].rows[0]")]
    [InlineData("""[{"type":"table","rows":[[{"text":"a"},{"text":1}]]}]""", "bodyBlocks[0].rows[0][1].text")]
    [InlineData("""[{"type":"table","rows":[],"headerRow":"yes"}]""", "bodyBlocks[0].headerRow")]
    public void Invalid_blocks_report_the_exact_field(string json, string path) => Assert.Equal(path, Validate(json)?.Path);

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("JavaScript:alert(1)")]
    [InlineData(" javascript:alert(1)")]
    [InlineData("java\tscript:alert(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("vbscript:msgbox(1)")]
    [InlineData("file:///etc/passwd")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("//evil.example/x")]
    [InlineData("/relative/path")]
    [InlineData("https://")]
    [InlineData("ftp://example.com/a")]
    [InlineData("https://exa mple.com")]
    public void Dangerous_or_relative_links_are_rejected(string href)
    {
        Assert.False(BodyBlocks.IsSafeHref(href));
        var json = JsonSerializer.Serialize(new[] { new { type = "paragraph", text = "x", runs = new[] { new { text = "x", href } } } });
        Assert.Equal("bodyBlocks[0].runs[0].href", Validate(json)?.Path);
    }

    [Theory]
    [InlineData("https://forum.example.com/thread/abc.123/")]
    [InlineData("http://example.com")]
    [InlineData("HTTPS://EXAMPLE.COM/A")]
    [InlineData("mailto:a@example.com")]
    public void Http_https_and_mailto_links_are_allowed(string href) => Assert.True(BodyBlocks.IsSafeHref(href));

    [Fact]
    public void Size_limits_are_enforced()
    {
        static string Blocks(int n) => "[" + string.Join(',', Enumerable.Repeat("""{"type":"paragraph","text":"a"}""", n)) + "]";
        Assert.Null(Validate(Blocks(BodyBlocks.MaxBlocks)));
        Assert.Equal("bodyBlocks", Validate(Blocks(BodyBlocks.MaxBlocks + 1))?.Path);

        Assert.Null(Validate(JsonSerializer.Serialize(new[] { new { type = "paragraph", text = new string('a', BodyBlocks.MaxTextLength) } })));
        Assert.Equal("bodyBlocks[0].text", Validate(JsonSerializer.Serialize(new[] { new { type = "paragraph", text = new string('a', BodyBlocks.MaxTextLength + 1) } }))?.Path);

        var runs = Enumerable.Range(0, BodyBlocks.MaxRuns + 1).Select(_ => new { text = "a" }).ToArray();
        Assert.Equal("bodyBlocks[0].runs", Validate(JsonSerializer.Serialize(new[] { new { type = "paragraph", text = new string('a', runs.Length), runs } }))?.Path);

        var items = Enumerable.Range(0, BodyBlocks.MaxListItems + 1).Select(_ => new { text = "a" }).ToArray();
        Assert.Equal("bodyBlocks[0].items", Validate(JsonSerializer.Serialize(new[] { new { type = "list", ordered = false, items } }))?.Path);

        var row = Enumerable.Range(0, BodyBlocks.MaxCells).Select(_ => new { text = "a" }).ToArray();
        Assert.Null(Validate(JsonSerializer.Serialize(new[] { new { type = "table", rows = Enumerable.Repeat(row, BodyBlocks.MaxRows).ToArray() } })));
        Assert.Equal("bodyBlocks[0].rows", Validate(JsonSerializer.Serialize(new[] { new { type = "table", rows = Enumerable.Repeat(row, BodyBlocks.MaxRows + 1).ToArray() } }))?.Path);
        var wide = Enumerable.Range(0, BodyBlocks.MaxCells + 1).Select(_ => new { text = "a" }).ToArray();
        Assert.Equal("bodyBlocks[0].rows[0]", Validate(JsonSerializer.Serialize(new[] { new { type = "table", rows = new[] { wide } } }))?.Path);

        Assert.Equal("bodyBlocks[0].alt", Validate(JsonSerializer.Serialize(new[] { new { type = "image", mediaId = Guid.NewGuid(), alt = new string('a', 301) } }))?.Path);
        Assert.Equal("bodyBlocks[0].caption", Validate(JsonSerializer.Serialize(new[] { new { type = "image", mediaId = Guid.NewGuid(), alt = "", caption = new string('a', 501) } }))?.Path);
        Assert.Equal("bodyBlocks[0].runs[0].href", Validate(JsonSerializer.Serialize(new[] { new { type = "paragraph", text = "a", runs = new[] { new { text = "a", href = "https://e.com/" + new string('a', 2000) } } } }))?.Path);
    }

    [Fact]
    public void Field_name_prefixes_the_path() =>
        Assert.Equal("initialBodyBlocks[0].type", BodyBlocks.Validate(JsonDocument.Parse("""[{}]""").RootElement, "initialBodyBlocks")?.Path);

    [Fact]
    public void Ai_input_reads_rich_bodies_once_without_links_or_runs()
    {
        var body = """
            [{"type":"paragraph","text":"Chip A19 mạnh","runs":[{"text":"Chip A19","marks":["bold"]},{"text":" mạnh","href":"https://cdn.example/x"}]},
             {"type":"list","ordered":false,"items":[{"text":"Pin 4000 mAh"},{"text":"Sạc 45 W"}]},
             {"type":"table","rows":[[{"text":"Giá"},{"text":"30 triệu"}]]}]
            """;
        var input = MetadataPrompts.BuildInput(new AiContentSource("VIDEO", "Trên tay", null, body), 10_000);
        Assert.Equal("Tiêu đề: Trên tay\nNội dung: Chip A19 mạnh Pin 4000 mAh Sạc 45 W Giá 30 triệu", input);
    }
}
