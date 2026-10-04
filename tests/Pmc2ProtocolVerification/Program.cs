using FanControl.Core;
using EcPmcConfigProbe;

int checks = 0;
void Check(bool condition, string message)
{
    checks++;
    if (!condition) throw new InvalidOperationException(message);
}
void Reject<T>(Action action, string message) where T : Exception
{
    try { action(); }
    catch (T) { checks++; return; }
    throw new InvalidOperationException(message);
}
Pmc2QueryReader Reader(FakeTransport transport, TimeSpan? timeout = null) =>
    new(transport, timeout, maximumPolls: 3, pollDelayMilliseconds: 0);

var normal = new FakeTransport();
var reader = Reader(normal);
var result = reader.Read();
Check(result.Fan1Rpm == 3422 && result.Fan2Rpm == 7500 && result.Flags == 0xA5, "Reply endian/flags");
Check(normal.QueryParameters.SequenceEqual(new byte[] { 0x12, 0x18, 0x19, 0x12 }), "Candidate query sequence");
Check(normal.Commands.All(c => c == 0xD5) && normal.Commands.Count == 4, "Sent a non-query command");
Check(normal.DataReads == 6 && normal.Disposals == 1 && !reader.Stopped, "Response consumption/lease");
int normalOperations = normal.Operations;

var zero = new FakeTransport { Fan1 = 0, Fan2 = 0 };
Check(Reader(zero).Read().Fan1Rpm == 0, "Valid zero RPM was rejected");
var tooHigh = new FakeTransport { Fan2 = 7501 };
Reject<InvalidDataException>(() => Reader(tooHigh).Read(), "Out-of-range feedback accepted");
var stale = new FakeTransport { StaleOutput = true };
var staleReader = Reader(stale);
Reject<InvalidDataException>(() => staleReader.Read(), "Stale FIFO was ignored");
Check(stale.DataReads == 0 && stale.Commands.Count == 0 && stale.Disposals == 1, "Stale bytes were drained or query sent");
var busy = new FakeTransport { AlwaysBusy = true };
Reject<TimeoutException>(() => Reader(busy).Read(), "Busy status was unbounded");
Check(busy.Commands.Count == 0 && busy.StatusReads == 3, "Busy state caused query/retry");
var shortReply = new FakeTransport { ShortFirstFanReply = true };
var shortReader = Reader(shortReply);
Reject<TimeoutException>(() => shortReader.Read(), "Partial response accepted");
Check(shortReply.QueryParameters.Count == 2 && shortReply.DataReads == 2 && shortReader.Stopped, "Partial response led to a next command");
var extra = new FakeTransport { ExtraSecondFanByte = true };
Reject<InvalidDataException>(() => Reader(extra).Read(), "Extra response was silently drained");
Check(extra.QueryParameters.Count == 3 && extra.DataReads == 5, "Unknown response byte consumed");
var flagsChanged = new FakeTransport { ChangeFlags = true };
Reject<InvalidDataException>(() => Reader(flagsChanged).Read(), "Mixed flag state accepted");
var delayed = new FakeTransport { DelayFirstStatusMilliseconds = 20 };
Reject<TimeoutException>(() => Reader(delayed, TimeSpan.FromMilliseconds(5)).Read(), "Late transport result accepted");
Check(delayed.Commands.Count == 0, "Deadline overrun sent a query");
// Deadline errors must identify the completed stage, including setup/cleanup.
// One delayed call per category proves no later protocol operation runs.
foreach (var (operation, stage) in new[] {
    (1, "transaction setup"), (3, "D5/12 command write"),
    (5, "D5/12 parameter write"), (7, "D5/12 data read 0"),
    (normalOperations, "transaction cleanup") })
{
    var transport = new FakeTransport { DelayOperation = operation };
    var lateReader = Reader(transport, TimeSpan.FromMilliseconds(10));
    try { lateReader.Read(); throw new InvalidOperationException("Late operation accepted"); }
    catch (TimeoutException e) { Check(e.Message.Contains(stage), $"Incorrect timeout stage: {e.Message}"); }
    Check(lateReader.Stopped && transport.Disposals == 1, "Late operation did not stop/clean up");
    Check(transport.Operations == operation + (operation == normalOperations ? 0 : 1), "Protocol continued after deadline");
}
using (var cancelled = new CancellationTokenSource())
{
    cancelled.Cancel();
    var transport = new FakeTransport();
    var cancelledReader = Reader(transport);
    Reject<OperationCanceledException>(() => cancelledReader.Read(cancelled.Token), "Pre-cancelled query ran");
    Check(transport.Operations == 0 && !cancelledReader.Stopped, "Pre-cancel poisoned/entered transport");
}
using (var cancelDuring = new CancellationTokenSource())
{
    var transport = new FakeTransport { AfterCommand = cancelDuring.Cancel };
    var cancelledReader = Reader(transport);
    Reject<OperationCanceledException>(() => cancelledReader.Read(cancelDuring.Token), "Mid-query cancellation ignored");
    Check(transport.Commands.Count == 1 && transport.QueryParameters.Count == 0 && transport.Disposals == 1 && cancelledReader.Stopped, "Mid-query cancellation sent more bytes");
}
// Inject I/O failures before/after each operation, including response consumption
// and lease disposal. No failure may cause an automatic query retry.
for (int operation = 1; operation <= normalOperations; operation++)
{
    foreach (bool after in new[] { false, true })
    {
        var transport = new FakeTransport { FaultOperation = operation, FaultAfter = after };
        var failingReader = Reader(transport);
        Reject<IOException>(() => failingReader.Read(), $"I/O fault {operation}/{after} ignored");
        Check(failingReader.Stopped, "Reader survived a partial transaction");
        int count = transport.Operations;
        Reject<InvalidOperationException>(() => failingReader.Read(), "Stopped reader retried");
        Check(transport.Operations == count, "Stopped reader touched transport");
    }
}
foreach (int count in new[] { 0, 1, 2 }) {
    var recoveryIo = new RecoveryTransport(count);
    var recovery = QueryResponseRecovery.Run(recoveryIo);
    Check(recovery.DiscardedBytes.Length == count && recoveryIo.Disposals == 1, "Recovery lost bytes/lease");
    Check(recoveryIo.Writes == 0 && recoveryIo.BytesRead == count, "Recovery wrote a command or over-read");
}
var longResidual = new RecoveryTransport(3);
Reject<InvalidDataException>(() => QueryResponseRecovery.Run(longResidual), "Recovery drained unknown long response");
Check(longResidual.BytesRead == 2 && longResidual.Disposals == 1 && longResidual.Writes == 0, "Residual limit violated");
var busyRecovery = new RecoveryTransport(1) { Busy = true };
Reject<InvalidDataException>(() => QueryResponseRecovery.Run(busyRecovery), "Recovery accepted busy input");
Check(busyRecovery.BytesRead == 0 && busyRecovery.Disposals == 1 && busyRecovery.Writes == 0, "Busy recovery consumed output");
Console.WriteLine($"PMC2 candidate query checks passed: {checks}; injected I/O boundaries: {normalOperations * 2}; hardware access: none.");

sealed class RecoveryTransport(int count) : IPmc2QueryTransport
{
    int remaining = count;
    public bool Busy { get; init; }
    public int BytesRead { get; private set; }
    public int Writes { get; private set; }
    public int Disposals { get; private set; }
    public IDisposable BeginExclusiveTransaction() => new Lease(this);
    public byte ReadStatus() => (byte)((Busy ? 2 : 0) | (remaining > 0 ? 1 : 0));
    public byte ReadData() { if (remaining <= 0) throw new InvalidOperationException("Over-read"); remaining--; return (byte)++BytesRead; }
    public void WriteQueryCommand(byte value) { Writes++; throw new InvalidOperationException("Recovery command write"); }
    public void WriteQueryParameter(byte value) { Writes++; throw new InvalidOperationException("Recovery parameter write"); }
    sealed class Lease(RecoveryTransport owner) : IDisposable { public void Dispose() { owner.Disposals++; } }
}

sealed class FakeTransport : IPmc2QueryTransport
{
    private readonly Queue<byte> fifo = new();
    public int Fan1 { get; init; } = 3422;
    public int Fan2 { get; init; } = 7500;
    public bool StaleOutput { get; init; }
    public bool AlwaysBusy { get; init; }
    public bool ShortFirstFanReply { get; init; }
    public bool ExtraSecondFanByte { get; init; }
    public bool ChangeFlags { get; init; }
    public int DelayFirstStatusMilliseconds { get; init; }
    public int DelayOperation { get; init; }
    public int FaultOperation { get; init; }
    public bool FaultAfter { get; init; }
    public Action? AfterCommand { get; init; }
    public int Operations { get; private set; }
    public int Disposals { get; private set; }
    public int DataReads { get; private set; }
    public int StatusReads { get; private set; }
    public List<byte> Commands { get; } = [];
    public List<byte> QueryParameters { get; } = [];
    private T Operation<T>(Func<T> action)
    {
        Operations++;
        bool fault = Operations == FaultOperation;
        if (fault && !FaultAfter) throw new IOException("Injected transport failure");
        T value = action();
        if (Operations == DelayOperation) Thread.Sleep(30);
        if (fault && FaultAfter) throw new IOException("Injected transport failure after effect");
        return value;
    }
    public IDisposable BeginExclusiveTransaction() => Operation<IDisposable>(() => new Lease(this));
    public byte ReadStatus() => Operation(() =>
    {
        StatusReads++;
        if (StatusReads == 1 && DelayFirstStatusMilliseconds != 0) Thread.Sleep(DelayFirstStatusMilliseconds);
        return (byte)(StaleOutput ? 1 : AlwaysBusy ? 2 : fifo.Count > 0 ? 1 : 0);
    });
    public byte ReadData() => Operation(() => { DataReads++; return fifo.Dequeue(); });
    public void WriteQueryCommand(byte command) => Operation(() =>
    {
        if (command != 0xD5 || fifo.Count != 0) throw new InvalidOperationException("Invalid query command");
        Commands.Add(command); AfterCommand?.Invoke(); return 0;
    });
    public void WriteQueryParameter(byte parameter) => Operation(() =>
    {
        QueryParameters.Add(parameter);
        switch (parameter)
        {
            case 0x12: fifo.Enqueue((byte)(ChangeFlags && QueryParameters.Count == 4 ? 0xAD : 0xA5)); break;
            case 0x18:
                fifo.Enqueue((byte)Fan1);
                if (!ShortFirstFanReply) fifo.Enqueue((byte)(Fan1 >> 8));
                break;
            case 0x19:
                fifo.Enqueue((byte)Fan2); fifo.Enqueue((byte)(Fan2 >> 8));
                if (ExtraSecondFanByte) fifo.Enqueue(0x77);
                break;
            default: throw new InvalidOperationException("Non-query parameter");
        }
        return 0;
    });
    private sealed class Lease(FakeTransport owner) : IDisposable
    {
        public void Dispose() => owner.Operation(() => { owner.Disposals++; return 0; });
    }
}
