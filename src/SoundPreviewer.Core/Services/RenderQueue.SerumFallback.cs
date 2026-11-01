using System.Buffers.Binary;
using SoundPreviewer.Core.Audio;
using SoundPreviewer.Core.Models;
using SoundPreviewer.Core.Scanning;

namespace SoundPreviewer.Core.Services;

public sealed partial class RenderQueue
{
    private sealed partial class Execution
    {
        private bool TryDeferSerumFallback(List<QueueItem> items, string reason)
        {
            if (!OperatingSystem.IsWindows() || items.Count == 0 || items.Any(item => item.Record.Synth != SynthKind.Serum1)) return false;
            var plugin = settings.Plugins.FirstOrDefault(candidate => candidate.Synth == SynthKind.Serum2);
            if (plugin?.Backend != RenderBackend.DawDreamer) return false;
            var state = readiness.GetValueOrDefault(SynthKind.Serum2) ?? RenderReadinessService.Evaluate(plugin, settings.DiscoveredPlugins);
            if (!state.CanAttemptRender || !IsConfirmedSerumSource(items[0].Record)) return false;
            settings.Template.Validate();
            string key = Fingerprints.HashText("serum1-via-serum2-v1|" + Fingerprints.CacheKey(items[0].Record, plugin, settings.Template));
            if (!force && items.All(item => item.Record.AttemptedCacheKey == key && item.Record.Attempts >= settings.MaxAttempts)) return false;
            lock (gate)
            {
                if (!serumFallbacks.TryGetValue(key, out var job))
                {
                    job = new RenderJob(key, plugin, [], Serum1Fallback: true);
                    serumFallbacks.Add(key, job);
                    batchOutputs.Add(Path.GetFullPath(Path.Combine(settings.PreviewDirectory, key + PreviewExtension)));
                }
                foreach (var item in items)
                {
                    var preset = item.Record;
                    item.Previous ??= HasValidPreview(preset, token) ? Clone(preset) : null;
                    item.AttemptInProgress = false;
                    if (preset.AttemptedCacheKey != key) preset.Attempts = 0;
                    preset.AttemptedCacheKey = key;
                    preset.Status = PresetStatus.Pending;
                    preset.LastRenderOutcome = "等待 Serum 2 回退";
                    preset.Message = preset.LastRenderMessage = "Serum 1 无法加载，已排到本批末尾，等待 Serum 2 后台自动导入。" + reason;
                    job.Items.Add(item);
                    Persist(item);
                    Report(QueueStages.Validating, preset.Message, item);
                }
            }
            return true;
        }

        private bool IsConfirmedSerumSource(PresetRecord preset)
        {
            if (preset.InternalPath.Length > 0 || preset.BankSlot is not null || preset.Status == PresetStatus.Unidentified ||
                !Path.GetExtension(preset.SourcePath).Equals(".fxp", StringComparison.OrdinalIgnoreCase) ||
                preset.ContentHash.Length != 64 || !preset.ContentHash.All(Uri.IsHexDigit)) return false;
            try
            {
                using var stream = File.OpenRead(preset.SourcePath);
                if (stream.Length is <= 60 or > 64L * 1024 * 1024) return false;
                var envelope = PresetDetection.ReadEnvelope(stream, stream.Length, bank: false);
                if (!envelope.IsValid || envelope.PluginId != "XfsX") return false;
                Span<byte> header = stackalloc byte[60];
                stream.ReadExactly(header);
                if (!header[8..12].SequenceEqual("FPCh"u8) || BinaryPrimitives.ReadUInt32BigEndian(header[56..60]) != stream.Length - 60)
                    return false;
                return string.Equals(Fingerprints.HashFile(preset.SourcePath, token), preset.ContentHash, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
        }
    }
}
