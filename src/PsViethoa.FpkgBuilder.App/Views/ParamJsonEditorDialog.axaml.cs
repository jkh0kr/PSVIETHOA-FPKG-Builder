using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Interactivity;
using PsViethoa.FpkgBuilder.Core.Localization;
using PsViethoa.FpkgBuilder.Core.Services;

namespace PsViethoa.FpkgBuilder.App.Views;

/// <summary>
/// Trình sửa sce_sys/param.json: mở sẵn bản gốc (áp phần ghi đè đã lưu nếu có), người dùng sửa trực tiếp, khi Lưu chỉ phần
/// KHÁC gốc được giữ lại làm phần ghi đè (xem <see cref="ParamJsonPatch.BuildOverrideDiff"/>) và được kiểm tra bằng
/// <see cref="ParamJsonPatch.TryParseOverride"/>. Nguồn gốc không bao giờ bị ghi.
/// </summary>
public partial class ParamJsonEditorDialog : Window
{
    private static readonly JsonDocumentOptions DocumentOptions = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private JsonObject? _original;

    public ParamJsonEditorDialog()
    {
        InitializeComponent();
        Opened += (_, _) => Services.DebugLog.Write($"ParamJsonEditorDialog opened: {Title}");
        Closed += (_, _) => Services.DebugLog.Write($"ParamJsonEditorDialog closed: {Title}");
    }

    /// <summary>Nội dung phần ghi đè sẽ lưu (null = người dùng trả về như gốc — xoá phần ghi đè đang có).</summary>
    public string? OverrideJson { get; private set; }

    /// <summary>Điền nội dung mở sẵn: bản gốc (hoặc gộp phần ghi đè đã lưu vào bản gốc cho người dùng sửa tiếp từ đó).</summary>
    public void Prepare(string? originalJson, string? currentOverride)
    {
        _original = ParseOrNull(originalJson) ?? new JsonObject();
        var shown = _original.DeepClone().AsObject();
        if (ParseOrNull(currentOverride) is { Count: > 0 } saved)
        {
            // Chỉ để hiển thị: hợp nhất phần ghi đè cũ vào bản gốc; DRM giữ nguyên trạng thái gốc cho trung thực.
            ParamJsonPatch.ApplyCustomOverride(shown, saved, forceStandardDrm: false);
        }

        JsonBox.Text = shown.ToJsonString(WriteOptions);
        JsonBox.CaretIndex = 0;
    }

    private static JsonObject? ParseOrNull(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json, documentOptions: DocumentOptions) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        var edited = ParseOrNull(JsonBox.Text);
        if (edited == null)
        {
            ShowError(string.IsNullOrWhiteSpace(JsonBox.Text)
                ? Loc.T("Param.OverrideEmpty")
                : Loc.T("ParamEditor.InvalidJson"));
            return;
        }

        var diff = ParamJsonPatch.BuildOverrideDiff(_original, edited);
        if (diff == null)
        {
            // Trả về như gốc: không còn gì phải ghi đè.
            OverrideJson = null;
            Close(true);
            return;
        }

        var text = diff.ToJsonString(WriteOptions);
        if (!ParamJsonPatch.TryParseOverride(text, out _, out var errors))
        {
            ShowError(Loc.F("ParamEditor.OverrideInvalid", string.Join("\n", errors)));
            return;
        }

        OverrideJson = text;
        Close(true);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorPanel.IsVisible = true;
    }

    protected override void OnKeyDown(Avalonia.Input.KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Avalonia.Input.Key.Escape)
        {
            e.Handled = true;
            Close(false);
        }
    }
}
