using SoundPreviewer.Core.Models;

namespace SoundPreviewer.Vst2Host;

internal sealed record RenderFramePlan(int Hold, int Tail, int SampleRate)
{
    public int Frames => checked(Hold + Tail);
    public long OutputBytes => checked(56L + Frames * 3L * 2 * sizeof(float));
    public int? NoteOffOffset(int blockPosition) => Hold >= blockPosition && Hold < blockPosition + HostCallbacks.BlockSize ? Hold - blockPosition : null;
    public static RenderFramePlan Create(PreviewTemplate template, long maximumBytes)
    {
        template.Validate();
        var plan = new RenderFramePlan(checked((int)Math.Round(template.HoldSeconds * template.SampleRate)), checked((int)Math.Round(template.TailSeconds * template.SampleRate)), template.SampleRate);
        if (plan.OutputBytes > Math.Min(maximumBytes, 256L * 1024 * 1024)) throw new IOException("三音区输出超过允许大小。");
        return plan;
    }
}
