using System.Diagnostics;
using EcFeedbackProbe;
using EcIdentityProbe;

namespace EcPmcConfigProbe;

/// <summary>
/// Fixed-address, read-only snapshot of EC window 0800-080F. Bytes 0C-0F are
/// exposed only as raw candidates; this reader does not validate their mapping.
/// </summary>
internal sealed record FanOverrideBaselineResult(
    bool Available,
    byte[]? FirstBytes,
    byte[]? SecondBytes,
    ushort? ChipIdBefore,
    ushort? ChipIdAfter,
    byte? Mapping1060Before,
    byte? Mapping1060After,
    bool SelectorsRestored,
    int? Fan1Rpm,
    int? Fan2Rpm,
    byte? Target0804,
    byte? Target0805,
    byte? RawCandidateOverride0C,
    byte? RawCandidateOverride0D,
    byte? RawCandidateOverride0E,
    byte? RawCandidateOverride0F,
    double? StopwatchElapsedMilliseconds,
    bool MutexAbandoned,
    IReadOnlyList<string> Errors);

internal sealed class FanOverrideBaselineReader
{
    private const ushort ExpectedChipId = 0x5571;
    private static readonly TimeSpan TransactionLimit = TimeSpan.FromSeconds(2);
    private readonly ISelectorIo io;
    private readonly IEcMutexGate mutexGate;
    private readonly object sync = new();
    private bool stopped;

    internal FanOverrideBaselineReader(ISelectorIo io, IEcMutexGate? mutexGate = null)
    {
        this.io = io ?? throw new ArgumentNullException(nameof(io));
        this.mutexGate = mutexGate ?? new GlobalIsaMutexGate();
    }

    internal FanOverrideBaselineResult Read()
    {
        lock (sync)
        {
            long started = Stopwatch.GetTimestamp();
            var errors = new List<string>();
            ushort? chipBefore = null, chipAfter = null;
            byte? mappingBefore = null, mappingAfter = null;
            byte[]? first = null, second = null;
            bool selectorsRestored = false, mutexAbandoned = false;
            IDisposable? mutexLease = null;

            if (stopped)
                errors.Add("Baseline read stopped after a prior mutex or selector restoration failure.");
            else
            {
                EcMutexWaitResult wait;
                try { wait = mutexGate.Acquire(TransactionLimit); }
                catch (Exception e)
                {
                    wait = new(EcMutexWaitStatus.Failed, Error: $"ISA mutex acquisition failed: {e.GetType().Name}: {e.Message}");
                }

                if (wait.Status == EcMutexWaitStatus.Acquired && wait.Lease is not null)
                {
                    mutexLease = wait.Lease;
                    try
                    {
                        IdentityResult before = new IdentityReader(io).Read();
                        chipBefore = before.RawChipId;
                        mappingBefore = before.Mapping1060;
                        errors.AddRange(before.Errors.Select(e => $"Initial identity: {e}"));
                        selectorsRestored = before.SelectorsRestored;
                        if (!IdentityValid(before))
                        {
                            errors.Add("Initial EC identity, mapping, or selector restoration gate failed.");
                            stopped = true;
                        }
                        else
                            ReadFixedWindow();
                    }
                    catch (Exception e)
                    {
                        errors.Add($"Initial identity/window read failed: {e.GetType().Name}: {e.Message}");
                        stopped = true;
                    }

                    try
                    {
                        IdentityResult after = new IdentityReader(io).Read();
                        chipAfter = after.RawChipId;
                        mappingAfter = after.Mapping1060;
                        errors.AddRange(after.Errors.Select(e => $"Final identity: {e}"));
                        selectorsRestored &= after.SelectorsRestored;
                        if (!IdentityValid(after))
                        {
                            errors.Add("Final EC identity, mapping, or selector restoration gate failed.");
                            stopped = true;
                        }
                    }
                    catch (Exception e)
                    {
                        errors.Add($"Final identity read failed: {e.GetType().Name}: {e.Message}");
                        stopped = true;
                    }
                }
                else
                {
                    mutexAbandoned = wait.Status == EcMutexWaitStatus.Abandoned;
                    if (wait.Status == EcMutexWaitStatus.Abandoned) stopped = true;
                    errors.Add(wait.Status switch
                    {
                        EcMutexWaitStatus.TimedOut => "Timed out waiting up to 2 seconds for the global ISA mutex; no port access occurred.",
                        EcMutexWaitStatus.Abandoned => "Global ISA mutex was abandoned; no port access occurred.",
                        _ => wait.Error ?? "Could not acquire the global ISA mutex; no port access occurred."
                    });
                }
            }

            if (mutexLease is not null)
            {
                try { mutexLease.Dispose(); }
                catch (Exception e)
                {
                    stopped = true;
                    errors.Add($"ISA mutex release failed: {e.GetType().Name}: {e.Message}");
                }
            }

            long ended = Stopwatch.GetTimestamp();
            double? elapsed = null;
            if (ended < started || Stopwatch.Frequency <= 0)
                errors.Add("Invalid Stopwatch interval; baseline is unavailable.");
            else
            {
                elapsed = (ended - started) * 1000d / Stopwatch.Frequency;
                if (elapsed > TransactionLimit.TotalMilliseconds)
                    errors.Add($"Baseline transaction took {elapsed:F1} ms, exceeding the 2 second limit.");
            }

            bool bytesAgree = first is not null && second is not null && first.AsSpan().SequenceEqual(second);
            if (first is not null && second is not null && !bytesAgree)
                errors.Add("The two complete 0800-080F snapshots differ.");

            int? fan1 = bytesAgree ? (first![0] << 8) | first[1] : null;
            int? fan2 = bytesAgree ? (first![2] << 8) | first[3] : null;
            if (fan1 > 7500 || fan2 > 7500)
                errors.Add("EC feedback exceeds 7500 RPM; baseline RPM values are rejected.");

            bool identitiesValid = chipBefore == ExpectedChipId && chipAfter == ExpectedChipId &&
                mappingBefore == 0 && mappingAfter == 0;
            bool available = bytesAgree && selectorsRestored && identitiesValid &&
                elapsed <= TransactionLimit.TotalMilliseconds && fan1 <= 7500 && fan2 <= 7500 && errors.Count == 0;

            return new FanOverrideBaselineResult(
                available, first, second, chipBefore, chipAfter, mappingBefore, mappingAfter,
                selectorsRestored, available ? fan1 : null, available ? fan2 : null,
                bytesAgree ? first![4] : null, bytesAgree ? first![5] : null,
                first is { Length: 16 } ? first[12] : null,
                first is { Length: 16 } ? first[13] : null,
                first is { Length: 16 } ? first[14] : null,
                first is { Length: 16 } ? first[15] : null,
                elapsed, mutexAbandoned, errors);

            void ReadFixedWindow()
            {
                byte? originalIndex = null, originalHigh = null, originalLow = null;
                try
                {
                    originalIndex = io.Read(0x2E);
                    originalHigh = ReadSelector(0x11);
                    originalLow = ReadSelector(0x10);
                    first = ReadWindow();
                    second = ReadWindow();
                }
                catch (Exception e)
                {
                    errors.Add($"Fixed 0800-080F read failed: {e.GetType().Name}: {e.Message}");
                    stopped = true;
                }
                finally
                {
                    selectorsRestored = RestoreSelectors(originalIndex, originalHigh, originalLow, errors);
                    if (!selectorsRestored)
                    {
                        stopped = true;
                        errors.Add("Selector restoration or readback failed.");
                    }
                }
            }
        }
    }

    private static bool IdentityValid(IdentityResult result) => result.RawChipId == ExpectedChipId &&
        result.Mapping1060 == 0 && result.SelectorsRestored && result.Errors.Count == 0;

    private byte ReadSelector(byte selector)
    {
        io.Write(0x2E, selector);
        return io.Read(0x2F);
    }

    private void WriteSelector(byte selector, byte value)
    {
        if (selector is not (0x10 or 0x11)) throw new ArgumentOutOfRangeException(nameof(selector));
        io.Write(0x2E, selector);
        io.Write(0x2F, value);
    }

    private byte[] ReadWindow()
    {
        var bytes = new byte[16];
        for (int offset = 0; offset < bytes.Length; offset++)
        {
            WriteSelector(0x11, 0x08);
            WriteSelector(0x10, (byte)offset);
            io.Write(0x2E, 0x12);
            bytes[offset] = io.Read(0x2F);
        }
        return bytes;
    }

    private bool RestoreSelectors(byte? originalIndex, byte? originalHigh, byte? originalLow,
        ICollection<string> errors)
    {
        bool highRestored = originalHigh is null;
        bool lowRestored = originalLow is null;
        bool highVerified = originalHigh is null;
        bool lowVerified = originalLow is null;
        bool indexRestored = false;

        if (originalHigh is byte high)
        {
            try { WriteSelector(0x11, high); highRestored = true; }
            catch (Exception e) { errors.Add($"Selector restoration of address 11 failed: {e.GetType().Name}: {e.Message}"); }
        }
        if (originalLow is byte low)
        {
            try { WriteSelector(0x10, low); lowRestored = true; }
            catch (Exception e) { errors.Add($"Selector restoration of address 10 failed: {e.GetType().Name}: {e.Message}"); }
        }

        if (originalHigh is byte expectedHigh)
        {
            try { highVerified = ReadSelector(0x11) == expectedHigh; }
            catch (Exception e) { errors.Add($"Selector readback of address 11 failed: {e.GetType().Name}: {e.Message}"); }
            if (!highVerified) errors.Add("Selector readback mismatch for address 11.");
        }
        if (originalLow is byte expectedLow)
        {
            try { lowVerified = ReadSelector(0x10) == expectedLow; }
            catch (Exception e) { errors.Add($"Selector readback of address 10 failed: {e.GetType().Name}: {e.Message}"); }
            if (!lowVerified) errors.Add("Selector readback mismatch for address 10.");
        }

        if (originalIndex is byte index)
        {
            try
            {
                io.Write(0x2E, index);
                indexRestored = io.Read(0x2E) == index;
                if (!indexRestored) errors.Add("Indirect selector readback mismatch.");
            }
            catch (Exception e) { errors.Add($"Indirect selector restoration failed: {e.GetType().Name}: {e.Message}"); }
        }

        return highRestored && lowRestored && highVerified && lowVerified && indexRestored;
    }
}
