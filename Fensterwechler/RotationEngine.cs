namespace Fensterwechler;

public enum RotationState { Stopped, Running, Paused, Waiting }

/// <summary>
/// Rotation state machine. Free of WPF and Win32: time is passed in as a monotonic TimeSpan and window
/// activation is injected, so the scheduling logic is testable. Driven by <see cref="Tick"/>.
/// </summary>
public sealed class RotationEngine(
    IList<RotationEntry> entries,
    Func<RotationEntry, ActivationResult> activate,
    Func<RotationEntry, bool> isShown)
{
    public static readonly TimeSpan WaitInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan VerifyDelay = TimeSpan.FromSeconds(1);

    int lastIndex;
    // Failure bookkeeping survives a successful-looking activation until it is verified,
    // so an entry that "activates" but never comes to the front cannot be retried forever.
    RotationEntry? failingEntry;
    int failedAttempts;
    bool retryPending;
    TimeSpan? verifyAt;
    TimeSpan pausedRemaining;
    RotationState stateBeforePause;

    public RotationState State { get; private set; }
    public RotationEntry? Current { get; private set; }
    /// <summary>Monotonic time of the next switch (or re-check while waiting / retrying).</summary>
    public TimeSpan NextSwitch { get; private set; }
    public FailureStrategy Strategy { get; set; } = FailureStrategy.RetryThenSkip;
    public int RetryCount { get; set; } = 2;
    public string? WaitReason { get; private set; }

    public event Action? Changed;

    public TimeSpan Remaining(TimeSpan now) => State switch
    {
        RotationState.Paused => pausedRemaining,
        RotationState.Stopped => TimeSpan.Zero,
        _ => NextSwitch > now ? NextSwitch - now : TimeSpan.Zero,
    };

    public void Start(TimeSpan now)
    {
        if (State != RotationState.Stopped) return;
        lastIndex = 0;
        ClearFailure();
        Log.Info("Rotation gestartet");
        Advance(now, fromStart: true);
    }

    public void Stop()
    {
        if (State == RotationState.Stopped) return;
        SetCurrent(null);
        ClearFailure();
        verifyAt = null;
        WaitReason = null;
        State = RotationState.Stopped;
        Log.Info("Rotation beendet");
        Changed?.Invoke();
    }

    public void Pause(TimeSpan now)
    {
        if (State is not (RotationState.Running or RotationState.Waiting)) return;
        pausedRemaining = Remaining(now);
        stateBeforePause = State;
        State = RotationState.Paused;
        verifyAt = null;
        Changed?.Invoke();
    }

    public void Resume(TimeSpan now)
    {
        if (State != RotationState.Paused) return;
        State = stateBeforePause;
        NextSwitch = now + pausedRemaining;
        Changed?.Invoke();
    }

    /// <summary>Jump to the next available entry. While paused the new window is shown and the rotation stays paused.</summary>
    public void Next(TimeSpan now)
    {
        if (State == RotationState.Stopped) return;
        bool paused = State == RotationState.Paused;
        ClearFailure();
        Advance(now);
        if (paused) Pause(now);
    }

    public void Tick(TimeSpan now)
    {
        if (State is RotationState.Stopped or RotationState.Paused) return;
        if (verifyAt is { } at && now >= at)
        {
            verifyAt = null;
            if (Current != null && !isShown(Current))
            {
                Fail(Current, "Fenster wurde nicht in den Vordergrund gebracht", now);
                return;
            }
            if (Current == failingEntry) ClearFailure();
        }
        if (now >= NextSwitch) Advance(now);
    }

    void Advance(TimeSpan now, bool fromStart = false)
    {
        int n = entries.Count;
        int start;
        if (retryPending && failingEntry != null && entries.IndexOf(failingEntry) is >= 0 and var retryIndex) start = retryIndex;
        else
        {
            int current = Current == null ? -1 : entries.IndexOf(Current);
            // Current removed from the list: its successor now sits at lastIndex.
            start = fromStart ? 0 : current >= 0 ? current + 1 : lastIndex;
        }
        retryPending = false;

        for (int step = 0; step < n; step++)
        {
            int index = (start + step) % n;
            var entry = entries[index];
            if (!entry.Enabled) continue;

            ActivationResult result;
            try { result = activate(entry); }
            catch (Exception ex) { result = new(ActivationOutcome.Failed, ex.Message); } // never let one window stop the rotation

            if (result.Outcome == ActivationOutcome.Activated)
            {
                if (entry != failingEntry) ClearFailure();
                lastIndex = index;
                Log.Debug($"Wechsel zu {entry.DisplayName} ({entry.DurationSeconds} s)");
                SetCurrent(entry);
                SetStatus(entry, result.Detail ?? "Angezeigt", LogLevel.Debug);
                NextSwitch = now + TimeSpan.FromSeconds(entry.DurationSeconds);
                verifyAt = now + VerifyDelay;
                WaitReason = null;
                State = RotationState.Running;
                Changed?.Invoke();
                return;
            }
            if (result.Outcome == ActivationOutcome.Failed && ScheduleRetry(entry, result.Detail, now)) return;

            SetStatus(entry, (result.Detail ?? "Nicht verfügbar") + " – übersprungen", LogLevel.Warning);
        }

        // Nothing could be shown: keep checking without spinning.
        SetCurrent(null);
        verifyAt = null;
        WaitReason = entries.Any(e => e.Enabled) ? "Kein Fenster der Rotation verfügbar" : "Keine aktiven Einträge";
        if (State != RotationState.Waiting) Log.Warn($"Wartezustand: {WaitReason}");
        State = RotationState.Waiting;
        NextSwitch = now + WaitInterval;
        Changed?.Invoke();
    }

    void Fail(RotationEntry entry, string detail, TimeSpan now)
    {
        if (ScheduleRetry(entry, detail, now)) return;
        SetStatus(entry, detail + " – übersprungen", LogLevel.Warning);
        Advance(now);
    }

    bool ScheduleRetry(RotationEntry entry, string? detail, TimeSpan now)
    {
        if (entry != failingEntry)
        {
            failingEntry = entry;
            failedAttempts = 0;
        }
        if (Strategy != FailureStrategy.RetryThenSkip || failedAttempts >= RetryCount)
        {
            ClearFailure();
            return false;
        }
        failedAttempts++;
        retryPending = true;
        SetStatus(entry, $"{detail ?? "Aktivierung fehlgeschlagen"} – Wiederholung {failedAttempts}/{RetryCount}", LogLevel.Warning);
        NextSwitch = now + RetryDelay;
        verifyAt = null;
        State = RotationState.Running;
        Changed?.Invoke();
        return true;
    }

    void ClearFailure()
    {
        failingEntry = null;
        failedAttempts = 0;
        retryPending = false;
    }

    void SetCurrent(RotationEntry? entry)
    {
        if (Current != null) Current.IsCurrent = false;
        Current = entry;
        if (entry != null) entry.IsCurrent = true;
    }

    /// <summary>Logs only on change, so a waiting rotation does not flood the log every few seconds.</summary>
    static void SetStatus(RotationEntry entry, string status, LogLevel level)
    {
        if (entry.Status == status) return;
        entry.Status = status;
        Log.Write(level, $"{entry.DisplayName}: {status}");
    }
}
