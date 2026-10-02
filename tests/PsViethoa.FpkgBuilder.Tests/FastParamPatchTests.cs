using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using PsViethoa.FpkgBuilder.Core.Services;
using Xunit;

namespace PsViethoa.FpkgBuilder.Tests;

/// <summary>진단용: 실제 패키지(CNT에 param.json 원시 저장)로 "빠른 재봉인" 전 경로를 점검한다 (파일이 없으면 건너뜀).</summary>
public sealed class FastParamPatchTests
{
    private static readonly string Source =
        @"C:\git\ps5\PPSA01289 [ 01.024 ]-[DLPSGAME.COM]\PPSA01289-app-pkg\UP9000-PPSA01289_00-SACKBOYADVENTURE-A0124-V0124.pkg";

    [Fact]
    public void Patch_ReplacesParamJsonAndReseals()
    {
        if (!File.Exists(Source))
        {
            return; // 이 머신에만 있는 진단 파일
        }

        // 원본 param.json은 이전에 뽑아 둔 것을 쓴다 (라이브러리가 파일을 물기 전 상태 유지).
        var rawParam = Path.Combine(Path.GetTempPath(), "opencode", "sackboy-param.json");
        if (!File.Exists(rawParam))
        {
            return;
        }

        var copy = Path.Combine(Path.GetTempPath(), "opencode", "sackboy-fastpatch.pkg");
        TryDelete(copy);
        try
        {
            File.Copy(Source, copy);

            const string newTitle = "Sackboy FASTPATCH OK";
            var node = JsonNode.Parse(File.ReadAllText(rawParam))!.AsObject();
            node["localizedParameters"]!["en-US"]!["titleName"] = newTitle;

            // GUI 경로 그대로: 에디터는 들여쓰기 본문을 보여주고, 전송 전 컴팩트로 다시 직렬화한다.
            var pretty = node.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            var reparsed = JsonNode.Parse(pretty)!.AsObject();
            var compact = reparsed.ToJsonString(new System.Text.Json.JsonSerializerOptions());
            Assert.True(pretty.Length > compact.Length, "indented text must be larger than compact");

            var report = CntParamPatcher.PatchInPlace(copy, Encoding.UTF8.GetBytes(compact));
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
