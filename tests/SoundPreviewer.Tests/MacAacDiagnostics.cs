using System.Text.Json;
using SoundPreviewer.Core.Audio;

namespace SoundPreviewer.Tests;

internal static partial class Program
{
    private static async Task<string> MacAacDecodeDiagnosticsAsync(string source, int rate)
    {
        string scratch = Fixture("mac-aac-native-diagnostics");
        var results = new List<object>();
        string primeHelp;
        try
        {
            var help = await ProcessRunner.RunAsync("/usr/bin/afconvert", ["-h"], scratch, TimeSpan.FromSeconds(10));
            primeHelp = string.Join("\n", (help.StandardOutput + "\n" + help.StandardError).Split('\n')
                .Where(line => line.Contains("prime", StringComparison.OrdinalIgnoreCase)));
        }
        catch (Exception error) { primeHelp = error.ToString(); }
        var reference = WindowsAacFixtureReference(rate);
        foreach (var variant in new (string Name, string[] Options)[]
        {
            ("container-default", []),
            ("prime-override-0-0", ["--prime-override", "0", "0"]),
            ("prime-method-0", ["--prime-method", "0"]),
            ("prime-method-1", ["--prime-method", "1"]),
            ("prime-method-2", ["--prime-method", "2"])
        })
        {
            string output = Path.Combine(scratch, variant.Name + ".wav");
            try
            {
                string[] arguments = [source, output, "-f", "WAVE", "-d", "LEF32", .. variant.Options];
                var outcome = await ProcessRunner.RunAsync("/usr/bin/afconvert", arguments, scratch, TimeSpan.FromSeconds(10));
                if (outcome.ExitCode != 0)
                {
                    results.Add(new { variant.Name, variant.Options, outcome.ExitCode, outcome.StandardError });
                    continue;
                }
                var decoded = WaveFile.Read(output);
                var regions = new List<object>();
                if (decoded.Channels == 2 && decoded.SampleRate == rate)
                    for (int region = 0; region < 3; region++)
                    {
                        int first = region * (rate / 2) + 256, end = first + 7744, bestShift = int.MaxValue;
                        double bestScore = double.NegativeInfinity;
                        for (int shift = -4096; shift <= 4096; shift++)
                        {
                            double dot = 0, aEnergy = 0, bEnergy = 0;
                            for (int frame = first; frame < end; frame += 3)
                            {
                                int candidate = frame + shift;
                                if (candidate < 0 || candidate >= decoded.Frames) continue;
                                double a = reference.Samples[frame * 2], b = decoded.Samples[candidate * 2];
                                dot += a * b; aEnergy += a * a; bEnergy += b * b;
                            }
                            double score = aEnergy > 0 && bEnergy > 0 ? dot / Math.Sqrt(aEnergy * bEnergy) : -1;
                            if (score > bestScore) { bestScore = score; bestShift = shift; }
                        }
                        regions.Add(new { Region = region, ShiftFrames = bestShift, Correlation = bestScore });
                    }
                results.Add(new { variant.Name, variant.Options, outcome.ExitCode, decoded.SampleRate, decoded.Channels,
                    decoded.Frames, ExpectedFrames = reference.Frames, Regions = regions, outcome.StandardError });
            }
            catch (Exception error) { results.Add(new { variant.Name, variant.Options, Error = error.ToString() }); }
        }
        return "Mac native afconvert diagnostics: " + JsonSerializer.Serialize(new { PrimeHelp = primeHelp, Results = results });
    }
}
