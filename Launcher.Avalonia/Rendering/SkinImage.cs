using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Launcher.App.Services;

namespace Launcher.Avalonia.Rendering;

/// <summary>
/// Собирает превью скина средствами Avalonia. Раскладка частей общая с WPF
/// (<see cref="SkinLayout"/>) — здесь только работа с пикселями.
/// </summary>
/// <remarks>
/// Картинки строятся в НАТИВНОМ размере (8×8 голова, 16×32 фигура): увеличивает уже
/// элемент с отключённым сглаживанием. Промежуточное масштабирование замылило бы скин.
/// </remarks>
public static class SkinImage
{
    public static Bitmap? RenderHead(byte[]? pngBytes)
        => Render(pngBytes, SkinLayout.HeadSize, SkinLayout.HeadSize, (_, _) => SkinLayout.BuildHead());

    public static Bitmap? RenderBody(byte[]? pngBytes, bool slim)
        => Render(pngBytes, SkinLayout.BodyWidth, SkinLayout.BodyHeight,
            (_, height) => SkinLayout.BuildBody(SkinLayout.IsLegacyTexture(height), slim));

    private static Bitmap? Render(
        byte[]? pngBytes,
        int targetWidth,
        int targetHeight,
        Func<int, int, IReadOnlyList<SkinDrawOp>> buildOps)
    {
        if (pngBytes is null || pngBytes.Length == 0)
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream(pngBytes, writable: false);
            using var source = WriteableBitmap.Decode(stream);

            var sourceWidth = source.PixelSize.Width;
            var sourceHeight = source.PixelSize.Height;
            if (!SkinLayout.IsSupportedTexture(sourceWidth, sourceHeight))
            {
                return null;
            }

            var sourcePixels = ReadPixels(source, out var sourceStride);
            var target = new uint[targetWidth * targetHeight];

            foreach (var op in buildOps(sourceWidth, sourceHeight))
            {
                Blit(op, sourcePixels, sourceStride / 4, sourceWidth, sourceHeight, target, targetWidth, targetHeight);
            }

            return WritePixels(target, targetWidth, targetHeight);
        }
        catch
        {
            // Битый или неожиданный файл скина не должен ронять кабинет.
            return null;
        }
    }

    private static uint[] ReadPixels(WriteableBitmap bitmap, out int stride)
    {
        using var buffer = bitmap.Lock();
        stride = buffer.RowBytes;
        var pixels = new uint[buffer.RowBytes / 4 * buffer.Size.Height];
        System.Runtime.InteropServices.Marshal.Copy(
            buffer.Address, (int[])(object)pixels, 0, pixels.Length);
        return pixels;
    }

    /// <summary>
    /// Копирует прямоугольник текстуры в целевую картинку с учётом прозрачности:
    /// второй слой скина накладывается поверх первого, а не затирает его.
    /// </summary>
    private static void Blit(
        SkinDrawOp op,
        uint[] source,
        int sourceStridePixels,
        int sourceWidth,
        int sourceHeight,
        uint[] target,
        int targetWidth,
        int targetHeight)
    {
        for (var y = 0; y < op.Height; y++)
        {
            var sy = op.SourceY + y;
            var ty = op.TargetY + y;
            if (sy < 0 || sy >= sourceHeight || ty < 0 || ty >= targetHeight)
            {
                continue;
            }

            for (var x = 0; x < op.Width; x++)
            {
                var sx = op.Mirrored ? op.SourceX + op.Width - 1 - x : op.SourceX + x;
                var tx = op.TargetX + x;
                if (sx < 0 || sx >= sourceWidth || tx < 0 || tx >= targetWidth)
                {
                    continue;
                }

                var pixel = source[sy * sourceStridePixels + sx];
                // Полностью прозрачные пиксели пропускаем: иначе второй слой стирал бы первый.
                if ((pixel >> 24) == 0)
                {
                    continue;
                }

                target[ty * targetWidth + tx] = pixel;
            }
        }
    }

    private static Bitmap WritePixels(uint[] pixels, int width, int height)
    {
        var bitmap = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);

        using var buffer = bitmap.Lock();
        System.Runtime.InteropServices.Marshal.Copy(
            (int[])(object)pixels, 0, buffer.Address, pixels.Length);
        return bitmap;
    }
}
