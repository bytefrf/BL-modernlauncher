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

    // Потолок 1600x900 на 1920x1080 давал окно почти во весь экран — тесно и неудобно. Вернулись
    // к 1440x810 как размеру ПО УМОЛЧАНИЮ: спорить о «правильном» размере больше не нужно, потому
    // что окно теперь тянется мышью и выбранный размер запоминается.
    public const double MaxWidth = 1440;
    public const double MaxHeight = 810;

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

    /// <summary>
    /// Проверяет сохранённый размер окна и вписывает его в текущую рабочую область.
    /// Возвращает null, если сохранённого размера нет или он бессмысленный.
    /// </summary>
    /// <remarks>
    /// Монитор мог смениться на меньший, а в настройках лежит размер от прошлого: без проверки окно
    /// открылось бы за краями экрана, и часть кнопок стала бы недоступна.
    /// </remarks>
    public static (double Width, double Height)? Restore(double savedWidth, double savedHeight, double workWidth, double workHeight)
    {
        if (savedWidth < MinWidth || savedHeight < MinHeight)
        {
            return null;
        }

        return (
            Math.Min(savedWidth, Math.Max(MinWidth, workWidth)),
            Math.Min(savedHeight, Math.Max(MinHeight, workHeight)));
    }
}
