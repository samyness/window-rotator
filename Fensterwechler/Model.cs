namespace Fensterwechler;

public enum FailureStrategy { Skip, RetryThenSkip }

public enum LogLevel { Debug, Info, Warning, Error }

/// <summary>Global, profile-independent settings (settings.json).</summary>
public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;
    public string? LastProfile { get; set; }
    public bool StartRotationOnLaunch { get; set; }
    public bool CloseToTray { get; set; } = true;
    public LogLevel LogLevel { get; set; } = LogLevel.Info;
}

/// <summary>A saved rotation plan with its options (profiles\&lt;name&gt;.json).</summary>
public sealed class RotationProfile
{
    public const int MinDuration = 3;
    public const int MaxDuration = 3600;

    public int SchemaVersion { get; set; } = 1;
    public string Name { get; set; } = "Standard";
    /// <summary>Monitor device name (e.g. \\.\DISPLAY2); null = leave windows where they are.</summary>
    public string? TargetMonitor { get; set; }
    public FailureStrategy FailureStrategy { get; set; } = FailureStrategy.RetryThenSkip;
    public int RetryCount { get; set; } = 2;
    /// <summary>Double ALT tap when Windows refuses the foreground switch. Turn off if a target reacts to ALT.</summary>
    public bool AltKeyFallback { get; set; } = true;
    public List<RotationEntryData> Entries { get; set; } = [];
}

/// <summary>Stable window characteristics; handles are never persisted.</summary>
public sealed class RotationEntryData
{
    public string Title { get; set; } = "";
    /// <summary>Optional user-defined title fragment that must be contained in the window title.</summary>
    public string? TitleFilter { get; set; }
    public string ProcessName { get; set; } = "";
    public string? ProcessPath { get; set; }
    public string ClassName { get; set; } = "";
    public int DurationSeconds { get; set; } = 30;
    public bool Enabled { get; set; } = true;
}
