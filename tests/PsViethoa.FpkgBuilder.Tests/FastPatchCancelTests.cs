using System.IO;
using System.Text;
using PsViethoa.FpkgBuilder.Core.Services;
using Xunit;

namespace PsViethoa.FpkgBuilder.Tests;

/// <summary>취소 검증: 병렬 복사 중 token 취소 → OperationCanceledException + 부분 파일 (호출자가 지울 것).</summary>
public sealed class FastPatchCancelTests
{
    [Fact]
    public void ParallelCopy_CancelStopsWithinSeconds()
    {
        var source = Environment.GetEnvironmentVariable("FPKG_FASTPATCH_SOURCE")
            ?? @"C:\git\UP0102-PPSA02530_00-PRAGMATA00000000-A01200-V01200.pkg";
        if (!File.Exists(source))
        {
            return; // 이 머신에만 있는 진단 파일
        }

        var target = Path.Combine(Path.GetTempPath(), "opencode", "pragmata-cancel.pkg");
        try
        {
            if (File.Exists(target))
            {
                File.Delete(target);
            }
        }
        catch (Exception)
        {
        }

        try
        {
            using var cts = new CancellationTokenSource();
            var cancelled = false;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                // 취소를 위해 시작 3초 뒤 Cancel 발사. param.json 자리에 들어갈 dummy(실제 슬롯보다 크게 하여 검증 생략).
                cts.CancelAfter(TimeSpan.FromSeconds(3));
                CntParamPatcher.Patch(source, target, Encoding.UTF8.GetBytes("{}"), null, cts.Token);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }

            watch.Stop();
            Assert.True(cancelled, "must throw OperationCanceledException");
            Assert.True(watch.Elapsed.TotalSeconds < 30, $"cancel took {watch.Elapsed.TotalSeconds:0}s — too slow");

            // SetLength(total) cấp phát trước nên kích thước tệp có thể đã bằng gốc — chỉ cần tệp tồn tại (gọi chính xoá).
            Assert.True(!File.Exists(target) || new FileInfo(target).Length > 0, "partial or deleted file expected");
        }
        finally
        {
            try
            {
                if (File.Exists(target))
                {
                    File.Delete(target);
                }
            }
            catch (Exception)
            {
            }
        }
    }
}
