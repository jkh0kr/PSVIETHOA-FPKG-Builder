using PsViethoa.FpkgBuilder.Core.ExFat;

namespace PsViethoa.FpkgBuilder.Core.Services;

/// <summary>
/// Tàn dư mà bản dump để lại trong thư mục ứng dụng và làm game treo cứng ở màn hình splash (Hard Splash Lock, phải tắt nguồn) khi
/// cài từ gói. Quy tắc lấy từ hướng dẫn dọn Stellar Blade (DLPS, PPSA13197 EUR kèm DLC) do cộng đồng chia sẻ (ghi công trong
/// giao diện, nhật ký, CHANGELOG và README):
/// <list type="bullet">
/// <item>thư mục <c>_DUBLEX_</c> ở gốc — bỏ cả thư mục;</item>
/// <item><c>Engine/Saved</c> của Unreal Engine — bỏ; nếu <c>Engine</c> chỉ có mỗi <c>Saved</c> thì bỏ luôn <c>Engine</c>;</item>
/// <item><c>&lt;dự án&gt;/Saved</c> (Stellar Blade: <c>SB/Saved</c>) — bỏ, với <c>&lt;dự án&gt;</c> là thư mục có <c>Content/Paks</c> của Unreal.</item>
/// </list>
/// Hai bước còn lại của hướng dẫn (<c>ampr_emu.index</c>, <c>fakelib/libSceAmpr.sprx</c>) là tuỳ chọn "Bỏ tàn dư AMPR emu" có sẵn.
/// Thư mục <c>Saved</c> là dữ liệu game Unreal tự ghi lúc chạy (log, cấu hình, crash) — gói phát hành không bao giờ có.
/// </summary>
public static class DumpLeftoverInspector
{
    public const string DublexFolder = "_DUBLEX_";

    public const string EngineFolder = "Engine";

    public const string SavedFolder = "Saved";

    /// <summary>Thư mục ở gốc (đường dẫn tương đối, đúng hoa thường trên đĩa) cần bỏ khỏi gói.</summary>
    public static IReadOnlyList<string> FindInFolder(string appFolder)
    {
        try
        {
            return Find(relative =>
            {
                var path = relative.Length == 0 ? appFolder : Path.Combine(appFolder, relative.Replace('/', Path.DirectorySeparatorChar));
                return Directory.Exists(path)
                    ? new DirectoryInfo(path).EnumerateFileSystemInfos().Select(entry => (entry.Name, (entry.Attributes & FileAttributes.Directory) != 0)).ToList()
                    : null;
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>Như <see cref="FindInFolder"/> nhưng đọc thẳng trong ảnh exFAT (ổ ảo Dokan ẩn được cả thư mục).</summary>
    public static IReadOnlyList<string> FindInImage(ExFatImage image, ExFatEntry appRoot) => Find(relative =>
    {
        var entry = relative.Length == 0 ? appRoot : image.Find(relative, appRoot);
        return entry is { IsDirectory: true } ? image.Enumerate(entry).Select(child => (child.Name, child.IsDirectory)).ToList() : null;
    });

    /// <summary>Đường dẫn có phải thư mục do quy tắc này chọn không (để ghi nhật ký riêng).</summary>
    public static bool LooksLikeCandidate(string relative)
    {
        var parts = relative.Split('/');
        return parts.Length switch
        {
            1 => parts[0].Equals(DublexFolder, StringComparison.OrdinalIgnoreCase) || parts[0].Equals(EngineFolder, StringComparison.OrdinalIgnoreCase),
            2 => parts[1].Equals(SavedFolder, StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    /// <param name="list">Liệt kê con của một thư mục (tương đối, "" = gốc): (tên, là thư mục); null khi không có thư mục đó.</param>
    private static IReadOnlyList<string> Find(Func<string, IReadOnlyList<(string Name, bool IsDirectory)>?> list)
    {
        var result = new List<string>();
        var root = list(string.Empty);
        if (root == null)
        {
            return result;
        }

        foreach (var (name, isDirectory) in root.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (!isDirectory)
            {
                continue;
            }

            if (name.Equals(DublexFolder, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(name);
                continue;
            }

            var children = list(name);
            var saved = children?.FirstOrDefault(child => child.IsDirectory && child.Name.Equals(SavedFolder, StringComparison.OrdinalIgnoreCase));
            if (children == null || saved is not { Name: { } savedName })
            {
                continue;
            }

            if (name.Equals(EngineFolder, StringComparison.OrdinalIgnoreCase))
            {
                // Engine chỉ còn Saved: bỏ cả Engine; còn thư mục khác (Content, Plugins…) thì chỉ bỏ Saved.
                result.Add(children.Count == 1 ? name : name + "/" + savedName);
                continue;
            }

            // Thư mục dự án Unreal: có Content/Paks cạnh Saved.
            var content = children.FirstOrDefault(child => child.IsDirectory && child.Name.Equals("Content", StringComparison.OrdinalIgnoreCase));
            if (content is { Name: { } contentName } &&
                list(name + "/" + contentName)?.Any(child => child.IsDirectory && child.Name.Equals("Paks", StringComparison.OrdinalIgnoreCase)) == true)
            {
                result.Add(name + "/" + savedName);
            }
        }

        return result;
    }
}
