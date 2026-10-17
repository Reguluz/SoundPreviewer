using System.Buffers.Binary;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace SoundPreviewer.Core.Scanning;

// Reads only the root's string values. No plist conversion process or bundle executable is launched.
internal static class MacBundleMetadata
{
    private const int MaximumBytes = 4 * 1024 * 1024;

    public static Dictionary<string, string> Read(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (file.Length is < 8 or > MaximumBytes) throw new InvalidDataException("Info.plist 大小无效或超过 4 MB 限制。");
        byte[] data = new byte[(int)file.Length];
        file.ReadExactly(data);
        if (data.AsSpan(0, 8).SequenceEqual("bplist00"u8)) return ReadBinary(data);
        try
        {
            using var source = new MemoryStream(data, false);
            using var reader = XmlReader.Create(source, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, MaxCharactersInDocument = MaximumBytes
            });
            var document = XDocument.Load(reader);
            var root = document.Root;
            if (root?.Name != "plist" || root.Elements().SingleOrDefault()?.Name != "dict")
                throw new InvalidDataException("Info.plist 根节点必须是字典。");
            var children = root.Element("dict")!.Elements().ToArray();
            if (children.Length % 2 != 0 || children.Length > 8192) throw new InvalidDataException("Info.plist 字典无效或过大。");
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < children.Length; i += 2)
            {
                if (children[i].Name != "key") throw new InvalidDataException("Info.plist 字典缺少键名。");
                string key = children[i].Value;
                string value = children[i + 1].Name == "string" ? children[i + 1].Value : "";
                if (!values.TryAdd(key, value) && values[key] != value) throw new InvalidDataException("Info.plist 重复键名的值不一致。");
            }
            return values;
        }
        catch (XmlException error) { throw new InvalidDataException("Info.plist XML 无效。", error); }
        catch (InvalidOperationException error) { throw new InvalidDataException("Info.plist XML 字典无效。", error); }
    }

    private static Dictionary<string, string> ReadBinary(byte[] data)
    {
        if (data.Length < 40) throw new InvalidDataException("二进制 Info.plist 截断。");
        int trailer = data.Length - 32;
        int offsetSize = data[trailer + 6], referenceSize = data[trailer + 7];
        ulong count = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(trailer + 8));
        ulong root = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(trailer + 16));
        ulong table = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(trailer + 24));
        if (offsetSize is < 1 or > 8 || referenceSize is < 1 or > 8 || count is 0 or > 65536 || root >= count ||
            table < 8 || table > (ulong)trailer || count > ((ulong)trailer - table) / (uint)offsetSize)
            throw new InvalidDataException("二进制 Info.plist 对象表无效。");
        int limit = (int)table;
        ulong Number(int position, int size, int boundary)
        {
            if (position < 0 || size is < 1 or > 8 || position > boundary - size)
                throw new InvalidDataException("二进制 Info.plist 地址越界。");
            ulong value = 0;
            for (int i = 0; i < size; i++) value = (value << 8) | data[position + i];
            return value;
        }
        int ObjectOffset(ulong reference)
        {
            if (reference >= count) throw new InvalidDataException("二进制 Info.plist 对象引用越界。");
            ulong offset = Number(checked((int)(table + reference * (uint)offsetSize)), offsetSize, trailer);
            if (offset < 8 || offset >= table) throw new InvalidDataException("二进制 Info.plist 对象地址越界。");
            return (int)offset;
        }
        (int Start, int Length) Payload(int position)
        {
            int length = data[position++] & 15;
            if (length == 15)
            {
                if (position >= limit || (data[position] >> 4) != 1 || (data[position] & 15) > 3)
                    throw new InvalidDataException("二进制 Info.plist 长度编码无效。");
                int size = 1 << (data[position++] & 15);
                ulong extended = Number(position, size, limit);
                if (extended > MaximumBytes) throw new InvalidDataException("二进制 Info.plist 内容过大。");
                length = (int)extended;
                position += size;
            }
            return (position, length);
        }
        string? String(ulong reference)
        {
            int position = ObjectOffset(reference), type = data[position] >> 4;
            if (type is not (5 or 6)) return null;
            var payload = Payload(position);
            int bytes = checked(payload.Length * (type == 6 ? 2 : 1));
            if (payload.Start > limit - bytes) throw new InvalidDataException("二进制 Info.plist 字符串截断。");
            return (type == 6 ? Encoding.BigEndianUnicode : Encoding.ASCII).GetString(data, payload.Start, bytes);
        }
        int dictionary = ObjectOffset(root);
        if ((data[dictionary] >> 4) != 13) throw new InvalidDataException("二进制 Info.plist 根节点必须是字典。");
        var entries = Payload(dictionary);
        if (entries.Length > 4096 || entries.Start > limit - entries.Length * referenceSize * 2)
            throw new InvalidDataException("二进制 Info.plist 字典截断或过大。");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < entries.Length; i++)
        {
            string key = String(Number(entries.Start + i * referenceSize, referenceSize, limit)) ??
                throw new InvalidDataException("二进制 Info.plist 字典键必须是字符串。");
            string value = String(Number(entries.Start + (entries.Length + i) * referenceSize, referenceSize, limit)) ?? "";
            if (!result.TryAdd(key, value) && result[key] != value) throw new InvalidDataException("二进制 Info.plist 重复键名的值不一致。");
        }
        return result;
    }
}
