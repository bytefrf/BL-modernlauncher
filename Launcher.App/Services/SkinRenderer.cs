using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Launcher.App.Services;

/// <summary>
/// Собирает превью скина (вид спереди) и «голову» из PNG-файла скина Minecraft.
///
/// Поддерживаются оба формата: современный 64×64 (со вторым слоем — шапка, куртка, рукава)
/// и старый 64×32, где левая рука/нога — зеркало правой и второго слоя нет.
/// Масштабирование только пиксельное (NearestNeighbor), иначе скин мылится.
/// </summary>
public static class SkinRenderer
{
    // Прямоугольники в текстуре скина: вид СПЕРЕДИ.
    private static readonly Int32Rect Head = new(8, 8, 8, 8);
    private static readonly Int32Rect HeadOverlay = new(40, 8, 8, 8);
    private static readonly Int32Rect Body = new(20, 20, 8, 12);
    private static readonly Int32Rect BodyOverlay = new(20, 36, 8, 12);
    private static readonly Int32Rect RightLeg = new(4, 20, 4, 12);
    private static readonly Int32Rect RightLegOverlay = new(4, 36, 4, 12);
    private static readonly Int32Rect LeftLeg = new(20, 52, 4, 12);
    private static readonly Int32Rect LeftLegOverlay = new(4, 52, 4, 12);

    /// <summary>
    /// Голова для аватарки: лицо + шапка поверх.
    /// Собираем в НАТИВНОМ размере (8×8): увеличивает уже сам элемент с NearestNeighbor, иначе
    /// промежуточный RenderTargetBitmap интерполирует пиксели и скин выглядит мыльным.
    /// </summary>
    public static BitmapSource? RenderHead(byte[] pngBytes, int scale = 1)
    {
        var skin = Decode(pngBytes);
        if (skin is null)
        {
            return null;
        }

        return Compose(8, 8, scale, context =>
        {
            DrawPart(context, skin, Head, new Rect(0, 0, 8, 8));
            DrawPart(context, skin, HeadOverlay, new Rect(0, 0, 8, 8));
        });
    }

    /// <summary>Персонаж целиком, вид спереди. Нативный размер 16×32 — растягивает уже элемент.</summary>
    public static BitmapSource? RenderBody(byte[] pngBytes, bool slim, int scale = 1)
    {
        var skin = Decode(pngBytes);
        if (skin is null)
        {
            return null;
        }

        var legacy = skin.PixelHeight <= 32;
        var armWidth = slim ? 3 : 4;

        // У slim-модели рука уже на 1 пиксель, но рисуем её в той же колонке.
        var rightArm = new Int32Rect(44, 20, armWidth, 12);
        var rightArmOverlay = new Int32Rect(44, 36, armWidth, 12);
        var leftArm = new Int32Rect(36, 52, armWidth, 12);
        var leftArmOverlay = new Int32Rect(52, 52, armWidth, 12);

        return Compose(16, 32, scale, context =>
        {
            DrawPart(context, skin, Head, new Rect(4, 0, 8, 8));
            DrawPart(context, skin, Body, new Rect(4, 8, 8, 12));
            DrawPart(context, skin, rightArm, new Rect(4 - armWidth, 8, armWidth, 12));
            DrawPart(context, skin, RightLeg, new Rect(4, 20, 4, 12));

            if (legacy)
            {
                // 64×32: левой половины в текстуре нет — зеркалим правую.
                DrawPartMirrored(context, skin, rightArm, new Rect(12, 8, armWidth, 12));
                DrawPartMirrored(context, skin, RightLeg, new Rect(8, 20, 4, 12));
            }
            else
            {
                DrawPart(context, skin, leftArm, new Rect(12, 8, armWidth, 12));
                DrawPart(context, skin, LeftLeg, new Rect(8, 20, 4, 12));

                // Второй слой поверх — только для 64×64.
                DrawPart(context, skin, BodyOverlay, new Rect(4, 8, 8, 12));
                DrawPart(context, skin, rightArmOverlay, new Rect(4 - armWidth, 8, armWidth, 12));
                DrawPart(context, skin, leftArmOverlay, new Rect(12, 8, armWidth, 12));
                DrawPart(context, skin, RightLegOverlay, new Rect(4, 20, 4, 12));
                DrawPart(context, skin, LeftLegOverlay, new Rect(8, 20, 4, 12));
            }

            // Шапка рисуется последней, чтобы перекрывать волосы.
            DrawPart(context, skin, HeadOverlay, new Rect(4, 0, 8, 8));
        });
    }

    private static BitmapSource? Decode(byte[] pngBytes)
    {
        if (pngBytes is null || pngBytes.Length == 0)
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream(pngBytes, writable: false);
            var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];

            // Скин обязан быть 64 пикселя в ширину — иначе координаты частей не сойдутся.
            return frame.PixelWidth < 64 ? null : frame;
        }
        catch
        {
            return null;
        }
    }

    private static BitmapSource Compose(int width, int height, int scale, Action<DrawingContext> draw)
    {
        var visual = new DrawingVisual();
        // Пиксельное масштабирование: без этого скин размывается билинейной интерполяцией.
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.NearestNeighbor);
        RenderOptions.SetEdgeMode(visual, EdgeMode.Aliased);

        using (var context = visual.RenderOpen())
        {
            context.PushTransform(new ScaleTransform(scale, scale));
            draw(context);
            context.Pop();
        }

        var target = new RenderTargetBitmap(width * scale, height * scale, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    private static void DrawPart(DrawingContext context, BitmapSource skin, Int32Rect source, Rect target)
    {
        var part = CropSafely(skin, source);
        if (part is not null)
        {
            context.DrawImage(part, target);
        }
    }

    private static void DrawPartMirrored(DrawingContext context, BitmapSource skin, Int32Rect source, Rect target)
    {
        var part = CropSafely(skin, source);
        if (part is null)
        {
            return;
        }

        context.PushTransform(new ScaleTransform(-1, 1, target.X + target.Width / 2, 0));
        context.DrawImage(part, target);
        context.Pop();
    }

    private static BitmapSource? CropSafely(BitmapSource skin, Int32Rect source)
    {
        if (source.X + source.Width > skin.PixelWidth || source.Y + source.Height > skin.PixelHeight)
        {
            return null;
        }

        try
        {
            return new CroppedBitmap(skin, source);
        }
        catch
        {
            return null;
        }
    }
}
