using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace EcPmcConfigProbe;

// A short-lived, per-process timer request for the query diagnostic's Sleep(1).
// It does not guarantee scheduling latency or make native calls interruptible.
internal sealed class PollTimerResolution : IDisposable
{
    private bool active;
    internal uint? EndResult { get; private set; }
    internal PollTimerResolution()
    {
        uint result = timeBeginPeriod(1);
        if (result != 0) throw new InvalidOperationException($"1ms timer request failed: {result}");
        active = true;
    }
    public void Dispose()
    {
        if (!active) return;
        active = false;
        EndResult = timeEndPeriod(1);
        if (EndResult != 0) throw new InvalidOperationException($"Timer request release failed: {EndResult}");
    }
    internal static int Measure(string outputPath)
    {
        using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        var before = SleepSamples();
        double[] during;
        var timer = new PollTimerResolution();
        using (timer) during = SleepSamples();
        var after = SleepSamples();
        JsonSerializer.Serialize(output, new { CapturedUtc = DateTimeOffset.UtcNow, HardwareAccess = false,
            RequestedSleepMs = 1, TimerRequestMs = 1, BeforeMs = before, DuringMs = during, AfterMs = after,
            TimerEndResult = timer.EndResult }, new JsonSerializerOptions { WriteIndented = true });
        return 0;
    }
    private static double[] SleepSamples()
    {
        var samples = new double[30];
        for (int i = 0; i < samples.Length; i++) {
            long start = Stopwatch.GetTimestamp();
            Thread.Sleep(1);
            samples[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
        return samples;
    }
    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint period);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint period);
}
