using System.Diagnostics;
using EcIdentityProbe;

namespace EcFeedbackProbe;

internal sealed record LowFeedbackReadResult(
    bool Available,
    bool ReadCompleted,
    ushort? ChipIdBefore,
    ushort? ChipIdAfter,
    byte? Mapping1060Before,
    byte? Mapping1060After,
    bool SelectorsRestored,
    byte[]? FirstBytes,
    byte[]? SecondBytes,
    int? Fan1Rpm,
    int? Fan2Rpm,
    byte? Target0804,
    byte? Target0805,
    int? Target0804Times100Rpm,
    int? Target0805Times100Rpm,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset FinishedAtUtc,
    double? StopwatchElapsedMilliseconds,
    bool MutexAbandoned,
    bool ReaderStopped,
    IReadOnlyList<string> Errors);

internal enum EcMutexWaitStatus
{
    Acquired,
    TimedOut,
    Abandoned,
    Failed
}

internal sealed record EcMutexWaitResult(EcMutexWaitStatus Status, IDisposable? Lease = null, string? Error = null);

internal interface IEcMutexGate
{
    EcMutexWaitResult Acquire(TimeSpan timeout);
}

internal sealed class GlobalIsaMutexGate : IEcMutexGate
{
    internal const string MutexName = @"Global\Access_ISABUS.HTP.Method";

    public EcMutexWaitResult Acquire(TimeSpan timeout)
    {
        Mutex mutex;
        try
        {
            mutex = new Mutex(initiallyOwned: false, name: MutexName);
        }
        catch (Exception exception)
        {
            return new(EcMutexWaitStatus.Failed, Error: $"ISA 互斥锁创建失败：{exception.GetType().Name}: {exception.Message}");
        }

        try
        {
            if (!mutex.WaitOne(timeout))
            {
                mutex.Dispose();
                return new(EcMutexWaitStatus.TimedOut);
            }

            return new(EcMutexWaitStatus.Acquired, new MutexLease(mutex));
        }
        catch (AbandonedMutexException)
        {
            // WaitOne grants ownership when it reports abandonment. Release it, then stop without port access.
            try { mutex.ReleaseMutex(); } catch { }
            mutex.Dispose();
            return new(EcMutexWaitStatus.Abandoned);
        }
        catch (Exception exception)
        {
            mutex.Dispose();
            return new(EcMutexWaitStatus.Failed, Error: $"ISA 互斥锁等待失败：{exception.GetType().Name}: {exception.Message}");
        }
    }

    private sealed class MutexLease(Mutex mutex) : IDisposable
    {
        private Mutex? _mutex = mutex;

        public void Dispose()
        {
            Mutex? owned = Interlocked.Exchange(ref _mutex, null);
            if (owned is null) return;
            try { owned.ReleaseMutex(); }
            finally { owned.Dispose(); }
        }
    }
}

internal interface ILowFeedbackClock
{
    DateTimeOffset UtcNow { get; }
    long GetTimestamp();
    long Frequency { get; }
}

internal sealed class StopwatchLowFeedbackClock : ILowFeedbackClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    public long GetTimestamp() => Stopwatch.GetTimestamp();
    public long Frequency => Stopwatch.Frequency;
}

/// <summary>
/// Reads only the experimentally identified 0800-0805 feedback window. All writes are
/// limited to the 10/11 address selectors and the 2E index port; selector 12 is read-only.
/// </summary>
internal sealed class LowFeedbackReader
{
    private static readonly TimeSpan RoundLimit = TimeSpan.FromSeconds(2);
    private const ushort ExpectedChipId = 0x5571;
    private readonly object _gate = new();
    private readonly ISelectorIo _io;
    private readonly IEcMutexGate _mutexGate;
    private readonly ILowFeedbackClock _clock;
    private bool _stopped;

    internal LowFeedbackReader(
        ISelectorIo io,
        IEcMutexGate? mutexGate = null,
        ILowFeedbackClock? clock = null)
    {
        _io = io;
        _mutexGate = mutexGate ?? new GlobalIsaMutexGate();
        _clock = clock ?? new StopwatchLowFeedbackClock();
    }

    internal LowFeedbackReadResult Read()
    {
        lock (_gate)
        {
            DateTimeOffset startedAtUtc = _clock.UtcNow;
            long startedTimestamp = _clock.GetTimestamp();
            var errors = new List<string>();
            ushort? chipBefore = null, chipAfter = null;
            byte? mappingBefore = null, mappingAfter = null;
            byte[]? first = null, second = null;
            bool selectorsRestored = false;
            bool readCompleted = false;
            bool mutexAbandoned = false;
            IDisposable? lease = null;
            byte? originalSelector = null, originalHigh = null, originalLow = null;

            if (_stopped)
            {
                errors.Add("EC 读取已停止；之前的 ISA 互斥锁或选择器恢复状态不可靠。");
            }
            else
            {
                EcMutexWaitResult wait;
                try { wait = _mutexGate.Acquire(RoundLimit); }
                catch (Exception exception)
                {
                    wait = new(EcMutexWaitStatus.Failed,
                        Error: $"ISA 互斥锁等待失败：{exception.GetType().Name}: {exception.Message}");
                }

                switch (wait.Status)
                {
                    case EcMutexWaitStatus.Acquired:
                        lease = wait.Lease;
                        if (lease is null)
                        {
                            _stopped = true;
                            errors.Add("ISA 互斥锁没有返回有效租约；未访问端口。");
                            break;
                        }
                        ReadUnderMutex();
                        break;
                    case EcMutexWaitStatus.TimedOut:
                        _stopped = true;
                        errors.Add("等待全局 ISA 互斥锁超过 2 秒；本轮未读取端口。");
                        break;
                    case EcMutexWaitStatus.Abandoned:
                        mutexAbandoned = true;
                        _stopped = true;
                        errors.Add("全局 ISA 互斥锁已遗弃；停止 EC 读取，本轮未访问端口。");
                        break;
                    default:
                        _stopped = true;
                        errors.Add(wait.Error ?? "无法取得全局 ISA 互斥锁；本轮未读取端口。");
                        break;
                }
            }

            if (lease is not null)
            {
                try { lease.Dispose(); }
                catch (Exception exception)
                {
                    _stopped = true;
                    errors.Add($"释放 ISA 互斥锁失败：{exception.GetType().Name}: {exception.Message}");
                }
            }

            DateTimeOffset finishedAtUtc = _clock.UtcNow;
            long finishedTimestamp = _clock.GetTimestamp();
            double? elapsedMilliseconds = null;
            bool timingValid = true;
            if (_clock.Frequency <= 0 || finishedTimestamp < startedTimestamp)
            {
                timingValid = false;
                errors.Add("Stopwatch 时间戳无效；丢弃本轮 EC 读数。");
            }
            else
            {
                elapsedMilliseconds = (finishedTimestamp - startedTimestamp) * 1000d / _clock.Frequency;
                if (elapsedMilliseconds > RoundLimit.TotalMilliseconds)
                {
                    timingValid = false;
                    errors.Add($"Stopwatch 检测到 EC 整轮事务耗时 {elapsedMilliseconds:F1} ms，超过 2 秒；丢弃本轮读数。");
                }
            }

            TimeSpan utcElapsed = finishedAtUtc - startedAtUtc;
            if (utcElapsed < TimeSpan.Zero)
            {
                timingValid = false;
                errors.Add("UTC 结束时间早于开始时间；丢弃本轮 EC 读数。");
            }
            else if (utcElapsed > RoundLimit)
            {
                timingValid = false;
                errors.Add($"EC 整轮 UTC 间隔为 {utcElapsed.TotalMilliseconds:F1} ms，超过 2 秒；丢弃本轮读数。");
            }

            bool repeatedBytesAgree = first is not null && second is not null && first.SequenceEqual(second);
            if (readCompleted && !repeatedBytesAgree)
                errors.Add("0800-0805 两次完整读取的字节不一致；拒绝本轮转速。");

            int? candidateFan1Rpm = null, candidateFan2Rpm = null;
            byte? target0804 = null, target0805 = null;
            if (repeatedBytesAgree && first is not null)
            {
                candidateFan1Rpm = (first[0] << 8) | first[1];
                candidateFan2Rpm = (first[2] << 8) | first[3];
                target0804 = first[4];
                target0805 = first[5];
                if (candidateFan1Rpm > 7500 || candidateFan2Rpm > 7500)
                    errors.Add("EC 报告转速超过 7500 RPM；拒绝本轮转速。");
            }

            bool identityAndMappingValid = chipBefore == ExpectedChipId && chipAfter == ExpectedChipId &&
                mappingBefore == 0 && mappingAfter == 0;
            bool available = readCompleted && selectorsRestored && timingValid && repeatedBytesAgree &&
                identityAndMappingValid && candidateFan1Rpm.HasValue && candidateFan2Rpm.HasValue &&
                candidateFan1Rpm <= 7500 && candidateFan2Rpm <= 7500 && errors.Count == 0;
            int? fan1Rpm = available ? candidateFan1Rpm : null;
            int? fan2Rpm = available ? candidateFan2Rpm : null;
            int? target0804Times100 = available && target0804.HasValue ? target0804.Value * 100 : null;
            int? target0805Times100 = available && target0805.HasValue ? target0805.Value * 100 : null;
            return new LowFeedbackReadResult(available, readCompleted, chipBefore, chipAfter,
                mappingBefore, mappingAfter, selectorsRestored, first, second, fan1Rpm, fan2Rpm,
                target0804, target0805, target0804Times100, target0805Times100,
                startedAtUtc, finishedAtUtc, elapsedMilliseconds, mutexAbandoned, _stopped, errors);

            void ReadUnderMutex()
            {
                try
                {
                    originalSelector = _io.Read(0x2E);
                    originalHigh = ReadSelector(0x11);
                    originalLow = ReadSelector(0x10);

                    chipBefore = ReadChipId();
                    if (chipBefore != ExpectedChipId)
                        throw new NotSupportedException($"EC 芯片 ID 为 0x{chipBefore:X4}，需要实测匹配的 0x5571；未读取数据窗口。");

                    mappingBefore = ReadAddress(0x1060);
                    if (mappingBefore != 0)
                        throw new NotSupportedException($"EC 映射 0x1060 为 0x{mappingBefore:X2}，需要 0x00；未读取数据窗口。");

                    first = ReadWindow();
                    second = ReadWindow();

                    chipAfter = ReadChipId();
                    if (chipAfter != ExpectedChipId)
                        throw new InvalidOperationException($"读取后 EC 芯片 ID 变为 0x{chipAfter:X4}；拒绝本轮数据。");
                    mappingAfter = ReadAddress(0x1060);
                    if (mappingAfter != mappingBefore)
                        throw new InvalidOperationException($"读取前后 0x1060 不一致（0x{mappingBefore:X2} / 0x{mappingAfter:X2}）；拒绝本轮数据。");
                    readCompleted = true;
                }
                catch (Exception exception)
                {
                    _stopped = true;
                    errors.Add($"EC 固定窗口读取失败：{exception.GetType().Name}: {exception.Message}");
                }
                finally
                {
                    selectorsRestored = RestoreSelectors(originalSelector, originalHigh, originalLow, errors);
                    if (!selectorsRestored)
                    {
                        _stopped = true;
                        errors.Add("EC 地址选择器恢复或回读失败；停止后续 EC 读取。");
                    }
                }
            }
        }
    }

    private ushort ReadChipId() => (ushort)((ReadAddress(0x2000) << 8) | ReadAddress(0x2001));

    private byte ReadSelector(byte selector)
    {
        _io.Write(0x2E, selector);
        return _io.Read(0x2F);
    }

    private void WriteSelector(byte selector, byte value)
    {
        if (selector is not (0x10 or 0x11)) throw new ArgumentOutOfRangeException(nameof(selector));
        _io.Write(0x2E, selector);
        _io.Write(0x2F, value);
    }

    private byte ReadAddress(ushort address)
    {
        if (address is not (0x2000 or 0x2001 or 0x1060) && address is not (>= 0x0800 and <= 0x0805))
            throw new ArgumentOutOfRangeException(nameof(address));
        WriteSelector(0x11, (byte)(address >> 8));
        WriteSelector(0x10, (byte)address);
        _io.Write(0x2E, 0x12);
        return _io.Read(0x2F);
    }

    private byte[] ReadWindow()
    {
        var bytes = new byte[6];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = ReadAddress((ushort)(0x0800 + i));
        return bytes;
    }

    private bool RestoreSelectors(byte? originalSelector, byte? originalHigh, byte? originalLow,
        ICollection<string> errors)
    {
        bool highOk = originalHigh is null, lowOk = originalLow is null, selectorOk = false;
        if (originalHigh is byte high)
            try { WriteSelector(0x11, high); highOk = true; }
            catch (Exception exception) { errors.Add($"恢复高地址选择器失败：{exception.GetType().Name}: {exception.Message}"); }
        if (originalLow is byte low)
            try { WriteSelector(0x10, low); lowOk = true; }
            catch (Exception exception) { errors.Add($"恢复低地址选择器失败：{exception.GetType().Name}: {exception.Message}"); }

        if (originalHigh is byte verifyHigh && originalLow is byte verifyLow && highOk && lowOk)
        {
            try
            {
                if (ReadSelector(0x11) != verifyHigh || ReadSelector(0x10) != verifyLow)
                    throw new InvalidOperationException("地址选择器回读不一致。");
            }
            catch (Exception exception)
            {
                highOk = lowOk = false;
                errors.Add($"地址选择器恢复回读失败：{exception.GetType().Name}: {exception.Message}");
            }
        }

        if (originalSelector is byte selector)
        {
            try
            {
                _io.Write(0x2E, selector);
                selectorOk = _io.Read(0x2E) == selector;
                if (!selectorOk) errors.Add("间接选择器恢复回读不一致。");
            }
            catch (Exception exception)
            {
                errors.Add($"间接选择器恢复失败：{exception.GetType().Name}: {exception.Message}");
            }
        }

        return highOk && lowOk && selectorOk;
    }
}
