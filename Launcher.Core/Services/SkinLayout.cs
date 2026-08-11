namespace Launcher.App.Services;

/// <param name="SourceX">Левый край области в текстуре скина.</param>
/// <param name="Mirrored">Отразить по горизонтали — для старого формата 64×32.</param>
public sealed record SkinDrawOp(
    int SourceX,
    int SourceY,
    int Width,
    int Height,
    int TargetX,
    int TargetY,
    bool Mirrored = false);

/// <summary>
/// Раскладка частей скина Minecraft при виде спереди: какие прямоугольники текстуры
/// куда рисовать и в каком порядке.
/// </summary>
/// <remarks>
/// Здесь только геометрия — она одинакова для WPF и Avalonia. Декодирование PNG и запись
/// пикселей остаются за интерфейсом: в .NET нет встроенного кроссплатформенного декодера.
///
/// Поддерживаются оба формата: современный 64×64 (со вторым слоем — шапка, куртка, рукава)
/// и старый 64×32, где левая рука и нога — зеркало правой, а второго слоя нет.
/// </remarks>
public static class SkinLayout
{
    public const int HeadSize = 8;
    public const int BodyWidth = 16;
    public const int BodyHeight = 32;

    /// <summary>Голова для аватарки: лицо и шапка поверх.</summary>
    public static IReadOnlyList<SkinDrawOp> BuildHead() =>
    [
        new(8, 8, 8, 8, 0, 0),
        new(40, 8, 8, 8, 0, 0)
    ];

    /// <param name="legacy">Текстура 64×32 — без второго слоя и левой половины.</param>
    /// <param name="slim">Модель с узкой рукой (3 пикселя вместо 4).</param>
    public static IReadOnlyList<SkinDrawOp> BuildBody(bool legacy, bool slim)
    {
        var armWidth = slim ? 3 : 4;
        var ops = new List<SkinDrawOp>
        {
            new(8, 8, 8, 8, 4, 0),                       // голова
            new(20, 20, 8, 12, 4, 8),                    // туловище
            new(44, 20, armWidth, 12, 4 - armWidth, 8),  // правая рука
            new(4, 20, 4, 12, 4, 20)                     // правая нога
        };

        if (legacy)
        {
            // 64×32: левой половины в текстуре нет — зеркалим правую.
            ops.Add(new(44, 20, armWidth, 12, 12, 8, Mirrored: true));
            ops.Add(new(4, 20, 4, 12, 8, 20, Mirrored: true));
        }
        else
        {
            ops.Add(new(36, 52, armWidth, 12, 12, 8));   // левая рука
            ops.Add(new(20, 52, 4, 12, 8, 20));          // левая нога

            // Второй слой поверх — только для 64×64.
            ops.Add(new(20, 36, 8, 12, 4, 8));                       // куртка
            ops.Add(new(44, 36, armWidth, 12, 4 - armWidth, 8));     // правый рукав
            ops.Add(new(52, 52, armWidth, 12, 12, 8));               // левый рукав
            ops.Add(new(4, 36, 4, 12, 4, 20));                       // правая штанина
            ops.Add(new(4, 52, 4, 12, 8, 20));                       // левая штанина
        }

        // Шапка рисуется последней, чтобы перекрывать волосы.
        ops.Add(new(40, 8, 8, 8, 4, 0));
        return ops;
    }

    /// <summary>Текстура пригодна, только если её ширина 64 — иначе координаты частей не сойдутся.</summary>
    public static bool IsSupportedTexture(int width, int height) => width >= 64 && height >= 32;

    public static bool IsLegacyTexture(int height) => height <= 32;
}
