namespace StrafeLab.Models;

public sealed class AppPreferences
{
    public AttemptColumnPreferences Columns { get; set; } = new();
    public ColorPreferences Colors { get; set; } = new();
    public string StartStopHotkey { get; set; } = "F9";
    public string PeekArmHotkey { get; set; } = "F8";
    public string RemoveLastHotkey { get; set; } = "Q";
    public bool ShowAdvancedFilters { get; set; } = true;
    public bool PlayHotkeySounds { get; set; } = true;
    public int DefaultTraceCount { get; set; } = 24;
    public string ModeSwitchSessionChoice { get; set; } = "Ask";
    public bool GuidedDemoSeen { get; set; }

    public double CounterMinMs { get; set; } = 0;
    public double CounterMaxMs { get; set; } = 80;
    public double ClickMinMs { get; set; } = 0;
    public double ClickMaxMs { get; set; } = 160;
    public double HallEffectToleranceMs { get; set; } = 8;
    public double CleanFastMaxTotalMs { get; set; } = 90;
    public double CleanPerfectMaxTotalMs { get; set; } = 145;
    public double CleanJustInTimeMinTotalMs { get; set; } = 190;
    public double CounterPairWindowMs { get; set; } = 400;
    public double MouseTraceMaxMs { get; set; } = 750;
    public int MouseTraceMaxPoints { get; set; } = 180;

    public double Dpi { get; set; } = 1600;
    public double Sensitivity { get; set; } = 0.4;
    public double Yaw { get; set; } = 0.022;
    public double Pitch { get; set; } = 0.022;
    public double Multiplier { get; set; } = 1.0;

    public double PeekCleanMaxMs { get; set; } = 45;
    public double PeekOverlapToleranceMs { get; set; } = 8;
    public double PeekSprayHoldMs { get; set; } = 180;
    public double PeekMouseTraceMaxMs { get; set; } = 900;
    public int PeekMouseTraceMaxPoints { get; set; } = 180;
    public double PeekResetMouseAfterClickMs { get; set; } = 250;
}

public sealed class ColorPreferences
{
    public string WindowBackground { get; set; } = "#0B1020";
    public string Background { get; set; } = "#0B1020";
    public string Card { get; set; } = "#11182C";
    public string Surface { get; set; } = "#172036";
    public string Accent { get; set; } = "#7C5CFF";
    public string AccentAlt { get; set; } = "#00E0FF";
    public string Text { get; set; } = "#F5F7FF";
    public string MutedText { get; set; } = "#A8B0C8";
    public string Border { get; set; } = "#26304A";
    public string Good { get; set; } = "#38D996";
    public string Warning { get; set; } = "#FFCE45";
    public string Bad { get; set; } = "#FF5C7A";
    public string StepLeft { get; set; } = "#00E0FF";
    public string StepRight { get; set; } = "#7C5CFF";
    public string Spray { get; set; } = "#FFCE45";
    public string TraceHighlight { get; set; } = "#00E0FF";

    public ColorPreferences Clone() => new()
    {
        WindowBackground = WindowBackground,
        Background = Background,
        Card = Card,
        Surface = Surface,
        Accent = Accent,
        AccentAlt = AccentAlt,
        Text = Text,
        MutedText = MutedText,
        Border = Border,
        Good = Good,
        Warning = Warning,
        Bad = Bad,
        StepLeft = StepLeft,
        StepRight = StepRight,
        Spray = Spray,
        TraceHighlight = TraceHighlight
    };
}

public sealed class AttemptColumnPreferences
{
    public bool Use { get; set; } = true;
    public bool Index { get; set; } = true;
    public bool Direction { get; set; } = true;
    public bool CounterDelay { get; set; } = true;
    public bool ClickDelay { get; set; } = true;
    public bool Mistakes { get; set; } = true;
    public bool Result { get; set; } = true;
    public bool WhatHappened { get; set; } = true;
}
