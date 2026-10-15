using System.Buffers.Binary;
using System.Security.Cryptography;
using SoundPreviewer.Core.Models;

namespace SoundPreviewer.Vst2Host;

internal static class HostSelfTest
{
    public static void Run()
    {
        VstRenderer.CheckBufferInterop();
        string path = Path.Combine(Path.GetTempPath(), "SoundPreviewer-vst2-self-test-" + Guid.NewGuid().ToString("N") + ".fxp");
        try
        {
            byte[] fxp = new byte[64];
            "CcnK"u8.CopyTo(fxp);
            BinaryPrimitives.WriteUInt32BigEndian(fxp.AsSpan(4), 56);
            "FPCh"u8.CopyTo(fxp.AsSpan(8));
            BinaryPrimitives.WriteUInt32BigEndian(fxp.AsSpan(12), 1);
            "syl1"u8.CopyTo(fxp.AsSpan(16));
            "Test\0old name"u8.CopyTo(fxp.AsSpan(28));
            BinaryPrimitives.WriteUInt32BigEndian(fxp.AsSpan(56), 4);
            fxp[60] = 1;
            File.WriteAllBytes(path, fxp);
            var request = new RenderRequest { Preset = new PresetRecord { Synth = SynthKind.Sylenth1, SourcePath = path, ContentHash = Hash(fxp) } };
            var parsed = FxpPreset.Read(request);
            Require(parsed.PluginId == 0x73796c31 && parsed.ProgramName == "Test" && parsed.Chunk is { Length: 4 }, "Valid FXP parse failed.");
            request.Preset.ContentHash = new string('0', 64);
            Reject(() => FxpPreset.Read(request));
            request.Preset.ContentHash = Hash(fxp);
            fxp[16] = (byte)'x';
            File.WriteAllBytes(path, fxp);
            request.Preset.ContentHash = Hash(fxp);
            Reject(() => FxpPreset.Read(request));
            fxp[16] = (byte)'s';
            fxp[59] = 5;
            File.WriteAllBytes(path, fxp);
            request.Preset.ContentHash = Hash(fxp);
            Reject(() => FxpPreset.Read(request));
            request.Preset.InternalPath = "bank-entry.fxp";
            Reject(() => FxpPreset.Read(request));
            var template = new PreviewTemplate { HoldSeconds = 5120.0 / 44100, TailSeconds = 0.2 };
            var plan = RenderFramePlan.Create(template, 10 * 1024 * 1024);
            Require(plan.Hold == 5120 && plan.Tail == 8820 && plan.Frames == 13940, "Frame rounding failed.");
            Require(plan.NoteOffOffset(4608) is null && plan.NoteOffOffset(5120) == 0 && plan.NoteOffOffset(5632) is null, "MIDI block boundary failed.");
            Require(plan.OutputBytes == 56L + 13940 * 3 * 8, "Float32 size bound failed.");
            Reject(() => RenderFramePlan.Create(template, plan.OutputBytes - 1));
            Console.WriteLine("{\"Architecture\":\"x86\",\"VstNet\":\"2.1.0\",\"BufferInterop\":true,\"FxpParse\":true,\"BadInput\":true,\"FrameBoundaries\":true,\"PluginsLoaded\":0}");
        }
        finally { File.Delete(path); }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is InvalidDataException or IOException or NotSupportedException) { return; }
        throw new InvalidOperationException("Invalid input was accepted.");
    }
}
