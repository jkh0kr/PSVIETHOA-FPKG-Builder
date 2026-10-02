using System.Buffers.Binary;

namespace PsViethoa.FpkgBuilder.Core.Services;

public readonly record struct PatchDeltaAuditResult(int TotalBlocks, int DifferentBlocks)
{
    public bool Clean => DifferentBlocks == 0;
}

/// <summary>
/// Kiểm tra "độ tinh khiết" của một bản vá UPDATE: so imagedigs.dat (mỗi khối 64 KiB của PFS ngoài một SHA3) giữa gói gốc
/// và gói remastered vừa tạo. param.json nằm trong CNT — không thuộc PFS ngoài — nên một bản vá chỉ sửa param.json phải có
/// imagedigs GIỐNG HỆT: khối nào khác nghĩa là bản vá mang theo dữ liệu game (nén lại không khớp gốc), thứ người dùng lo
/// (eboot.bin / chống cheat). Đọc mỗi bên một mục CNT vài MB nên rất rẻ so với băm lại cả gói.
/// </summary>
public static class PatchDeltaAudit
{
    private const uint ImageDigestsEntryId = 0x040A;
    private static readonly byte[] FihMagic = [0x7F, (byte)'F', (byte)'I', (byte)'H'];

    /// <summary>So sánh hai gói theo từng khối PFS ngoài. Null khi một trong hai gói không đọc được imagedigs.</summary>
    public static PatchDeltaAuditResult? Compare(string basePackage, string remastered)
    {
        var left = ReadImageDigests(basePackage);
        var right = ReadImageDigests(remastered);
        if (left == null || right == null)
        {
            return null;
        }

        // Hai gói có thể có số khối khác nhau (ảnh ngoài dài khác nhau) — phần thiếu bên nào coi như một khối khác.
        var total = (Math.Max(left.Length, right.Length) + 31) / 32;
        var different = 0;
        for (var block = 0; block < total; block++)
        {
            var offset = block * 32;
            if (offset + 32 > left.Length || offset + 32 > right.Length || !left.AsSpan(offset, 32).SequenceEqual(right.AsSpan(offset, 32)))
            {
                different++;
            }
        }

        return new PatchDeltaAuditResult(total, different);
    }

    /// <summary>Đọc mục imagedigs (Id 0x040A) thẳng từ vùng CNT của gói PLAINTEXT; null khi không có.</summary>
    private static byte[]? ReadImageDigests(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.RandomAccess);
            if (stream.Length < 0x1000)
            {
                return null;
            }

            var fih = new byte[0x1000];
            stream.Position = 0;
            stream.ReadAtLeast(fih, 0x1000, throwOnEndOfStream: false);
            if (!fih.AsSpan(0, 4).SequenceEqual(FihMagic))
            {
                return null;
            }

            var cntOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(fih.AsSpan(0x58));
            var cnt = new byte[0x10000];
            stream.Position = cntOffset;
            stream.ReadAtLeast(cnt, 0x10000, throwOnEndOfStream: false);

            var count = BinaryPrimitives.ReadUInt32BigEndian(cnt.AsSpan(0x10));
            var table = BinaryPrimitives.ReadUInt32BigEndian(cnt.AsSpan(0x18));
            if (count == 0 || count > 0x10000 || table + (long)count * 0x20 > cnt.Length)
            {
                return null;
            }

            for (var index = 0; index < count; index++)
            {
                var record = (int)table + index * 0x20;
                if (BinaryPrimitives.ReadUInt32BigEndian(cnt.AsSpan(record)) != ImageDigestsEntryId)
                {
                    continue;
                }

                var offset = BinaryPrimitives.ReadUInt32BigEndian(cnt.AsSpan(record + 0x10));
                var size = BinaryPrimitives.ReadUInt32BigEndian(cnt.AsSpan(record + 0x14));
                if (size == 0 || size > 64 << 20 || offset + (long)size > stream.Length - cntOffset)
                {
                    return null;
                }

                var data = new byte[size];
                stream.Position = cntOffset + offset;
                stream.ReadExactly(data);
                return data;
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
