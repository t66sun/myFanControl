using System.Collections;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using System.Windows.Threading;
using FanControl.Core;
using Microsoft.Win32;
using myFanControl;
using myFanControl.Editing;
using myFanControl.Monitoring;

internal static class Program
{
    private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly string[] LegacyDetailNames =
        ["ModelText", "BiosText", "BiosDateText", "ThermalText", "VpcText", "TemperatureText", "StatusText", "ErrorsText"];

    [STAThread]
    private static int Main(string[] args)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        try { return Verify(args); }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
        finally { app.Shutdown(); }
    }

    private static int Verify(string[] args)
    {
        if (args.Length != 2) throw new ArgumentException("Expected probe JSON and image directory");
        Directory.CreateDirectory(args[1]);
        var privateRoot = Path.Combine(args[1], "ui-verification-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(privateRoot);
        try { VerifyOfflineUi(args[0], args[1], privateRoot); }
        finally { Directory.Delete(privateRoot, recursive: true); }
        Console.WriteLine("Offline WPF UI, editor, settings, tray, and synthetic power checks passed; no sensor monitor or control backend was started.");
        return 0;
    }

    private static void VerifyOfflineUi(string fixturePath, string imageDirectory, string privateRoot)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(fixturePath));
        var sample = document.RootElement.GetProperty("samples")[0];
        var readings = sample.GetProperty("temperatures").EnumerateArray().Select(s => new TemperatureReading(
            s.GetProperty("hardwareType").GetString()!, s.GetProperty("hardwareName").GetString()!,
            s.GetProperty("sensorName").GetString()!, s.GetProperty("identifier").GetString()!,
            s.GetProperty("valueCelsius").ValueKind == JsonValueKind.Null ? null : s.GetProperty("valueCelsius").GetSingle())).ToArray();
        var errors = sample.GetProperty("errors").EnumerateArray().Select(e => e.GetString()!).ToArray();
        var fans = sample.GetProperty("fans").Deserialize<FanSpeedReading[]>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var ec = sample.TryGetProperty("ecFanTelemetry", out var ecJson) && ecJson.ValueKind != JsonValueKind.Null
            ? ecJson.Deserialize<EcFanTelemetrySnapshot>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) : null;
        var fixture = new TemperatureSnapshot(sample.GetProperty("capturedAtUtc").GetDateTimeOffset(), readings, errors)
            { Fans = fans, EcFanTelemetry = ec };
        Program.fixture = fixture;
        var thermal = document.RootElement.GetProperty("thermalState").Deserialize<LenovoThermalSnapshot>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        if (!thermal.Available) throw new InvalidOperationException("Expected an actual successful firmware query fixture.");
        var vpc = document.RootElement.GetProperty("vpcFanState").Deserialize<VpcFanSnapshot>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        if (vpc.Available || vpc.RawMode != null || !vpc.Errors.Any(e => e.Contains("邮箱回显")))
            throw new InvalidOperationException("Retired VPC interface must not report a fan mode.");

        string settingsPath = Path.Combine(privateRoot, "control-settings.json");
        var window = new MainWindow(settingsPath);
        DetachLoadedHandler(window);
        Invoke(window, "InitializeControlUi"); // Explicit setup only; OnLoaded (which starts hardware sampling) is detached.
        ShowIdentity(window, MachineIdentityReader.Read());
        ShowSnapshot(window, fixture with { CapturedAtUtc = DateTimeOffset.UtcNow });
        ShowThermal(window, thermal);
        ShowVpc(window, vpc);
        VerifySensorPresentation(window, fans, ec, thermal, vpc, errors);
        VerifyCompatibilityAndSettings(window, settingsPath);
        VerifyEditor(window, settingsPath);
        VerifyThemeColor(window, settingsPath, privateRoot);
        VerifyAppliedSummary(window);

        InitializeTray(window);
        VerifyTemperatureTray(window, imageDirectory);
        window.Show(); // Creates an HWND and registers native power notices; no Loaded sampler remains.
        window.UpdateLayout();
        VerifyLayoutsAndScreenshots(window, imageDirectory);
        VerifyCurveLive(window,imageDirectory);
        CaptureManualTab(window, imageDirectory);
        var graph = (UIElement)window.FindName("CurveGraph")!;
        Invoke(window, "LoadNodes", 0, "40:2500;60:3500;75:5000;90:7500"); Invoke(window, "LoadNodes", 1, "40:2500;60:3500;75:5000;90:7500"); Invoke(window, "UpdateEditorState");
        Require(ReferenceEquals(System.Windows.Input.Keyboard.Focus(graph), graph), "Curve editor could not receive keyboard focus.");
        var source = PresentationSource.FromVisual(window) ?? throw new InvalidOperationException("Window has no WPF input source.");
        var keyboardGrid = (DataGrid)window.FindName("NodeGrid")!;
        var keyboardNodes = (IList)keyboardGrid.ItemsSource!;
        graph.GetType().GetProperty("SelectedNode")!.SetValue(graph, keyboardNodes.Count - 1);
        keyboardGrid.SelectedItem = keyboardNodes[^1];
        Click(window, "RemoveNodeButton");
        Require(keyboardNodes.Count == 3, "Removing the selected final row did not update the curve editor's node collection.");
        RaiseKey(graph, source, Key.Right);
        Invoke(window, "LoadNodes", 0, "40:2500;60:3500;75:5000;90:7500"); Invoke(window, "UpdateEditorState");
        graph.GetType().GetProperty("SelectedNode")!.SetValue(graph, -1);
        RaiseKey(graph, source, Key.End);
        Require((int)graph.GetType().GetProperty("SelectedNode")!.GetValue(graph)! == 3, "End key did not select the last curve node.");
        RaiseKey(graph, source, Key.Right); // Last node remains fixed at 90°C / 7500 RPM.
        Require(Box(window, "Fan1CurveBox").Text.EndsWith("90:7500"), "Arrow-key editing moved the fixed last node.");
        RaiseKey(graph, source, Key.Home);
        Require((int)graph.GetType().GetProperty("SelectedNode")!.GetValue(graph)! == 0, "Home key did not select the first curve node.");
        RaiseKey(graph, source, Key.Left);
        Require(Box(window, "Fan1CurveBox").Text.StartsWith("39:2500;"), "Left arrow did not edit the selected point through the routed keyboard path.");
        RaiseKey(graph, source, Key.Up);
        Require(Box(window, "Fan1CurveBox").Text.StartsWith("39:2600;"), "Up arrow did not edit the selected point through the routed keyboard path.");
        RaiseKey(graph, source, Key.Down);
        Invoke(window, "LoadNodes", 0, "40:2500;60:3500;75:5000;90:7500"); Invoke(window, "UpdateEditorState");
        var tabTraversal = graph.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        Require(tabTraversal && !ReferenceEquals(System.Windows.Input.Keyboard.FocusedElement, graph), "Tab-order traversal did not leave the curve editor.");
        VerifyPowerNotices(window);
        VerifyRealGridEditing(window, imageDirectory);
        VerifyPresetCrud(window, settingsPath, imageDirectory);
        VerifyExitCancelKeepsWindowOpen(window);
        VerifyTrayLifecycle(window);
        VerifyManualPersistencePreservesCurveSelection(privateRoot);
    }

    private static void VerifySensorPresentation(MainWindow window, FanSpeedReading[] fans, EcFanTelemetrySnapshot? ec,
        LenovoThermalSnapshot thermal, VpcFanSnapshot vpc, string[] fixtureErrors)
    {
        Require(Text(window, "CpuTemperatureText").Text != "不可用", "Fresh CPU temperature was not shown in the summary.");
        Require(Text(window, "GpuTemperatureText").Text != "不可用", "Fresh GPU temperature was not shown in the summary.");
        if (ec is { Available: true })
        {
            Require(Text(window, "Fan1ActualText").Text.Contains($"{ec.Fan1Rpm} RPM"), "Fresh EC fan 1 RPM was not shown.");
            Require(Text(window, "Fan2ActualText").Text.Contains($"{ec.Fan2Rpm} RPM"), "Fresh EC fan 2 RPM was not shown.");
        }
        var missing = new TemperatureSnapshot(DateTimeOffset.UtcNow - TimeSpan.FromSeconds(6), [], [])
            { Fans = [], EcFanTelemetry = null };
        ShowSnapshot(window, missing);
        Require(Text(window, "CpuTemperatureText").Text == "不可用" && Text(window, "Fan1ActualText").Text == "不可用",
            "Stale telemetry must be unavailable in the top summary.");
        var future = fixture with { CapturedAtUtc = DateTimeOffset.UtcNow.AddSeconds(1) };
        ShowSnapshot(window, future);
        Require(Text(window, "CpuTemperatureText").Text == "不可用" && Text(window, "GpuTemperatureText").Text == "不可用" &&
                Text(window, "Fan1ActualText").Text == "不可用" && Text(window, "Fan2ActualText").Text == "不可用",
            "Future-dated telemetry must be unavailable in the summary.");
        var empty = new TemperatureSnapshot(DateTimeOffset.UtcNow, [], []) { Fans = [], EcFanTelemetry = null };
        ShowSnapshot(window, empty);
        Require(Text(window, "CpuTemperatureText").Text == "不可用" && Text(window, "GpuTemperatureText").Text == "不可用" &&
                Text(window, "Fan1ActualText").Text == "不可用" && Text(window, "Fan2ActualText").Text == "不可用",
            "Missing telemetry must be unavailable in the summary.");
        ShowSnapshot(window, fixture with { CapturedAtUtc = DateTimeOffset.UtcNow });

        Require(Text(window, "ThermalText").Text.Contains(thermal.ModeName), "Firmware thermal mode was not displayed in details.");
        Require(Text(window, "VpcText").Text.Contains("双路 RPM 控制") && Text(window, "VpcText").Text.Contains("正式签名"),
            $"Verified control backend was not displayed in details: '{Text(window, "VpcText").Text}'.");
        Require(fans.Length != 0 || Text(window, "TemperatureText").Text.Contains("未提供有效转速"),
            "Missing fan telemetry was not displayed in details.");
        if (ec is { Available: true })
            Require(Text(window, "TemperatureText").Text.Contains("EC 测速反馈") && Text(window, "TemperatureText").Text.Contains($"{ec.Fan1Rpm} RPM"),
                "Detailed EC telemetry was not retained.");
        foreach (string name in LegacyDetailNames) _ = window.FindName(name) ?? throw new InvalidOperationException($"Missing legacy detail control {name}.");
        string shownErrors = Text(window, "ErrorsText").Text;
        Require(fixtureErrors.All(error => shownErrors.Contains(error)), "Sensor errors were not retained in the separate detail area.");
    }

    private static TemperatureSnapshot fixture = null!;

    private static void VerifyThemeColor(MainWindow window, string settingsPath, string privateRoot)
    {
        var originalControl = GetField(window, "_controlClient");
        var originalMode = GetField(window, "_controlMode");
        string manual = Box(window, "Fan1RpmBox").Text;
        Box(window, "Fan1RpmBox").Text = "unfinished";
        Require((bool)Invoke(window, "SaveThemeColor", "#7C3AED")!, "Custom theme could not be saved.");
        Require(((SolidColorBrush)((Button)window.FindName("ApplyButton")!).Background).Color == Color.FromRgb(124, 58, 237),
            "Changing the theme did not update the existing button resource.");
        Require(Box(window, "Fan1RpmBox").Text == "unfinished", "Theme selection overwrote the manual draft.");
        using (var saved = JsonDocument.Parse(File.ReadAllText(settingsPath)))
        {
            Require(saved.RootElement.GetProperty("ThemeColor").GetString() == "#7C3AED", "Custom theme was not persisted.");
            Require(saved.RootElement.GetProperty("Fan1Rpm").GetInt32() == (int)GetField(window, "_settingsFan1Rpm")!,
                "Theme persistence saved an unfinished control draft.");
        }
        var reloaded = new MainWindow(settingsPath); DetachLoadedHandler(reloaded); Invoke(reloaded, "InitializeControlUi");
        Require((string)GetField(reloaded, "_themeColor")! == "#7C3AED", "Custom theme was not restored on reload.");
        reloaded.Hide();
        foreach (var (color, foreground) in new[] { ("#FFFFFF", Colors.Black), ("#FFFF00", Colors.Black), ("#000000", Colors.White) })
        {
            Require((bool)Invoke(window, "SaveThemeColor", color)!, "Edge theme could not be saved.");
            Require(((SolidColorBrush)window.FindResource("AccentForeground")).Color == foreground, "Theme foreground is unreadable.");
            Require(((SolidColorBrush)window.FindResource("AccentInk")).Color != Colors.White, "White theme hid chart/focus strokes.");
        }
        Click(window, "ResetThemeColorButton");
        Require((string)GetField(window, "_themeColor")! == "#0F766E", "Reset did not restore the default theme.");
        Require(ReferenceEquals(originalControl, GetField(window, "_controlClient")) && Equals(originalMode, GetField(window, "_controlMode")),
            "Changing theme touched the control backend or mode.");
        Box(window, "Fan1RpmBox").Text = manual;

        var failing = new MainWindow(Path.Combine(privateRoot, "missing-theme-directory", "settings.json"));
        DetachLoadedHandler(failing); Invoke(failing, "InitializeControlUi");
        Require(!(bool)Invoke(failing, "SaveThemeColor", "#7C3AED")! && (string)GetField(failing, "_themeColor")! == "#0F766E",
            "Failed theme save changed the active theme.");
        failing.Hide();
        string invalidThemePath = Path.Combine(privateRoot, "invalid-theme.json");
        File.WriteAllText(invalidThemePath, JsonSerializer.Serialize(new { Fan1Rpm = 4200, Fan2Rpm = 4400,
            Fan1Curve = "40:2500;90:7500", Fan2Curve = "40:2500;90:7500", ThemeColor = "not-a-color" }));
        var invalidTheme = new MainWindow(invalidThemePath); DetachLoadedHandler(invalidTheme); Invoke(invalidTheme, "InitializeControlUi");
        Require((string)GetField(invalidTheme, "_themeColor")! == "#0F766E" && Box(invalidTheme, "Fan1RpmBox").Text == "4200" &&
            Text(invalidTheme, "SettingsStatusText").Text.Contains("主题色无效"), "Invalid theme discarded valid control settings or lacked feedback.");
        invalidTheme.Hide();
    }

    private static void VerifyCompatibilityAndSettings(MainWindow window, string settingsPath)
    {
        string legacyPath = Path.Combine(Path.GetDirectoryName(settingsPath)!, "legacy-spaced.json");
        File.WriteAllText(legacyPath, """
            { "Fan1Rpm": 4200, "Fan2Rpm": 4400, "Fan1Curve": " 40.5 : 2500 ;60:3500;75:5000;90:7500 ", "Fan2Curve": " 40.5 : 2500 ;60:3500;75:5000;90:7500 " }
            """);
        // Re-initialization is intentionally idempotent. A second offline window exercises legacy loading.
        var legacy = new MainWindow(legacyPath);
        DetachLoadedHandler(legacy); Invoke(legacy, "InitializeControlUi");
        Require(((TextBox)legacy.FindName("Fan1RpmBox")!).Text == "4200" && ((TextBox)legacy.FindName("Fan2RpmBox")!).Text == "4400",
            "The old four-field settings format was not loaded.");
        Require(!(bool)legacy.GetType().GetProperty("CurveDirty", PrivateInstance)!.GetValue(legacy)!,
            "Loading a legal legacy curve's whitespace/double representation marked it dirty before editing.");
        Require(Box(legacy, "Fan1CurveBox").Text.Contains("40.5:2500"), "Legacy decimal node precision was rounded or lost.");
        Require((bool)Invoke(legacy, "SaveControlSettings")!, "Saving upgraded settings failed.");
        using var saved = JsonDocument.Parse(File.ReadAllText(legacyPath));
        foreach (string property in new[] { "Fan1Rpm", "Fan2Rpm", "Fan1Curve", "Fan2Curve" })
            Require(saved.RootElement.TryGetProperty(property, out _), $"Saved settings lost {property}.");
        Require(Box(legacy, "IncreaseDelayBox").Text == "0" && saved.RootElement.GetProperty("IncreaseDelaySeconds").GetInt32() == 0,
            "Legacy settings did not default to an immediate fan increase.");
        legacy.Hide();

        string delayPath = Path.Combine(Path.GetDirectoryName(settingsPath)!, "delay.json");
        File.WriteAllText(delayPath, JsonSerializer.Serialize(new { Fan1Rpm = 3600, Fan2Rpm = 3600,
            Fan1Curve = "40:2500;90:7500", Fan2Curve = "40:2500;90:7500", IncreaseDelaySeconds = 1 }));
        var delayed = new MainWindow(delayPath); DetachLoadedHandler(delayed); Invoke(delayed, "InitializeControlUi");
        Require(Box(delayed, "IncreaseDelayBox").Text == "1", "Saved heating delay was not loaded.");
        delayed.Hide();
        File.WriteAllText(delayPath, JsonSerializer.Serialize(new { Fan1Rpm = 3600, Fan2Rpm = 3600,
            Fan1Curve = "40:2500;90:7500", Fan2Curve = "40:2500;90:7500", IncreaseDelaySeconds = 1,
            Fan1Response = new { HeatingHysteresis = 3, CoolingHysteresis = 5, HeatingDelaySeconds = 2, CoolingDelaySeconds = 4 },
            Fan2Response = new { HeatingHysteresis = 1, CoolingHysteresis = 2, HeatingDelaySeconds = 6, CoolingDelaySeconds = 0 } }));
        var independent = new MainWindow(delayPath); DetachLoadedHandler(independent); Invoke(independent, "InitializeControlUi");
        Require(Box(independent, "IncreaseDelayBox").Text == "2", "Per-fan response did not override the legacy shared delay.");
        Require(Box(independent, "HeatingHysteresisBox").Text == "3" && Box(independent, "DecreaseDelayBox").Text == "4", "Fan 1 response did not load.");
        ((RadioButton)independent.FindName("Fan2Choice")!).IsChecked = true;
        Require(Box(independent, "IncreaseDelayBox").Text == "6" && Box(independent, "HeatingHysteresisBox").Text == "1", "Fan 2 response did not remain independent.");
        Box(independent, "HeatingHysteresisBox").Text = "11";
        Require(!((Button)independent.FindName("ApplyButton")!).IsEnabled, "Out-of-range response was accepted.");
        ((RadioButton)independent.FindName("Fan1Choice")!).IsChecked = true;
        Require(!((Button)independent.FindName("ApplyButton")!).IsEnabled && Box(independent, "IncreaseDelayBox").Text == "2", "Switching fans lost an invalid draft or changed the other fan.");
        independent.Hide();
        File.WriteAllText(delayPath,"""
            {"Fan1Rpm":4200,"Fan2Rpm":4400,"Fan1Curve":"40:2500;90:7500","Fan2Curve":"40:2500;90:7500",
             "Fan1Response":{"HeatingHysteresis":"bad"},"Fan2Response":{"HeatingDelaySeconds":6}}
            """);
        var malformed=new MainWindow(delayPath); DetachLoadedHandler(malformed); Invoke(malformed,"InitializeControlUi");
        Require(Box(malformed,"Fan1RpmBox").Text=="4200" && Box(malformed,"HeatingHysteresisBox").Text=="0", "A malformed response discarded valid settings instead of using defaults.");
        ((RadioButton)malformed.FindName("Fan2Choice")!).IsChecked=true;
        Require(Box(malformed,"IncreaseDelayBox").Text=="6" && Text(malformed,"SettingsStatusText").Text.Contains("响应参数无效"),"Malformed response lost the other fan or lacked feedback."); malformed.Hide();
        File.WriteAllText(delayPath, JsonSerializer.Serialize(new { Fan1Rpm = 3600, Fan2Rpm = 3600,
            Fan1Curve = "40:2500;90:7500", Fan2Curve = "40:2500;90:7500", IncreaseDelaySeconds = 11 }));
        var invalidDelay = new MainWindow(delayPath); DetachLoadedHandler(invalidDelay); Invoke(invalidDelay, "InitializeControlUi");
        Require(Box(invalidDelay, "IncreaseDelayBox").Text == "0" && Text(invalidDelay, "SettingsStatusText").Text.Contains("升速延迟无效"),
            "Invalid saved delay was not isolated from valid control settings.");
        invalidDelay.Hide();

        var badPath = Path.Combine(Path.GetDirectoryName(settingsPath)!, "missing-directory", "settings.json");
        var failing = new MainWindow(badPath); DetachLoadedHandler(failing); Invoke(failing, "InitializeControlUi");
        Text(failing, "ErrorsText").Text = "fixture diagnostic remains separate";
        Require(!(bool)Invoke(failing, "SaveControlSettings")!, "A save into a missing directory unexpectedly succeeded.");
        Require(Text(failing, "SettingsStatusText").Text.Contains("保存失败"), "Save failure was not reported in the settings status area.");
        Require(Text(failing, "ErrorsText").Text == "fixture diagnostic remains separate", "A settings save failure overwrote sensor diagnostics.");
        failing.Hide();
    }

    private static void VerifyEditor(MainWindow window, string settingsPath)
    {
        var grid = (DataGrid)window.FindName("NodeGrid")!;
        var graph = window.FindName("CurveGraph")!;
        Invoke(window, "UpdateEditorState");
        Require(((Button)window.FindName("ApplyButton")!).IsEnabled, "Valid default curves should be applicable.");
        Box(window, "IncreaseDelayBox").Text = "11";
        Require(!((Button)window.FindName("ApplyButton")!).IsEnabled && Text(window, "IncreaseDelayValidationText").Text.Contains("0～10"),
            "Invalid heating delay did not block applying the curve.");
        Box(window, "IncreaseDelayBox").Text = "1";
        Require(((Button)window.FindName("ApplyButton")!).IsEnabled && Text(window, "DraftStatusText").Text.Contains("未保存"),
            "A valid heating delay did not become an applicable draft.");
        Box(window, "IncreaseDelayBox").Text = "0";
        Require(Box(window, "Fan1CurveBox").Text == "40:2500;60:3500;75:5000;90:7500", "Default curve changed.");
        var nodes = (IList)grid.ItemsSource!;
        Require(nodes.Count == 4, "Default curve should contain four editable nodes.");

        // Exercise the graph's node-move callback and its ordering/RPM clamps; the callback updates the bound grid model.
        Invoke(graph, "MoveNode", 1, 62d, 4200);
        Require(Box(window, "Fan1CurveBox").Text.Contains("62:4200"), "Dragging/editing a graph node did not update the curve draft.");
        Invoke(window, "UpdateEditorState");
        Require(Text(window, "DraftStatusText").Text.Contains("未保存"), "Curve edits did not mark the draft dirty.");
        Invoke(window, "LoadNodes", 0, "40:2500;60:3500;75:5000;90:7500"); Invoke(window, "UpdateEditorState");
        Invoke(graph, "MoveNode", 1, 100d, 7500);
        Require(Box(window, "Fan1CurveBox").Text.Contains("74:5000"), "A graph drag crossing its upper neighbor did not clamp temperature and RPM to a monotonic point.");
        Invoke(window, "LoadNodes", 0, "40:2500;60:3500;75:5000;90:7500"); Invoke(window, "UpdateEditorState");
        Invoke(graph, "MoveNode", 0, 100d, 7500);
        Require(Box(window, "Fan1CurveBox").Text.StartsWith("59:3500;"), "A graph drag crossing the next node did not clamp the first point.");
        Invoke(window, "LoadNodes", 0, "40:2500;60:3500;75:5000;90:7500"); Invoke(window, "UpdateEditorState");
        Invoke(graph, "MoveNode", 3, 100d, 1500);
        Require(Box(window, "Fan1CurveBox").Text.EndsWith("90:7500"), "The curve endpoint moved away from its required 90°C/7500 RPM bounds.");
        Invoke(window, "LoadNodes", 0, "40:2500;60:3500;75:5000;90:7500"); Invoke(window, "UpdateEditorState");
        var firstNode = nodes[0]!;
        Set(firstNode, "Temperature", "not-a-temperature");
        Invoke(window, "UpdateEditorState");
        Require(!((Button)window.FindName("ApplyButton")!).IsEnabled && Text(window, "CurveValidationText").Text.Length > 0,
            "An invalid table node did not block applying the curve.");
        Require(!(bool)Invoke(window, "SavePreset", false)!, "Invalid curve data passed the preset save validation.");
        Set(firstNode, "Temperature", "40"); Invoke(window, "UpdateEditorState");
        Set(nodes[^1]!, "Temperature", "91"); Invoke(window, "UpdateEditorState");
        Require(!((Button)window.FindName("ApplyButton")!).IsEnabled && !((Button)window.FindName("SaveAsPresetButton")!).IsEnabled,
            "A terminal temperature above 90°C was accepted by the editor.");
        Set(nodes[^1]!, "Temperature", "90"); Invoke(window, "UpdateEditorState");
        Invoke(window, "SetUiBusy", true);
        Require(!((Button)window.FindName("ApplyButton")!).IsEnabled && !((Button)window.FindName("RestoreButton")!).IsEnabled,
            "An in-progress control action left repeat submission enabled.");
        Invoke(window, "SetUiBusy", false);

        // Restore the default draft before structural editing. The buttons execute the same handlers as user clicks.
        Invoke(window, "LoadNodes", 0, "40:2500;60:3500;75:5000;90:7500");
        Invoke(window, "UpdateEditorState");
        grid.SelectedIndex = 0;
        ((Button)window.FindName("AddNodeButton")!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(nodes.Count == 5, "Adding a selected curve node did not add one row.");
        ((Button)window.FindName("RemoveNodeButton")!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(nodes.Count == 4, "Removing a selected curve node did not remove one row.");
        Invoke(window, "LoadNodes", 0, "40:2500;90:7500"); Invoke(window, "UpdateEditorState");
        grid.SelectedIndex = 0;
        Require(!((Button)window.FindName("RemoveNodeButton")!).IsEnabled, "The editor allowed deleting below the two-node minimum.");
        Invoke(window, "LoadNodes", 0, "40:2500;60:3500;75:5000;90:7500");
        Invoke(window, "CopyCurve", 0, 1); Invoke(window, "UpdateEditorState");
        Require(Box(window, "Fan2CurveBox").Text == Box(window, "Fan1CurveBox").Text, "Copying fan 1 to fan 2 did not copy the curve.");
        Invoke(window, "LoadNodes", 1, "40:2600;60:3600;75:5100;90:7500"); Invoke(window, "UpdateEditorState");
        Invoke(window, "CopyCurve", 1, 0);
        Require(Box(window, "Fan1CurveBox").Text == Box(window, "Fan2CurveBox").Text, "Copying fan 2 to fan 1 did not copy the curve.");
        Invoke(window, "LoadNodes", 0, "40:2500;60:3500;75:5000;90:7500");
        Invoke(window, "LoadNodes", 1, "40:2500;60:3500;75:5000;90:7500"); Invoke(window, "UpdateEditorState");

        var presetList = (IList)GetField(window, "_curvePresets")!;
        presetList.Clear(); presetList.Add(new CurvePreset("验收预设", Box(window, "Fan1CurveBox").Text, Box(window, "Fan2CurveBox").Text));
        Require((bool)Invoke(window, "SaveControlSettings")!, "Saving valid preset/settings failed.");
        using var settings = JsonDocument.Parse(File.ReadAllText(settingsPath));
        Require(settings.RootElement.GetProperty("CurvePresets").GetArrayLength() == 1, "Named preset was not persisted.");
        var selector = (ComboBox)window.FindName("PresetPicker")!;
        Invoke(window, "RefreshPresets", "验收预设");
        Require(selector.SelectedItem is CurvePreset preset && preset.Name == "验收预设", "Saved preset could not be selected.");
        Set(nodes[1]!, "Rpm", "3600"); Invoke(window, "UpdateEditorState");
        Require(Text(window, "DraftStatusText").Text.Contains("未保存"), "A node table edit did not mark the draft dirty.");
        Require(Text(window, "DraftStatusText").Text.Contains("尚未应用"), "Editing a draft was confused with applying it.");
        Invoke(window, "LoadNodes", 0, "40:2500;60:3500;75:5000;90:7500");
        Invoke(window, "LoadNodes", 1, "40:2500;60:3500;75:5000;90:7500");
        Invoke(window, "MarkEditorSaved"); Invoke(window, "UpdateEditorState");
        Require(Text(window, "DraftStatusText").Text.Contains("已保存"), "Saved curve state was not reflected in the draft status.");

        // Tab traversal remains available to keyboard users; the custom graph handles arrows and leaves Tab to WPF.
        var tabs = (TabControl)window.FindName("EditorTabs")!;
        tabs.SelectedIndex = 1; Invoke(window, "UpdateEditorState");
        Require(((Button)window.FindName("ApplyButton")!).Content?.ToString() == "应用手动转速", "Tab selection did not switch the footer action.");
        tabs.SelectedIndex = 0; Invoke(window, "UpdateEditorState");
        var radio = (RadioButton)window.FindName("Fan2Choice")!;
        radio.IsChecked = true;
        Invoke(graph, "MoveNode", 1, 100d, 7500);
        Require(Box(window, "Fan2CurveBox").Text.Contains("74:5000"), "The second curve did not receive a graph node edit.");
        ((RadioButton)window.FindName("Fan1Choice")!).IsChecked = true;
        Invoke(window, "LoadNodes", 1, "40:2500;60:3500;75:5000;90:7500"); Invoke(window, "UpdateEditorState");
    }

    private static void VerifyAppliedSummary(MainWindow window)
    {
        object originalMode = GetField(window, "_controlMode")!;
        object? original1 = GetField(window, "_fan1Applied"), original2 = GetField(window, "_fan2Applied");
        object? originalPolicy1 = GetField(window, "_fan1Policy"), originalPolicy2 = GetField(window, "_fan2Policy");
        bool originalFirmware = (bool)GetField(window, "_firmwareRestoreConfirmed")!;
        try
        {
            Type modeType = originalMode.GetType();
            SetField(window, "_controlMode", Enum.Parse(modeType, "Manual"));
            var applied = new AppliedControl(3600, 67, DateTimeOffset.UtcNow);
            SetField(window, "_fan1Applied", applied); SetField(window, "_fan2Applied", new AppliedControl(4100, 67, DateTimeOffset.UtcNow));
            Invoke(window, "RefreshAppliedUi");
            Require(Text(window, "ControlModeText").Text.Contains("手动") && Text(window, "Fan1TargetText").Text.Contains("3600") && Text(window, "Fan2TargetText").Text.Contains("4100"),
                "Applied mode/targets were not reflected in the summary.");
            SetField(window, "_fan1Applied", new AppliedControl(7500, 90, DateTimeOffset.UtcNow));
            SetField(window, "_fan2Applied", new AppliedControl(7500, 90, DateTimeOffset.UtcNow)); Invoke(window, "RefreshAppliedUi");
            Require(Text(window, "ControlModeText").Text.Contains("高温保护") && Text(window, "Fan1TargetText").Text.Contains("7500 RPM"),
                "The manual high-temperature mode did not show the protected target.");
            SetField(window, "_controlMode", Enum.Parse(modeType, "Curve")); SetField(window, "_fan1Applied", null); SetField(window, "_fan2Applied", null);
            SetField(window, "_fan1Policy", new FanControlPolicy(new FanCurve([new CurvePoint(40, 2500), new CurvePoint(90, 7500)], new RpmLimits(1500, 7500)), ["cpu"], TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1), 2, 90, 300));
            SetField(window, "_fan2Policy", GetField(window, "_fan1Policy")); Invoke(window, "RefreshAppliedUi");
            Require(Text(window, "ControlModeText").Text.Contains("等待确认") && Text(window, "Fan1TargetText").Text.EndsWith("—"),
                "Curve mode without a confirmed sample showed a fictitious target.");
            SetField(window, "_firmwareRestoreConfirmed", false); SetField(window, "_controlMode", Enum.Parse(modeType, "Firmware"));
            SetField(window, "_fan1Applied", null); SetField(window, "_fan2Applied", null); Invoke(window, "RefreshAppliedUi");
            Require(Text(window, "ControlModeText").Text.Contains("未确认"), "Unconfirmed firmware restore was not shown in the mode summary.");
        }
        finally
        {
            SetField(window, "_controlMode", originalMode); SetField(window, "_fan1Applied", original1); SetField(window, "_fan2Applied", original2);
            SetField(window, "_fan1Policy", originalPolicy1); SetField(window, "_fan2Policy", originalPolicy2);
            SetField(window, "_firmwareRestoreConfirmed", originalFirmware); Invoke(window, "RefreshAppliedUi");
        }
    }

    private static void VerifyManualPersistencePreservesCurveSelection(string privateRoot)
    {
        const string curveA = "40:2500;60:3500;75:5000;90:7500";
        const string curveB = "40:2500;60:3700;75:5200;90:7500";
        string path = Path.Combine(privateRoot, "manual-persistence-settings.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { Fan1Rpm = 3600, Fan2Rpm = 3600, Fan1Curve = curveA, Fan2Curve = curveA }));
        var window = new MainWindow(path); DetachLoadedHandler(window); Invoke(window, "InitializeControlUi");
        var presets = (IList)GetField(window, "_curvePresets")!;
        presets.Add(new CurvePreset("预设B", curveB, curveB));
        Require((bool)Invoke(window, "SaveControlSettings")!, "Could not persist the repro preset.");
        Invoke(window, "RefreshPresets", "默认曲线");
        var selector = (ComboBox)window.FindName("PresetPicker")!;
        selector.SelectedItem = ((IEnumerable)selector.ItemsSource!).Cast<CurvePreset>().Single(p => p.Name == "预设B");
        Require(Box(window, "Fan1CurveBox").Text == curveB && Box(window, "Fan2CurveBox").Text == curveB,
            "Selecting preset B did not establish B as the clean curve selection.");
        Require(!(bool)window.GetType().GetProperty("CurveDirty", PrivateInstance)!.GetValue(window)!, "Selected preset B should begin as a clean draft.");
        Require((string)GetField(window, "_settingsFan1Curve")! == curveA, "Repro requires persisted settings curve A to remain unchanged.");

        SetField(window, "_settingsFan1Rpm", 3800); SetField(window, "_settingsFan2Rpm", 3900);
        var saveApplied = (Task)Invoke(window, "SaveAppliedSettingsAsync")!;
        saveApplied.GetAwaiter().GetResult();
        Require(!(bool)window.GetType().GetProperty("CurveDirty", PrivateInstance)!.GetValue(window)!,
            "Saving manual RPM parameters changed the saved/selected curve B baseline.");
        window.Hide();
    }

    private static void VerifyPresetCrud(MainWindow window, string settingsPath, string imageDirectory)
    {
        const string firstCurve = "40:2500;60:3700;75:5200;90:7500";
        var presets = (IList)GetField(window, "_curvePresets")!;
        presets.Clear();
        Require((bool)Invoke(window, "SaveControlSettings")!, "Could not clear prior preset test data.");
        Invoke(window, "RefreshPresets", "默认曲线");
        SetCurve(window, 0, firstCurve); SetCurve(window, 1, firstCurve);
        object oldMode = GetField(window, "_controlMode")!;
        object? oldActive1 = GetField(window, "_activeFan1Curve"), oldActive2 = GetField(window, "_activeFan2Curve");
        object? oldApplied1 = GetField(window, "_fan1Applied"), oldApplied2 = GetField(window, "_fan2Applied");

        Box(window,"HeatingHysteresisBox").Text="3"; Box(window,"DecreaseDelayBox").Text="4";
        ((RadioButton)window.FindName("Fan2Choice")!).IsChecked=true;
        Box(window,"HeatingHysteresisBox").Text="1"; Box(window,"IncreaseDelayBox").Text="6";
        ((RadioButton)window.FindName("Fan1Choice")!).IsChecked=true;

        AutoClickOwnedDialog(window, "保存双路曲线预设", dialog => SetDialogTextAndClick(dialog, "保存", "验收甲"),
            () => Click(window, "SaveAsPresetButton"), dialog => SaveDialogScreenshot(dialog, imageDirectory, "ui-dialog-preset-name.png"));
        CurvePreset first = presets.Cast<CurvePreset>().Single(p => p.Name == "验收甲");
        Require(first.Fan1Curve == firstCurve && first.Fan2Curve == firstCurve, "Save As did not persist both current curves.");
        using (var saved = JsonDocument.Parse(File.ReadAllText(settingsPath)))
        {
            Require(saved.RootElement.GetProperty("CurvePresets").EnumerateArray().Any(p => p.GetProperty("Name").GetString() == "验收甲"),
                "Save As did not write the named preset to disk.");
            Require(saved.RootElement.GetProperty("Fan1Response").GetProperty("HeatingHysteresis").GetInt32()==3 && saved.RootElement.GetProperty("Fan2Response").GetProperty("HeatingDelaySeconds").GetInt32()==6,
                "Saving through the UI lost independent fan response settings.");
        }
        var reloaded=new MainWindow(settingsPath); DetachLoadedHandler(reloaded); Invoke(reloaded,"InitializeControlUi");
        Require(Box(reloaded,"HeatingHysteresisBox").Text=="3" && Box(reloaded,"DecreaseDelayBox").Text=="4","Saved fan 1 response did not reload.");
        ((RadioButton)reloaded.FindName("Fan2Choice")!).IsChecked=true;
        Require(Box(reloaded,"IncreaseDelayBox").Text=="6","Saved fan 2 response did not reload."); reloaded.Hide();
        VerifyPresetSaveDidNotApply(window, oldMode, oldActive1, oldActive2, oldApplied1, oldApplied2);
        Require(Text(window, "DraftStatusText").Text.Contains("已保存") && Text(window, "DraftStatusText").Text.Contains("尚未应用"),
            "Saving a preset did not distinguish saved edits from an unapplied control curve.");

        var nodes = (IList)((DataGrid)window.FindName("NodeGrid")!).ItemsSource!;
        Set(nodes[1]!, "Rpm", "3800");
        var bothFans = (Array)GetField(window, "_nodes")!;
        Set(((IList)bothFans.GetValue(1)!)[1]!, "Rpm", "3800"); Invoke(window, "UpdateEditorState");
        AutoRespondNativeMessage(window, "确认覆盖", yes: true, () => Click(window, "SavePresetButton"));
        first = presets.Cast<CurvePreset>().Single(p => p.Name == "验收甲");
        Require(first.Fan1Curve.Contains("60:3800") && first.Fan2Curve.Contains("60:3800"), "Saving an existing preset did not overwrite its curves.");
        VerifyPresetSaveDidNotApply(window, oldMode, oldActive1, oldActive2, oldApplied1, oldApplied2);

        AutoClickOwnedDialog(window, "重命名预设", dialog => SetDialogTextAndClick(dialog, "保存", "验收改名"),
            () => Click(window, "RenamePresetButton"));
        Require(!presets.Cast<CurvePreset>().Any(p => p.Name == "验收甲") && presets.Cast<CurvePreset>().Any(p => p.Name == "验收改名"),
            "Rename did not replace the preset name.");

        Set(nodes[2]!, "Rpm", "5300"); Invoke(window, "UpdateEditorState");
        AutoClickOwnedDialog(window, "保存双路曲线预设", dialog => SetDialogTextAndClick(dialog, "保存", "备用曲线"),
            () => Click(window, "SaveAsPresetButton"));
        Require(presets.Cast<CurvePreset>().Any(p => p.Name == "备用曲线"), "Save As did not add a second preset.");

        Set(nodes[1]!, "Rpm", "4000"); Invoke(window, "UpdateEditorState");
        var selector = (ComboBox)window.FindName("PresetPicker")!;
        CurvePreset renamed = selector.ItemsSource.Cast<CurvePreset>().Single(p => p.Name == "验收改名");
        AutoClickOwnedDialog(window, "处理未保存修改", dialog => ClickDialogButton(dialog, "取消"),
            () => selector.SelectedItem = renamed, dialog => SaveDialogScreenshot(dialog, imageDirectory, "ui-dialog-dirty-draft.png"));
        Require(selector.SelectedItem is CurvePreset selectedAfterCancel && selectedAfterCancel.Name == "备用曲线" &&
                Box(window, "Fan1CurveBox").Text.Contains("60:4000") && Text(window, "DraftStatusText").Text.Contains("未保存"),
            "Canceling a dirty preset switch did not preserve the current draft and selection.");

        AutoClickOwnedDialog(window, "处理未保存修改", dialog => ClickDialogButton(dialog, "放弃"),
            () => selector.SelectedItem = renamed);
        Require(selector.SelectedItem is CurvePreset selectedAfterDiscard && selectedAfterDiscard.Name == "验收改名" &&
                Box(window, "Fan1CurveBox").Text.Contains("60:3800") && !Text(window, "DraftStatusText").Text.Contains("未保存"),
            "Discarding a dirty preset switch did not restore the saved draft before selecting the new preset.");

        AutoRespondNativeMessage(window, "删除预设", yes: true, () => Click(window, "DeletePresetButton"));
        Require(!presets.Cast<CurvePreset>().Any(p => p.Name == "验收改名"), "Delete did not remove the selected preset.");
        VerifyPresetSaveDidNotApply(window, oldMode, oldActive1, oldActive2, oldApplied1, oldApplied2);
    }

    private static void VerifyRealGridEditing(MainWindow window, string imageDirectory)
    {
        var grid = (DataGrid)window.FindName("NodeGrid")!;
        var nodes = (IList)grid.ItemsSource!;
        object first = nodes[0]!, last = nodes[^1]!;
        grid.ScrollIntoView(first); window.UpdateLayout();
        grid.SelectedItem = first; grid.CurrentCell = new DataGridCellInfo(first, grid.Columns[0]); grid.Focus();
        Require(grid.BeginEdit(), "Temperature node table did not enter cell edit mode.");
        var cellEditor = FindVisualChildren<TextBox>(grid).FirstOrDefault() ?? throw new InvalidOperationException("Node grid did not create its WPF text editor.");
        cellEditor.Text = "not-a-temperature";
        grid.CommitEdit(DataGridEditingUnit.Cell, true); grid.CommitEdit(DataGridEditingUnit.Row, true);
        Invoke(window, "UpdateEditorState");
        Require(Text(window, "CurveValidationText").Text.Length > 0 && !((Button)window.FindName("ApplyButton")!).IsEnabled,
            "A committed invalid table cell did not block curve application.");
        Require(!(bool)Invoke(window, "SavePreset", false)!, "A committed invalid table cell was accepted by curve saving.");
        SaveMainScreenshot(window, imageDirectory, "ui-table-error.png");
        Set(first, "Temperature", "40"); Invoke(window, "UpdateEditorState");

        grid.ScrollIntoView(last); window.UpdateLayout();
        grid.SelectedItem = last; grid.CurrentCell = new DataGridCellInfo(last, grid.Columns[1]); grid.Focus();
        Require(!grid.BeginEdit(), "The table allowed editing the fixed endpoint RPM.");
        Require((string)last.GetType().GetProperty("Rpm")!.GetValue(last)! == "7500", "The fixed endpoint RPM changed during table editing.");
    }

    private static void VerifyPresetSaveDidNotApply(MainWindow window, object oldMode, object? oldActive1, object? oldActive2, object? oldApplied1, object? oldApplied2)
    {
        Require(Equals(GetField(window, "_controlMode"), oldMode) && Equals(GetField(window, "_activeFan1Curve"), oldActive1) &&
                Equals(GetField(window, "_activeFan2Curve"), oldActive2) && Equals(GetField(window, "_fan1Applied"), oldApplied1) &&
                Equals(GetField(window, "_fan2Applied"), oldApplied2), "Preset editing changed active mode, curves, or applied targets.");
    }

    private static void VerifyExitCancelKeepsWindowOpen(MainWindow window)
    {
        var node = ((IList)((DataGrid)window.FindName("NodeGrid")!).ItemsSource!)[1]!;
        Set(node, "Rpm", "3900"); Invoke(window, "UpdateEditorState");
        AutoClickOwnedDialog(window, "处理未保存修改", dialog => ClickDialogButton(dialog, "取消"),
            () => ((Task)Invoke(window, "RequestExitAsync", false)!).GetAwaiter().GetResult());
        Require(window.IsVisible && !(bool)GetField(window, "_closing")!, "Canceling explicit exit with a dirty draft did not keep the window running.");
    }

    private static void SetCurve(MainWindow window, int fan, string curve)
    {
        Invoke(window, "LoadNodes", fan, curve); Invoke(window, "UpdateEditorState");
    }

    private static void Click(MainWindow window, string name) => ((Button)window.FindName(name)!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static void SetDialogTextAndClick(Window dialog, string buttonText, string text)
    {
        var input = FindVisualChildren<TextBox>(dialog).FirstOrDefault() ?? throw new InvalidOperationException("Owned prompt has no text box.");
        input.Text = text;
        ClickDialogButton(dialog, buttonText);
    }

    private static void ClickDialogButton(Window dialog, string content)
    {
        var button = FindVisualChildren<Button>(dialog).FirstOrDefault(b => b.Content?.ToString() == content)
            ?? throw new InvalidOperationException($"Owned dialog has no '{content}' button.");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static void AutoClickOwnedDialog(MainWindow owner, string title, Action<Window> respond, Action invoke, Action<Window>? beforeRespond = null)
    {
        bool seen = false, timedOut = false; Exception? responseFailure = null; var watch = Stopwatch.StartNew();
        DispatcherTimer timer = null!;
        timer = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Background, (_, _) =>
        {
            Window? dialog = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w != owner && ReferenceEquals(w.Owner, owner) && w.Title == title && w.IsVisible);
            if (dialog is not null)
            {
                seen = true;
                try { beforeRespond?.Invoke(dialog); respond(dialog); } catch (Exception exception) { responseFailure = exception; dialog.Close(); }
                timerStop();
            }
            else if (watch.Elapsed >= TimeSpan.FromSeconds(5))
            {
                timedOut = true;
                Window? stuck = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w != owner && ReferenceEquals(w.Owner, owner) && w.IsVisible);
                stuck?.Close(); // Only close a modal owned by this verification window.
                timerStop();
            }
        }, owner.Dispatcher);
        void timerStop() { if (timer.IsEnabled) timer.Stop(); }
        timer.Start();
        try { invoke(); }
        finally { timerStop(); }
        if (responseFailure is not null) throw new InvalidOperationException("Owned modal response failed.", responseFailure);
        if (!seen || timedOut) throw new TimeoutException($"Owned WPF dialog '{title}' did not appear and accept a response within 5 seconds.");
    }

    private static void AutoRespondNativeMessage(MainWindow owner, string titlePart, bool yes, Action invoke)
    {
        int uiThread = GetCurrentThreadId();
        bool found = false, timedOut = false;
        var watch = Stopwatch.StartNew();
        Task responder = Task.Run(() =>
        {
            while (watch.Elapsed < TimeSpan.FromSeconds(5))
            {
                IntPtr dialog = FindOwnedNativeDialog(uiThread, titlePart);
                if (dialog != IntPtr.Zero)
                {
                    found = true;
                    SendMessage(dialog, 0x0111, new IntPtr(yes ? 6 : 7), IntPtr.Zero); // WM_COMMAND, IDYES/IDNO
                    return;
                }
                Thread.Sleep(40);
            }
            IntPtr stuck = FindOwnedNativeDialog(uiThread, titlePart);
            if (stuck == IntPtr.Zero) stuck = FindAnyCurrentThreadDialog(uiThread);
            if (stuck != IntPtr.Zero) SendMessage(stuck, 0x0111, new IntPtr(2), IntPtr.Zero); // IDCANCEL
            else timedOut = true;
        });
        invoke();
        responder.GetAwaiter().GetResult();
        Require(found && !timedOut, $"Native message box '{titlePart}' did not appear in this process/thread within 5 seconds.");
        _ = owner;
    }

    private static IntPtr FindOwnedNativeDialog(int threadId, string titlePart)
    {
        IntPtr result = IntPtr.Zero; int currentPid = Environment.ProcessId;
        EnumThreadWindows(threadId, (hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out int pid);
            if (pid != currentPid) return true;
            var className = new System.Text.StringBuilder(128); GetClassName(hwnd, className, className.Capacity);
            var title = new System.Text.StringBuilder(256); GetWindowText(hwnd, title, title.Capacity);
            if (className.ToString() == "#32770" && title.ToString().Contains(titlePart, StringComparison.Ordinal))
            { result = hwnd; return false; }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    private static IntPtr FindAnyCurrentThreadDialog(int threadId)
    {
        IntPtr result = IntPtr.Zero; int currentPid = Environment.ProcessId;
        EnumThreadWindows(threadId, (hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out int pid);
            if (pid != currentPid) return true;
            var className = new System.Text.StringBuilder(128); GetClassName(hwnd, className, className.Capacity);
            if (className.ToString() == "#32770") { result = hwnd; return false; }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    private static void VerifyLayoutsAndScreenshots(MainWindow window, string output)
    {
        Require(window.Width == 960 && window.Height == 760 && window.MinWidth == 800 && window.MinHeight == 640,
            "Window default or minimum dimensions differ from the UI acceptance target.");
        var root = (FrameworkElement)window.Content;
        var details = FindVisualChildren<Expander>(root).FirstOrDefault();
        if (details is not null) details.IsExpanded = true;
        foreach (var size in new[] { (Name: "default", Width: 960d, Height: 760d), (Name: "minimum", Width: 800d, Height: 640d) })
        {
            window.Width = size.Width; window.Height = size.Height; window.UpdateLayout(); root.UpdateLayout();
            var footer = (FrameworkElement)window.FindName("ApplyButton")!;
            var top = (FrameworkElement)window.FindName("CpuTemperatureText")!;
            var nodeGrid = (DataGrid)window.FindName("NodeGrid")!;
            Console.WriteLine($"{size.Name} NodeGrid {nodeGrid.ActualWidth:F0}px columns: {string.Join(" / ", nodeGrid.Columns.Select(column => column.ActualWidth.ToString("F0")))}px");
            foreach (var name in LegacyDetailNames.Concat(new[] { "CpuTemperatureText", "GpuTemperatureText", "Fan1ActualText", "Fan2ActualText", "Fan1TargetText", "Fan2TargetText", "ControlModeText", "ThemeColorButton", "ResetThemeColorButton", "CurveGraph", "NodeGrid", "PresetPicker", "ApplyButton", "RestoreButton" }))
            {
                var element = (FrameworkElement)window.FindName(name)!;
                if (element.Visibility == Visibility.Collapsed) continue;
                var rect = element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));
                if (rect.Left < -1 || rect.Right > root.ActualWidth + 1 || element.ActualHeight <= 0)
                    throw new InvalidOperationException($"{name} is clipped horizontally or not laid out at {size.Name}: {rect}.");
            }
            var topRect = top.TransformToAncestor(root).TransformBounds(new Rect(top.RenderSize));
            var footerRect = footer.TransformToAncestor(root).TransformBounds(new Rect(footer.RenderSize));
            Require(topRect.Top >= 0 && topRect.Bottom <= root.ActualHeight && footerRect.Top >= 0 && footerRect.Bottom <= root.ActualHeight,
                $"Summary or footer is outside the visible window at {size.Name} size.");
            Require(footer.ActualHeight >= footer.MinHeight && footer.ActualWidth >= footer.MinWidth,
                $"Footer action is clipped at {size.Name} size: {footer.ActualWidth}x{footer.ActualHeight}.");
            if (size.Name == "default")
            {
                var editorScroll = (ScrollViewer)window.FindName("EditorScroll")!;
                foreach (string name in new[] { "CurveGraph", "NodeGrid", "Fan1Choice", "Fan2Choice", "AddNodeButton", "RemoveNodeButton" })
                {
                    var element = (FrameworkElement)window.FindName(name)!;
                    var bounds = element.TransformToAncestor(editorScroll).TransformBounds(new Rect(element.RenderSize));
                    Require(bounds.Top >= 0 && bounds.Bottom <= editorScroll.ActualHeight - 8,
                        $"{name} is obscured by the footer at default size: {bounds}.");
                }
            }
            foreach (double dpi in new[] { 96d, 144d, 192d })
            {
                SaveVisualScreenshot(root, root.RenderSize, window.Background ?? Brushes.White,
                    Path.Combine(output, $"ui-{size.Name}-{(int)(dpi / 96 * 100)}.png"), dpi);
            }
            if (size.Name == "default")
            {
                Require((bool)Invoke(window, "SaveThemeColor", "#7C3AED")!, "Could not apply screenshot theme.");
                window.UpdateLayout();
                SaveMainScreenshot(window, output, "ui-theme-purple-100.png");
                Click(window, "ResetThemeColorButton"); window.UpdateLayout();
            }
        }
    }

    private static void CaptureManualTab(MainWindow window, string output)
    {
        double oldWidth = window.Width, oldHeight = window.Height;
        var tabs = (TabControl)window.FindName("EditorTabs")!;
        int oldTab = tabs.SelectedIndex;
        window.Width = 960; window.Height = 760; tabs.SelectedIndex = 1;
        Invoke(window, "UpdateEditorState"); window.UpdateLayout();
        SaveMainScreenshot(window, output, "ui-manual-100.png");
        tabs.SelectedIndex = oldTab; window.Width = oldWidth; window.Height = oldHeight;
        Invoke(window, "UpdateEditorState"); window.UpdateLayout();
    }

    private static void SaveMainScreenshot(MainWindow window, string output, string fileName)
    {
        var root = (FrameworkElement)window.Content;
        root.UpdateLayout();
        SaveVisualScreenshot(root, root.RenderSize, window.Background ?? Brushes.White, Path.Combine(output, fileName), 96);
    }

    private static void SaveDialogScreenshot(Window dialog, string output, string fileName)
    {
        if (dialog.Content is not FrameworkElement content) throw new InvalidOperationException("Dialog content is not a framework element.");
        content.UpdateLayout();
        double width = Math.Max(240, content.ActualWidth);
        double height = Math.Max(80, content.ActualHeight) + 36;
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            drawing.DrawText(new FormattedText(dialog.Title, System.Globalization.CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI"), 13, Brushes.Black, 1), new Point(12, 9));
            drawing.DrawRectangle(new VisualBrush(content), null, new Rect(0, 36, width, height - 36));
        }
        SaveVisualScreenshot(visual, new Size(width, height), Brushes.White, Path.Combine(output, fileName), 96);
    }

    private static void SaveVisualScreenshot(Visual visual, Size logicalSize, Brush background, string path, double dpi)
    {
        int width = (int)Math.Ceiling(logicalSize.Width * dpi / 96d);
        int height = (int)Math.Ceiling(logicalSize.Height * dpi / 96d);
        var composition = new DrawingVisual();
        using (var drawing = composition.RenderOpen())
        {
            drawing.DrawRectangle(background, null, new Rect(0, 0, logicalSize.Width, logicalSize.Height));
            drawing.DrawRectangle(new VisualBrush(visual), null, new Rect(0, 0, logicalSize.Width, logicalSize.Height));
        }
        var bitmap = new RenderTargetBitmap(width, height, dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(composition);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }

    private static void VerifyPowerNotices(MainWindow window)
    {
        var registered = typeof(MainWindow).GetProperty("SuspendResumeNotificationsRegistered", PrivateInstance)!;
        Require((bool)registered.GetValue(window)!, "Native power notification registration failed.");
        var events = new List<PowerModes>();
        Action<PowerModes> handler = events.Add;
        typeof(MainWindow).GetEvent("SuspendResumeObserved", PrivateInstance)!.GetAddMethod(true)!.Invoke(window, [handler]);
        var hook = typeof(MainWindow).GetMethod("PowerBroadcastHook", PrivateInstance)!;
        foreach (long signal in new long[] { 4, 0x12, 7 }) hook.Invoke(window, [IntPtr.Zero, 0x0218, new IntPtr(signal), IntPtr.Zero, false]);
        Require(events.SequenceEqual(new[] { PowerModes.Suspend, PowerModes.Resume }), "Synthetic power routing/deduplication failed.");
        Require((bool)registered.GetValue(window)!, "Synthetic power messages unexpectedly released the registration.");
        // The explicit exit below verifies OnClosed unregisters the native registration.
    }

    private static void VerifyCurveLive(MainWindow window,string output)
    {
        object mode=GetField(window,"_controlMode")!;
        SetField(window,"_controlMode",Enum.Parse(mode.GetType(),"Curve"));
        SetField(window,"_activeFan1Curve","40:2500;90:7500");
        SetField(window,"_activeFan2Curve","40:2500;90:7500");
        SetField(window,"_fan1Applied",new AppliedControl(3600,60,DateTimeOffset.UtcNow));
        SetField(window,"_fan2Applied",new AppliedControl(3700,60,DateTimeOffset.UtcNow));
        Invoke(window,"RefreshAppliedUi");
        var live=new TemperatureSnapshot(DateTimeOffset.UtcNow,[new("Cpu","CPU","Package","cpu",70)],[]);
        ShowSnapshot(window,live);
        Require(Text(window,"CurveLiveText").Text.Contains("5500") && Text(window,"CurveLiveText").Text.Contains("3600"),"Live curve confused the active curve target with the applied RPM or draft.");
        Require(Text(window,"CurveLiveText").Text.Contains("草稿"),"Draft curve was not distinguished from the running curve.");
        double oldWidth=window.Width,oldHeight=window.Height;
        window.Width=960; window.Height=760; window.UpdateLayout(); SaveMainScreenshot(window,output,"ui-live-curve.png");
        window.Width=oldWidth; window.Height=oldHeight; window.UpdateLayout();
        ShowSnapshot(window,live with { CapturedAtUtc=DateTimeOffset.UtcNow.AddSeconds(-6) });
        Require(!Text(window,"CurveLiveText").Text.Contains("5500"),"Stale live markers were retained.");
        SetField(window,"_controlMode",mode); SetField(window,"_fan1Applied",null); SetField(window,"_fan2Applied",null);
        SetField(window,"_activeFan1Curve",null); SetField(window,"_activeFan2Curve",null);
        ShowSnapshot(window,fixture with { CapturedAtUtc=DateTimeOffset.UtcNow });
    }

    private static void VerifyTemperatureTray(MainWindow window, string imageDirectory)
    {
        var cpu=(System.Windows.Forms.NotifyIcon)GetField(window,"_trayIcon")!;
        Require(cpu.Visible, "Temperature icons must be visible even while the main window is open.");
        var gpu=(System.Windows.Forms.NotifyIcon)GetField(window,"_gpuTrayIcon")!;
        ShowSnapshot(window,fixture with { CapturedAtUtc=DateTimeOffset.UtcNow });
        Require(cpu.Text.StartsWith("CPU") && cpu.Text.Contains("°C") && gpu.Visible && gpu.Text.StartsWith("GPU") && gpu.Text.Contains("°C"), "Temperature tray tooltips were not updated from the sample.");
        foreach(int size in new[]{16,24,32})
        {
            using var icon=new System.Drawing.Icon(cpu.Icon!,size,size);
            using var bitmap=icon.ToBitmap(); bitmap.Save(Path.Combine(imageDirectory,$"tray-cpu-{size}.png"));
        }
        ShowSnapshot(window,fixture with { CapturedAtUtc=DateTimeOffset.UtcNow.AddSeconds(-6) });
        Require(cpu.Text.Contains("不可用") && gpu.Text.Contains("不可用"), "Stale tray readings were retained.");
        ShowSnapshot(window,fixture with { CapturedAtUtc=DateTimeOffset.UtcNow });
        Invoke(window,"ShowFromTray");
        Require(cpu.Visible && gpu.Visible,"Opening the main window hid temperature icons.");
    }

    private static void VerifyTrayLifecycle(MainWindow window)
    {
        var tray = GetField(window, "_trayIcon")!;
        window.WindowState = WindowState.Minimized;
        Require(!window.IsVisible && (bool)tray.GetType().GetProperty("Visible")!.GetValue(tray)!, "Minimizing did not hide to the tray.");
        Invoke(window, "ShowFromTray");
        window.Close(); // Product X path cancels the close and hides into the tray.
        Require(!window.IsVisible && (bool)tray.GetType().GetProperty("Visible")!.GetValue(tray)!, "Window close/X did not hide to the tray.");
        Invoke(window, "ShowFromTray");
        Require(window.IsVisible && window.WindowState == WindowState.Normal, "Tray show did not restore the window.");
        // Use the same explicit exit request as the tray menu, bypassing only the discard dialog; no control client exists.
        var exit = (Task)typeof(MainWindow).GetMethod("RequestExitAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [true])!;
        exit.GetAwaiter().GetResult();
        Require(!(bool)GetField(window, "_trayInitialized")!, "Explicit exit did not dispose the tray icon/menu.");
        Require(!(bool)typeof(MainWindow).GetProperty("SuspendResumeNotificationsRegistered", PrivateInstance)!.GetValue(window)!, "Window close did not release power registration.");
        Require((bool)GetField(window, "_controlStopped")!, "Explicit exit did not stop the control lifecycle.");
    }

    private static void InitializeTray(MainWindow window) => Invoke(window, "InitializeTrayUi");
    private static void ShowIdentity(MainWindow window, MachineIdentity identity) => Invoke(window, "ShowIdentity", identity);
    private static void ShowSnapshot(MainWindow window, TemperatureSnapshot snapshot) => Invoke(window, "ShowSnapshot", snapshot);
    private static void ShowThermal(MainWindow window, LenovoThermalSnapshot thermal) => Invoke(window, "ShowThermalSnapshot", thermal);
    private static void ShowVpc(MainWindow window, VpcFanSnapshot vpc) => Invoke(window, "ShowVpcSnapshot", vpc);

    private static void DetachLoadedHandler(MainWindow window)
    {
        var handler = (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), window,
            typeof(MainWindow).GetMethod("OnLoaded", PrivateInstance | BindingFlags.DeclaredOnly)!);
        window.Loaded -= handler;
    }

    private static TextBlock Text(MainWindow window, string name) => (TextBlock)window.FindName(name)!;
    private static TextBox Box(MainWindow window, string name) => (TextBox)window.FindName(name)!;
    private static void RaiseKey(UIElement target, PresentationSource source, Key key)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = Keyboard.KeyDownEvent };
        target.RaiseEvent(args);
        Require(args.Handled, $"Curve editor did not handle routed key {key}.");
    }
    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is T item) yield return item;
            foreach (T descendant in FindVisualChildren<T>(child)) yield return descendant;
        }
    }
    private static object? Invoke(object target, string method, params object?[] args) => target.GetType().GetMethod(method, PrivateInstance)!.Invoke(target, args);
    private static object? GetField(object target, string field) => target.GetType().GetField(field, PrivateInstance)!.GetValue(target);
    private static void SetField(object target, string field, object? value) => target.GetType().GetField(field, PrivateInstance)!.SetValue(target, value);
    private static void Set(object target, string property, object? value) => target.GetType().GetProperty(property)!.SetValue(target, value);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private delegate bool EnumWindowsCallback(IntPtr hwnd, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern int GetCurrentThreadId();
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumThreadWindows(int threadId, EnumWindowsCallback callback, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, System.Text.StringBuilder text, int maxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder className, int maxCount);
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

}
