using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Launcher.App.Services;

/// <summary>
/// Короткие интерфейсные звуки. Звук синтезируется в память (16-bit PCM WAV), поэтому
/// в репозитории и в релизе нет бинарных ассетов, а single-file публикация не ломается.
/// Любая ошибка воспроизведения глушится: звук — украшение, он не должен мешать игре.
/// </summary>
/// <remarks>
/// Кроссплатформенность: <c>System.Media.SoundPlayer</c> есть только на Windows, поэтому
/// на Windows зовём <c>winmm.dll</c> напрямую, а на Linux и macOS — системный проигрыватель
/// (<c>paplay</c>/<c>aplay</c>/<c>afplay</c>). Нет ни одного из них — просто тишина.
/// </remarks>
public sealed class LauncherSoundService
{
    private const int SampleRate = 44100;

    /// <summary>Выключатель из настроек. Ставится при загрузке настроек и при их сохранении.</summary>
    public bool Enabled { get; set; } = true;

    private readonly Lazy<byte[]> _achievement = new(() => BuildChime([(659.25, 0.0), (880.0, 0.10), (1174.66, 0.20)], 0.75));
    private readonly Lazy<byte[]> _ready = new(() => BuildChime([(587.33, 0.0), (880.0, 0.09)], 0.45));

    public void PlayAchievement() => Play(_achievement);

    public void PlayReady() => Play(_ready);

    [DllImport("winmm.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool PlaySound(string? sound, IntPtr module, uint flags);

    private const uint SndFilename = 0x00020000;
    private const uint SndAsync = 0x0001;
    private const uint SndNoDefault = 0x0002;

    // Проигрыватели Unix в порядке предпочтения. Первый найденный и используется.
    private static readonly (string File, string Args)[] UnixPlayers =
    [
        ("afplay", "\"{0}\""),          // macOS, входит в систему
        ("paplay", "\"{0}\""),          // PulseAudio / PipeWire — на большинстве десктопных Linux
        ("aplay", "-q \"{0}\""),        // ALSA напрямую
        ("ffplay", "-nodisp -autoexit -loglevel quiet \"{0}\"")
    ];

    private void Play(Lazy<byte[]> sound)
    {
        if (!Enabled)
        {
            return;
        }

        // В фоне: проигрывание не должно задерживать интерфейс.
        _ = Task.Run(() =>
        {
            try
            {
                // И winmm, и проигрыватели Unix читают ФАЙЛ, а не поток. Пишем во временный
                // и переиспользуем: звуки короткие и постоянные, писать их каждый раз незачем.
                var path = EnsureFile(sound);

                if (OperatingSystem.IsWindows())
                {
                    PlaySound(path, IntPtr.Zero, SndFilename | SndAsync | SndNoDefault);
                    return;
                }

                PlayUnix(path);
            }
            catch
            {
                // Нет звуковой карты, нет проигрывателя, занят выход — молча пропускаем.
            }
        });
    }

    private readonly Dictionary<Lazy<byte[]>, string> _files = [];

    private string EnsureFile(Lazy<byte[]> sound)
    {
        lock (_files)
        {
            if (_files.TryGetValue(sound, out var existing) && File.Exists(existing))
            {
                return existing;
            }

            var path = Path.Combine(Path.GetTempPath(), $"bl-launcher-{Guid.NewGuid():N}.wav");
            File.WriteAllBytes(path, sound.Value);
            _files[sound] = path;
            return path;
        }
    }

    private static void PlayUnix(string path)
    {
        foreach (var (file, argsFormat) in UnixPlayers)
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo(file, string.Format(argsFormat, path))
                {
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                });

                if (process is not null)
                {
                    return;
                }
            }
            catch
            {
                // Этого проигрывателя в системе нет — пробуем следующий.
            }
        }
    }

    /// <summary>
    /// Собирает мягкий перезвон: несколько нот со сдвигом по времени, у каждой — затухающая
    /// огибающая и лёгкая вторая гармоника (иначе синус звучит «пищаще»).
    /// </summary>
    private static byte[] BuildChime(IReadOnlyList<(double Frequency, double StartSeconds)> notes, double totalSeconds)
    {
        var sampleCount = (int)(SampleRate * totalSeconds);
        var samples = new double[sampleCount];

        foreach (var (frequency, startSeconds) in notes)
        {
            var start = (int)(startSeconds * SampleRate);
            var noteLength = sampleCount - start;
            if (noteLength <= 0)
            {
                continue;
            }

            for (var i = 0; i < noteLength; i++)
            {
                var t = i / (double)SampleRate;
                // Экспоненциальное затухание + короткая атака, чтобы не было щелчка на старте.
                var decay = Math.Exp(-3.2 * t);
                var attack = Math.Min(1.0, i / (SampleRate * 0.006));
                var wave = Math.Sin(2 * Math.PI * frequency * t)
                           + 0.28 * Math.Sin(4 * Math.PI * frequency * t);
                samples[start + i] += wave * decay * attack * 0.30;
            }
        }

        return EncodeWav(samples);
    }

    private static byte[] EncodeWav(double[] samples)
    {
        var dataBytes = samples.Length * 2;
        using var buffer = new MemoryStream(44 + dataBytes);
        using var writer = new BinaryWriter(buffer);

        writer.Write("RIFF"u8.ToArray());
        writer.Write(36 + dataBytes);
        writer.Write("WAVE"u8.ToArray());
        writer.Write("fmt "u8.ToArray());
        writer.Write(16);               // размер fmt-блока
        writer.Write((short)1);         // PCM
        writer.Write((short)1);         // моно
        writer.Write(SampleRate);
        writer.Write(SampleRate * 2);   // байт в секунду
        writer.Write((short)2);         // выравнивание блока
        writer.Write((short)16);        // бит на сэмпл
        writer.Write("data"u8.ToArray());
        writer.Write(dataBytes);

        foreach (var sample in samples)
        {
            writer.Write((short)(Math.Clamp(sample, -1.0, 1.0) * short.MaxValue));
        }

        writer.Flush();
        return buffer.ToArray();
    }
}
