using Avalonia.Media.Imaging;

namespace Launcher.Avalonia.ViewModels;

/// <summary>
/// Карточка снимка в галерее. Тип публичный и лежит здесь, потому что компилятор XAML Avalonia
/// требует <c>x:DataType</c> для шаблона: с вложенным приватным типом привязки не собираются.
/// </summary>
public sealed class ScreenshotViewModel(string path, string fileName, string caption, Bitmap? preview)
{
    public string Path { get; } = path;
    public string FileName { get; } = fileName;
    public string Caption { get; } = caption;
    public Bitmap? Preview { get; } = preview;
}
