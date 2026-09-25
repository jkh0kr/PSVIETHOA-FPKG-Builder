using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using PsViethoa.FpkgBuilder.Core.Models;
using PsViethoa.FpkgBuilder.Core.Services;
using Xunit;

namespace PsViethoa.FpkgBuilder.Tests;

/// <summary>
/// Các thay đổi của bộ công cụ sdk-fpkg279-fix12 không cần Publishing Tools: chọn cỡ gói (attributePub), sửa header SELF, chia chunk
/// ngôn ngữ khi số chunk nhỏ; và quy tắc dọn tàn dư bản dump (Stellar Blade).
/// </summary>
public sealed class SonySdkFix12Tests : IDisposable
{
    private const string Passcode = "00000000000000000000000000000000";
    private const string ContentId = "EP9999-PPSA99996_00-PSVIETHOAFIX1200";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "psviethoa-fix12-" + Guid.NewGuid().ToString("N"));

    public SonySdkFix12Tests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception)
        {
        }
    }

    public static TheoryData<long, int, string, int> SizeCases => new()
    {
        { 1_000, 10, "sdk279", 0 },
        { 162_000_000_000, 1_000, "sdk279", 0 },
        { 170_000_000_000, 1_000, "sdk279", 0 },
        { 190_000_000_000, 1_000, "sdk279", 1 },
        { 250_000_000_000, 1_000, "sdk279", 0 },
        { 400_000_000_000, 1_000, "sdk279", 0 },
        { 100_000_000_000, 1_000, "sdk279", 2 },
        { 150_000_000_000, 1_000, "sdk279", 2 },
        { 250_000_000_000, 1_000, "sdk279", 2 },
        { 270_000_000_000, 1_000, "sdk313", 0 },
        { 400_000_000_000, 1_000, "sdk313", 0 },
        { 250_000_000_000, 499_000, "sdk279", 0 },
    };

    /// <summary>Kết quả phải giống <c>choose_package_size</c> của chính script fix12 (chạy khi máy có Python).</summary>
    [Theory]
    [MemberData(nameof(SizeCases))]
    public void Choose_MatchesTheToolkitScript(long bytes, int files, string profile, int mountLevel)
    {
        var selection = SonySdkPackageSize.Choose(bytes, files, profile, mountLevel);
        var script = SonySdkFidelityTests.FindToolkitScript();
        var python = SonySdkFidelityTests.FindPython();
        if (script == null || python == null)
        {
            return;
        }

        var code = "import importlib.util, sys\n" +
                   "spec = importlib.util.spec_from_file_location('gp5', sys.argv[1]); m = importlib.util.module_from_spec(spec); spec.loader.exec_module(m)\n" +
                   "print(m.choose_package_size(int(sys.argv[2]), int(sys.argv[3]), sys.argv[4], int(sys.argv[5])))\n";
        var start = new ProcessStartInfo(python) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[] { "-c", code, script, bytes.ToString(CultureInfo.InvariantCulture), files.ToString(CultureInfo.InvariantCulture), profile, mountLevel.ToString(CultureInfo.InvariantCulture) })
        {
            start.ArgumentList.Add(argument);
        }

        using var run = Process.Start(start)!;
        var output = run.StandardOutput.ReadToEnd().Trim();
        var error = run.StandardError.ReadToEnd();
        run.WaitForExit();
        Assert.True(run.ExitCode == 0, error);
        Assert.Equal($"({selection.AttributePub}, {(selection.AppSizeInGib?.ToString(CultureInfo.InvariantCulture) ?? "None")}, {selection.EstimatedBytes}, {selection.MountLevel})", output);
    }

    [Fact]
    public void Choose_PicksTheSmallestLevelAndLowersTheMountLevelOnlyAsFarAsNeeded()
    {
        Assert.Equal(0, SonySdkPackageSize.Choose(1_000, 1, "sdk279").AttributePub);
        Assert.Equal(1, SonySdkPackageSize.Choose(180_000_000_000, 1, "sdk279").AttributePub);
        Assert.Equal(2, SonySdkPackageSize.Choose(240_000_000_000, 1, "sdk279").AttributePub);
        // SDK 2.79 không có mức lớn hơn lv2: vẫn lv2, img_create quyết định sau khi nén.
        Assert.Equal(2, SonySdkPackageSize.Choose(300_000_000_000, 1, "sdk279").AttributePub);
        Assert.Equal(new SonySdkSizeSelection(4, 320, SonySdkPackageSize.Choose(400_000_000_000, 1, "sdk313").EstimatedBytes, 0), SonySdkPackageSize.Choose(400_000_000_000, 1, "sdk313"));

        // Mức mount 2 giới hạn ~97,9 GB: vừa thì giữ (attributePub 0), không vừa thì hạ xuống 1, rồi 0.
        Assert.Equal((0, 2), Pick(SonySdkPackageSize.Choose(90_000_000_000, 1, "sdk279", 2)));
        Assert.Equal((0, 1), Pick(SonySdkPackageSize.Choose(120_000_000_000, 1, "sdk279", 2)));
        Assert.Equal((1, 1), Pick(SonySdkPackageSize.Choose(180_000_000_000, 1, "sdk279", 2)));
        Assert.Equal((2, 0), Pick(SonySdkPackageSize.Choose(240_000_000_000, 1, "sdk279", 1)));

        // lv2 của Publishing Tools đã vá: tối đa 500 000 tệp.
        Assert.Throws<InvalidDataException>(() => SonySdkPackageSize.Choose(240_000_000_000, 500_001, "sdk279"));
        Assert.Equal(2, SonySdkPackageSize.Choose(240_000_000_000, 500_000, "sdk279").AttributePub);

        static (int, int) Pick(SonySdkSizeSelection selection) => (selection.AttributePub, selection.MountLevel);
    }

    [Fact]
    public void Apply_WritesAttributePubLikeWriteStandardParam()
    {
        var param = JsonNode.Parse("{\"applicationCategoryType\":0,\"attributePub\":7,\"kernel\":{\"appSizeInGib\":300,\"addcontMountLevel\":2},\"z\":1}")!.AsObject();
        SonySdkPackageSize.Apply(param, new SonySdkSizeSelection(1, null, 1, 1), "param.json");
        // Khoá có sẵn giữ vị trí; appSizeInGib bị bỏ khi không phải lv3; mức mount hạ ghi đè tại chỗ.
        Assert.Equal("{\n  \"applicationCategoryType\": 0,\n  \"attributePub\": 1,\n  \"kernel\": {\n    \"addcontMountLevel\": 1\n  },\n  \"z\": 1\n}", PythonJson.Serialize(param));

        var fresh = JsonNode.Parse("{\"applicationCategoryType\":0}")!.AsObject();
        SonySdkPackageSize.Apply(fresh, new SonySdkSizeSelection(0, null, 1, 0), "param.json");
        Assert.Equal("{\n  \"applicationCategoryType\": 0,\n  \"attributePub\": 0\n}", PythonJson.Serialize(fresh));

        // Chỉ game gốc (applicationCategoryType 0); mức mount phải là số nguyên 0..2.
        Assert.Throws<InvalidDataException>(() => SonySdkPackageSize.Apply(JsonNode.Parse("{\"applicationCategoryType\":65536}")!.AsObject(), new SonySdkSizeSelection(0, null, 1, 0), "p"));
        Assert.Throws<InvalidDataException>(() => SonySdkPackageSize.Apply(JsonNode.Parse("{}")!.AsObject(), new SonySdkSizeSelection(0, null, 1, 0), "p"));
        Assert.Throws<InvalidDataException>(() => SonySdkPackageSize.ReadMountLevel(JsonNode.Parse("{\"kernel\":{\"addcontMountLevel\":1.0}}")!.AsObject(), "p"));
        Assert.Throws<InvalidDataException>(() => SonySdkPackageSize.ReadMountLevel(JsonNode.Parse("{\"kernel\":{\"addcontMountLevel\":true}}")!.AsObject(), "p"));
        Assert.Throws<InvalidDataException>(() => SonySdkPackageSize.ReadMountLevel(JsonNode.Parse("{\"kernel\":[]}")!.AsObject(), "p"));
        Assert.Equal(0, SonySdkPackageSize.ReadMountLevel(JsonNode.Parse("{\"kernel\":{}}")!.AsObject(), "p"));
    }

    [Fact]
    public void SelfRepair_FixesTheMagicAndTheSceVersionAlignment()
    {
        var legacy = Path.Combine(_root, "legacy.prx");
        File.WriteAllBytes(legacy, SonySdkFidelityTests.SelfFile(legacy: true, misalignment: 3));
        var plan = SonySdkSelfRepair.Plan(legacy);
        Assert.Equal(new SelfRepairPlan(true, 0x40 - 3, 3), plan);
        Assert.Equal("Prospero magic, .sceversion +3-byte alignment", plan!.Value.Description);

        var repaired = Path.Combine(_root, "out", "legacy.prx");
        SonySdkSelfRepair.Write(legacy, repaired, plan.Value);
        var original = File.ReadAllBytes(legacy);
        var bytes = File.ReadAllBytes(repaired);
        Assert.Equal(original.Length + 3, bytes.Length);
        Assert.Equal(new byte[] { 0x54, 0x14, 0xF5, 0xEE }, bytes[..4]);
        Assert.Equal(original[4..(0x40 - 3)], bytes[4..(0x40 - 3)]);
        Assert.Equal(new byte[3], bytes[(0x40 - 3)..0x40]);
        Assert.Equal(original[(0x40 - 3)..], bytes[0x40..]);
        Assert.Null(SonySdkSelfRepair.Plan(repaired));

        // SELF đúng và tệp không phải SELF: không đụng tới.
        var good = Path.Combine(_root, "good.prx");
        File.WriteAllBytes(good, SonySdkFidelityTests.SelfFile(legacy: false, misalignment: 0));
        Assert.Null(SonySdkSelfRepair.Plan(good));
        var data = Path.Combine(_root, "data.bin");
        File.WriteAllBytes(data, new byte[64]);
        Assert.Null(SonySdkSelfRepair.Plan(data));
        Assert.Throws<IOException>(() => SonySdkSelfRepair.Write(legacy, repaired, plan.Value));
    }

    [Theory]
    [InlineData(100, new[] { 1, 2, 3 }, "0-99")]
    [InlineData(3, new[] { 1, 2, 2 }, "0-2")]
    [InlineData(1, new[] { 0, 0, 0 }, "0")]
    public void ScriptFallback_SharesTheLastChunkWhenThereAreTooFewChunks(int chunkCount, int[] payloadChunks, string order)
    {
        var sceSys = Path.Combine(_root, "pg" + chunkCount, "sce_sys");
        Directory.CreateDirectory(sceSys);
        File.WriteAllText(Path.Combine(sceSys, "playgo-scenario.json"),
            "{\"chunkDefaultLanguage\":\"en-US\",\"chunkSupportedLanguages\":[\"en-US\",\"fr-FR\",\"ja-JP\"],\"scenarioCount\":1,\"scenarioDefaultId\":0,\"scenarioDefaultLanguage\":\"en-US\",\"scenarios\":[{\"id\":0,\"type\":\"playmode\",\"en-US\":{\"title\":\"Main\"}}]}");
        var structure = SonySdkPlayGo.ScriptFallback(sceSys, "en-US", out var warning, chunkCount);
        Assert.Null(warning);
        Assert.False(structure.IsTrivial);
        Assert.Equal(chunkCount, structure.Chunks.Count);
        Assert.Equal(payloadChunks, structure.LanguagePayloads.Select(payload => payload.ChunkId));
        Assert.Equal(["playgo-languages/01-en-US.bin", "playgo-languages/02-fr-FR.bin", "playgo-languages/03-ja-JP.bin"], structure.LanguagePayloads.Select(payload => payload.Destination));
        var xml = SonySdkPlayGo.ChunkInfoXml(structure);
        Assert.Contains("initial_chunk_count=\"" + chunkCount + "\" label=\"Main\">" + order + "</scenario>", xml);
        if (chunkCount == 3)
        {
            Assert.Contains("<chunk id=\"2\" label=\"Chunk #2\" layer_no=\"0\" languages=\"fr-FR ja-JP\" />", xml);
        }
    }

    /// <summary>Hướng dẫn dọn Stellar Blade: _DUBLEX_, Engine (khi chỉ còn Saved) hoặc Engine/Saved, SB/Saved.</summary>
    [Fact]
    public void DumpLeftovers_FollowTheStellarBladeCleanup()
    {
        var app = Path.Combine(_root, "sb");
        foreach (var folder in new[] { "_DUBLEX_/x", "Engine/Saved/Logs", "SB/Saved/Config", "SB/Content/Paks", "Other/Saved", "sce_sys" })
        {
            Directory.CreateDirectory(Path.Combine(app, folder));
        }

        File.WriteAllBytes(Path.Combine(app, "_DUBLEX_", "x", "a.bin"), new byte[1]);
        File.WriteAllBytes(Path.Combine(app, "Engine", "Saved", "Logs", "log.txt"), new byte[1]);
        File.WriteAllBytes(Path.Combine(app, "SB", "Saved", "Config", "c.ini"), new byte[1]);
        File.WriteAllBytes(Path.Combine(app, "SB", "Content", "Paks", "p.pak"), new byte[1]);
        File.WriteAllBytes(Path.Combine(app, "Other", "Saved", "keep.bin"), new byte[1]);
        Assert.Equal(["Engine", "SB/Saved", "_DUBLEX_"], DumpLeftoverInspector.FindInFolder(app));

        // Engine còn thư mục khác: chỉ bỏ Engine/Saved.
        Directory.CreateDirectory(Path.Combine(app, "Engine", "Content"));
        Assert.Equal(["Engine/Saved", "SB/Saved", "_DUBLEX_"], DumpLeftoverInspector.FindInFolder(app));
        Assert.All(DumpLeftoverInspector.FindInFolder(app), path => Assert.True(DumpLeftoverInspector.LooksLikeCandidate(path)));
        Assert.True(new BuildRequest().RemoveDumpLeftovers);
    }

    /// <summary>GP5 của SDK bỏ nguyên thư mục trong kế hoạch (tuỳ chọn), ghi lý do một lần cho cả thư mục; nguồn không đổi.</summary>
    [Fact]
    public void Project_SkipsWholeFoldersFromThePlan()
    {
        var app = Path.Combine(_root, "game");
        foreach (var folder in new[] { "_DUBLEX_", "SB/Saved/Logs", "SB/Content/Paks", "sce_sys" })
        {
            Directory.CreateDirectory(Path.Combine(app, folder));
        }

        File.WriteAllText(Path.Combine(app, "sce_sys", "param.json"), "{\"applicationCategoryType\":0,\"contentId\":\"" + ContentId + "\"}");
        File.WriteAllBytes(Path.Combine(app, "sce_sys", "keystone"), new byte[96]);
        File.WriteAllBytes(Path.Combine(app, "_DUBLEX_", "d.bin"), new byte[1]);
        File.WriteAllBytes(Path.Combine(app, "SB", "Saved", "Logs", "l.txt"), new byte[1]);
        File.WriteAllBytes(Path.Combine(app, "SB", "Content", "Paks", "p.pak"), new byte[1]);
        var skip = new HashSet<string>(DumpLeftoverInspector.FindInFolder(app), StringComparer.OrdinalIgnoreCase);
        var plan = SonySdkSourcePlan.Pure with { Skip = skip, AutoSizeProfile = SonySdkPackageSize.Sdk279 };

        var result = SonySdkProject.Create(app, Path.Combine(_root, "out", "game.gp5"), Passcode, path => path, plan);

        Assert.Equal(["_DUBLEX_/ (excluded by option)", "SB/Saved/ (excluded by option)"], result.SkippedByApp);
        var gp5 = File.ReadAllText(result.ProjectPath);
        Assert.Contains("dst_path=\"SB/Content/Paks/p.pak\"", gp5);
        Assert.DoesNotContain("_DUBLEX_", gp5);
        Assert.DoesNotContain("Saved", gp5);
        Assert.True(File.Exists(Path.Combine(app, "SB", "Saved", "Logs", "l.txt")));
        Assert.Contains("\"attributePub\": 0", File.ReadAllText(result.ParamJsonPath));
    }
}
