using System.Buffers.Binary;
using System.Text;
using SoundPreviewer.Core.Models;
using SoundPreviewer.Core.Audio;

namespace SoundPreviewer.Vst2Host;

internal sealed record FxpPreset(int PluginId, byte[]? Chunk, float[]? Parameters, string ProgramName, uint PluginVersion = 0)
{
    internal int ProgramIndex(int programCount)
    {
        // This single-program Sylenth1 layout stores the selected slot in its state.
        // Do not interpret offsets in opaque chunks from other formats or versions.
        if (PluginId != 0x73796c31 || Chunk is not { Length: 1028 } || !Chunk.AsSpan(0, 4).SequenceEqual("1lys"u8)) return 0;
        int index = BinaryPrimitives.ReadInt32LittleEndian(Chunk.AsSpan(16, 4));
        if (index < 0 || index >= programCount)
            throw new InvalidDataException("Sylenth1 音色中的程序位置超出实际插件范围，拒绝加载。");
        return index;
    }

    public static FxpPreset Read(RenderRequest request)
    {
        if (request.Preset.Synth is not (SynthKind.Sylenth1 or SynthKind.Nexus2) || request.Plugin.Synth != request.Preset.Synth)
            throw new NotSupportedException("32 位内置宿主仅适配身份匹配的 Sylenth1 / Nexus 2 独立 FXP。");
        if (request.Preset.InternalPath.Length > 0 || request.Preset.BankSlot is not null || !Path.GetExtension(request.Preset.SourcePath).Equals(".fxp", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("只支持独立 FXP；银行、银行条目和其他格式不进入宿主。");
        var file = new FileInfo(request.Preset.SourcePath);
        if (!file.Exists || file.Length is < 56 or > 64 * 1024 * 1024) throw new InvalidDataException("FXP 文件不存在或大小不受支持。");
        byte[] bytes = File.ReadAllBytes(file.FullName);
        if (!string.Equals(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)), request.Preset.ContentHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("FXP 内容与索引 SHA256 不一致，请重新扫描。");
        if (!bytes.AsSpan(0, 4).SequenceEqual("CcnK"u8)) throw new InvalidDataException("FXP 缺少 CcnK 文件头。");
        uint size = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(4, 4));
        int id = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4));
        bool nexus = request.Preset.Synth == SynthKind.Nexus2;
        uint version = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4));
        bool legacyTrailer = nexus && bytes.Length == 61 + NexusPresetFormat.LegacyChunkBytes &&
            bytes.AsSpan(8, 4).SequenceEqual("FPCh"u8) &&
            BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(56, 4)) == NexusPresetFormat.LegacyChunkBytes &&
            NexusPresetFormat.IsLegacy(version, NexusPresetFormat.LegacyChunkBytes) &&
            (size == bytes.Length - 9 || size == bytes.Length - 1);
        if (legacyTrailer) bytes = bytes[..^1]; // Hash above binds the complete, untouched source file.
        if (size != bytes.Length - 8 && size != bytes.Length) throw new InvalidDataException("FXP 文件长度字段无效。");
        if (id != (nexus ? 0x4e455855 : 0x73796c31)) throw new InvalidDataException("FXP 插件 ID 与所选音源不匹配，拒绝加载。");
        int nameEnd = Array.IndexOf(bytes, (byte)0, 28, 28);
        string name = Encoding.Latin1.GetString(bytes, 28, nameEnd < 0 ? 28 : nameEnd - 28);
        if (bytes.AsSpan(8, 4).SequenceEqual("FPCh"u8))
        {
            if (bytes.Length <= 60 || BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(56, 4)) != bytes.Length - 60)
                throw new InvalidDataException("FXP chunk 数据长度无效。");
            byte[] chunk = bytes[60..];
            if (nexus && !NexusPresetFormat.IsSupported(version, chunk.Length, chunk))
                throw new InvalidDataException("Nexus 2 FXP 状态格式尚未核对，未交给插件加载。");
            return new(id, chunk, null, name, version);
        }
        if (nexus || !bytes.AsSpan(8, 4).SequenceEqual("FxCk"u8)) throw new InvalidDataException("此文件不是受支持的独立 FXP program。");
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

    public static void ValidatePlugin(string path, SynthKind synth)
    {
        if (!Path.GetExtension(path).Equals(".dll", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("需要 Windows x86 VST2 DLL。");
        if (synth == SynthKind.Nexus2)
        {
            var metadata = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
            if (!string.Equals(metadata.CompanyName?.Trim(), "reFX", StringComparison.OrdinalIgnoreCase) ||
                !(metadata.ProductName ?? "").Contains("Nexus", StringComparison.OrdinalIgnoreCase) ||
                (metadata.FileMajorPart > 0 ? metadata.FileMajorPart : metadata.ProductMajorPart) != 2)
                throw new InvalidDataException("所选插件尚未确认为 reFX Nexus 2，不能仅凭 NEXU 插件 ID 加载。");
        }
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
