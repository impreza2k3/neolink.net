using System.Text.Json;
using Neolink.Recording;

namespace Neolink.Notifications;

/// <summary>
/// Optional overnight alert for a human detection that remains active. The
/// timer begins when this worker first observes the human label, so pre-roll
/// cannot turn a short detection into a false 20-second alert.
/// </summary>
public sealed class NightWatch
{
    private sealed class Settings
    {
        public bool Enabled { get; set; }
        public string Camera { get; set; } = "SouthSideStreet";
        public int StartHour { get; set; } = 22;
        public int EndHour { get; set; } = 6;
        public int MinimumSeconds { get; set; } = 20;
        public bool AllowUnverifiedProximity { get; set; }
    }

    private readonly EventStore _events;
    private readonly Notifier _notifier;
    private readonly string _settingsPath;
    private readonly string _sentPath;
    private readonly Dictionary<string, DateTime> _firstSeen = new();
    private readonly HashSet<string> _sent = new(StringComparer.Ordinal);

    public NightWatch(EventStore events, Notifier notifier, string stateDir)
    {
        _events = events;
        _notifier = notifier;
        _settingsPath = Path.Combine(stateDir, "night-watch.json");
        _sentPath = Path.Combine(stateDir, "night-watch-sent.json");
        try
        {
            if (File.Exists(_sentPath))
                foreach (var id in JsonSerializer.Deserialize<string[]>(File.ReadAllText(_sentPath)) ?? [])
                    _sent.Add(id);
        }
        catch (Exception ex) { Log.Warn($"Night watch: sent history unavailable: {Log.Flatten(ex)}"); }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { Poll(); }
            catch (Exception ex) { Log.Warn($"Night watch: {Log.Flatten(ex)}"); }
            try { await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void Poll()
    {
        if (!File.Exists(_settingsPath)) return;
        var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(_settingsPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (settings is not { Enabled: true, AllowUnverifiedProximity: true }
            || settings.StartHour is < 0 or > 23 || settings.EndHour is < 0 or > 23
            || settings.MinimumSeconds is < 1 or > 300 || string.IsNullOrWhiteSpace(settings.Camera)) return;
        int hour = DateTime.Now.Hour;
        bool inWindow = settings.StartHour < settings.EndHour
            ? hour >= settings.StartHour && hour < settings.EndHour
            : hour >= settings.StartHour || hour < settings.EndHour;
        if (!inWindow) { _firstSeen.Clear(); return; }

        var ongoing = _events.List(camera: settings.Camera, limit: 200)
            .Where(e => e.Ongoing && e.Labels.Any(l => l.Equals("person", StringComparison.OrdinalIgnoreCase)
                || l.Equals("loitering", StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var active = ongoing.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var id in _firstSeen.Keys.Where(id => !active.Contains(id)).ToList())
            _firstSeen.Remove(id);
        foreach (var e in ongoing)
        {
            if (_sent.Contains(e.Id)) continue;
            if (!_firstSeen.TryGetValue(e.Id, out var first))
            {
                _firstSeen[e.Id] = DateTime.UtcNow;
                continue;
            }
            if (DateTime.UtcNow - first < TimeSpan.FromSeconds(settings.MinimumSeconds)) continue;
            var local = DateTime.Now;
            var body = $"Human detection on {e.Camera} has stayed active for at least {settings.MinimumSeconds} seconds " +
                $"as of {local:yyyy-MM-dd HH:mm:ss} (server local time). " +
                "The person's proximity to the white truck has not been verified. " +
                $"Event ID: {e.Id}.";
            if (!string.IsNullOrWhiteSpace(e.AiDescription))
                body += " AI description: " + e.AiDescription.Trim();
            var alert = new Alert($"night-watch:{e.Id}", false,
                $"SouthSideStreet: human present {settings.MinimumSeconds}+ seconds",
                "Overnight human detection", body, Channels: AlertChannels.Email,
                Event: new EventInfo(e.Camera, e.Labels.ToArray(), e.StartUtc,
                    (DateTime.UtcNow - first).TotalSeconds, true, e.Id));
            if (!_notifier.Send(alert)) continue;
            _sent.Add(e.Id);
            // Keep enough IDs to cover restarts during an active night without
            // allowing this small state file to grow forever.
            var recent = _sent.TakeLast(500).ToArray();
            var temp = _sentPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(recent));
            File.Move(temp, _sentPath, overwrite: true);
        }
    }
}
