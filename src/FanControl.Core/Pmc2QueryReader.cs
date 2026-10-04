using System.Diagnostics;

namespace FanControl.Core;

/// <summary>A future signed adapter must validate the 21CX/HYCN42WW profile,
/// PMC2 68/6C configuration and acquire exclusive bus access before returning a lease.
/// The developer diagnostic adapter implements these gates; production signing
/// remains unresolved. This interface exposes only the operations
/// needed by the candidate query protocol; the reader never sets fan targets.</summary>
public interface IPmc2QueryTransport
{
    IDisposable BeginExclusiveTransaction();
    byte ReadStatus(); // 6C; does not consume response data.
    byte ReadData(); // 68; consumes exactly one response byte.
    void WriteQueryCommand(byte command); // 6C.
    void WriteQueryParameter(byte parameter); // 68.
}

public sealed record Pmc2FanFeedback(byte Flags, int Fan1Rpm, int Fan2Rpm,
    DateTimeOffset CapturedAtUtc, TimeSpan Elapsed);

/// <summary>Candidate protocol from HYEC42WW static analysis.
/// Driver operations must be bounded by the adapter; the deadline here cannot
/// interrupt a blocked native call. On a partial/failed transaction, stop using
/// this reader so another command cannot silently consume leftover bytes.</summary>
public sealed class Pmc2QueryReader
{
    private readonly IPmc2QueryTransport transport;
    private readonly object sync = new();
    private readonly TimeSpan timeout;
    private readonly int maximumPolls;
    private readonly int pollDelayMilliseconds;
    private bool stopped;
    public bool Stopped { get { lock (sync) return stopped; } }

    public Pmc2QueryReader(IPmc2QueryTransport transport, TimeSpan? timeout = null,
        int maximumPolls = 128, int pollDelayMilliseconds = 1)
    {
        ArgumentNullException.ThrowIfNull(transport);
        this.timeout = timeout ?? TimeSpan.FromMilliseconds(100);
        if (this.timeout <= TimeSpan.Zero || this.timeout > TimeSpan.FromSeconds(1))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        if (maximumPolls is < 1 or > 1024) throw new ArgumentOutOfRangeException(nameof(maximumPolls));
        if (pollDelayMilliseconds is < 0 or > 10) throw new ArgumentOutOfRangeException(nameof(pollDelayMilliseconds));
        this.transport = transport;
        this.maximumPolls = maximumPolls;
        this.pollDelayMilliseconds = pollDelayMilliseconds;
    }

    public Pmc2FanFeedback Read(CancellationToken cancellationToken = default)
    {
        lock (sync)
        {
            if (stopped) throw new InvalidOperationException("PMC2 reader stopped after a failed transaction.");
            cancellationToken.ThrowIfCancellationRequested();
            var clock = Stopwatch.StartNew();
            try
            {
                byte flagsBefore, flagsAfter;
                int fan1, fan2;
                using (transport.BeginExclusiveTransaction())
                {
                    CheckTime("transaction setup");
                    flagsBefore = Query(0x12, 1)[0];
                    fan1 = DecodeRpm(Query(0x18, 2));
                    fan2 = DecodeRpm(Query(0x19, 2));
                    flagsAfter = Query(0x12, 1)[0];
                    if (flagsBefore != flagsAfter)
                        throw new InvalidDataException("PMC2 flags changed during fan queries.");
                    // Reject extra response bytes. Never drain unknown/stale data.
                    byte status = transport.ReadStatus();
                    CheckTime("final status read");
                    if ((status & 3) != 0)
                        throw new InvalidDataException("PMC2 was not idle after the final response.");
                }
                CheckTime("transaction cleanup");
                return new(flagsAfter, fan1, fan2, DateTimeOffset.UtcNow, clock.Elapsed);
            }
            catch
            {
                stopped = true;
                throw;
            }

            void CheckTime(string stage)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (clock.Elapsed > timeout) throw new TimeoutException($"PMC2 transaction deadline exceeded after {stage}: {clock.Elapsed.TotalMilliseconds:F1} ms > {timeout.TotalMilliseconds:F1} ms.");
            }
            void WaitInputIdle(string stage)
            {
                WaitStatus(status =>
                {
                    if ((status & 1) != 0)
                        throw new InvalidDataException("Unexpected PMC2 output pending before query input.");
                    return (status & 2) == 0;
                }, stage);
            }
            void WaitStatus(Func<byte, bool> ready, string stage)
            {
                for (int poll = 0; poll < maximumPolls; poll++)
                {
                    CheckTime($"{stage} polling");
                    byte status = transport.ReadStatus();
                    CheckTime($"{stage} status read");
                    if (ready(status)) return;
                    if (pollDelayMilliseconds != 0) Thread.Sleep(pollDelayMilliseconds);
                }
                throw new TimeoutException($"PMC2 status polling limit exceeded during {stage}.");
            }
            byte[] Query(byte parameter, int responseLength)
            {
                string request = $"D5/{parameter:X2}";
                WaitInputIdle($"{request} before command");
                transport.WriteQueryCommand(0xD5);
                CheckTime($"{request} command write");
                WaitInputIdle($"{request} before parameter");
                transport.WriteQueryParameter(parameter);
                CheckTime($"{request} parameter write");
                var bytes = new byte[responseLength];
                for (int index = 0; index < bytes.Length; index++)
                {
                    WaitStatus(status => (status & 3) == 1, $"{request} response {index}");
                    bytes[index] = transport.ReadData();
                    CheckTime($"{request} data read {index}");
                }
                return bytes;
            }
        }
    }

    private static int DecodeRpm(byte[] response)
    {
        // Firmware 8E6C/8E7B emits the low byte first, then the high byte.
        int rpm = response[0] | (response[1] << 8);
        if (rpm > 7500) throw new InvalidDataException("PMC2 feedback exceeded the candidate firmware clamp.");
        return rpm;
    }
}
