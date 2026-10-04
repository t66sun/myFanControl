using System.Diagnostics;
using System.Text.Json;

namespace ThinkBookControl;

/// <summary>The separate host releases RPM overrides when stdin closes or heartbeats stop.</summary>
internal sealed class ControlClient : IAsyncDisposable
{
    private readonly Process process;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Task<string> errors;
    private bool ready, disposed;

    internal ControlClient(int? parentPid = null)
    {
        string helper = Path.Combine(AppContext.BaseDirectory, "control-host", "EcPmcConfigProbe.exe");
        if (!File.Exists(helper)) throw new FileNotFoundException("控制后端未找到。", helper);
        process = new Process { StartInfo = new ProcessStartInfo(helper) {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        }};
        process.StartInfo.ArgumentList.Add("--control-host");
        process.StartInfo.ArgumentList.Add((parentPid ?? Environment.ProcessId).ToString());
        if (!process.Start()) throw new IOException("控制后端启动失败。");
        errors = process.StandardError.ReadToEndAsync();
    }

    internal Task SetAsync(int fan1, int fan2) => SendAsync(new { mode = "manual", fan1, fan2 });
    internal Task RestoreAsync() => SendAsync(new { mode = "firmware" });
    internal Task HeartbeatAsync() => SendAsync(new { mode = "heartbeat" });

    private async Task SendAsync(object command)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try {
            if (disposed) throw new ObjectDisposedException(nameof(ControlClient));
            if (!ready) { await ReadAckAsync().ConfigureAwait(false); ready = true; }
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(command)).ConfigureAwait(false);
            await process.StandardInput.FlushAsync().ConfigureAwait(false);
            await ReadAckAsync().ConfigureAwait(false);
        } finally { gate.Release(); }
    }

    private async Task ReadAckAsync()
    {
        string? line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
        if (line is null) {
            string detail = errors.IsCompleted ? await errors.ConfigureAwait(false) : "";
            throw new IOException("控制后端已停止。" + detail);
        }
        using var ack = JsonDocument.Parse(line);
        if (!ack.RootElement.GetProperty("ok").GetBoolean())
            throw new IOException(ack.RootElement.GetProperty("error").GetString() ?? "控制后端拒绝请求。");
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try {
            if (disposed) return;
            disposed = true;
            process.StandardInput.Close();
            // Never terminate a host before its independent override clear finishes.
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            string detail = await errors.ConfigureAwait(false);
            int exit = process.ExitCode;
            process.Dispose();
            if (exit != 0) throw new IOException("控制后端退出时报告错误：" + detail);
        } finally { gate.Release(); }
    }
}
