namespace Launcher.App.Platform;

/// <summary>
/// Подбор размера главного окна по рабочей области экрана. Раньше эта арифметика была продублирована
/// в WPF и в Avalonia, и версии успевали разъехаться; вдобавок её нельзя было покрыть офлайн-тестом,
/// потому что SmokeTest не ссылается на UI-проекты. Теперь она одна и проверяется
/// через <c>SmokeTest --window-size</c>.
/// </summary>
public static class WindowSizeCalculator
{
    public const double MinWidth = 980;
    public const double MinHeight = 560;

    // Потолок подняли с 1440x810: на 1920x1080 окно занимало 56% площади экрана и выглядело мелким,
    // вокруг оставались широкие поля. Дальше 1600x900 не растём намеренно — на 2K/4K окно во весь
    // экран расползается пустотой, интерфейс на это не рассчитан.
    public const double MaxWidth = 1600;
    public const double MaxHeight = 900;

    /// <summary>
    /// Размер подбирается ПЛАВНО от рабочей области, а не ступенями: раньше было четыре фиксированных
    /// пресета, из-за чего на разных мониторах интерфейс заметно «прыгал». Пропорции держим близко
    /// к 16:9 и обязательно вписываемся в рабочую область с полями.
    /// </summary>
    public static (double Width, double Height) Select(double workWidth, double workHeight)
    {
        // Оставляем поля вокруг окна, чтобы оно не липло к краям и к панели задач.
        var availableWidth = Math.Max(MinWidth, workWidth - 80);
        var availableHeight = Math.Max(MinHeight, workHeight - 80);

        // Доля поднята с 0.78: на 1920x1080 старая формула упиралась в потолок 1440 и окно занимало
        // чуть больше половины экрана. Теперь на 1080p получается ровно 1600x900.
        var width = Math.Clamp(workWidth * 0.86, MinWidth, MaxWidth);
        var height = Math.Clamp(width * 9 / 16, MinHeight, MaxHeight);

        // Если по высоте не влезли — пересчитываем ширину от высоты, сохраняя пропорции.
        if (height > availableHeight)
        {
            height = availableHeight;
            width = Math.Clamp(height * 16 / 9, MinWidth, MaxWidth);
        }

        return (Math.Min(width, availableWidth), Math.Min(height, availableHeight));
    }
}
