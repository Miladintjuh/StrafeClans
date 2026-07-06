using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using StrafeLab.Models;

namespace StrafeLab;

public partial class SettingsView : UserControl
{
    public Border CalibrationDemoTarget => CalibrationCard;
    private readonly AppPreferences _preferences;
    private string? _activeColorKey;
    private Button? _activeColorButton;
    private bool _loading;
    private bool _updatingPicker;
    private string? _recordingHotkeyTarget;
    private ColorPreferences _loadedColors = new();

    public event Action? BackRequested;
    public event Action? ShowDemoRequested;
    public event Action<AppPreferences>? PreferencesApplied;

    public SettingsView(AppPreferences preferences)
    {
        InitializeComponent();
        _preferences = preferences;
        PreviewKeyDown += SettingsView_PreviewKeyDown;
        Loaded += (_, _) => LoadFromPreferences();
    }

    private void LoadFromPreferences()
    {
        _loading = true;
        _loadedColors = _preferences.Colors.Clone();
        SetHotkeyButtonText(StartStopHotkeyButton, _preferences.StartStopHotkey);
        SetHotkeyButtonText(PeekHotkeyButton, _preferences.PeekArmHotkey);
        SetHotkeyButtonText(RemoveHotkeyButton, _preferences.RemoveLastHotkey);
        FillModeSwitchBox();

        UseColumnCheckBox.IsChecked = _preferences.Columns.Use;
        IndexColumnCheckBox.IsChecked = _preferences.Columns.Index;
        DirectionColumnCheckBox.IsChecked = _preferences.Columns.Direction;
        CounterDelayColumnCheckBox.IsChecked = _preferences.Columns.CounterDelay;
        ClickDelayColumnCheckBox.IsChecked = _preferences.Columns.ClickDelay;
        MistakesColumnCheckBox.IsChecked = _preferences.Columns.Mistakes;
        ResultColumnCheckBox.IsChecked = _preferences.Columns.Result;
        WhatHappenedColumnCheckBox.IsChecked = _preferences.Columns.WhatHappened;

        CounterMinBox.Text = _preferences.CounterMinMs.ToString(CultureInfo.InvariantCulture);
        CounterMaxBox.Text = _preferences.CounterMaxMs.ToString(CultureInfo.InvariantCulture);
        ClickMinBox.Text = _preferences.ClickMinMs.ToString(CultureInfo.InvariantCulture);
        ClickMaxBox.Text = _preferences.ClickMaxMs.ToString(CultureInfo.InvariantCulture);
        KeyboardOverlapToleranceBox.Text = _preferences.HallEffectToleranceMs.ToString(CultureInfo.InvariantCulture);
        CleanFastMaxBox.Text = _preferences.CleanFastMaxTotalMs.ToString(CultureInfo.InvariantCulture);
        CleanPerfectMaxBox.Text = _preferences.CleanPerfectMaxTotalMs.ToString(CultureInfo.InvariantCulture);
        CleanJustInTimeMinBox.Text = _preferences.CleanJustInTimeMinTotalMs.ToString(CultureInfo.InvariantCulture);

        DpiBox.Text = _preferences.Dpi.ToString(CultureInfo.InvariantCulture);
        SensBox.Text = _preferences.Sensitivity.ToString(CultureInfo.InvariantCulture);
        YawBox.Text = _preferences.Yaw.ToString(CultureInfo.InvariantCulture);
        PitchBox.Text = _preferences.Pitch.ToString(CultureInfo.InvariantCulture);
        MultiplierBox.Text = _preferences.Multiplier.ToString(CultureInfo.InvariantCulture);

        PeekCleanMaxBox.Text = _preferences.PeekCleanMaxMs.ToString(CultureInfo.InvariantCulture);
        PeekOverlapToleranceBox.Text = _preferences.PeekOverlapToleranceMs.ToString(CultureInfo.InvariantCulture);
        PeekSprayHoldBox.Text = _preferences.PeekSprayHoldMs.ToString(CultureInfo.InvariantCulture);
        PeekMouseTraceMaxBox.Text = _preferences.PeekMouseTraceMaxMs.ToString(CultureInfo.InvariantCulture);
        PeekMouseTracePointsBox.Text = _preferences.PeekMouseTraceMaxPoints.ToString(CultureInfo.InvariantCulture);
        PeekResetAfterClickBox.Text = _preferences.PeekResetMouseAfterClickMs.ToString(CultureInfo.InvariantCulture);

        ShowAdvancedFiltersCheckBox.IsChecked = _preferences.ShowAdvancedFilters;
        PlaySoundsCheckBox.IsChecked = _preferences.PlayHotkeySounds;
        DefaultTraceCountBox.Text = _preferences.DefaultTraceCount.ToString(CultureInfo.InvariantCulture);

        LoadColorBoxes(_preferences.Colors);
        UpdateCalibrationPreview();
        _loading = false;
    }

    private static void SetHotkeyButtonText(Button button, string hotkey)
    {
        button.Content = string.IsNullOrWhiteSpace(hotkey) ? "Record" : $"Record: {hotkey}";
    }

    private void RecordHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is null) return;
        _recordingHotkeyTarget = button.Tag.ToString();
        button.Content = "Press a key...";
        button.Focus();
    }

    private void SettingsView_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_recordingHotkeyTarget)) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin) return;
        string display = HotkeyDisplayName(key);
        if (string.IsNullOrWhiteSpace(display)) return;

        switch (_recordingHotkeyTarget)
        {
            case "StartStop":
                _preferences.StartStopHotkey = display;
                SetHotkeyButtonText(StartStopHotkeyButton, display);
                break;
            case "Peek":
                _preferences.PeekArmHotkey = display;
                SetHotkeyButtonText(PeekHotkeyButton, display);
                break;
            case "Remove":
                _preferences.RemoveLastHotkey = display;
                SetHotkeyButtonText(RemoveHotkeyButton, display);
                break;
        }
        _recordingHotkeyTarget = null;
        e.Handled = true;
    }

    private static string HotkeyDisplayName(Key key)
    {
        if (key >= Key.A && key <= Key.Z) return key.ToString();
        if (key >= Key.F1 && key <= Key.F24) return key.ToString();
        if (key >= Key.D0 && key <= Key.D9) return key.ToString()[1..];
        return key switch
        {
            Key.Space => "Space",
            Key.Insert => "Insert",
            Key.Delete => "Delete",
            Key.Home => "Home",
            Key.End => "End",
            Key.PageUp => "PageUp",
            Key.PageDown => "PageDown",
            Key.Oem3 => "`",
            Key.OemMinus => "-",
            Key.OemPlus => "=",
            _ => key.ToString()
        };
    }

    private void FillModeSwitchBox()
    {
        ModeSwitchChoiceBox.Items.Clear();
        ModeSwitchChoiceBox.SelectedValuePath = "Tag";
        ModeSwitchChoiceBox.Items.Add(new ComboBoxItem { Content = "Ask every time", Tag = "Ask" });
        ModeSwitchChoiceBox.Items.Add(new ComboBoxItem { Content = "End running session automatically", Tag = "AutoEnd" });
        ModeSwitchChoiceBox.Items.Add(new ComboBoxItem { Content = "Do not switch while recording", Tag = "Stay" });

        string selected = string.IsNullOrWhiteSpace(_preferences.ModeSwitchSessionChoice) ? "Ask" : _preferences.ModeSwitchSessionChoice;
        foreach (ComboBoxItem item in ModeSwitchChoiceBox.Items)
        {
            if (string.Equals(item.Tag?.ToString(), selected, StringComparison.OrdinalIgnoreCase))
            {
                ModeSwitchChoiceBox.SelectedItem = item;
                break;
            }
        }
        ModeSwitchChoiceBox.SelectedItem ??= ModeSwitchChoiceBox.Items[0];
    }

    private void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        var oldColors = _preferences.Colors.Clone();

        _preferences.Columns.Use = UseColumnCheckBox.IsChecked == true;
        _preferences.Columns.Index = IndexColumnCheckBox.IsChecked == true;
        _preferences.Columns.Direction = DirectionColumnCheckBox.IsChecked == true;
        _preferences.Columns.CounterDelay = CounterDelayColumnCheckBox.IsChecked == true;
        _preferences.Columns.ClickDelay = ClickDelayColumnCheckBox.IsChecked == true;
        _preferences.Columns.Mistakes = MistakesColumnCheckBox.IsChecked == true;
        _preferences.Columns.Result = ResultColumnCheckBox.IsChecked == true;
        _preferences.Columns.WhatHappened = WhatHappenedColumnCheckBox.IsChecked == true;

        _preferences.CounterMinMs = ParseDouble(CounterMinBox.Text, 0);
        _preferences.CounterMaxMs = ParseDouble(CounterMaxBox.Text, 80);
        _preferences.ClickMinMs = ParseDouble(ClickMinBox.Text, 0);
        _preferences.ClickMaxMs = ParseDouble(ClickMaxBox.Text, 160);
        _preferences.HallEffectToleranceMs = ParseDouble(KeyboardOverlapToleranceBox.Text, 8);
        _preferences.CleanFastMaxTotalMs = ParseDouble(CleanFastMaxBox.Text, 90);
        _preferences.CleanPerfectMaxTotalMs = ParseDouble(CleanPerfectMaxBox.Text, 145);
        _preferences.CleanJustInTimeMinTotalMs = ParseDouble(CleanJustInTimeMinBox.Text, 190);
        NormalizeCleanTimingThresholds();

        _preferences.Dpi = ParseDouble(DpiBox.Text, 1600);
        _preferences.Sensitivity = ParseDouble(SensBox.Text, 0.4);
        _preferences.Yaw = ParseDouble(YawBox.Text, 0.022);
        _preferences.Pitch = ParseDouble(PitchBox.Text, 0.022);
        _preferences.Multiplier = ParseDouble(MultiplierBox.Text, 1.0);

        _preferences.PeekCleanMaxMs = ParseDouble(PeekCleanMaxBox.Text, 45);
        _preferences.PeekOverlapToleranceMs = ParseDouble(PeekOverlapToleranceBox.Text, 8);
        _preferences.PeekSprayHoldMs = ParseDouble(PeekSprayHoldBox.Text, 180);
        _preferences.PeekMouseTraceMaxMs = ParseDouble(PeekMouseTraceMaxBox.Text, 900);
        _preferences.PeekMouseTraceMaxPoints = Math.Clamp(ParseInt(PeekMouseTracePointsBox.Text, 180), 16, 1000);
        _preferences.PeekResetMouseAfterClickMs = ParseDouble(PeekResetAfterClickBox.Text, 250);

        _preferences.StartStopHotkey = ExtractRecordedHotkey(StartStopHotkeyButton, "F9");
        _preferences.PeekArmHotkey = ExtractRecordedHotkey(PeekHotkeyButton, "F8");
        _preferences.RemoveLastHotkey = ExtractRecordedHotkey(RemoveHotkeyButton, "Q");
        _preferences.ModeSwitchSessionChoice = ModeSwitchChoiceBox.SelectedValue?.ToString() ?? "Ask";
        _preferences.ShowAdvancedFilters = ShowAdvancedFiltersCheckBox.IsChecked == true;
        _preferences.PlayHotkeySounds = PlaySoundsCheckBox.IsChecked == true;
        _preferences.DefaultTraceCount = int.TryParse(DefaultTraceCountBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int count)
            ? Math.Clamp(count, 1, 200)
            : 24;

        _preferences.Colors = ReadColorsFromBoxes();
        ApplyColors(_preferences.Colors);
        UpdateCalibrationPreview();
        PreferencesApplied?.Invoke(_preferences);

        bool colorsChanged = !ColorsEqual(oldColors, _preferences.Colors);
        if (colorsChanged)
        {
            _loadedColors = _preferences.Colors.Clone();
            var result = MessageBox.Show(
                "Graphical settings were saved. Some screens may only fully refresh after reloading StrafeLab. Reload now?",
                "Reload StrafeLab?",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                RestartApplication();
            }
        }
    }

    private void NormalizeCleanTimingThresholds()
    {
        _preferences.CleanFastMaxTotalMs = Math.Max(0, _preferences.CleanFastMaxTotalMs);
        _preferences.CleanPerfectMaxTotalMs = Math.Max(_preferences.CleanFastMaxTotalMs, _preferences.CleanPerfectMaxTotalMs);
        _preferences.CleanJustInTimeMinTotalMs = Math.Max(_preferences.CleanPerfectMaxTotalMs, _preferences.CleanJustInTimeMinTotalMs);
    }

    private static string ExtractRecordedHotkey(Button button, string fallback)
    {
        string text = button.Content?.ToString() ?? string.Empty;
        const string prefix = "Record:";
        if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            string value = text[prefix.Length..].Trim();
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return fallback;
    }

    private void UpdateCalibrationPreview()
    {
        double dpi = ParseDouble(DpiBox.Text, 1600);
        double sens = ParseDouble(SensBox.Text, 0.4);
        double yaw = ParseDouble(YawBox.Text, 0.022);
        double multiplier = ParseDouble(MultiplierBox.Text, 1.0);
        double degPerCount = sens * yaw * multiplier;
        double countsPer360 = degPerCount <= 0 ? 0 : 360.0 / degPerCount;
        double cm360 = dpi <= 0 || countsPer360 <= 0 ? 0 : countsPer360 / dpi * 2.54;
        CalibrationPreviewText.Text = cm360 <= 0
            ? "Preview: cm/360 unavailable. Check DPI, sensitivity, yaw, and multiplier."
            : $"Preview: {cm360:0.0} cm/360 · {degPerCount:0.000000}° per raw count.";
    }

    private void LoadColorBoxes(ColorPreferences colors)
    {
        ColorWindowBackgroundBox.Text = NormalizeHex(colors.WindowBackground, "#0B1020");
        ColorBackgroundBox.Text = NormalizeHex(colors.Background, "#0B1020");
        ColorCardBox.Text = NormalizeHex(colors.Card, "#11182C");
        ColorSurfaceBox.Text = NormalizeHex(colors.Surface, "#172036");
        ColorAccentBox.Text = NormalizeHex(colors.Accent, "#7C5CFF");
        ColorAccentAltBox.Text = NormalizeHex(colors.AccentAlt, "#00E0FF");
        ColorTextBox.Text = NormalizeHex(colors.Text, "#F5F7FF");
        ColorMutedTextBox.Text = NormalizeHex(colors.MutedText, "#A8B0C8");
        ColorBorderBox.Text = NormalizeHex(colors.Border, "#26304A");
        ColorGoodBox.Text = NormalizeHex(colors.Good, "#38D996");
        ColorWarningBox.Text = NormalizeHex(colors.Warning, "#FFCE45");
        ColorBadBox.Text = NormalizeHex(colors.Bad, "#FF5C7A");
        ColorStepLeftBox.Text = NormalizeHex(colors.StepLeft, "#00E0FF");
        ColorStepRightBox.Text = NormalizeHex(colors.StepRight, "#7C5CFF");
        ColorSprayBox.Text = NormalizeHex(colors.Spray, "#FFCE45");
        ColorTraceHighlightBox.Text = NormalizeHex(colors.TraceHighlight, "#00E0FF");
    }

    private ColorPreferences ReadColorsFromBoxes() => new()
    {
        WindowBackground = NormalizeHex(ColorWindowBackgroundBox.Text, "#0B1020"),
        Background = NormalizeHex(ColorBackgroundBox.Text, "#0B1020"),
        Card = NormalizeHex(ColorCardBox.Text, "#11182C"),
        Surface = NormalizeHex(ColorSurfaceBox.Text, "#172036"),
        Accent = NormalizeHex(ColorAccentBox.Text, "#7C5CFF"),
        AccentAlt = NormalizeHex(ColorAccentAltBox.Text, "#00E0FF"),
        Text = NormalizeHex(ColorTextBox.Text, "#F5F7FF"),
        MutedText = NormalizeHex(ColorMutedTextBox.Text, "#A8B0C8"),
        Border = NormalizeHex(ColorBorderBox.Text, "#26304A"),
        Good = NormalizeHex(ColorGoodBox.Text, "#38D996"),
        Warning = NormalizeHex(ColorWarningBox.Text, "#FFCE45"),
        Bad = NormalizeHex(ColorBadBox.Text, "#FF5C7A"),
        StepLeft = NormalizeHex(ColorStepLeftBox.Text, "#00E0FF"),
        StepRight = NormalizeHex(ColorStepRightBox.Text, "#7C5CFF"),
        Spray = NormalizeHex(ColorSprayBox.Text, "#FFCE45"),
        TraceHighlight = NormalizeHex(ColorTraceHighlightBox.Text, "#00E0FF")
    };

    private void ColorButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string key) return;
        _activeColorKey = key;
        _activeColorButton = button;
        ColorPickerTitleText.Text = $"Pick {key}";
        string current = NormalizeHex(GetColorTextBox(key)?.Text ?? "#FFFFFF", "#FFFFFF");
        SetPickerFromHex(current);
        ColorPickerStatusText.Text = string.Empty;
        ColorPickerPopup.PlacementTarget = button;
        ColorPickerPopup.Placement = PlacementMode.Bottom;
        ColorPickerPopup.HorizontalOffset = -148;
        ColorPickerPopup.VerticalOffset = 6;
        ColorPickerPopup.IsOpen = true;
    }

    private void CloseColorPickerButton_Click(object sender, RoutedEventArgs e) => CloseColorPicker();
    private void ColorPickerPopup_Closed(object sender, EventArgs e) => CloseColorPicker(resetPopup: false);

    private void CloseColorPicker(bool resetPopup = true)
    {
        if (resetPopup) ColorPickerPopup.IsOpen = false;
        _activeColorKey = null;
        _activeColorButton = null;
    }

    private void ColorPickerHexBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || _updatingPicker || ColorPickerPreview == null || ColorPickerHexBox == null) return;
        string fallback = _activeColorKey == null ? "#FFFFFF" : GetColorTextBox(_activeColorKey)?.Text ?? "#FFFFFF";
        var color = ParseColor(ColorPickerHexBox.Text, ParseColor(fallback, Colors.White));
        UpdatePickerPreview(color);
        SetSlidersFromColor(color);
    }

    private void ColorSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || _updatingPicker || ColorPickerHexBox == null || RedSlider == null || GreenSlider == null || BlueSlider == null) return;
        var color = Color.FromRgb((byte)RedSlider.Value, (byte)GreenSlider.Value, (byte)BlueSlider.Value);
        _updatingPicker = true;
        ColorPickerHexBox.Text = ToHex(color);
        _updatingPicker = false;
        UpdatePickerPreview(color);
    }

    private void TestColorButton_Click(object sender, RoutedEventArgs e)
    {
        if (_activeColorKey == null) return;
        var target = GetColorTextBox(_activeColorKey);
        if (target == null) return;

        string hex = NormalizeHex(ColorPickerHexBox.Text, target.Text);
        if (!IsValidHex(ColorPickerHexBox.Text))
        {
            ColorPickerStatusText.Text = "Invalid hex; using the previous safe value.";
        }
        else
        {
            ColorPickerStatusText.Text = "Temporary preview applied. Press Apply to save.";
        }

        target.Text = hex;
        ApplyColors(ReadColorsFromBoxes());
    }

    private void SetPickerFromHex(string hex)
    {
        var color = ParseColor(hex, Colors.White);
        _updatingPicker = true;
        ColorPickerHexBox.Text = ToHex(color);
        RedSlider.Value = color.R;
        GreenSlider.Value = color.G;
        BlueSlider.Value = color.B;
        _updatingPicker = false;
        UpdatePickerPreview(color);
    }

    private void SetSlidersFromColor(Color color)
    {
        _updatingPicker = true;
        RedSlider.Value = color.R;
        GreenSlider.Value = color.G;
        BlueSlider.Value = color.B;
        _updatingPicker = false;
    }

    private void UpdatePickerPreview(Color color)
    {
        ColorPickerPreview.Background = new SolidColorBrush(color);
    }

    private TextBox? GetColorTextBox(string key) => key switch
    {
        "WindowBackground" => ColorWindowBackgroundBox,
        "Background" => ColorBackgroundBox,
        "Card" => ColorCardBox,
        "Surface" => ColorSurfaceBox,
        "Accent" => ColorAccentBox,
        "AccentAlt" => ColorAccentAltBox,
        "Text" => ColorTextBox,
        "MutedText" => ColorMutedTextBox,
        "Border" => ColorBorderBox,
        "Good" => ColorGoodBox,
        "Warning" => ColorWarningBox,
        "Bad" => ColorBadBox,
        "StepLeft" => ColorStepLeftBox,
        "StepRight" => ColorStepRightBox,
        "Spray" => ColorSprayBox,
        "TraceHighlight" => ColorTraceHighlightBox,
        _ => null
    };

    private static string NormalizeHex(string value, string fallback)
    {
        try
        {
            string text = value.Trim();
            if (!text.StartsWith("#", StringComparison.Ordinal)) text = "#" + text;
            var color = (Color)ColorConverter.ConvertFromString(text)!;
            return ToHex(color);
        }
        catch
        {
            return NormalizeHexNoThrow(fallback);
        }
    }

    private static string NormalizeHexNoThrow(string fallback)
    {
        try
        {
            string text = fallback.Trim();
            if (!text.StartsWith("#", StringComparison.Ordinal)) text = "#" + text;
            var color = (Color)ColorConverter.ConvertFromString(text)!;
            return ToHex(color);
        }
        catch
        {
            return "#FFFFFF";
        }
    }

    private static bool IsValidHex(string value)
    {
        try
        {
            string text = value.Trim();
            if (!text.StartsWith("#", StringComparison.Ordinal)) text = "#" + text;
            _ = (Color)ColorConverter.ConvertFromString(text)!;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private static Color ParseColor(string value, Color fallback)
    {
        try
        {
            string text = value.Trim();
            if (!text.StartsWith("#", StringComparison.Ordinal)) text = "#" + text;
            return (Color)ColorConverter.ConvertFromString(text)!;
        }
        catch
        {
            return fallback;
        }
    }

    private static void ApplyColors(ColorPreferences colors)
    {
        SetBrush("WindowBgBrush", ParseColor(colors.WindowBackground, Color.FromRgb(11, 16, 32)));
        SetBrush("BgBrush", ParseColor(colors.Background, Color.FromRgb(11, 16, 32)));
        SetBrush("BackgroundBrush", ParseColor(colors.Background, Color.FromRgb(11, 16, 32)));
        SetBrush("CardBrush", ParseColor(colors.Card, Color.FromRgb(17, 24, 44)));
        SetBrush("SoftBrush", ParseColor(colors.Surface, Color.FromRgb(23, 32, 54)));
        SetBrush("AccentBrush", ParseColor(colors.Accent, Color.FromRgb(124, 92, 255)));
        SetBrush("Accent2Brush", ParseColor(colors.AccentAlt, Color.FromRgb(0, 224, 255)));
        SetBrush("TextBrush", ParseColor(colors.Text, Color.FromRgb(245, 247, 255)));
        SetBrush("MutedTextBrush", ParseColor(colors.MutedText, Color.FromRgb(168, 176, 200)));
        SetBrush("BorderBrush", ParseColor(colors.Border, Color.FromRgb(38, 48, 74)));
        SetBrush("GoodBrush", ParseColor(colors.Good, Color.FromRgb(56, 217, 150)));
        SetBrush("WarnBrush", ParseColor(colors.Warning, Color.FromRgb(255, 206, 69)));
        SetBrush("BadBrush", ParseColor(colors.Bad, Color.FromRgb(255, 92, 122)));
        SetBrush("StepLeftBrush", ParseColor(colors.StepLeft, Color.FromRgb(0, 224, 255)));
        SetBrush("StepRightBrush", ParseColor(colors.StepRight, Color.FromRgb(124, 92, 255)));
        SetBrush("SprayBrush", ParseColor(colors.Spray, Color.FromRgb(255, 206, 69)));
        SetBrush("TraceHighlightBrush", ParseColor(colors.TraceHighlight, Color.FromRgb(0, 224, 255)));
    }

    private static void SetBrush(string key, Color color)
    {
        if (Application.Current.Resources[key] is SolidColorBrush brush && !brush.IsFrozen) brush.Color = color;
        else Application.Current.Resources[key] = new SolidColorBrush(color);
    }

    private static bool ColorsEqual(ColorPreferences a, ColorPreferences b)
        => string.Equals(NormalizeHexNoThrow(a.WindowBackground), NormalizeHexNoThrow(b.WindowBackground), StringComparison.OrdinalIgnoreCase)
        && string.Equals(NormalizeHexNoThrow(a.Background), NormalizeHexNoThrow(b.Background), StringComparison.OrdinalIgnoreCase)
        && string.Equals(NormalizeHexNoThrow(a.Card), NormalizeHexNoThrow(b.Card), StringComparison.OrdinalIgnoreCase)
        && string.Equals(NormalizeHexNoThrow(a.Surface), NormalizeHexNoThrow(b.Surface), StringComparison.OrdinalIgnoreCase)
        && string.Equals(NormalizeHexNoThrow(a.Accent), NormalizeHexNoThrow(b.Accent), StringComparison.OrdinalIgnoreCase)
        && string.Equals(NormalizeHexNoThrow(a.AccentAlt), NormalizeHexNoThrow(b.AccentAlt), StringComparison.OrdinalIgnoreCase)
        && string.Equals(NormalizeHexNoThrow(a.Text), NormalizeHexNoThrow(b.Text), StringComparison.OrdinalIgnoreCase)
        && string.Equals(NormalizeHexNoThrow(a.MutedText), NormalizeHexNoThrow(b.MutedText), StringComparison.OrdinalIgnoreCase)
        && string.Equals(NormalizeHexNoThrow(a.Border), NormalizeHexNoThrow(b.Border), StringComparison.OrdinalIgnoreCase)
        && string.Equals(NormalizeHexNoThrow(a.Good), NormalizeHexNoThrow(b.Good), StringComparison.OrdinalIgnoreCase)
        && string.Equals(NormalizeHexNoThrow(a.Warning), NormalizeHexNoThrow(b.Warning), StringComparison.OrdinalIgnoreCase)
        && string.Equals(NormalizeHexNoThrow(a.Bad), NormalizeHexNoThrow(b.Bad), StringComparison.OrdinalIgnoreCase)
        && string.Equals(NormalizeHexNoThrow(a.StepLeft), NormalizeHexNoThrow(b.StepLeft), StringComparison.OrdinalIgnoreCase)
        && string.Equals(NormalizeHexNoThrow(a.StepRight), NormalizeHexNoThrow(b.StepRight), StringComparison.OrdinalIgnoreCase)
        && string.Equals(NormalizeHexNoThrow(a.Spray), NormalizeHexNoThrow(b.Spray), StringComparison.OrdinalIgnoreCase)
        && string.Equals(NormalizeHexNoThrow(a.TraceHighlight), NormalizeHexNoThrow(b.TraceHighlight), StringComparison.OrdinalIgnoreCase);

    private static double ParseDouble(string value, double fallback)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ? parsed : fallback;
    }

    private static int ParseInt(string value, int fallback)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : fallback;
    }

    private void OpenDataFolderButton_Click(object sender, RoutedEventArgs e)
    {
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StrafeLab", "sessions");
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo("explorer.exe", folder) { UseShellExecute = true });
    }


    public void SetDemoButtonVisible(bool visible)
    {
        if (ShowDemoButton != null) ShowDemoButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowDemoButton_Click(object sender, RoutedEventArgs e) => ShowDemoRequested?.Invoke();

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyColors(_preferences.Colors);
        BackRequested?.Invoke();
    }

    private static void RestartApplication()
    {
        try
        {
            string? path = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(path))
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                Application.Current.Shutdown();
            }
        }
        catch
        {
            MessageBox.Show("Settings were saved. Please close and reopen StrafeLab to fully reload the palette.", "Reload needed", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
