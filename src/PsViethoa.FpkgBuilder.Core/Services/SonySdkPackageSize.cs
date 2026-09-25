using PsViethoa.FpkgBuilder.Core.Localization;

namespace PsViethoa.FpkgBuilder.Core.Services;

/// <summary>
/// Lựa chọn cỡ gói lớn của SDK (<c>choose_package_size</c> của script fix12): mức <c>attributePub</c> nhỏ nhất chứa được ước lượng,
/// mức <c>kernel.addcontMountLevel</c> sau khi hạ (nếu phải hạ) và <c>appSizeInGib</c> (chỉ SDK 3.13).
/// </summary>
/// <param name="AttributePub">0 = gói thường, 1 = lv1, 2 = lv2 (SDK 2.79), 4 = lv3 (SDK 3.13).</param>
/// <param name="AppSizeInGib">Chỉ có ở lv3 (SDK 3.13); null với SDK 2.79.</param>
/// <param name="EstimatedBytes">Kích thước chưa nén của mọi tệp trong GP5 cộng phần dự phòng metadata/căn lề.</param>
/// <param name="MountLevel">kernel.addcontMountLevel sau khi chọn (giữ nguyên hoặc hạ xuống).</param>
public sealed record SonySdkSizeSelection(int AttributePub, int? AppSizeInGib, long EstimatedBytes, int MountLevel);

/// <summary>
/// Bộ công cụ fix12 tự chọn <c>attributePub</c> trong param.json chuẩn hoá cho gói APP đầy đủ (không áp cho bản vá), từ tổng kích
/// thước CHƯA NÉN của các tệp thật sự có trong GP5. Đây là ước lượng trước khi tạo, không phải kích thước gói cuối; img_create quyết
/// định sau khi nén. Port từng dòng của <c>choose_package_size</c>, <c>addcont_mount_level</c> và phần tương ứng của
/// <c>write_standard_param</c>.
/// </summary>
public static class SonySdkPackageSize
{
    /// <summary>Profile truyền cho <c>--auto-size-profile</c> (build-from-folder.ps1 của fix12 luôn dùng sdk279).</summary>
    public const string Sdk279 = "sdk279";

    public const string Sdk313 = "sdk313";

    private const long Mib = 1024L * 1024;
    private const long Gib = 1024L * Mib;

    public const long StandardPackageLimit = 162_514_599_936;
    public const long LargePackageLv1Limit = 195_878_191_104;
    public const long LargePackageLv2Limit = 260_436_918_272;
    public const long AddcontMountLv2Limit = 97_945_387_008;

    /// <summary>Số tệp tối đa của lv2 với Publishing Tools đã vá (fix12 nâng lên 500 000).</summary>
    public const int LargePackageLv2FileLimit = 500_000;

    /// <summary><c>choose_package_size</c>: ước lượng = chưa nén + max(512 MiB, 1 %) + 4 KiB mỗi tệp, rồi chọn mức nhỏ nhất vừa.</summary>
    public static SonySdkSizeSelection Choose(long unpackedBytes, int fileCount, string sdkProfile, int mountLevel = 0)
    {
        if (sdkProfile is not (Sdk279 or Sdk313))
        {
            throw new ArgumentException($"unsupported auto-size SDK profile: {sdkProfile}", nameof(sdkProfile));
        }

        if (unpackedBytes < 0 || fileCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(unpackedBytes), "unpacked size and file count must not be negative");
        }

        if (mountLevel is < 0 or > 2)
        {
            throw new InvalidDataException(Loc.F("Sdk.MountLevelInvalid", "param.json", mountLevel));
        }

        var allowance = Math.Max(512 * Mib, (unpackedBytes + 99) / 100) + fileCount * 4096L;
        var estimated = unpackedBytes + allowance;
        // Giữ mức mount cao nhất còn chứa được ảnh: thuộc tính lv2 không nâng giới hạn của mức 1, và lv3 cần mức 0.
        var selectedMountLevel = mountLevel;
        if (selectedMountLevel == 2 && estimated > AddcontMountLv2Limit)
        {
            selectedMountLevel = 1;
        }

        if (selectedMountLevel == 1 && estimated > LargePackageLv1Limit)
        {
            selectedMountLevel = 0;
        }

        if (selectedMountLevel == 2 || estimated <= StandardPackageLimit)
        {
            return new SonySdkSizeSelection(0, null, estimated, selectedMountLevel);
        }

        if (estimated <= LargePackageLv1Limit)
        {
            return new SonySdkSizeSelection(1, null, estimated, selectedMountLevel);
        }

        if (estimated <= LargePackageLv2Limit || sdkProfile == Sdk279)
        {
            // SDK 2.79 không có mức lớn hơn: giữ lv2 và để img_create quyết định sau khi nén.
            if (fileCount > LargePackageLv2FileLimit)
            {
                throw new InvalidDataException(Loc.F("Sdk.Lv2TooManyFiles", LargePackageLv2FileLimit, fileCount));
            }

            return new SonySdkSizeSelection(2, null, estimated, selectedMountLevel);
        }

        // appSizeInGib tối đa 320; ước lượng chưa nén có thể vượt trong khi gói đã nén vẫn vừa.
        var appSizeGib = (int)Math.Min(320, Math.Max(256, (estimated + Gib - 1) / Gib));
        return new SonySdkSizeSelection(4, appSizeGib, estimated, selectedMountLevel);
    }

    /// <summary>Giới hạn khai báo lớn nhất của profile (để cảnh báo khi ước lượng vượt).</summary>
    public static long ProfileLimit(string sdkProfile) => sdkProfile == Sdk279 ? LargePackageLv2Limit : 320 * Gib;

    /// <summary>
    /// <c>addcont_mount_level</c>: kernel phải là object nếu có; addcontMountLevel mặc định 0, phải là số nguyên 0/1/2 (không nhận
    /// 1.0 hay true, đúng <c>type(level) is int</c> của Python).
    /// </summary>
    public static int ReadMountLevel(System.Text.Json.Nodes.JsonObject param, string source)
    {
        var kernelNode = param["kernel"];
        if (param.ContainsKey("kernel") && kernelNode is not System.Text.Json.Nodes.JsonObject)
        {
            throw new InvalidDataException(Loc.F("Sdk.KernelNotObject", source));
        }

        if (kernelNode is not System.Text.Json.Nodes.JsonObject kernel || !kernel.ContainsKey("addcontMountLevel"))
        {
            return 0;
        }

        var level = kernel["addcontMountLevel"];
        if (level is System.Text.Json.Nodes.JsonValue value &&
            value.GetValueKind() == System.Text.Json.JsonValueKind.Number &&
            IsIntegerLiteral(value.ToJsonString()) &&
            value.TryGetValue<int>(out var number) && number is >= 0 and <= 2)
        {
            return number;
        }

        throw new InvalidDataException(Loc.F("Sdk.MountLevelInvalid", source, level?.ToJsonString() ?? "null"));
    }

    private static bool IsIntegerLiteral(string text) => text.Length > 0 && text.All(c => c is >= '0' and <= '9' || c == '-');

    /// <summary>
    /// Phần cỡ gói của <c>write_standard_param</c>: bắt buộc game gốc (applicationCategoryType 0), đặt attributePub, hạ
    /// kernel.addcontMountLevel khi đã hạ, appSizeInGib chỉ với lv3 (ngược lại bỏ khỏi kernel). Thứ tự khoá như dict của Python: khoá
    /// có sẵn giữ vị trí, khoá mới thêm vào cuối.
    /// </summary>
    public static void Apply(System.Text.Json.Nodes.JsonObject value, SonySdkSizeSelection selection, string source)
    {
        if (!IsZero(value["applicationCategoryType"]))
        {
            throw new InvalidDataException(Loc.T("Sdk.AutoSizeNotGame"));
        }

        value["attributePub"] = selection.AttributePub;
        var originalMountLevel = ReadMountLevel(value, source);
        var kernel = value["kernel"] as System.Text.Json.Nodes.JsonObject;
        if (selection.MountLevel != originalMountLevel)
        {
            if (kernel == null)
            {
                kernel = new System.Text.Json.Nodes.JsonObject();
                value["kernel"] = kernel;
            }

            kernel["addcontMountLevel"] = selection.MountLevel;
        }

        if (selection.AppSizeInGib is { } appSize)
        {
            if (kernel == null)
            {
                kernel = new System.Text.Json.Nodes.JsonObject();
                value["kernel"] = kernel;
            }

            if (selection.MountLevel != 0)
            {
                throw new InvalidDataException("lv3 requires kernel.addcontMountLevel to be absent or 0");
            }

            kernel["appSizeInGib"] = appSize;
        }
        else
        {
            kernel?.Remove("appSizeInGib");
        }

        if ((selection.MountLevel == 1 && selection.AttributePub is not (0 or 1)) || (selection.MountLevel == 2 && selection.AttributePub != 0))
        {
            throw new InvalidDataException($"attributePub={selection.AttributePub} is incompatible with kernel.addcontMountLevel={selection.MountLevel}");
        }
    }

    /// <summary><c>value.get(...) != 0</c> của Python: 0, 0.0 và false đều bằng 0; thiếu khoá (None) thì khác.</summary>
    private static bool IsZero(System.Text.Json.Nodes.JsonNode? node) => node is System.Text.Json.Nodes.JsonValue value && value.GetValueKind() switch
    {
        System.Text.Json.JsonValueKind.Number => value.TryGetValue<double>(out var number) && number == 0,
        System.Text.Json.JsonValueKind.False => true,
        _ => false,
    };
}
