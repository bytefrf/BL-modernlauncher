using System.Buffers.Binary;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Launcher.App.Services;

/// <summary>
/// Минимальный клиент Discord Rich Presence поверх локального канала (discord-ipc-*).
/// Не требует внешних зависимостей и тихо отключается, если Discord не запущен
/// или Application ID не задан.
/// </summary>
/// <remarks>
/// Транспорт зависит от системы: на Windows это именованный канал, на Linux и macOS —
/// unix-сокет в рантайм-каталоге. Именованные каналы .NET на Unix лежат в своём месте
/// и с сокетом Discord не совпадают, поэтому одной реализацией не обойтись.
/// </remarks>
public sealed class DiscordPresenceService : IDisposable
{
    private const int OpHandshake = 0;
    private const int OpFrame = 1;
    private const int OpClose = 2;

    private readonly string _clientId;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly long _startUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private readonly string _largeImageKey;
    private readonly string _largeImageText;

    private Stream? _pipe;
    private bool _connected;
    private bool _disposed;

    public DiscordPresenceService(string clientId, string largeImageKey = "logo", string largeImageText = "BL-modern TFGM")
    {
        _clientId = clientId?.Trim() ?? string.Empty;
        _largeImageKey = largeImageKey;
        _largeImageText = largeImageText;
    }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_clientId)
        && !_clientId.StartsWith("REPLACE", StringComparison.OrdinalIgnoreCase);

    public bool IsConnected => _connected;

    /// <summary>Пытается подключиться к Discord. Возвращает false, если Discord не запущен / ID не задан.</summary>
    public async Task<bool> TryConnectAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured || _disposed)
        {
            return false;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_connected)
            {
                return true;
            }

            for (var index = 0; index < 10; index++)
            {
                try
                {
                    _pipe = await OpenTransportAsync(index, cancellationToken);
                    if (_pipe is null)
                    {
                        continue;
                    }

                    var handshake = JsonSerializer.Serialize(new { v = 1, client_id = _clientId });
                    await WriteFrameAsync(OpHandshake, handshake, cancellationToken);
                    await DrainFrameAsync(cancellationToken); // READY (игнорируем содержимое)

                    _connected = true;
                    return true;
                }
                catch
                {
                    SafeDisposePipe();
                }
            }

            return false;
        }
        catch
        {
            SafeDisposePipe();
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Устанавливает статус. Если соединение потеряно — пытается переподключиться.</summary>
    public async Task SetPresenceAsync(string details, string state, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured || _disposed)
        {
            return;
        }

        if (!_connected && !await TryConnectAsync(cancellationToken))
        {
            return;
        }

        object activity = string.IsNullOrWhiteSpace(_largeImageKey)
            ? new
            {
                details,
                state,
                timestamps = new { start = _startUnixSeconds }
            }
            : new
            {
                details,
                state,
                timestamps = new { start = _startUnixSeconds },
                assets = new { large_image = _largeImageKey, large_text = _largeImageText }
            };

        var payload = JsonSerializer.Serialize(new
        {
            cmd = "SET_ACTIVITY",
            nonce = Guid.NewGuid().ToString(),
            args = new
            {
                pid = Environment.ProcessId,
                activity
            }
        });

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await WriteFrameAsync(OpFrame, payload, cancellationToken);
            await DrainFrameAsync(cancellationToken);
        }
        catch
        {
            // Соединение разорвано (Discord закрыли) — сбрасываем, переподключимся при следующем вызове.
            SafeDisposePipe();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Открывает канал к Discord под номером <paramref name="index"/> или возвращает
    /// <c>null</c>, если такого канала нет.
    /// </summary>
    private static async Task<Stream?> OpenTransportAsync(int index, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            var pipe = new NamedPipeClientStream(".", $"discord-ipc-{index}", PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(1000, cancellationToken);
            return pipe;
        }

        foreach (var path in EnumerateUnixSocketPaths(index))
        {
            try
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                // Сокет есть, но не отвечает (Discord закрывается) — пробуем следующий путь.
            }
        }

        return null;
    }

    /// <summary>
    /// Где Discord держит сокет на Linux и macOS. Помимо рантайм-каталога проверяем подпапки
    /// Flatpak и Snap: в этих упаковках сокет лежит НЕ в корне, и без них Rich Presence
    /// просто молчит у части игроков.
    /// </summary>
    private static IEnumerable<string> EnumerateUnixSocketPaths(int index)
    {
        var roots = new[]
        {
            Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR"),
            Environment.GetEnvironmentVariable("TMPDIR"),
            Environment.GetEnvironmentVariable("TMP"),
            Environment.GetEnvironmentVariable("TEMP"),
            "/tmp"
        };

        var subdirectories = new[]
        {
            string.Empty,
            "app/com.discordapp.Discord",
            "app/com.discordapp.DiscordCanary",
            "snap.discord",
            "snap.discord-canary"
        };

        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            foreach (var subdirectory in subdirectories)
            {
                yield return Path.Combine(root.TrimEnd('/'), subdirectory, $"discord-ipc-{index}");
            }
        }
    }

    private async Task WriteFrameAsync(int opcode, string json, CancellationToken cancellationToken)
    {
        if (_pipe is null)
        {
            throw new InvalidOperationException("Discord pipe is not connected.");
        }

        var data = Encoding.UTF8.GetBytes(json);
        var buffer = new byte[8 + data.Length];
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(0, 4), opcode);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4, 4), data.Length);
        data.CopyTo(buffer.AsSpan(8));

        await _pipe.WriteAsync(buffer, cancellationToken);
        await _pipe.FlushAsync(cancellationToken);
    }

    private async Task DrainFrameAsync(CancellationToken cancellationToken)
    {
        if (_pipe is null)
        {
            return;
        }

        var header = new byte[8];
        if (!await ReadExactAsync(header, cancellationToken))
        {
            return;
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4));
        if (length is <= 0 or > 64 * 1024)
        {
            return;
        }

        var payload = new byte[length];
        await ReadExactAsync(payload, cancellationToken);
    }

    private async Task<bool> ReadExactAsync(byte[] buffer, CancellationToken cancellationToken)
    {
        if (_pipe is null)
        {
            return false;
        }

        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await _pipe.ReadAsync(buffer.AsMemory(offset), cancellationToken);
            if (read == 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }

    private void SafeDisposePipe()
    {
        _connected = false;
        try
        {
            _pipe?.Dispose();
        }
        catch
        {
            // ignore
        }
        finally
        {
            _pipe = null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            if (_pipe is not null)
            {
                var close = JsonSerializer.Serialize(new { v = 1, client_id = _clientId });
                WriteFrameAsync(OpClose, close, CancellationToken.None).GetAwaiter().GetResult();
            }
        }
        catch
        {
            // ignore
        }

        SafeDisposePipe();
        _gate.Dispose();
    }
}
