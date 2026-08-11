using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Launcher.App.Services;

public sealed record MinecraftPingResult(int PlayersOnline, int PlayersMax, string Version);

/// <summary>
/// Прямой пинг Minecraft-сервера по протоколу Server List Ping (как делает сам клиент).
/// Каждый сервер отдаёт свой реальный онлайн — без бэкенда и модов. Резолвит SRV-запись
/// (_minecraft._tcp.host), т.к. сервера часто живут на нестандартном порту за общим IP.
/// </summary>
public sealed class MinecraftServerPinger
{
    public async Task<MinecraftPingResult?> PingAsync(string host, CancellationToken cancellationToken, int defaultPort = 25565)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return null;
        }

        try
        {
            var (targetHost, targetPort) = await ResolveAsync(host.Trim(), defaultPort, cancellationToken);
            return await PingDirectAsync(targetHost, targetPort, host.Trim(), cancellationToken);
        }
        catch
        {
            return null; // недоступен / не ответил — статус «оффлайн»
        }
    }

    private static async Task<(string host, int port)> ResolveAsync(string host, int defaultPort, CancellationToken cancellationToken)
    {
        var srv = await TryResolveSrvAsync(host, cancellationToken);
        return srv ?? (host, defaultPort);
    }

    // ── Minecraft Server List Ping (статус) ──────────────────────────────────────────────
    private static async Task<MinecraftPingResult?> PingDirectAsync(string connectHost, int port, string virtualHost, CancellationToken cancellationToken)
    {
        using var tcp = new TcpClient();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(4));

        await tcp.ConnectAsync(connectHost, port, cts.Token);
        await using var stream = tcp.GetStream();

        // Handshake: protocol=-1 (любой), адрес/порт, nextState=1 (status)
        using var handshake = new MemoryStream();
        WriteVarInt(handshake, 0x00);
        WriteVarInt(handshake, -1);
        WriteString(handshake, virtualHost);
        handshake.WriteByte((byte)(port >> 8));
        handshake.WriteByte((byte)(port & 0xFF));
        WriteVarInt(handshake, 1);
        await WritePacketAsync(stream, handshake.ToArray(), cts.Token);

        // Status request (пустой пакет 0x00)
        using var statusRequest = new MemoryStream();
        WriteVarInt(statusRequest, 0x00);
        await WritePacketAsync(stream, statusRequest.ToArray(), cts.Token);

        // Status response: [len][packetId=0][jsonLen][json]
        _ = await ReadVarIntAsync(stream, cts.Token); // длина пакета
        _ = await ReadVarIntAsync(stream, cts.Token); // packetId (0x00)
        var jsonLength = await ReadVarIntAsync(stream, cts.Token);
        if (jsonLength is <= 0 or > 2 * 1024 * 1024)
        {
            return null;
        }

        var jsonBytes = await ReadExactAsync(stream, jsonLength, cts.Token);
        using var document = JsonDocument.Parse(jsonBytes);
        var root = document.RootElement;

        var online = 0;
        var max = 0;
        if (root.TryGetProperty("players", out var players))
        {
            if (players.TryGetProperty("online", out var onlineEl) && onlineEl.TryGetInt32(out var o)) online = o;
            if (players.TryGetProperty("max", out var maxEl) && maxEl.TryGetInt32(out var m)) max = m;
        }

        var version = root.TryGetProperty("version", out var ver) && ver.TryGetProperty("name", out var verName)
            ? verName.GetString() ?? string.Empty
            : string.Empty;

        return new MinecraftPingResult(online, max, version);
    }

    private static async Task WritePacketAsync(Stream stream, byte[] data, CancellationToken cancellationToken)
    {
        using var lengthBuffer = new MemoryStream();
        WriteVarInt(lengthBuffer, data.Length);
        await stream.WriteAsync(lengthBuffer.ToArray(), cancellationToken);
        await stream.WriteAsync(data, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static void WriteVarInt(Stream stream, int value)
    {
        var v = (uint)value;
        while (true)
        {
            if ((v & ~0x7Fu) == 0)
            {
                stream.WriteByte((byte)v);
                return;
            }

            stream.WriteByte((byte)((v & 0x7F) | 0x80));
            v >>= 7;
        }
    }

    private static void WriteString(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteVarInt(stream, bytes.Length);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static async Task<int> ReadVarIntAsync(Stream stream, CancellationToken cancellationToken)
    {
        var numRead = 0;
        var result = 0;
        byte read;
        do
        {
            var buffer = await ReadExactAsync(stream, 1, cancellationToken);
            read = buffer[0];
            result |= (read & 0x7F) << (7 * numRead);
            numRead++;
            if (numRead > 5)
            {
                throw new InvalidDataException("VarInt is too big.");
            }
        }
        while ((read & 0x80) != 0);

        return result;
    }

    private static async Task<byte[]> ReadExactAsync(Stream stream, int count, CancellationToken cancellationToken)
    {
        var buffer = new byte[count];
        var read = 0;
        while (read < count)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read, count - read), cancellationToken);
            if (n == 0)
            {
                throw new EndOfStreamException();
            }

            read += n;
        }

        return buffer;
    }

    // ── DNS SRV (минимальный резолвер, без внешних зависимостей) ──────────────────────────
    private static async Task<(string host, int port)?> TryResolveSrvAsync(string host, CancellationToken cancellationToken)
    {
        var query = $"_minecraft._tcp.{host}";
        foreach (var dnsServer in GetDnsServers())
        {
            try
            {
                var result = await QuerySrvAsync(dnsServer, query, cancellationToken);
                if (result is not null)
                {
                    return result;
                }
            }
            catch
            {
                // следующий DNS-сервер
            }
        }

        return null;
    }

    private static IEnumerable<IPAddress> GetDnsServers()
    {
        var servers = new List<IPAddress>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                foreach (var dns in ni.GetIPProperties().DnsAddresses)
                {
                    if (dns.AddressFamily == AddressFamily.InterNetwork)
                    {
                        servers.Add(dns);
                    }
                }
            }
        }
        catch
        {
            // используем публичные резолверы ниже
        }

        servers.Add(IPAddress.Parse("1.1.1.1"));
        servers.Add(IPAddress.Parse("8.8.8.8"));
        return servers.Distinct();
    }

    private static async Task<(string host, int port)?> QuerySrvAsync(IPAddress dnsServer, string name, CancellationToken cancellationToken)
    {
        var queryBytes = BuildDnsQuery(name, 33); // 33 = SRV
        using var udp = new UdpClient(dnsServer.AddressFamily);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(2));

        await udp.SendAsync(queryBytes, queryBytes.Length, new IPEndPoint(dnsServer, 53));
        var response = await udp.ReceiveAsync(cts.Token);
        return ParseSrvResponse(response.Buffer);
    }

    private static byte[] BuildDnsQuery(string name, int qtype)
    {
        using var ms = new MemoryStream();
        var id = (ushort)Random.Shared.Next(0, ushort.MaxValue);
        ms.WriteByte((byte)(id >> 8));
        ms.WriteByte((byte)id);
        ms.WriteByte(0x01); // RD
        ms.WriteByte(0x00);
        ms.WriteByte(0x00); ms.WriteByte(0x01); // QDCOUNT=1
        ms.WriteByte(0x00); ms.WriteByte(0x00); // ANCOUNT
        ms.WriteByte(0x00); ms.WriteByte(0x00); // NSCOUNT
        ms.WriteByte(0x00); ms.WriteByte(0x00); // ARCOUNT

        foreach (var label in name.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            ms.WriteByte((byte)bytes.Length);
            ms.Write(bytes, 0, bytes.Length);
        }

        ms.WriteByte(0x00); // конец имени
        ms.WriteByte((byte)(qtype >> 8)); ms.WriteByte((byte)qtype);
        ms.WriteByte(0x00); ms.WriteByte(0x01); // QCLASS = IN
        return ms.ToArray();
    }

    private static (string host, int port)? ParseSrvResponse(byte[] buffer)
    {
        if (buffer.Length < 12)
        {
            return null;
        }

        var qdCount = (buffer[4] << 8) | buffer[5];
        var anCount = (buffer[6] << 8) | buffer[7];
        if (anCount == 0)
        {
            return null;
        }

        var offset = 12;
        for (var i = 0; i < qdCount; i++)
        {
            ReadName(buffer, ref offset);
            offset += 4; // QTYPE + QCLASS
        }

        (string host, int port, int priority)? best = null;
        for (var i = 0; i < anCount && offset + 10 <= buffer.Length; i++)
        {
            ReadName(buffer, ref offset);
            var type = (buffer[offset] << 8) | buffer[offset + 1];
            offset += 2; // type
            offset += 2; // class
            offset += 4; // ttl
            var rdLength = (buffer[offset] << 8) | buffer[offset + 1];
            offset += 2;
            var rdStart = offset;

            if (type == 33 && rdLength >= 6)
            {
                var priority = (buffer[offset] << 8) | buffer[offset + 1];
                var port = (buffer[offset + 4] << 8) | buffer[offset + 5];
                var nameOffset = offset + 6;
                var target = ReadName(buffer, ref nameOffset).TrimEnd('.');
                if (!string.IsNullOrWhiteSpace(target) && (best is null || priority < best.Value.priority))
                {
                    best = (target, port, priority);
                }
            }

            offset = rdStart + rdLength;
        }

        return best is null ? null : (best.Value.host, best.Value.port);
    }

    private static string ReadName(byte[] buffer, ref int offset)
    {
        var sb = new StringBuilder();
        var jumped = false;
        var position = offset;
        var safety = 0;

        while (safety++ < 128)
        {
            if (position >= buffer.Length)
            {
                break;
            }

            var length = buffer[position];
            if ((length & 0xC0) == 0xC0) // указатель (compression)
            {
                if (position + 1 >= buffer.Length)
                {
                    break;
                }

                var pointer = ((length & 0x3F) << 8) | buffer[position + 1];
                if (!jumped)
                {
                    offset = position + 2;
                }

                jumped = true;
                position = pointer;
                continue;
            }

            if (length == 0)
            {
                position += 1;
                if (!jumped)
                {
                    offset = position;
                }

                break;
            }

            position += 1;
            if (sb.Length > 0)
            {
                sb.Append('.');
            }

            sb.Append(Encoding.ASCII.GetString(buffer, position, length));
            position += length;
        }

        return sb.ToString();
    }
}
