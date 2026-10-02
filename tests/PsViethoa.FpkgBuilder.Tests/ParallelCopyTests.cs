using System.IO;
using System.Reflection;
using PsViethoa.FpkgBuilder.Core.Services;
using Xunit;

namespace PsViethoa.FpkgBuilder.Tests;

/// <summary>Parallel copy integrity check: 100MB random bytes → CopyFile (private) → SHA-256 must match (SSD on C: is 4-thread path).</summary>
public sealed class ParallelCopyTests
{
    [Fact]
    public void CopyFile_ParallelProducesIdenticalBytes()
    {
        var folder = Path.Combine(Path.GetTempPath(), "opencode");
        Directory.CreateDirectory(folder);
        var source = Path.Combine(folder, "parallel-copy-src.bin");
        var target = Path.Combine(folder, "parallel-copy-dst.bin");
        TryDelete(source);
        TryDelete(target);
        try
        {
            const int mebibytes = 100;
            var random = new Random(12345);
            using (var stream = new FileStream(source, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            {
                var buffer = new byte[1 << 20];
                for (var written = 0; written < mebibytes; written++)
                {
                    random.NextBytes(buffer);
                    stream.Write(buffer, 0, buffer.Length);
                }
            }

            var method = typeof(CntParamPatcher).GetMethod("CopyFile", BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException("CopyFile not found");
            method.Invoke(null, [source, target, null, CancellationToken.None]);

            Assert.Equal(new FileInfo(source).Length, new FileInfo(target).Length);
            Assert.Equal(SHA256(source), SHA256(target));
        }
        finally
        {
            TryDelete(source);
            TryDelete(target);
        }
    }

    private static string SHA256(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = System.Security.Cryptography.SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream));
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
        catch (Exception)
        {
        }
    }
}
