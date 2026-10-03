using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using PsViethoa.FpkgBuilder.Core.Services;
using Xunit;

namespace PsViethoa.FpkgBuilder.Tests;

/// <summary>진단용: 실제 패키지(CNT에 param.json 원시 저장)로 "빠른 재봉인" 전 경로를 점검한다 (파일이 없으면 건너뜀).</summary>
public sealed class FastParamPatchTests
{
    private static readonly string Source = Environment.GetEnvironmentVariable("FPKG_FASTPATCH_SOURCE")
        ?? @"C:\git\UP0102-PPSA02530_00-PRAGMATA00000000-A01200-V01200.pkg";

    [Fact]
    public void Patch_ReplacesParamJsonAndReseals()
    {
        var rawParam = Path.Combine(Path.GetTempPath(), "opencode", "pragmata-param.json");
        if (!File.Exists(Source) || !File.Exists(rawParam))
        {
            return; // 이 머신에만 있는 진단 파일
        }

        var copy = Path.Combine(Path.GetTempPath(), "opencode", "sackboy-fastpatch.pkg");
        TryDelete(copy);
        try
        {
            const string newTitle = "프래그마 FASTPATCH OK 한글";
            var node = JsonNode.Parse(File.ReadAllText(rawParam))!.AsObject();
            var localized = node["localizedParameters"]!.AsObject();
            var language = localized["defaultLanguage"]?.GetValue<string>() ?? "en-US";
            var block = localized[language] as JsonObject ?? localized.Where(pair => pair.Value is JsonObject).Select(pair => pair.Value).OfType<JsonObject>().First();
            block["titleName"] = newTitle;

            // GUI 경로 그대로: 에디터는 들여쓰기 본문을 보여주고, 전송 전 컴팩트로 다시 직렬화한다.
            var pretty = node.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            var reparsed = JsonNode.Parse(pretty)!.AsObject();
            var compact = reparsed.ToJsonString(new System.Text.Json.JsonSerializerOptions());
            Assert.True(pretty.Length > compact.Length, "indented text must be larger than compact");

            var bytes = Encoding.UTF8.GetBytes(compact);

            // Đường đầy đủ (Patch): sao chép song song + vá tại chỗ — phủ cả CopyFile.
            var report = CntParamPatcher.Patch(Source, copy, bytes);
            Assert.True(report.SlotSize > 0);
            Assert.True(report.PaddedBytes >= 0);
            Assert.True(report.ResealedBlocks > 0);

            // 다시 읽기: 같은 슬롯에서 새 내용이 나와야 함.
            var after = PackageInspector.Inspect(copy, new string('0', 32), CancellationToken.None);
            Assert.Null(after.ParamJsonError);
            Assert.Equal(newTitle, after.Params?.Title);
            Assert.Contains("FASTPATCH", Encoding.UTF8.GetString(after.ParamJsonBytes!), StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(copy);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
