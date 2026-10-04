using EcFeedbackProbe;
using EcIdentityProbe;

namespace EcPmcConfigProbe;

/// <summary>Writes only the two validated fixed fan target addresses.</summary>
internal sealed class FixedRpmOverrideWriter(ISelectorIo io, IEcMutexGate? gate = null)
{
    private readonly IEcMutexGate mutexGate = gate ?? new GlobalIsaMutexGate();

    internal void Set(int fan, byte units)
    {
        if (fan is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(fan));
        if (units != 0 && units is < 15 or > 75) throw new ArgumentOutOfRangeException(nameof(units));

        EcMutexWaitResult wait = mutexGate.Acquire(TimeSpan.FromSeconds(2));
        if (wait.Status != EcMutexWaitStatus.Acquired || wait.Lease is null)
            throw new IOException($"ISA mutex {wait.Status}; RPM write was not attempted. {wait.Error}");

        using IDisposable lease = wait.Lease;
        IdentityResult? before = null;
        byte? savedIndex = null, savedHigh = null, savedLow = null;
        Exception? operationError = null;
        Exception? restoreError = null;
        bool haveIndex = false, haveHigh = false, haveLow = false;

        try
        {
            before = new IdentityReader(io).Read();
            RequireIdentity(before, "Initial");

            savedIndex = io.Read(0x2E); haveIndex = true;
            savedHigh = ReadSelector(0x11); haveHigh = true;
            savedLow = ReadSelector(0x10); haveLow = true;

            byte low = fan == 1 ? (byte)0x0C : (byte)0x0D;
            WriteSelector(0x11, 0x08);
            WriteSelector(0x10, low);
            io.Write(0x2E, 0x12);
            io.Write(0x2F, units);
            byte actual = io.Read(0x2F);
            if (actual != units) throw new IOException($"RPM target readback mismatch: wrote {units}, read {actual}.");
        }
        catch (Exception e) { operationError = e; }
        finally
        {
            // Attempt all selector restorations even when an earlier one fails.
            if (haveHigh) TryRestore(() => WriteSelector(0x11, savedHigh!.Value));
            if (haveLow) TryRestore(() => WriteSelector(0x10, savedLow!.Value));
            if (haveHigh && haveLow)
                TryRestore(() =>
                {
                    if (ReadSelector(0x11) != savedHigh || ReadSelector(0x10) != savedLow)
                        throw new IOException("RPM writer address selector restoration readback mismatch.");
                });
            if (haveIndex)
                TryRestore(() =>
                {
                    io.Write(0x2E, savedIndex!.Value);
                    if (io.Read(0x2E) != savedIndex.Value)
                        throw new IOException("RPM writer indirect selector restoration readback mismatch.");
                });
        }

        // Verify chip identity again while still holding the mutex, even after a
        // write failure. Any failure remains a failure; a later zero call can retry.
        try { RequireIdentity(new IdentityReader(io).Read(), "Final"); }
        catch (Exception e) { restoreError ??= e; }

        if (operationError is not null && restoreError is not null)
            throw new AggregateException("RPM write failed and cleanup/identity verification also failed.", operationError, restoreError);
        if (operationError is not null) throw new IOException("RPM write failed.", operationError);
        if (restoreError is not null) throw new IOException("RPM write cleanup or final identity verification failed.", restoreError);

        void TryRestore(Action restore)
        {
            try { restore(); }
            catch (Exception first)
            {
                restoreError ??= first;
                // Retry the same fixed restoration once so a transient port
                // failure does not leave a selector at the trial address.
                try { restore(); }
                catch (Exception retry) { restoreError ??= retry; }
            }
        }
    }

    private byte ReadSelector(byte selector)
    {
        io.Write(0x2E, selector);
        return io.Read(0x2F);
    }

    private void WriteSelector(byte selector, byte value)
    {
        io.Write(0x2E, selector);
        io.Write(0x2F, value);
    }

    private static void RequireIdentity(IdentityResult result, string stage)
    {
        if (result.RawChipId != 0x5571 || result.Mapping1060 != 0 ||
            !result.SelectorsRestored || result.Errors.Count != 0)
            throw new IOException($"{stage} identity/mapping/selector restoration gate failed.");
    }
}
