using Microsoft.Win32;

namespace myFanControl.Monitoring;

public sealed record MachineIdentity(
    string? Manufacturer,
    string? Model,
    string? BiosVendor,
    string? BiosVersion,
    string? BiosReleaseDate,
    IReadOnlyList<string> Errors)
{
    public string? ProductName { get; init; }
}

/// <summary>
/// Reads only the model and BIOS values Windows publishes in the BIOS registry key.
/// </summary>
public static class MachineIdentityReader
{
    private const string BiosRegistryPath = @"HARDWARE\DESCRIPTION\System\BIOS";

    public static MachineIdentity Read()
    {
        var errors = new List<string>();
        string? manufacturer = null;
        string? model = null;
        string? biosVendor = null;
        string? biosVersion = null;
        string? biosReleaseDate = null;
        string? productName = null;

        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(BiosRegistryPath, writable: false);
            if (key is null)
            {
                errors.Add($"无法打开只读注册表项 HKLM\\{BiosRegistryPath}。");
            }
            else
            {
                manufacturer = ReadString(key, "SystemManufacturer");
                productName = ReadString(key, "SystemProductName");
                model = string.Join(" / ", new[] { ReadString(key, "SystemVersion"), productName }
                    .OfType<string>());
                if (string.IsNullOrWhiteSpace(model)) model = null;
                biosVendor = ReadString(key, "BIOSVendor");
                biosVersion = ReadString(key, "BIOSVersion");
                biosReleaseDate = ReadString(key, "BIOSReleaseDate");

                if (manufacturer is null)
                    errors.Add("注册表未提供 SystemManufacturer 字段。");
                if (model is null)
                    errors.Add("注册表未提供 SystemProductName 型号字段。");
                if (biosVersion is null)
                    errors.Add("注册表未提供 BIOSVersion 字段。");
            }
        }
        catch (Exception exception)
        {
            errors.Add($"读取型号或 BIOS 注册表信息失败：{exception.GetType().Name}: {exception.Message}");
        }

        return new MachineIdentity(manufacturer, model, biosVendor, biosVersion, biosReleaseDate, errors)
        {
            ProductName = productName
        };
    }

    private static string? ReadString(RegistryKey key, string name)
    {
        object? value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (value is null)
            return null;

        string text = value switch
        {
            string[] values => string.Join(" ", values.Where(static item => !string.IsNullOrWhiteSpace(item))),
            _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty
        };

        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }
}
