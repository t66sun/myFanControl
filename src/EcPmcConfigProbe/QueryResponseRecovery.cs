using System.Diagnostics;
using FanControl.Core;

namespace EcPmcConfigProbe;

internal sealed record QueryResponseRecoveryResult(byte[] DiscardedBytes, double ElapsedMilliseconds, bool Completed = true);
internal static class QueryResponseRecovery
{
    // Explicit diagnostic recovery for our previous D5/12,18,19 requests.
    // These handlers emit at most two bytes. Never drain arbitrary output,
    // write a command, or interpret discarded bytes as fan telemetry.
    internal static QueryResponseRecoveryResult Run(IPmc2QueryTransport transport)
    {
        var clock = Stopwatch.StartNew();
        var discarded = new List<byte>();
        try {
        using (transport.BeginExclusiveTransaction()) {
            long? idleSince = null;
            while (true) {
                CheckTime();
                byte status = transport.ReadStatus();
                CheckTime();
                if ((status & 2) != 0) throw new InvalidDataException("Recovery found input busy; no further port access.");
                if ((status & 1) != 0) {
                    idleSince = null;
                    if (discarded.Count == 2) throw new InvalidDataException("Residual response exceeds the known two-byte query; recovery stopped.");
                    discarded.Add(transport.ReadData());
                    CheckTime();
                } else {
                    idleSince ??= Stopwatch.GetTimestamp();
                    if (Stopwatch.GetElapsedTime(idleSince.Value).TotalMilliseconds >= 10) break;
                }
                Thread.Sleep(1);
            }
        }
        CheckTime();
        return new(discarded.ToArray(), clock.Elapsed.TotalMilliseconds);
        } catch (Exception e) {
            e.Data["RecoveryDiscardedBytes"] = discarded.ToArray();
            e.Data["RecoveryElapsedMilliseconds"] = clock.Elapsed.TotalMilliseconds;
            throw;
        }
        void CheckTime() { if (clock.ElapsedMilliseconds > 100) throw new TimeoutException("Query response recovery exceeded 100ms; no retry."); }
    }
}
