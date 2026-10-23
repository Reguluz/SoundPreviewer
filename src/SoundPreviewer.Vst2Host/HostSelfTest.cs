using System.Buffers.Binary;
using System.Security.Cryptography;
using SoundPreviewer.Core.Models;
using SoundPreviewer.Core.Audio;

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
            Reject(() => VstRenderer.ValidateChunkLoaded(100, [1, 2], [1, 2], [3, 4]));
            VstRenderer.ValidateChunkLoaded(0, [1, 2], [1, 2], [1, 2]);
            Reject(() => VstRenderer.ValidateChunkLoaded(-1, [1], [2], [2]));
            Reject(() => VstRenderer.ValidateChunkLoaded(0, [], [], [1]));
            VstRenderer.ValidateChunkLoaded(100, [1, 2], [3, 4], [3, 4]);
            VstRenderer.ValidateChunkLoaded(0, [1, 2], [3, 4], [3, 4]);
            var knownChunk = new byte[1028];
            "1lys"u8.CopyTo(knownChunk);
            BinaryPrimitives.WriteInt32LittleEndian(knownChunk.AsSpan(16, 4), 3);
            var knownProgram = new FxpPreset(0x73796c31, knownChunk, null, "TEST ONLY");
            Require(knownProgram.ProgramIndex(128) == 3, "The recognized Sylenth program slot was not retained.");
            Reject(() => knownProgram.ProgramIndex(3));
            Reject(() => knownProgram.ProgramIndex(0));
            BinaryPrimitives.WriteInt32LittleEndian(knownChunk.AsSpan(16, 4), -1);
            Reject(() => knownProgram.ProgramIndex(128));
            knownChunk[0] = 0;
            Require(knownProgram.ProgramIndex(128) == 0, "An unknown chunk header was interpreted as a program slot.");
            Require(new FxpPreset(0x73796c31, [1, 2, 3, 4], null, "TEST ONLY").ProgramIndex(128) == 0,
                "An unknown chunk length was interpreted as a program slot.");
            var headerTarget = new byte[1028];
            "1lys"u8.CopyTo(headerTarget);
            BinaryPrimitives.WriteInt32LittleEndian(headerTarget.AsSpan(4), 2202);
            BinaryPrimitives.WriteInt32LittleEndian(headerTarget.AsSpan(8), 1);
            BinaryPrimitives.WriteInt32LittleEndian(headerTarget.AsSpan(12), 243);
            byte[] headerActual = [.. headerTarget];
            BinaryPrimitives.WriteInt32LittleEndian(headerActual.AsSpan(4), 2200);
            Require(SylenthPresetState.Matches(headerTarget, headerActual), "The observed metadata-only rewrite was rejected.");
            VstRenderer.ValidateChunkLoaded(0, headerActual, headerActual, headerTarget);
            headerActual[20] = 1;
            Require(!SylenthPresetState.Matches(headerTarget, headerActual), "A changed parameter was accepted as metadata.");
            Reject(() => VstRenderer.ValidateChunkLoaded(100, headerActual, headerActual, headerTarget));
            Console.WriteLine("{\"Architecture\":\"x86\",\"VstNet\":\"2.1.0\",\"BufferInterop\":true,\"FxpParse\":true,\"BadInput\":true,\"FrameBoundaries\":true,\"UnchangedChunkRejected\":true,\"MatchingUnchangedChunkAccepted\":true,\"ProgramSlotBounds\":true,\"CompatibleHeaderReadback\":true,\"PluginsLoaded\":0}");
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
