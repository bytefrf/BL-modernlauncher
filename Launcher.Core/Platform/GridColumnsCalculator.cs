namespace Launcher.App.Platform;

/// <summary>
/// Сколько колонок уместится в сетке карточек при текущей ширине.
/// </summary>
/// <remarks>
/// Карточки достижений раньше лежали в WrapPanel с фиксированной шириной: сколько влезло — столько
/// в ряд, остаток ширины оставался пустым. На широком окне это выглядело как «список прижат влево»,
/// потому что незанятый хвост доходил до половины блока. Считаем число колонок и растягиваем
/// карточки на всю ширину, чтобы пустого хвоста не оставалось совсем.
/// </remarks>
public static class GridColumnsCalculator
{
    /// <param name="availableWidth">Ширина, доступная сетке.</param>
    /// <param name="minItemWidth">Ширина, ниже которой карточка становится нечитаемой.</param>
    /// <param name="maxColumns">Больше этого не растягиваем: карточки превратятся в узкие полоски.</param>
    public static int Resolve(double availableWidth, double minItemWidth, int maxColumns = 8)
    {
        if (double.IsNaN(availableWidth) || double.IsInfinity(availableWidth) || availableWidth <= 0 || minItemWidth <= 0)
        {
            // Ширина ещё не известна (первый проход разметки) — одна колонка безопаснее нуля.
            return 1;
        }

        var columns = (int)Math.Floor(availableWidth / minItemWidth);
        return Math.Clamp(columns, 1, Math.Max(1, maxColumns));
    }
}
