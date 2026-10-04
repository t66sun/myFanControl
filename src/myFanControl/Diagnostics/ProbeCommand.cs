namespace myFanControl.Diagnostics;

internal sealed record ProbeOptions(int Samples, string OutputPath);

internal sealed record ProbeParseResult(ProbeOptions? Options, string? OutputPath, string? Error)
{
    public bool Succeeded => Options is not null;
}

internal static class ProbeCommand
{
    public static bool IsProbeRequested(IReadOnlyList<string> args) =>
        args.Any(static arg => string.Equals(arg, "--probe", StringComparison.OrdinalIgnoreCase));

    public static ProbeParseResult Parse(IReadOnlyList<string> args)
    {
        int samples = 5;
        string? outputPath = null;
        string? error = null;

        for (int index = 0; index < args.Count; index++)
        {
            string arg = args[index];
            if (string.Equals(arg, "--probe", StringComparison.OrdinalIgnoreCase))
                continue;

            if (string.Equals(arg, "--samples", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 >= args.Count || !int.TryParse(args[++index], out samples) || samples < 1 || samples > 600)
                {
                    error = "--samples 必须是 1 到 600 之间的整数。";
                    break;
                }

                continue;
            }

            if (string.Equals(arg, "--output", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 >= args.Count || string.IsNullOrWhiteSpace(args[++index]))
                {
                    error = "--output 后必须提供 JSON 文件路径。";
                    break;
                }

                outputPath = args[index];
                continue;
            }

            error = $"无法识别的诊断参数：{arg}";
            break;
        }

        if (error is null && outputPath is null)
            error = "诊断模式需要 --output <path> 指定 JSON 输出文件。";

        if (error is not null)
            return new ProbeParseResult(null, outputPath, error);

        return new ProbeParseResult(new ProbeOptions(samples, outputPath!), outputPath, null);
    }
}
