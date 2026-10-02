using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using PsViethoa.FpkgBuilder.Core.Models;
using PsViethoa.FpkgBuilder.Core.Services;
using Xunit;

namespace PsViethoa.FpkgBuilder.Tests;

public class ParamJsonOverrideTests
{
    private static JsonObject SampleParam() => new JsonObject
    {
        ["contentId"] = "UP9000-PPSA00001_00-PSVIETHOATEST001",
        ["contentVersion"] = "01.000.000",
        ["applicationDrmType"] = "standard",
        ["versionFileUri"] = "https://example.com/version",
        ["attribute3"] = 4,
        ["localizedParameters"] = new JsonObject
        {
            ["defaultLanguage"] = "en-US",
            ["en-US"] = new JsonObject { ["titleName"] = "Old Title" },
        },
    };

    [Fact]
    public void ApplyTo_MergesNestedObjectsAndReplacesScalarsAndArrays()
    {
        var node = SampleParam();
        var options = new ParamJsonPatchOptions(false, false, false)
        {
            CustomOverride = new JsonObject
            {
                ["contentVersion"] = "02.000.000",
                ["localizedParameters"] = new JsonObject { ["en-US"] = new JsonObject { ["titleName"] = "New Title" } },
                ["supportedLanguages"] = new JsonArray("en-US", "ja-JP"),
            },
        };

        var changes = ParamJsonPatch.ApplyTo(node, options);

        Assert.Equal("02.000.000", (string)node["contentVersion"]!);
        Assert.Equal("New Title", (string)node["localizedParameters"]!["en-US"]!["titleName"]!);
        Assert.Equal("en-US", (string)node["localizedParameters"]!["defaultLanguage"]!);
        Assert.Equal(2, node["supportedLanguages"]!.AsArray().Count);
        Assert.Contains(changes, change => change.Contains("localizedParameters.en-US.titleName", StringComparison.Ordinal));
        Assert.Contains(changes, change => change.Contains("contentVersion", StringComparison.Ordinal));
    }

    [Fact]
    public void ApplyTo_JsonNullDeletesKey()
    {
        var node = SampleParam();
        // Chỉ JSON null dạng text mới tạo ra nút null — C# null qua indexer là xoá khoá khỏi chính phần ghi đè.
        var custom = (JsonObject)JsonNode.Parse("""{"versionFileUri": null}""")!;
        var options = new ParamJsonPatchOptions(false, false, false) { CustomOverride = custom };

        ParamJsonPatch.ApplyTo(node, options);

        Assert.Null(node["versionFileUri"]);
    }

    [Fact]
    public void ApplyCustomOverride_ForcesStandardDrmWhenEnabled()
    {
        var forced = SampleParam();
        var custom = new JsonObject { ["applicationDrmType"] = "free" };

        var changes = ParamJsonPatch.ApplyCustomOverride(forced, custom, forceStandardDrm: true);
        Assert.Equal("standard", (string)forced["applicationDrmType"]!);
        Assert.Contains(changes, change => change.Contains("applicationDrmType", StringComparison.Ordinal));

        var kept = SampleParam();
        ParamJsonPatch.ApplyCustomOverride(kept, custom, forceStandardDrm: false);
        Assert.Equal("free", (string)kept["applicationDrmType"]!);
    }

    [Fact]
    public void ApplyTo_OverrideWinsOverStandardPatch()
    {
        var node = SampleParam();
        var custom = (JsonObject)JsonNode.Parse("""{"versionFileUri": "https://my-own.example/check"}""")!;
        var options = new ParamJsonPatchOptions(false, true, false) { CustomOverride = custom };

        ParamJsonPatch.ApplyTo(node, options);

        // standard patch xoá versionFileUri trước, phần ghi đè đặt lại sau cùng — người dùng thắng.
        Assert.Equal("https://my-own.example/check", (string)node["versionFileUri"]!);
    }

    [Fact]
    public void ApplyTo_WithoutOverride_BehavesAsBefore()
    {
        var node = SampleParam();
        var changes = ParamJsonPatch.ApplyTo(node, new ParamJsonPatchOptions(false, true, false));

        Assert.Equal(string.Empty, (string)node["versionFileUri"]!);
        Assert.Single(changes);
        Assert.Equal("01.000.000", (string)node["contentVersion"]!);
    }

    [Fact]
    public void Rewrite_WithOnlyCustomOverride_ProducesMergedBytes()
    {
        var original = Encoding.UTF8.GetBytes("""{"contentVersion": "01.000.000", "parentalLevel": 1}""");
        var options = new ParamJsonPatchOptions(false, false, false) { CustomOverride = new JsonObject { ["parentalLevel"] = 9 } };

        Assert.True(options.Any);
        var bytes = ParamJsonPatch.Rewrite(original, options, out var changes);

        Assert.NotNull(bytes);
        var json = JsonNode.Parse(bytes!)!.AsObject();
        Assert.Equal(9, (int)json["parentalLevel"]!);
        Assert.Contains(changes, change => change.Contains("parentalLevel", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("{\"parentalLevel\": 5}", true)]
    [InlineData("{\"contentVersion\": \"02.000.000\", \"applicationDrmType\": \"free\"}", true)]
    [InlineData("not json", false)]
    [InlineData("[1,2,3]", false)]
    [InlineData("", false)]
    public void TryParseOverride_ChecksShape(string raw, bool expected)
    {
        var result = ParamJsonPatch.TryParseOverride(raw, out var parsed, out var errors);

        Assert.Equal(expected, result);
        if (expected)
        {
            Assert.NotNull(parsed);
            Assert.Empty(errors);
        }
        else
        {
            Assert.Null(parsed);
            Assert.NotEmpty(errors);
        }
    }

    [Fact]
    public void TryParseOverride_RejectsBadCriticalFields()
    {
        Assert.False(ParamJsonPatch.TryParseOverride("""{"contentId": "BAD"}""", out _, out var contentIdErrors));
        Assert.Contains(contentIdErrors, error => error.Contains("contentId", StringComparison.Ordinal));

        Assert.False(ParamJsonPatch.TryParseOverride("""{"contentVersion": "abc"}""", out _, out var versionErrors));
        Assert.Contains(versionErrors, error => error.Contains("contentVersion", StringComparison.Ordinal));

        Assert.False(ParamJsonPatch.TryParseOverride("""{"masterVersion": "abc"}""", out _, out var masterErrors));
        Assert.Contains(masterErrors, error => error.Contains("masterVersion", StringComparison.Ordinal));

        Assert.False(ParamJsonPatch.TryParseOverride("""{"applicationDrmType": "premium"}""", out _, out var drmErrors));
        Assert.Contains(drmErrors, error => error.Contains("applicationDrmType", StringComparison.Ordinal));

        Assert.True(ParamJsonPatch.TryParseOverride("""{"contentId": "UP9000-PPSA00001_00-PSVIETHOATEST001"}""", out _, out _));
    }

    [Fact]
    public void ParamPatch_CarriesCustomOverrideFromRequest()
    {
        var custom = new JsonObject { ["parentalLevel"] = 5 };
        var request = new BuildRequest { ParamOverride = custom };

        Assert.Same(custom, request.ParamPatch.CustomOverride);
        Assert.True(request.ParamPatch.Any);
    }

    [Fact]
    public void WriteStandardParam_AppliesOverrideAfterStandardization()
    {
        var folder = Path.Combine(Path.GetTempPath(), "fpkg-override-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var source = Path.Combine(folder, "param.json");
            File.WriteAllText(source, """{"contentId": "UP9000-PPSA00001_00-PSVIETHOATEST001", "contentVersion": "01.000.000", "applicationDrmType": "free", "localizedParameters": {"defaultLanguage": "en-US", "en-US": {"titleName": "Old"}}}""");
            var destination = Path.Combine(folder, "out-param.json");
            var extra = new ParamJsonPatchOptions(false, false, false)
            {
                CustomOverride = new JsonObject { ["parentalLevel"] = 5, ["applicationDrmType"] = "free" },
            };

            var changes = SonySdkProject.WriteStandardParam(source, destination, extra);

            var json = JsonNode.Parse(File.ReadAllText(destination))!.AsObject();
            Assert.Equal(5, (int)json["parentalLevel"]!);
            Assert.Equal("standard", (string)json["applicationDrmType"]!);
            Assert.Equal("Old", (string)json["localizedParameters"]!["en-US"]!["titleName"]!);
            Assert.Contains(changes, change => change.Contains("parentalLevel", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void WriteStandardParam_WithoutOverride_OutputUnchanged()
    {
        var folder = Path.Combine(Path.GetTempPath(), "fpkg-override-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var source = Path.Combine(folder, "param.json");
            File.WriteAllText(source, """{"contentId": "UP9000-PPSA00001_00-PSVIETHOATEST001", "applicationDrmType": "free"}""");
            var destination = Path.Combine(folder, "out-param.json");

            SonySdkProject.WriteStandardParam(source, destination, new ParamJsonPatchOptions(false, false, false));

            var json = JsonNode.Parse(File.ReadAllText(destination))!.AsObject();
            Assert.Equal("standard", (string)json["applicationDrmType"]!);
            Assert.Equal("UP9000-PPSA00001_00-PSVIETHOATEST001", (string)json["contentId"]!);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void BuildOverrideDiff_ReturnsOnlyChangedKeys()
    {
        var original = SampleParam();
        var edited = SampleParam();
        edited["contentVersion"] = "02.000.000";
        edited["localizedParameters"]!["en-US"]!["titleName"] = "New";
        edited["parentalLevel"] = 9;

        var diff = ParamJsonPatch.BuildOverrideDiff(original, edited)!;

        Assert.Equal(3, diff.Count);
        Assert.Equal("02.000.000", (string)diff["contentVersion"]!);
        Assert.Equal("New", (string)diff["localizedParameters"]!["en-US"]!["titleName"]!);
        Assert.Equal(9, (int)diff["parentalLevel"]!);
    }

    [Fact]
    public void BuildOverrideDiff_DeletedKeyBecomesNull()
    {
        var original = SampleParam();
        var edited = SampleParam();
        edited.Remove("versionFileUri");

        var diff = ParamJsonPatch.BuildOverrideDiff(original, edited)!;

        Assert.True(diff.ContainsKey("versionFileUri"));
        Assert.Null(diff["versionFileUri"]);
    }

    [Fact]
    public void BuildOverrideDiff_IdenticalReturnsNull_AndUnparseableOriginalTreatedAsEmpty()
    {
        Assert.Null(ParamJsonPatch.BuildOverrideDiff(SampleParam(), SampleParam()));

        var edited = new JsonObject { ["contentVersion"] = "02.000.000" };
        var diff = ParamJsonPatch.BuildOverrideDiff(null, edited)!;
        Assert.Single(diff);
        Assert.Equal("02.000.000", (string)diff["contentVersion"]!);
    }

    [Fact]
    public void TryLoadOverrideFile_ReadsValidFileAndReportsProblems()
    {
        var folder = Path.Combine(Path.GetTempPath(), "fpkg-loadoverride-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var file = Path.Combine(folder, "override.json");
            File.WriteAllText(file, """{"parentalLevel": 5, "contentVersion": "02.000.000"}""");

            Assert.True(ParamJsonPatch.TryLoadOverrideFile(file, out var loaded, out var errors));
            Assert.NotNull(loaded);
            Assert.Empty(errors);
            Assert.Equal(2, loaded!.Count);
            Assert.Equal("02.000.000", (string)loaded["contentVersion"]!);

            Assert.False(ParamJsonPatch.TryLoadOverrideFile(Path.Combine(folder, "missing.json"), out var missing, out var missingErrors));
            Assert.Null(missing);
            Assert.NotEmpty(missingErrors);

            var bad = Path.Combine(folder, "bad.json");
            File.WriteAllText(bad, """{"contentId": "BAD"}""");
            Assert.False(ParamJsonPatch.TryLoadOverrideFile(bad, out var invalid, out var invalidErrors));
            Assert.Null(invalid);
            Assert.Contains(invalidErrors, error => error.Contains("contentId", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void MetadataReader_ExposesRawParamJsonText()
    {
        var folder = Path.Combine(Path.GetTempPath(), "fpkg-rawparam-" + Guid.NewGuid().ToString("N"));
        var sceSys = Path.Combine(folder, "sce_sys");
        Directory.CreateDirectory(sceSys);
        try
        {
            var text = "{\"contentId\": \"UP9000-PPSA00001_00-PSVIETHOATEST001\", \"contentVersion\": \"01.000.000\"}";
            File.WriteAllText(Path.Combine(sceSys, "param.json"), text);

            var metadata = MetadataReader.Read(folder, CancellationToken.None);

            Assert.True(metadata.HasParamJson);
            Assert.NotNull(metadata.RawParamJson);
            Assert.Contains("UP9000-PPSA00001_00-PSVIETHOATEST001", metadata.RawParamJson, StringComparison.Ordinal);

            File.WriteAllText(Path.Combine(sceSys, "param.json"), "{ broken");
            var broken = MetadataReader.Read(folder, CancellationToken.None);

            Assert.False(broken.HasParamJson);
            Assert.NotNull(broken.ParamJsonError);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}
