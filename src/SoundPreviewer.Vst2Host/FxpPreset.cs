using System.Buffers.Binary;
using System.Text;
using SoundPreviewer.Core.Models;

namespace SoundPreviewer.Vst2Host;

internal sealed record FxpPreset(int PluginId, byte[]? Chunk, float[]? Parameters, string ProgramName)
{
    public static FxpPreset Read(RenderRequest request)
    {
        if (request.Preset.Synth != SynthKind.Sylenth1) throw new NotSupportedException("32 位内置宿主目前仅适配 Sylenth1 独立 FXP。");
        if (request.Preset.InternalPath.Length > 0 || request.Preset.BankSlot is not null || !Path.GetExtension(request.Preset.SourcePath).Equals(".fxp", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("只支持独立 FXP；银行、银行条目和其他格式不进入宿主。");
        var file = new FileInfo(request.Preset.SourcePath);
        if (!file.Exists || file.Length is < 56 or > 64 * 1024 * 1024) throw new InvalidDataException("FXP 文件不存在或大小不受支持。");
        byte[] bytes = File.ReadAllBytes(file.FullName);
        if (!string.Equals(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)), request.Preset.ContentHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("FXP 内容与索引 SHA256 不一致，请重新扫描。");
        if (!bytes.AsSpan(0, 4).SequenceEqual("CcnK"u8)) throw new InvalidDataException("FXP 缺少 CcnK 文件头。");
        uint size = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(4, 4));
        if (size != bytes.Length - 8 && size != bytes.Length) throw new InvalidDataException("FXP 文件长度字段无效。");
        int id = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4));
        if (!bytes.AsSpan(16, 4).SequenceEqual("syl1"u8)) throw new InvalidDataException("FXP 插件 ID 不是已核对的 Sylenth1 syl1，拒绝加载。");
        int nameEnd = Array.IndexOf(bytes, (byte)0, 28, 28);
        string name = Encoding.Latin1.GetString(bytes, 28, nameEnd < 0 ? 28 : nameEnd - 28);
        if (bytes.AsSpan(8, 4).SequenceEqual("FPCh"u8))
        {
            if (bytes.Length <= 60 || BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(56, 4)) != bytes.Length - 60)
                throw new InvalidDataException("FXP chunk 数据长度无效。");
            return new(id, bytes[60..], null, name);
        }
        if (!bytes.AsSpan(8, 4).SequenceEqual("FxCk"u8)) throw new InvalidDataException("此文件不是独立 FXP program。");
        uint count = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(24, 4));
        if (count == 0 || count > 65536 || 56L + count * 4L != bytes.Length) throw new InvalidDataException("FXP 参数表长度无效。");
        var parameters = new float[count];
        for (int i = 0; i < parameters.Length; i++)
        {
            parameters[i] = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(56 + i * 4, 4)));
            if (!float.IsFinite(parameters[i]) || parameters[i] is < 0 or > 1) throw new InvalidDataException("FXP 包含无效参数数值。");
        }
        return new(id, null, parameters, name);
    }

    public static void ValidatePlugin(string path)
    {
        if (!Path.GetExtension(path).Equals(".dll", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("需要 Windows x86 VST2 DLL。");
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        if (stream.Length < 64 || reader.ReadUInt16() != 0x5a4d) throw new InvalidDataException("插件缺少 MZ 文件头。");
        stream.Position = 0x3c;
        int offset = reader.ReadInt32();
        if (offset < 64 || offset > stream.Length - 6) throw new InvalidDataException("插件 PE 文件头无效。");
        stream.Position = offset;
        if (reader.ReadUInt32() != 0x00004550 || reader.ReadUInt16() != 0x014c) throw new InvalidDataException("此宿主只能加载 PE x86 插件，不能加载 x64 或 ARM 插件。");
    }
}
