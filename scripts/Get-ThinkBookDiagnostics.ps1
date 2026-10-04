<#
.SYNOPSIS
    导出 ThinkBook 的只读系统与 WMI 接口诊断信息。

.DESCRIPTION
    读取系统型号、CPU、BIOS、Windows 版本；枚举 root/wmi 中 Lenovo、
    风扇、热管理和 ACPI 相关 CIM 类的静态方法及参数元数据；查找 Lenovo
    与 PawnIO 相关驱动服务名称。查询失败会按数据项记录在 JSON 的 Errors 数组。

    本脚本不查询风扇控制类的实例，不调用任何 WMI/CIM 方法，不安装驱动，
    也不启动、停止或修改服务。CIM 类元数据仅说明接口的声明，不代表该接口
    在当前机器上可用，也不代表调用其中任何方法是安全的。

.PARAMETER OutputDirectory
    JSON 输出目录。默认是项目根目录下的 artifacts/diagnostics。

.NOTES
    不收集或写出序列号、产品密钥、驱动文件路径或 WMI 实例数据。
#>
[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts/diagnostics')
)

$ErrorActionPreference = 'Stop'
$script:DiagnosticErrors = New-Object 'System.Collections.Generic.List[object]'

function Add-DiagnosticError {
    param(
        [Parameter(Mandatory = $true)][string]$Item,
        [Parameter(Mandatory = $true)][System.Management.Automation.ErrorRecord]$Record
    )

    $script:DiagnosticErrors.Add([ordered]@{
        Item          = $Item
        ExceptionType = $Record.Exception.GetType().FullName
        Message       = $Record.Exception.Message
    })
}

function Get-RegistryValue {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Item
    )

    try {
        $key = Get-ItemProperty -LiteralPath $Path -Name $Name -ErrorAction Stop
        return $key.$Name
    }
    catch {
        Add-DiagnosticError -Item $Item -Record $_
        return $null
    }
}

function Convert-QualifierValue {
    param([AllowNull()][object]$Value)

    if ($null -eq $Value) { return $null }
    if ($Value -is [System.Array]) {
        return @($Value | ForEach-Object { [string]$_ })
    }
    return [string]$Value
}

function Get-QualifierMetadata {
    param([object]$Qualifiers)

    $items = @()
    if ($null -eq $Qualifiers) { return $items }

    try {
        foreach ($qualifier in $Qualifiers) {
            if ($null -eq $qualifier) { continue }
            $items += [ordered]@{
                Name  = [string]$qualifier.Name
                Value = Convert-QualifierValue -Value $qualifier.Value
            }
        }
    }
    catch { Add-DiagnosticError -Item 'interfaces.qualifiers' -Record $_ }
    return $items
}

function Get-MethodMetadata {
    param([object]$CimClassMethod)

    $parameters = @()
    if ($null -ne $CimClassMethod.Parameters) {
        foreach ($parameter in $CimClassMethod.Parameters) {
            if ($null -eq $parameter) { continue }
            $parameters += [ordered]@{
                Name                = [string]$parameter.Name
                CimType             = [string]$parameter.CimType
                IsArray             = ([string]$parameter.CimType -match 'Array$')
                IsIn                = [bool]$parameter.Qualifiers['in'].Value
                IsOut               = [bool]$parameter.Qualifiers['out'].Value
                ReferenceClassName  = [string]$parameter.ReferenceClassName
                Qualifiers          = @(Get-QualifierMetadata -Qualifiers $parameter.Qualifiers)
            }
        }
    }

    [ordered]@{
        Name       = if ([string]::IsNullOrWhiteSpace([string]$CimClassMethod.CimMethodName)) { [string]$CimClassMethod.Name } else { [string]$CimClassMethod.CimMethodName }
        ReturnType = [string]$CimClassMethod.ReturnType
        Qualifiers = @(Get-QualifierMetadata -Qualifiers $CimClassMethod.Qualifiers)
        Parameters = @($parameters)
    }
}

$system = [ordered]@{
    Manufacturer = $null
    Model        = $null
    ProductName  = $null
    SystemType   = $null
}
$cpu = [ordered]@{
    Name                     = $null
    Manufacturer             = $null
    NumberOfCores            = $null
    NumberOfLogicalProcessors = $null
}
$bios = [ordered]@{
    Manufacturer      = $null
    SMBIOSBIOSVersion = $null
    Version            = $null
    ReleaseDate        = $null
}
$os = [ordered]@{
    Caption        = $null
    Version        = $null
    BuildNumber    = $null
    OSArchitecture = $null
}

try {
    $computerSystem = Get-CimInstance -ClassName Win32_ComputerSystem -ErrorAction Stop
    $system.Manufacturer = $computerSystem.Manufacturer
    $system.Model = $computerSystem.Model
    $system.SystemType = $computerSystem.SystemType
}
catch {
    Add-DiagnosticError -Item 'system.computerSystem' -Record $_
}

try {
    $processor = Get-CimInstance -ClassName Win32_Processor -ErrorAction Stop | Select-Object -First 1
    if ($null -ne $processor) {
        $cpu.Name = $processor.Name
        $cpu.Manufacturer = $processor.Manufacturer
        $cpu.NumberOfCores = $processor.NumberOfCores
        $cpu.NumberOfLogicalProcessors = $processor.NumberOfLogicalProcessors
    }
}
catch {
    Add-DiagnosticError -Item 'system.processor' -Record $_
}

try {
    $biosInstance = Get-CimInstance -ClassName Win32_BIOS -ErrorAction Stop | Select-Object -First 1
    if ($null -ne $biosInstance) {
        $bios.Manufacturer = $biosInstance.Manufacturer
        $bios.SMBIOSBIOSVersion = $biosInstance.SMBIOSBIOSVersion
        $bios.Version = $biosInstance.Version
        $bios.ReleaseDate = $biosInstance.ReleaseDate
    }
}
catch {
    Add-DiagnosticError -Item 'system.bios' -Record $_
}

try {
    $osInstance = Get-CimInstance -ClassName Win32_OperatingSystem -ErrorAction Stop | Select-Object -First 1
    if ($null -ne $osInstance) {
        $os.Caption = $osInstance.Caption
        $os.Version = $osInstance.Version
        $os.BuildNumber = $osInstance.BuildNumber
        $os.OSArchitecture = $osInstance.OSArchitecture
    }
}
catch {
    Add-DiagnosticError -Item 'system.operatingSystem' -Record $_
}

# Registry values are read only and used only when their CIM counterpart is absent.
$biosRegistryPath = 'HKLM:\HARDWARE\DESCRIPTION\System\BIOS'
$system.ProductName = Get-RegistryValue -Path $biosRegistryPath -Name 'SystemVersion' -Item 'registry.systemVersion'
if ([string]::IsNullOrWhiteSpace([string]$system.Manufacturer)) {
    $system.Manufacturer = Get-RegistryValue -Path $biosRegistryPath -Name 'SystemManufacturer' -Item 'fallback.registry.systemManufacturer'
}
if ([string]::IsNullOrWhiteSpace([string]$system.Model)) {
    $system.Model = Get-RegistryValue -Path $biosRegistryPath -Name 'SystemProductName' -Item 'fallback.registry.systemProductName'
}
if ([string]::IsNullOrWhiteSpace([string]$bios.Manufacturer)) {
    $bios.Manufacturer = Get-RegistryValue -Path $biosRegistryPath -Name 'BIOSVendor' -Item 'fallback.registry.biosVendor'
}
if ([string]::IsNullOrWhiteSpace([string]$bios.SMBIOSBIOSVersion)) {
    $bios.SMBIOSBIOSVersion = Get-RegistryValue -Path $biosRegistryPath -Name 'BIOSVersion' -Item 'fallback.registry.biosVersion'
}
if ([string]::IsNullOrWhiteSpace([string]$bios.ReleaseDate)) {
    $bios.ReleaseDate = Get-RegistryValue -Path $biosRegistryPath -Name 'BIOSReleaseDate' -Item 'fallback.registry.biosReleaseDate'
}
if ([string]::IsNullOrWhiteSpace([string]$cpu.Name)) {
    $cpu.Name = Get-RegistryValue -Path 'HKLM:\HARDWARE\DESCRIPTION\System\CentralProcessor\0' -Name 'ProcessorNameString' -Item 'fallback.registry.processorName'
}
if ([string]::IsNullOrWhiteSpace([string]$cpu.Manufacturer)) {
    $cpu.Manufacturer = Get-RegistryValue -Path 'HKLM:\HARDWARE\DESCRIPTION\System\CentralProcessor\0' -Name 'VendorIdentifier' -Item 'fallback.registry.processorManufacturer'
}
if ([string]::IsNullOrWhiteSpace([string]$os.Caption) -or
    [string]::IsNullOrWhiteSpace([string]$os.Version) -or
    [string]::IsNullOrWhiteSpace([string]$os.BuildNumber)) {
    $currentVersionPath = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
    if ([string]::IsNullOrWhiteSpace([string]$os.Caption)) {
        $os.Caption = Get-RegistryValue -Path $currentVersionPath -Name 'ProductName' -Item 'fallback.registry.osProductName'
    }
    if ([string]::IsNullOrWhiteSpace([string]$os.Version)) {
        $os.Version = Get-RegistryValue -Path $currentVersionPath -Name 'DisplayVersion' -Item 'fallback.registry.osDisplayVersion'
    }
    if ([string]::IsNullOrWhiteSpace([string]$os.BuildNumber)) {
        $os.BuildNumber = Get-RegistryValue -Path $currentVersionPath -Name 'CurrentBuildNumber' -Item 'fallback.registry.osBuildNumber'
    }
}
if ([string]::IsNullOrWhiteSpace([string]$os.OSArchitecture)) {
    $os.OSArchitecture = if ([Environment]::Is64BitOperatingSystem) { '64-bit' } else { '32-bit' }
}

$wmiClasses = @()
try {
    $allWmiClasses = Get-CimClass -Namespace 'root/wmi' -ErrorAction Stop
    foreach ($class in $allWmiClasses) {
        if ($class.CimClassName -notmatch '(?i)(LENOVO|FAN|THERMAL|ACPI)') { continue }

        $methods = @()
        if ($null -ne $class.CimClassMethods) {
            foreach ($method in $class.CimClassMethods) {
                if ($null -ne $method) {
                    $methods += ,(Get-MethodMetadata -CimClassMethod $method)
                }
            }
        }

        $wmiClasses += [ordered]@{
            Namespace = 'root/wmi'
            ClassName = [string]$class.CimClassName
            Methods   = @($methods)
        }
    }
}
catch {
    Add-DiagnosticError -Item 'interfaces.rootWmi.classMetadata' -Record $_
}

$driverServices = @()
$serviceIndex = @{}
try {
    $systemDrivers = Get-CimInstance -ClassName Win32_SystemDriver -ErrorAction Stop
    foreach ($driver in $systemDrivers) {
        $serviceName = [string]$driver.Name
        $displayName = [string]$driver.DisplayName
        if (($serviceName -match '(?i)(Lenovo|PawnIO)') -or ($displayName -match '(?i)(Lenovo|PawnIO)')) {
            $key = $serviceName.ToLowerInvariant()
            $serviceIndex[$key] = [ordered]@{
                ServiceName = $serviceName
                DisplayName = $displayName
                State       = [string]$driver.State
                StartMode   = [string]$driver.StartMode
                Source      = 'Win32_SystemDriver'
            }
        }
    }
}
catch {
    Add-DiagnosticError -Item 'services.win32SystemDriver' -Record $_
}

# The service registry inventory also finds installed entries that are currently stopped.
$servicesRegistryPath = 'HKLM:\SYSTEM\CurrentControlSet\Services'
try {
    $serviceKeys = Get-ChildItem -LiteralPath $servicesRegistryPath -ErrorAction Stop
    foreach ($serviceKey in $serviceKeys) {
        try {
            $serviceProperties = Get-ItemProperty -LiteralPath $serviceKey.PSPath -ErrorAction Stop
            $serviceName = [string]$serviceKey.PSChildName
            $displayName = [string]$serviceProperties.DisplayName
            $imagePath = [string]$serviceProperties.ImagePath
            if (($serviceName -match '(?i)(Lenovo|PawnIO)') -or
                ($displayName -match '(?i)(Lenovo|PawnIO)') -or
                ($imagePath -match '(?i)(Lenovo|PawnIO)')) {
                $key = $serviceName.ToLowerInvariant()
                if ($serviceIndex.ContainsKey($key)) {
                    $serviceIndex[$key].Source = 'Win32_SystemDriver+ServicesRegistry'
                    if ([string]::IsNullOrWhiteSpace([string]$serviceIndex[$key].DisplayName)) {
                        $serviceIndex[$key].DisplayName = $displayName
                    }
                }
                else {
                    $serviceIndex[$key] = [ordered]@{
                        ServiceName = $serviceName
                        DisplayName = $displayName
                        State       = $null
                        StartMode   = $null
                        Source      = 'ServicesRegistry'
                    }
                }
            }
        }
        catch {
            Add-DiagnosticError -Item ("services.registry.{0}" -f $serviceKey.PSChildName) -Record $_
        }
    }
}
catch {
    Add-DiagnosticError -Item 'services.registryEnumeration' -Record $_
}
$driverServices = @($serviceIndex.Values | Sort-Object ServiceName)

$diagnostics = [ordered]@{
    SchemaVersion = 1
    CapturedAtUtc = [DateTime]::UtcNow.ToString('o')
    ReadOnly      = $true
    System        = $system
    CPU           = $cpu
    BIOS          = $bios
    OperatingSystem = $os
    Interfaces    = [ordered]@{
        RootWmiLenovoAndThermalClasses = @($wmiClasses)
        MetadataOnly                  = $true
        MethodsInvoked                = $false
    }
    DriverServices = @($driverServices)
    Errors         = @($script:DiagnosticErrors.ToArray())
}

$resolvedOutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $resolvedOutputDirectory -Force | Out-Null
$fileName = 'thinkbook-diagnostics-{0}.json' -f [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')
$outputPath = Join-Path $resolvedOutputDirectory $fileName
$json = $diagnostics | ConvertTo-Json -Depth 16
[System.IO.File]::WriteAllText($outputPath, $json + [Environment]::NewLine, ([System.Text.UTF8Encoding]::new($false)))
Write-Output $outputPath
