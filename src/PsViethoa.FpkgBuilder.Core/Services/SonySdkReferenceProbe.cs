using LibProsperoPkg.PKG;

namespace PsViethoa.FpkgBuilder.Core.Services;

/// <summary>Ai đã tạo ra gói: quyết định gói có dùng làm tham chiếu cho <c>img_create --ref_pkg_path</c> được không.</summary>
public enum SonySdkReferenceOrigin
{
    /// <summary>Không đọc được vùng supplement, hoặc không có <c>common/etc/naps_meta_18.dat</c>.</summary>
    NoNapsMetadata,

    /// <summary>Engine tích hợp (LibProsperoPkg) — Publishing Tools không đọc được metadata này.</summary>
    BuiltInEngine,

    /// <summary>Publishing Tools (bộ công cụ Sony) — dùng làm tham chiếu được.</summary>
    PublishingTools,
}

/// <summary>
/// Phân biệt gói do Publishing Tools tạo với gói do engine tích hợp tạo, bằng cách đọc đúng một thành viên của vùng supplement.
/// <para>
/// <c>img_create --ref_pkg_path</c> đọc <c>prev_suppl/common/etc/naps_meta_18.dat</c> của gói gốc (chuỗi này nằm trong
/// <c>libScePubTools.dll</c>) và giải mã bằng khoá riêng của Publishing Tools. Gói do LibProsperoPkg tạo có tệp đó, kích thước
/// đúng, nhưng được mã hoá bằng khoá của thư viện — Publishing Tools giải ra rác và dừng với
/// <c>Unexpected logical error. (NAPS metadata missing)</c> SAU khi đã nén xong cả gói (đo thực tế: 4 phút 1 giây cho gói base
/// 33 GB; gói 100 GB mất hàng giờ).
/// </para>
/// <para>
/// Cách nhận ra, đo trên ba gói (SDK Sony, engine tích hợp, và gói base Days Gone của người dùng):
/// <list type="bullet">
/// <item>Gói của Publishing Tools: <see cref="ProsperoNapsMeta.DecryptMeta18"/> ra rác (khoá khác) và bộ mô tả
/// <c>naps_meta_300</c> dài 72 byte (ba bản ghi 24 byte).</item>
/// <item>Gói của engine tích hợp: giải mã sạch, bắt đầu bằng thẻ TLV <c>phdr</c>, và <c>naps_meta_300</c> chỉ dài 48 byte
/// (hai bản ghi — thiếu bản ghi <c>id=1</c>).</item>
/// </list>
/// Chỉ đọc một thành viên ZIP của vùng SI, không giải nén gì ra đĩa (đo: 0,1 giây cho gói 33 GB).
/// </para>
/// </summary>
public static class SonySdkReferenceProbe
{
    /// <summary>Thẻ TLV đầu tiên của naps_meta_18 sau khi giải mã, ghi theo little-endian nên đọc ngược thành "rdhp".</summary>
    private static ReadOnlySpan<byte> Meta18FirstTag => "rdhp"u8;

    /// <summary>Đọc gói và cho biết ai đã tạo ra nó. Không ném lỗi: mọi trục trặc đều quy về <see cref="SonySdkReferenceOrigin.NoNapsMetadata"/>.</summary>
    public static SonySdkReferenceOrigin Probe(string packagePath)
    {
        byte[]? raw;
        try
        {
            raw = ProsperoPublishingSidecar.TryReadNapsMeta18(packagePath);
        }
        catch (Exception)
        {
            // Gói hỏng hoặc không có vùng SI đọc được: coi như không có metadata.
            return SonySdkReferenceOrigin.NoNapsMetadata;
        }

        if (raw == null || raw.Length == 0)
        {
            return SonySdkReferenceOrigin.NoNapsMetadata;
        }

        return IsBuiltInEngineMeta18(raw) ? SonySdkReferenceOrigin.BuiltInEngine : SonySdkReferenceOrigin.PublishingTools;
    }

    /// <summary>true khi naps_meta_18 giải mã được bằng khoá của LibProsperoPkg, tức là do engine tích hợp ghi ra.</summary>
    public static bool IsBuiltInEngineMeta18(byte[] raw)
    {
        byte[] plain;
        try
        {
            plain = ProsperoNapsMeta.DecryptMeta18(raw);
        }
        catch (Exception)
        {
            // Không giải mã được bằng khoá của thư viện = khoá của Publishing Tools.
            return false;
        }

        // Thẻ TLV đầu tiên phải là phdr, với đúng một phần tử dài 24 byte. Dữ liệu giải sai khoá gần như không bao giờ khớp cả ba.
        if (plain.Length < 16 || !plain.AsSpan(0, 4).SequenceEqual(Meta18FirstTag))
        {
            return false;
        }

        var count = BitConverter.ToUInt32(plain, 4);
        var elementSize = BitConverter.ToUInt32(plain, 8);
        return count == 1 && elementSize == 24 && 16 + (long)count * elementSize <= plain.Length;
    }
}
