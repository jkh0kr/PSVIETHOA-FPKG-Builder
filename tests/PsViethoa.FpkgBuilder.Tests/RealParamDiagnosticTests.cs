using System.IO;
using System.Text.Json.Nodes;
using PsViethoa.FpkgBuilder.Core.Services;
using Xunit;

namespace PsViethoa.FpkgBuilder.Tests;

/// <summary>진단용: 실제 패키지에서 뽑은 param.json으로 편집기 검증 경로를 재현한다 (파일이 없으면 건너뜀).</summary>
public class RealParamDiagnosticTests
{
    [Fact]
    public void TryParseOverride_AcceptsRealSackboyParamJson()
    {
        var path = Path.Combine(Path.GetTempPath(), "opencode", "sackboy-param.json");
        if (!File.Exists(path))
        {
            return; // 이 머신에서만 존재하는 진단 파일
        }

        var text = File.ReadAllText(path);
        var options = new System.Text.Json.JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = System.Text.Json.JsonCommentHandling.Skip };
        if (JsonNode.Parse(text, documentOptions: options) is JsonObject pretty)
        {
            text = pretty.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        }

        var ok = ParamJsonPatch.TryParseOverride(text, out var parsed, out var errors);
        Assert.True(ok, string.Join("\n", errors));
        Assert.NotNull(parsed);
    }
}
