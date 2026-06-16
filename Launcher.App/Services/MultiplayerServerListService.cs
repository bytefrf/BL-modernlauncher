using System.IO.Compression;
using System.Reflection;
using System.Text;

namespace Launcher.App.Services;

public static class MultiplayerServerListService
{
    private const string EmbeddedServersTemplateResourceName = "Launcher.App.Defaults.servers.dat";

    public static void EnsureServer(string installRoot, string serverName, string serverIp)
    {
        if (string.IsNullOrWhiteSpace(installRoot) ||
            string.IsNullOrWhiteSpace(serverName) ||
            string.IsNullOrWhiteSpace(serverIp))
        {
            return;
        }

        var serversPath = Path.Combine(installRoot, "servers.dat");
        ServerListNbt root;

        if (File.Exists(serversPath))
        {
            try
            {
                root = ReadServerList(serversPath);
            }
            catch
            {
                root = TryReadEmbeddedTemplate() ?? new ServerListNbt();
            }
        }
        else
        {
            root = TryReadEmbeddedTemplate() ?? new ServerListNbt();
        }

        var existing = root.Servers.FirstOrDefault(server =>
            server.TryGetValue("ip", out var ipTag) &&
            ipTag is NbtString ip &&
            ip.Value.Equals(serverIp, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            existing["name"] = new NbtString(serverName);
        }
        else
        {
            root.Servers.Add(new Dictionary<string, NbtTag>
            {
                ["name"] = new NbtString(serverName),
                ["ip"] = new NbtString(serverIp)
            });
        }

        Directory.CreateDirectory(installRoot);
        WriteServerList(serversPath, root);
    }

    private static ServerListNbt? TryReadEmbeddedTemplate()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(EmbeddedServersTemplateResourceName);
            if (stream is null)
            {
                return null;
            }

            var tempPath = Path.Combine(Path.GetTempPath(), $"servers-template-{Guid.NewGuid():N}.dat");
            try
            {
                using (var file = File.Create(tempPath))
                {
                    stream.CopyTo(file);
                }

                return ReadServerList(tempPath);
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        }
        catch
        {
            return null;
        }
    }

    private static ServerListNbt ReadServerList(string path)
    {
        using var file = File.OpenRead(path);
        using var reader = CreateNbtReader(file);

        var rootType = reader.ReadByte();
        if (rootType != (byte)NbtType.Compound)
        {
            throw new InvalidDataException("servers.dat root tag must be a compound.");
        }

        _ = ReadString(reader); // root name
        var root = ReadCompoundPayload(reader);

        if (!root.TryGetValue("servers", out var serversTag) || serversTag is not NbtList list || list.ItemType != NbtType.Compound)
        {
            return new ServerListNbt();
        }

        var servers = list.Items
            .OfType<NbtCompound>()
            .Select(item => item.Value)
            .ToList();

        return new ServerListNbt { Servers = servers };
    }

    private static void WriteServerList(string path, ServerListNbt root)
    {
        using var file = File.Create(path);
        using var writer = new BinaryWriter(file, Encoding.UTF8, leaveOpen: false);

        writer.Write((byte)NbtType.Compound);
        WriteString(writer, string.Empty);

        var payload = new Dictionary<string, NbtTag>
        {
            ["servers"] = new NbtList(
                NbtType.Compound,
                root.Servers.Select(server => (NbtTag)new NbtCompound(server)).ToList())
        };

        WriteCompoundPayload(writer, payload);
    }

    private static BinaryReader CreateNbtReader(FileStream file)
    {
        if (file.Length >= 2)
        {
            var first = file.ReadByte();
            var second = file.ReadByte();
            file.Position = 0;

            if (first == 0x1F && second == 0x8B)
            {
                var gzip = new GZipStream(file, CompressionMode.Decompress);
                return new BinaryReader(gzip, Encoding.UTF8, leaveOpen: false);
            }
        }

        return new BinaryReader(file, Encoding.UTF8, leaveOpen: false);
    }

    private static Dictionary<string, NbtTag> ReadCompoundPayload(BinaryReader reader)
    {
        var compound = new Dictionary<string, NbtTag>(StringComparer.Ordinal);

        while (true)
        {
            var type = (NbtType)reader.ReadByte();
            if (type == NbtType.End)
            {
                return compound;
            }

            var name = ReadString(reader);
            compound[name] = ReadTagPayload(reader, type);
        }
    }

    private static NbtTag ReadTagPayload(BinaryReader reader, NbtType type)
    {
        return type switch
        {
            NbtType.Byte => new NbtByte(reader.ReadByte()),
            NbtType.Short => new NbtShort(ReadInt16BigEndian(reader)),
            NbtType.Int => new NbtInt(ReadInt32BigEndian(reader)),
            NbtType.Long => new NbtLong(ReadInt64BigEndian(reader)),
            NbtType.Float => new NbtFloat(ReadSingleBigEndian(reader)),
            NbtType.Double => new NbtDouble(ReadDoubleBigEndian(reader)),
            NbtType.ByteArray => new NbtByteArray(reader.ReadBytes(ReadInt32BigEndian(reader))),
            NbtType.String => new NbtString(ReadString(reader)),
            NbtType.List => ReadListPayload(reader),
            NbtType.Compound => new NbtCompound(ReadCompoundPayload(reader)),
            NbtType.IntArray => ReadIntArrayPayload(reader),
            NbtType.LongArray => ReadLongArrayPayload(reader),
            _ => throw new InvalidDataException($"Unsupported NBT tag type: {type}.")
        };
    }

    private static NbtList ReadListPayload(BinaryReader reader)
    {
        var itemType = (NbtType)reader.ReadByte();
        var length = ReadInt32BigEndian(reader);
        var items = new List<NbtTag>(length);

        for (var index = 0; index < length; index++)
        {
            items.Add(ReadTagPayload(reader, itemType));
        }

        return new NbtList(itemType, items);
    }

    private static NbtIntArray ReadIntArrayPayload(BinaryReader reader)
    {
        var length = ReadInt32BigEndian(reader);
        var values = new int[length];
        for (var index = 0; index < length; index++)
        {
            values[index] = ReadInt32BigEndian(reader);
        }

        return new NbtIntArray(values);
    }

    private static NbtLongArray ReadLongArrayPayload(BinaryReader reader)
    {
        var length = ReadInt32BigEndian(reader);
        var values = new long[length];
        for (var index = 0; index < length; index++)
        {
            values[index] = ReadInt64BigEndian(reader);
        }

        return new NbtLongArray(values);
    }

    private static void WriteCompoundPayload(BinaryWriter writer, Dictionary<string, NbtTag> compound)
    {
        foreach (var entry in compound)
        {
            writer.Write((byte)entry.Value.Type);
            WriteString(writer, entry.Key);
            WriteTagPayload(writer, entry.Value);
        }

        writer.Write((byte)NbtType.End);
    }

    private static void WriteTagPayload(BinaryWriter writer, NbtTag tag)
    {
        switch (tag)
        {
            case NbtByte value:
                writer.Write(value.Value);
                break;
            case NbtShort value:
                WriteInt16BigEndian(writer, value.Value);
                break;
            case NbtInt value:
                WriteInt32BigEndian(writer, value.Value);
                break;
            case NbtLong value:
                WriteInt64BigEndian(writer, value.Value);
                break;
            case NbtFloat value:
                WriteSingleBigEndian(writer, value.Value);
                break;
            case NbtDouble value:
                WriteDoubleBigEndian(writer, value.Value);
                break;
            case NbtByteArray value:
                WriteInt32BigEndian(writer, value.Value.Length);
                writer.Write(value.Value);
                break;
            case NbtString value:
                WriteString(writer, value.Value);
                break;
            case NbtList value:
                writer.Write((byte)value.ItemType);
                WriteInt32BigEndian(writer, value.Items.Count);
                foreach (var item in value.Items)
                {
                    WriteTagPayload(writer, item);
                }

                break;
            case NbtCompound value:
                WriteCompoundPayload(writer, value.Value);
                break;
            case NbtIntArray value:
                WriteInt32BigEndian(writer, value.Value.Length);
                foreach (var item in value.Value)
                {
                    WriteInt32BigEndian(writer, item);
                }

                break;
            case NbtLongArray value:
                WriteInt32BigEndian(writer, value.Value.Length);
                foreach (var item in value.Value)
                {
                    WriteInt64BigEndian(writer, item);
                }

                break;
            default:
                throw new InvalidDataException($"Unsupported NBT tag instance: {tag.GetType().Name}.");
        }
    }

    private static string ReadString(BinaryReader reader)
    {
        var length = ReadInt16BigEndian(reader);
        var bytes = reader.ReadBytes(length);
        return Encoding.UTF8.GetString(bytes);
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        WriteInt16BigEndian(writer, checked((short)bytes.Length));
        writer.Write(bytes);
    }

    private static short ReadInt16BigEndian(BinaryReader reader)
    {
        var bytes = reader.ReadBytes(2);
        if (bytes.Length != 2)
        {
            throw new EndOfStreamException();
        }

        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(bytes);
        }

        return BitConverter.ToInt16(bytes, 0);
    }

    private static int ReadInt32BigEndian(BinaryReader reader)
    {
        var bytes = reader.ReadBytes(4);
        if (bytes.Length != 4)
        {
            throw new EndOfStreamException();
        }

        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(bytes);
        }

        return BitConverter.ToInt32(bytes, 0);
    }

    private static long ReadInt64BigEndian(BinaryReader reader)
    {
        var bytes = reader.ReadBytes(8);
        if (bytes.Length != 8)
        {
            throw new EndOfStreamException();
        }

        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(bytes);
        }

        return BitConverter.ToInt64(bytes, 0);
    }

    private static float ReadSingleBigEndian(BinaryReader reader)
    {
        var bytes = reader.ReadBytes(4);
        if (bytes.Length != 4)
        {
            throw new EndOfStreamException();
        }

        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(bytes);
        }

        return BitConverter.ToSingle(bytes, 0);
    }

    private static double ReadDoubleBigEndian(BinaryReader reader)
    {
        var bytes = reader.ReadBytes(8);
        if (bytes.Length != 8)
        {
            throw new EndOfStreamException();
        }

        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(bytes);
        }

        return BitConverter.ToDouble(bytes, 0);
    }

    private static void WriteInt16BigEndian(BinaryWriter writer, short value)
    {
        var bytes = BitConverter.GetBytes(value);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(bytes);
        }

        writer.Write(bytes);
    }

    private static void WriteInt32BigEndian(BinaryWriter writer, int value)
    {
        var bytes = BitConverter.GetBytes(value);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(bytes);
        }

        writer.Write(bytes);
    }

    private static void WriteInt64BigEndian(BinaryWriter writer, long value)
    {
        var bytes = BitConverter.GetBytes(value);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(bytes);
        }

        writer.Write(bytes);
    }

    private static void WriteSingleBigEndian(BinaryWriter writer, float value)
    {
        var bytes = BitConverter.GetBytes(value);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(bytes);
        }

        writer.Write(bytes);
    }

    private static void WriteDoubleBigEndian(BinaryWriter writer, double value)
    {
        var bytes = BitConverter.GetBytes(value);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(bytes);
        }

        writer.Write(bytes);
    }

    private sealed class ServerListNbt
    {
        public List<Dictionary<string, NbtTag>> Servers { get; init; } = [];
    }

    private enum NbtType : byte
    {
        End = 0,
        Byte = 1,
        Short = 2,
        Int = 3,
        Long = 4,
        Float = 5,
        Double = 6,
        ByteArray = 7,
        String = 8,
        List = 9,
        Compound = 10,
        IntArray = 11,
        LongArray = 12
    }

    private abstract record NbtTag(NbtType Type);
    private sealed record NbtByte(byte Value) : NbtTag(NbtType.Byte);
    private sealed record NbtShort(short Value) : NbtTag(NbtType.Short);
    private sealed record NbtInt(int Value) : NbtTag(NbtType.Int);
    private sealed record NbtLong(long Value) : NbtTag(NbtType.Long);
    private sealed record NbtFloat(float Value) : NbtTag(NbtType.Float);
    private sealed record NbtDouble(double Value) : NbtTag(NbtType.Double);
    private sealed record NbtByteArray(byte[] Value) : NbtTag(NbtType.ByteArray);
    private sealed record NbtString(string Value) : NbtTag(NbtType.String);
    private sealed record NbtList(NbtType ItemType, List<NbtTag> Items) : NbtTag(NbtType.List);
    private sealed record NbtCompound(Dictionary<string, NbtTag> Value) : NbtTag(NbtType.Compound);
    private sealed record NbtIntArray(int[] Value) : NbtTag(NbtType.IntArray);
    private sealed record NbtLongArray(long[] Value) : NbtTag(NbtType.LongArray);
}
