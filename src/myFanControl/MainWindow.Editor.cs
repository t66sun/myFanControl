using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using FanControl.Core;
using myFanControl.Editing;
namespace myFanControl;
public partial class MainWindow
{
    private readonly ObservableCollection<CurveNode>[] _nodes = [new(), new()];
    private bool _editorReady, _editorChanging, _uiBusy;
    private string _savedCurve1 = DefaultCurve, _savedCurve2 = DefaultCurve, _savedManual1 = "3600", _savedManual2 = "3600", _savedDelaySeconds = "0";
    private CurvePreset? _selectedPreset;
    private int SelectedFan => Fan2Choice?.IsChecked == true ? 1 : 0;
    private bool CurveDirty => Fan1CurveBox.Text != _savedCurve1 || Fan2CurveBox.Text != _savedCurve2;
    private bool ManualDirty => Fan1RpmBox.Text != _savedManual1 || Fan2RpmBox.Text != _savedManual2;
    private bool DelayDirty => IncreaseDelayBox.Text != _savedDelaySeconds;
    private static string SerializeNodes(IEnumerable<CurveNode> nodes)
    {
        string text = string.Join(';', nodes.Select(n => n.Temperature + ":" + n.Rpm));
        try { return FormatCurve(ParseCurve(text)); }
        catch (ArgumentException) { return text; }
    }
    private void InitializeEditorUi()
    {
        if (_editorReady) return;
        _editorChanging = true;
        LoadNodes(0, Fan1CurveBox.Text); LoadNodes(1, Fan2CurveBox.Text);
        NodeGrid.ItemsSource = _nodes[0];
        CurveGraph.NodeDragged += (index, t, rpm) => { _editorChanging = true; var n = _nodes[SelectedFan][index]; n.Temperature = t.ToString(CultureInfo.InvariantCulture); n.Rpm = rpm.ToString(CultureInfo.InvariantCulture); _editorChanging = false; UpdateEditorState(); };
        _editorReady = true; _editorChanging = false; RefreshPresets(null); MarkEditorSaved();
    }
    private void LoadNodes(int fan, string text)
    {
        foreach (var n in _nodes[fan]) n.PropertyChanged -= OnNodeChanged;
        _nodes[fan].Clear();
        foreach (string item in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) { var pair = item.Split(':'); var node = new CurveNode { Temperature = pair[0], Rpm = pair.Length > 1 ? pair[1] : "" }; node.PropertyChanged += OnNodeChanged; _nodes[fan].Add(node); }
    }
    private void OnNodeChanged(object? sender, PropertyChangedEventArgs e) { if (!_editorChanging && e.PropertyName != nameof(CurveNode.Error)) UpdateEditorState(); }
    private void MarkEditorSaved()
    {
        _savedCurve1 = _settingsFan1Curve; _savedCurve2 = _settingsFan2Curve;
        _savedManual1 = _settingsFan1Rpm.ToString(CultureInfo.InvariantCulture); _savedManual2 = _settingsFan2Rpm.ToString(CultureInfo.InvariantCulture);
        _savedDelaySeconds = _settingsIncreaseDelaySeconds.ToString(CultureInfo.InvariantCulture);
        UpdateEditorState();
    }
    private bool ValidateNodes(int fan)
    {
        var nodes = _nodes[fan]; bool valid = nodes.Count >= 2; double previousT = -101; int previousRpm = 0;
        for (int i = 0; i < nodes.Count; i++)
        {
            var n = nodes[i]; string error = "";
            bool tValid = double.TryParse(n.Temperature, NumberStyles.Float, CultureInfo.InvariantCulture, out double t) && double.IsFinite(t);
            bool rpmValid = int.TryParse(n.Rpm, NumberStyles.Integer, CultureInfo.InvariantCulture, out int rpm);
            if (!tValid || t < -100 || t > 90) error = "温度需在 −100～90°C";
            else if (i > 0 && t <= previousT) error = "温度必须严格递增";
            else if (!rpmValid || rpm < 1500 || rpm > 7500 || rpm % 100 != 0) error = "RPM 需为 1500～7500 的 100 倍数";
            else if (i > 0 && rpm < previousRpm) error = "RPM 不能随温度下降";
            else if (i == nodes.Count - 1 && rpm != 7500) error = "最高温节点必须为 7500 RPM";
            n.Error = error; valid &= error.Length == 0; previousT = t; previousRpm = rpm;
        }
        if (valid) try { _ = CreatePolicy(ParseCurve(SerializeNodes(nodes))); } catch (ArgumentException e) { valid = false; nodes[^1].Error = e.Message; }
        return valid;
    }
    private bool ValidateManual(out int fan1, out int fan2)
    {
        bool valid = int.TryParse(Fan1RpmBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out fan1);
        valid &= int.TryParse(Fan2RpmBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out fan2);
        return valid && fan1 is >= 1500 and <= 7500 && fan2 is >= 1500 and <= 7500 && fan1 % 100 == 0 && fan2 % 100 == 0;
    }
    private void UpdateEditorState()
    {
        if (!_editorReady || _editorChanging) return;
        Fan1CurveBox.Text = SerializeNodes(_nodes[0]); Fan2CurveBox.Text = SerializeNodes(_nodes[1]);
        bool valid1 = ValidateNodes(0), valid2 = ValidateNodes(1), curvesValid = valid1 && valid2;
        CurveValidationText.Text = curvesValid ? "" : $"风扇 {(valid1 ? "2" : valid2 ? "1" : "1、2")} 曲线无效，请修正表格中标出的节点。";
        CurveGraph.Fan1 = valid1 ? ParseCurve(Fan1CurveBox.Text).Points : []; CurveGraph.Fan2 = valid2 ? ParseCurve(Fan2CurveBox.Text).Points : [];
        CurveGraph.SelectedFan = SelectedFan; CurveGraph.InvalidateVisual();
        bool manualValid = ValidateManual(out _, out _); ManualValidationText.Text = manualValid ? "" : "请输入 1500～7500 之间、100 的倍数的整数 RPM。";
        bool delayValid = TryParseIncreaseDelay(IncreaseDelayBox.Text, out int delaySeconds);
        IncreaseDelayValidationText.Text = delayValid ? "" : "升速延迟需为 0～10 秒的整数。";
        bool manualTab = EditorTabs.SelectedIndex == 1;
        ApplyButton.Content = _uiBusy ? "正在处理…" : manualTab ? "应用手动转速" : "应用自动曲线";
        ApplyButton.IsEnabled = !_uiBusy && (manualTab ? manualValid : curvesValid && delayValid);
        SavePresetButton.IsEnabled = SaveAsPresetButton.IsEnabled = !_uiBusy && curvesValid && delayValid;
        RenamePresetButton.IsEnabled = DeletePresetButton.IsEnabled = !_uiBusy && _selectedPreset is not null && _selectedPreset.Name != "默认曲线";
        AddNodeButton.IsEnabled = !_uiBusy && curvesValid;
        RemoveNodeButton.IsEnabled = !_uiBusy && _nodes[SelectedFan].Count > 2;
        bool sameApplied = manualTab ? _controlMode == ControlMode.Manual && manualValid && _fan1Applied?.Rpm == int.Parse(Fan1RpmBox.Text, CultureInfo.InvariantCulture) && _fan2Applied?.Rpm == int.Parse(Fan2RpmBox.Text, CultureInfo.InvariantCulture) : _controlMode == ControlMode.Curve && Fan1CurveBox.Text == _activeFan1Curve && Fan2CurveBox.Text == _activeFan2Curve && delayValid && delaySeconds == _activeIncreaseDelaySeconds;
        DraftStatusText.Text = (CurveDirty || ManualDirty || DelayDirty ? "● 未保存修改" : "修改已保存") + (sameApplied ? " · 当前配置已启用" : " · 当前输入尚未应用");
    }
    private void SetUiBusy(bool busy)
    {
        _uiBusy = busy; CurveEditorPanel.IsEnabled = ManualEditorPanel.IsEnabled = !busy; RestoreButton.IsEnabled = ThemeColorButton.IsEnabled = ResetThemeColorButton.IsEnabled = !busy; UpdateEditorState();
    }
    private void OnApplyClick(object sender, RoutedEventArgs e)
    {
        NodeGrid.CommitEdit(DataGridEditingUnit.Cell, true); NodeGrid.CommitEdit(DataGridEditingUnit.Row, true); UpdateEditorState();
        if (!ApplyButton.IsEnabled) return;
        if (EditorTabs.SelectedIndex == 1) OnApplyManualClick(sender, e); else OnApplyCurvesClick(sender, e);
    }
    private void OnEditorTabChanged(object sender, SelectionChangedEventArgs e) { if (ReferenceEquals(e.Source, EditorTabs)) UpdateEditorState(); }
    private void OnManualDraftChanged(object sender, TextChangedEventArgs e) => UpdateEditorState();
    private void OnIncreaseDelayChanged(object sender, TextChangedEventArgs e) => UpdateEditorState();
    private void OnNodeBeginningEdit(object sender, DataGridBeginningEditEventArgs e) { if (e.Column.DisplayIndex == 1 && ReferenceEquals(e.Row.Item, _nodes[SelectedFan].LastOrDefault())) e.Cancel = true; }
    private void OnNodeCellEditEnding(object sender, DataGridCellEditEndingEventArgs e) => Dispatcher.BeginInvoke(new Action(UpdateEditorState));
    private void OnSelectedFanChanged(object sender, RoutedEventArgs e) { if (!_editorReady) return; NodeGrid.ItemsSource = _nodes[SelectedFan]; CurveGraph.SelectedNode = -1; UpdateEditorState(); }
    private void OnCopyFan1Click(object sender, RoutedEventArgs e) => CopyCurve(0, 1);
    private void OnCopyFan2Click(object sender, RoutedEventArgs e) => CopyCurve(1, 0);
    private void CopyCurve(int from, int to) { _editorChanging = true; LoadNodes(to, SerializeNodes(_nodes[from])); _editorChanging = false; UpdateEditorState(); }
    private void OnAddNodeClick(object sender, RoutedEventArgs e)
    {
        var nodes = _nodes[SelectedFan]; int index = NodeGrid.SelectedIndex;
        if (index < 0 || index >= nodes.Count - 1) index = nodes.Count - 2;
        double a = double.Parse(nodes[index].Temperature, CultureInfo.InvariantCulture), b = double.Parse(nodes[index + 1].Temperature, CultureInfo.InvariantCulture);
        double t = Math.Round((a + b) / 2); if (t <= a || t >= b) { MessageBox.Show(this, "所选节点之间没有可插入的整数温度，请先拉开间距。", "增加节点"); return; }
        int rpm = int.Parse(nodes[index].Rpm, CultureInfo.InvariantCulture);
        var node = new CurveNode { Temperature = t.ToString(CultureInfo.InvariantCulture), Rpm = rpm.ToString(CultureInfo.InvariantCulture) }; node.PropertyChanged += OnNodeChanged; nodes.Insert(index + 1, node); NodeGrid.SelectedItem = node; UpdateEditorState();
    }
    private void OnRemoveNodeClick(object sender, RoutedEventArgs e)
    {
        var nodes = _nodes[SelectedFan]; if (nodes.Count <= 2 || NodeGrid.SelectedItem is not CurveNode node) return;
        _editorChanging = true; node.PropertyChanged -= OnNodeChanged; nodes.Remove(node); nodes[^1].Rpm = "7500"; _editorChanging = false; UpdateEditorState();
    }
    private void RefreshPresets(string? selectName)
    {
        _editorChanging = true; var presets = new List<CurvePreset> { new("默认曲线", DefaultCurve, DefaultCurve) }; presets.AddRange(_curvePresets); PresetPicker.ItemsSource = presets;
        _selectedPreset = presets.FirstOrDefault(p => p.Name == selectName) ?? presets.FirstOrDefault(p => p.Fan1Curve == Fan1CurveBox.Text && p.Fan2Curve == Fan2CurveBox.Text);
        PresetPicker.SelectedItem = _selectedPreset; _editorChanging = false; UpdateEditorState();
    }
    private void OnPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_editorReady || _editorChanging || PresetPicker.SelectedItem is not CurvePreset preset) return;
        if (!ConfirmDiscardUnsavedChanges()) { _editorChanging = true; PresetPicker.SelectedItem = _selectedPreset; _editorChanging = false; return; }
        _selectedPreset = preset; _editorChanging = true; LoadNodes(0, preset.Fan1Curve); LoadNodes(1, preset.Fan2Curve); _savedCurve1 = preset.Fan1Curve; _savedCurve2 = preset.Fan2Curve; _editorChanging = false; RefreshPresets(preset.Name); UpdateEditorState();
    }
    private void OnSavePresetClick(object sender, RoutedEventArgs e) => SavePreset(false);
    private void OnSaveAsPresetClick(object sender, RoutedEventArgs e) => SavePreset(true);
    private bool SavePreset(bool saveAs)
    {
        if (!ValidateNodes(0) || !ValidateNodes(1) || !TryParseIncreaseDelay(IncreaseDelayBox.Text, out int delaySeconds)) return false;
        string? name = !saveAs && _selectedPreset is not null && _selectedPreset.Name != "默认曲线" ? _selectedPreset.Name : PromptPresetName("保存双路曲线预设", "");
        if (name is null) return false;
        var existing = _curvePresets.Find(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null && MessageBox.Show(this, $"覆盖预设“{existing.Name}”？", "确认覆盖", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return false;
        var old = _curvePresets.ToList(); string old1 = _settingsFan1Curve, old2 = _settingsFan2Curve; int oldDelay = _settingsIncreaseDelaySeconds;
        if (existing is not null) _curvePresets.Remove(existing);
        _curvePresets.Add(new(name, Fan1CurveBox.Text, Fan2CurveBox.Text)); _settingsFan1Curve = Fan1CurveBox.Text; _settingsFan2Curve = Fan2CurveBox.Text; _settingsIncreaseDelaySeconds = delaySeconds;
        if (!SaveControlSettings()) { _curvePresets = old; _settingsFan1Curve = old1; _settingsFan2Curve = old2; _settingsIncreaseDelaySeconds = oldDelay; return false; }
        MarkEditorSaved(); RefreshPresets(name); return true;
    }
    private void OnRenamePresetClick(object sender, RoutedEventArgs e)
    {
        if (_selectedPreset is null || _selectedPreset.Name == "默认曲线") return;
        string? name = PromptPresetName("重命名预设", _selectedPreset.Name); if (name is null || name == _selectedPreset.Name) return;
        var old = _curvePresets.ToList(); var existing = _curvePresets.Find(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null && existing != _selectedPreset && MessageBox.Show(this, $"覆盖预设“{existing.Name}”？", "确认覆盖", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        _curvePresets.RemoveAll(p => p.Name == _selectedPreset.Name || string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)); _curvePresets.Add(_selectedPreset with { Name = name });
        if (!SaveControlSettings()) { _curvePresets = old; return; } RefreshPresets(name);
    }
    private void OnDeletePresetClick(object sender, RoutedEventArgs e)
    {
        if (_selectedPreset is null || _selectedPreset.Name == "默认曲线" || MessageBox.Show(this, $"删除预设“{_selectedPreset.Name}”？当前运行配置将继续控制。", "删除预设", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        var old = _curvePresets.ToList(); _curvePresets.RemoveAll(p => p.Name == _selectedPreset.Name); if (!SaveControlSettings()) { _curvePresets = old; return; } RefreshPresets(null);
    }
    private string? PromptPresetName(string title, string initial)
    {
        var input = new TextBox { Text = initial, Margin = new Thickness(0, 12, 0, 12) }; var content = new StackPanel { Margin = new Thickness(20) };
        content.Children.Add(new TextBlock { Text = "预设名称（默认曲线保留为只读）" }); content.Children.Add(input);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; var ok = new Button { Content = "保存", IsDefault = true, MinHeight = 32, Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(8, 0, 0, 0) }; var cancel = new Button { Content = "取消", IsCancel = true, MinHeight = 32, Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(8, 0, 0, 0) }; buttons.Children.Add(ok); buttons.Children.Add(cancel); content.Children.Add(buttons);
        var dialog = new Window { Owner = this, Resources = Resources, Background = (System.Windows.Media.Brush)FindResource("Canvas"), Foreground = (System.Windows.Media.Brush)FindResource("Ink"), FontSize = 13, FontFamily = new System.Windows.Media.FontFamily("Segoe UI, Microsoft YaHei UI"), Title = title, Content = content, Width = 370, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        ok.Style = (Style)FindResource("PrimaryButton"); System.Windows.Automation.AutomationProperties.SetName(input, "预设名称");
        ok.Click += (_, _) => { if (string.IsNullOrWhiteSpace(input.Text) || input.Text.Trim() == "默认曲线") { MessageBox.Show(dialog, "请输入有效名称，不能使用“默认曲线”。"); return; } dialog.DialogResult = true; }; dialog.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        return dialog.ShowDialog() == true ? input.Text.Trim() : null;
    }
    private bool ConfirmDiscardUnsavedChanges()
    {
        if (!_editorReady || (!CurveDirty && !ManualDirty && !DelayDirty)) return true;
        var content = new StackPanel { Margin = new Thickness(20) }; content.Children.Add(new TextBlock { Text = "存在未保存修改。曲线将保存为命名预设；手动参数和升速延迟保存为下次输入，不启用控制。", TextWrapping = TextWrapping.Wrap, MaxWidth = 390, Margin = new Thickness(0, 0, 0, 18) });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; int result = 0;
        var dialog = new Window { Owner = this, Resources = Resources, Background = (System.Windows.Media.Brush)FindResource("Canvas"), Foreground = (System.Windows.Media.Brush)FindResource("Ink"), FontSize = 13, FontFamily = new System.Windows.Media.FontFamily("Segoe UI, Microsoft YaHei UI"), Title = "处理未保存修改", Content = content, Width = 440, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        foreach (var choice in new[] { ("保存", 1), ("放弃", 2), ("取消", 0) }) { var button = new Button { Content = choice.Item1, IsCancel = choice.Item2 == 0, MinHeight = 32, Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(8, 0, 0, 0) }; if (choice.Item2 == 1) button.Style = (Style)FindResource("PrimaryButton"); button.Click += (_, _) => { result = choice.Item2; dialog.DialogResult = result != 0; }; buttons.Children.Add(button); } content.Children.Add(buttons); dialog.ShowDialog();
        if (result == 0) return false;
        if (result == 1)
        {
            if (!ValidateManual(out int rpm1, out int rpm2)) { MessageBox.Show(this, "手动参数无效，请修正后保存，或选择放弃。", "无法保存"); return false; }
            if (!TryParseIncreaseDelay(IncreaseDelayBox.Text, out int delaySeconds)) { MessageBox.Show(this, "升速延迟需为 0～10 秒的整数。", "无法保存"); return false; }
            if (CurveDirty && !SavePreset(true)) return false;
            int old1 = _settingsFan1Rpm, old2 = _settingsFan2Rpm, oldDelay = _settingsIncreaseDelaySeconds; _settingsFan1Rpm = rpm1; _settingsFan2Rpm = rpm2; _settingsIncreaseDelaySeconds = delaySeconds;
            if (!SaveControlSettings()) { _settingsFan1Rpm = old1; _settingsFan2Rpm = old2; _settingsIncreaseDelaySeconds = oldDelay; return false; } MarkEditorSaved(); return true;
        }
        _editorChanging = true; LoadNodes(0, _savedCurve1); LoadNodes(1, _savedCurve2); Fan1RpmBox.Text = _savedManual1; Fan2RpmBox.Text = _savedManual2; IncreaseDelayBox.Text = _savedDelaySeconds; _editorChanging = false; UpdateEditorState(); return true;
    }
}


