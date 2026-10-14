using System.IO;
using Fensterwechler;

// Self-check for the logic that must not break: scheduling, re-matching, persistence.
var temp = Directory.CreateTempSubdirectory("wwr-test-");
Log.Directory = Path.Combine(temp.FullName, "logs");
AppPaths.Roaming = temp.FullName;
Log.Level = LogLevel.Debug;

int failures = 0;
void Check(bool condition, string what)
{
    if (condition) return;
    failures++;
    Console.Error.WriteLine($"FAIL: {what}");
}

TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);
RotationEntry E(string title, int seconds = 10, bool enabled = true) =>
    new(new RotationEntryData { Title = title, ProcessName = title + ".exe", DurationSeconds = seconds, Enabled = enabled });

// Fake windows: per title an outcome; "shown" tracks what the activator brought to front.
var outcomes = new Dictionary<string, ActivationOutcome>();
var shown = new List<string>();
string? foreground = null;
bool foregroundSticks = true;
var unverified = new HashSet<string>(); // "activate" fine, but never actually reach the foreground
RotationEngine Engine(IList<RotationEntry> entries) => new(entries, e =>
{
    var outcome = outcomes.GetValueOrDefault(e.Title, ActivationOutcome.Activated);
    if (outcome == ActivationOutcome.Activated) { shown.Add(e.Title); foreground = foregroundSticks && !unverified.Contains(e.Title) ? e.Title : null; }
    return new ActivationResult(outcome, outcome == ActivationOutcome.Activated ? null : outcome.ToString());
}, e => foreground == e.Title);

void Reset() { outcomes.Clear(); shown.Clear(); unverified.Clear(); foreground = null; foregroundSticks = true; }

// 1. Order, per-entry duration, wrap-around
{
    Reset();
    var list = new List<RotationEntry> { E("A", 5), E("B", 3) };
    var engine = Engine(list);
    engine.Start(S(0));
    Check(engine.State == RotationState.Running && engine.Current?.Title == "A", "start shows first entry");
    engine.Tick(S(4.9));
    Check(engine.Current?.Title == "A", "A stays for its duration");
    engine.Tick(S(5));
    Check(engine.Current?.Title == "B", "switch to B after 5 s");
    engine.Tick(S(8));
    Check(engine.Current?.Title == "A" && string.Join(",", shown) == "A,B,A", "wrap-around after B's 3 s");
    Check(list[0].IsCurrent && !list[1].IsCurrent, "IsCurrent follows the shown entry");
    engine.Stop();
    Check(engine.State == RotationState.Stopped && engine.Current == null && !list[0].IsCurrent, "stop clears current");
}

// 2. Disabled and missing entries are skipped
{
    Reset();
    outcomes["C"] = ActivationOutcome.Missing;
    var engine = Engine([E("A", 5), E("B", 5, enabled: false), E("C", 5), E("D", 5)]);
    engine.Start(S(0));
    engine.Tick(S(5));
    Check(engine.Current?.Title == "D", "disabled B and missing C skipped");
}

// 3. Nothing available -> waiting, re-check, recover
{
    Reset();
    outcomes["A"] = ActivationOutcome.Missing;
    var list = new List<RotationEntry> { E("A", 5) };
    var engine = Engine(list);
    engine.Start(S(0));
    Check(engine.State == RotationState.Waiting && engine.Current == null, "waiting when no window available");
    Check(engine.NextSwitch == S(0) + RotationEngine.WaitInterval, "re-check after wait interval");
    outcomes.Clear();
    engine.Tick(S(4));
    Check(engine.State == RotationState.Waiting, "no re-check before interval");
    engine.Tick(RotationEngine.WaitInterval);
    Check(engine.State == RotationState.Running && engine.Current?.Title == "A", "recovers when window reappears");
    list[0].Enabled = false;
    engine.Tick(S(100));
    Check(engine.State == RotationState.Waiting && engine.WaitReason == "Keine aktiven Einträge", "all disabled -> waiting with reason");
}

// 4. Failed activation: retry, then skip
{
    Reset();
    outcomes["B"] = ActivationOutcome.Failed;
    var engine = Engine([E("A", 5), E("B", 5), E("C", 5)]);
    engine.RetryCount = 2;
    engine.Start(S(0));
    engine.Tick(S(5));
    Check(engine.Current?.Title == "A" && engine.NextSwitch == S(5) + RotationEngine.RetryDelay, "first failure schedules retry, A stays visible");
    engine.Tick(S(7));
    Check(engine.NextSwitch == S(7) + RotationEngine.RetryDelay, "second retry scheduled");
    engine.Tick(S(9));
    Check(engine.Current?.Title == "C", "after retries B is skipped");

    Reset();
    outcomes["B"] = ActivationOutcome.Failed;
    var skip = Engine([E("A", 5), E("B", 5), E("C", 5)]);
    skip.Strategy = FailureStrategy.Skip;
    skip.Start(S(0));
    skip.Tick(S(5));
    Check(skip.Current?.Title == "C", "skip strategy skips immediately");
}

// 5. Pause keeps the remaining time, resume continues
{
    Reset();
    var engine = Engine([E("A", 10), E("B", 10)]);
    engine.Start(S(0));
    engine.Pause(S(3));
    Check(engine.State == RotationState.Paused && engine.Remaining(S(50)) == S(7), "pause freezes remaining time");
    engine.Tick(S(100));
    Check(engine.Current?.Title == "A", "no switch while paused");
    engine.Resume(S(100));
    engine.Tick(S(106.9));
    Check(engine.Current?.Title == "A", "remaining 7 s after resume");
    engine.Tick(S(107));
    Check(engine.Current?.Title == "B", "switch after remaining time");

    engine.Pause(S(108));
    engine.Next(S(109));
    Check(engine.State == RotationState.Paused && engine.Current?.Title == "A" && engine.Remaining(S(200)) == S(10), "next while paused shows next and stays paused");
}

// 6. Activation not verified (window did not come to front) -> retry, then skip
{
    Reset();
    foregroundSticks = false;
    var engine = Engine([E("A", 30), E("B", 30)]);
    engine.RetryCount = 1;
    engine.Start(S(0));
    engine.Tick(RotationEngine.VerifyDelay);
    Check(engine.Current?.Title == "A" && engine.NextSwitch == RotationEngine.VerifyDelay + RotationEngine.RetryDelay, "unverified activation schedules retry");
    foregroundSticks = true;
    engine.Tick(S(3));
    Check(engine.Current?.Title == "A" && shown.Count == 2, "retry re-activates A");
    engine.Tick(S(3) + RotationEngine.VerifyDelay);
    Check(engine.Current?.Title == "A" && engine.NextSwitch == S(33), "verified retry keeps full duration");
}

// 6b. An entry that always "activates" but never reaches the foreground must not block the rotation
{
    Reset();
    unverified.Add("A");
    var engine = Engine([E("A", 30), E("B", 30)]);
    engine.RetryCount = 2;
    engine.Start(S(0));
    var t = S(0);
    for (int i = 0; i < 20 && engine.Current?.Title != "B"; i++) { t += S(0.5); engine.Tick(t); }
    Check(engine.Current?.Title == "B" && shown.Count(x => x == "A") == 3, "unverified A is retried twice, then B is shown");
    engine.Tick(t + RotationEngine.VerifyDelay);
    Check(engine.Current?.Title == "B" && engine.State == RotationState.Running, "B verified and stays");
}

// 7. Removing the current entry continues with its successor
{
    Reset();
    var list = new List<RotationEntry> { E("A", 5), E("B", 5), E("C", 5) };
    var engine = Engine(list);
    engine.Start(S(0));
    engine.Tick(S(5));
    list.RemoveAt(1); // B (current) removed
    engine.Tick(S(10));
    Check(engine.Current?.Title == "C", "successor of removed current entry is next");
}

// 8. Window matching after restart
{
    WindowInfo W(int h, string title, string process = "msedge.exe", string cls = "Chrome_WidgetWin_1") =>
        new() { Handle = h, Title = title, ProcessName = process, ClassName = cls, ProcessPath = @"C:\Apps\" + process };
    var data = new RotationEntryData { Title = "Dashboard - Microsoft Edge", ProcessName = "msedge.exe", ProcessPath = @"C:\Apps\msedge.exe", ClassName = "Chrome_WidgetWin_1" };
    var none = new HashSet<IntPtr>();

    var (w, amb) = WindowMatcher.Match(data, [W(1, "Mail - Microsoft Edge"), W(2, "Dashboard - Microsoft Edge")], none, exactOnly: false);
    Check(w?.Handle == 2 && !amb, "exact title wins");
    (w, amb) = WindowMatcher.Match(data, [W(1, "Dashboard (3) - Microsoft Edge")], none, exactOnly: false);
    Check(w?.Handle == 1, "single candidate with changed title is accepted");
    (w, _) = WindowMatcher.Match(data, [W(1, "Dashboard (3) - Microsoft Edge")], none, exactOnly: true);
    Check(w == null, "exact-only pass ignores changed titles");
    (w, amb) = WindowMatcher.Match(data, [W(1, "Mail - Microsoft Edge"), W(2, "Wiki - Microsoft Edge")], none, exactOnly: false);
    Check(w == null && amb, "two candidates without exact title are ambiguous");
    (w, amb) = WindowMatcher.Match(data, [W(1, "Mail - Microsoft Edge"), W(2, "Wiki - Microsoft Edge")], new HashSet<IntPtr> { 1 }, exactOnly: false);
    Check(w?.Handle == 2 && !amb, "windows bound to other entries are excluded");
    (w, _) = WindowMatcher.Match(data, [W(1, "Dashboard - Microsoft Edge", process: "chrome.exe")], none, exactOnly: false);
    Check(w == null, "other process never matches");
    data.TitleFilter = "wiki";
    (w, amb) = WindowMatcher.Match(data, [W(1, "Mail - Microsoft Edge"), W(2, "Wiki - Microsoft Edge")], none, exactOnly: false);
    Check(w?.Handle == 2 && !amb, "title filter resolves ambiguity");
}

// 9. Persistence
{
    Check(Store.ValidateProfileName("Halle 1 – Edge + iVMS") == null, "normal profile name is valid");
    Check(Store.ValidateProfileName("a/b") != null && Store.ValidateProfileName("CON") != null && Store.ValidateProfileName(" x") != null
          && Store.ValidateProfileName("") != null && Store.ValidateProfileName("x.") != null, "invalid profile names rejected");

    var profile = new RotationProfile { Name = "Test", TargetMonitor = @"\\.\DISPLAY2", RetryCount = 3 };
    profile.Entries.Add(new RotationEntryData { Title = "X", ProcessName = "x.exe", DurationSeconds = 15, TitleFilter = "Live" });
    Store.SaveProfile(profile);
    var loaded = Store.LoadProfile("Test");
    Check(loaded.TargetMonitor == @"\\.\DISPLAY2" && loaded.RetryCount == 3 && loaded.Entries.Single().DurationSeconds == 15
          && loaded.Entries[0].TitleFilter == "Live", "profile round trip");
    Check(Store.ProfileNames().SequenceEqual(["Test"]), "profile listed");

    File.WriteAllText(Store.ProfilePath("Kaputt"), "{ nicht json");
    try { Store.LoadProfile("Kaputt"); Check(false, "corrupt profile throws"); }
    catch (InvalidDataException) { Check(Directory.GetFiles(AppPaths.ProfileDirectory, "Kaputt.json.defekt-*").Length == 1, "corrupt profile backed up"); }

    File.WriteAllText(Store.ProfilePath("Neu"), """{ "SchemaVersion": 99, "Entries": [] }""");
    try { Store.LoadProfile("Neu"); Check(false, "newer schema throws"); }
    catch (InvalidDataException ex) { Check(ex.Message.Contains("neueren"), "newer schema explained"); }

    File.WriteAllText(Store.ProfilePath("Grenzen"), """{ "SchemaVersion": 1, "RetryCount": 99, "Entries": [ { "ProcessName": "a.exe", "DurationSeconds": 0 }, { "ProcessName": "" } ] }""");
    var clamped = Store.LoadProfile("Grenzen");
    Check(clamped.RetryCount == 10 && clamped.Entries.Count == 1 && clamped.Entries[0].DurationSeconds == RotationProfile.MinDuration, "loaded values are validated");

    File.WriteAllText(AppPaths.SettingsFile, "[]");
    Check(Store.LoadSettings().CloseToTray, "settings with wrong JSON structure fall back to defaults");

    Store.SaveSettings(new AppSettings { LastProfile = "Test", StartRotationOnLaunch = true });
    var settings = Store.LoadSettings();
    Check(settings.LastProfile == "Test" && settings.StartRotationOnLaunch && settings.CloseToTray, "settings round trip");
}

// 9b. Shipped sample profiles must load
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "samples"))) dir = dir.Parent;
    var samples = dir == null ? [] : Directory.GetFiles(Path.Combine(dir.FullName, "samples"), "*.json");
    Check(samples.Length > 0, "sample profiles found");
    foreach (var file in samples)
    {
        try { Check(Store.LoadProfileFile(file).Entries.Count > 0, $"sample {Path.GetFileName(file)} has entries"); }
        catch (InvalidDataException ex) { Check(false, $"sample {Path.GetFileName(file)} loads: {ex.Message}"); }
    }
}

// 10. Duration input is clamped to the valid range
{
    var entry = E("A");
    entry.DurationSeconds = 1;
    Check(entry.DurationSeconds == RotationProfile.MinDuration, "duration lower bound");
    entry.DurationSeconds = 100000;
    Check(entry.DurationSeconds == RotationProfile.MaxDuration, "duration upper bound");
}

try { temp.Delete(recursive: true); } catch (IOException) { }
Console.WriteLine(failures == 0 ? "Alle Prüfungen bestanden." : $"{failures} Prüfung(en) fehlgeschlagen.");
return failures == 0 ? 0 : 1;
