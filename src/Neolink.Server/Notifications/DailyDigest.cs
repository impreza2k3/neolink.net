using System.Net;
using System.Text;
using System.Text.Json;
using Neolink.Recording;

namespace Neolink.Notifications;

/// <summary>
/// Optional local-time digest of recorded human detections. Delivery uses the
/// same Notifier queue and SMTP settings as Neolink's other email messages.
/// </summary>
public sealed class DailyDigest
{
    internal static bool IsOvernightLocal(DateTime eventLocal, DateTime due)
    {
        var start = due.Date.AddDays(-1).AddHours(22);
        var end = due.Date.AddHours(6).AddMinutes(30);
        return eventLocal >= start && eventLocal < end;
    }

    private sealed class Settings
    {
        public bool Enabled { get; set; }
        public int Hour { get; set; } = 6;
        public int Minute { get; set; }
        public bool AllCameras { get; set; } = true;
        public string Camera { get; set; } = "SouthSideStreet";
        public string PublicUrl { get; set; } = "";
    }

    private readonly EventStore _events;
    private readonly Notifier _notifier;
    private readonly string _settingsPath;
    private readonly string _sentPath;

    public DailyDigest(EventStore events, Notifier notifier, string stateDir)
    {
        _events = events;
        _notifier = notifier;
        _settingsPath = Path.Combine(stateDir, "daily-digest.json");
        _sentPath = Path.Combine(stateDir, "daily-digest-last-sent.txt");
    }

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { SendDue(); }
            catch (Exception ex) { Log.Warn($"Daily digest: {Log.Flatten(ex)}"); }
            try { await Task.Delay(TimeSpan.FromMinutes(1), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void SendDue()
    {
        if (!File.Exists(_settingsPath)) return;
        var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(_settingsPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (settings is not { Enabled: true } || settings.Hour is < 0 or > 23
            || settings.Minute is < 0 or > 59) return;
        var now = DateTime.Now;
        var due = now.Date.AddHours(settings.Hour).AddMinutes(settings.Minute);
        if (now < due) return;
        var dayKey = due.ToString("yyyy-MM-dd");
        if (File.Exists(_sentPath) && File.ReadAllText(_sentPath).Trim() == dayKey) return;
        var fromUtc = due.AddDays(-1).ToUniversalTime();
        var toUtc = due.ToUniversalTime();
        var records = _events.List(limit: 100_000, localDate: due.AddDays(-1),
                localTo: due)
            .Where(e => e.StartUtc >= fromUtc && e.StartUtc < toUtc
                && (settings.AllCameras || string.Equals(e.Camera, settings.Camera,
                    StringComparison.OrdinalIgnoreCase))
                && e.Labels.Any(l => l.Equals("person", StringComparison.OrdinalIgnoreCase)
                    || l.Equals("loitering", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(e => e.StartUtc)
            .ToList();

        var configuredUrl = string.IsNullOrWhiteSpace(settings.PublicUrl)
            ? _notifier.Store.Snapshot().PublicUrl : settings.PublicUrl;
        Uri? baseUri = null;
        if (Uri.TryCreate(configuredUrl.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var candidate)
            && candidate.Scheme is "http" or "https")
            baseUri = candidate;
        string? EventLink(EventRecord e) => baseUri == null ? null
            : new Uri(baseUri, $"events?event={Uri.EscapeDataString(e.Id)}").ToString();
        var allEventsLink = baseUri == null ? null : new Uri(baseUri, "events").ToString();

        var overnightStart = due.Date.AddDays(-1).AddHours(22);
        var overnightEnd = due.Date.AddHours(6).AddMinutes(30);
        var overnight = records.Where(e => IsOvernightLocal(e.StartUtc.ToLocalTime(), due)).ToList();
        var other = records.Where(e => !IsOvernightLocal(e.StartUtc.ToLocalTime(), due)).ToList();
        var period = $"{due.AddDays(-1):MMM d, h:mm tt} – {due:MMM d, h:mm tt} (server local time)";
        var body = new StringBuilder()
            .AppendLine($"HUMAN EVENTS | {period}")
            .AppendLine($"{overnight.Count} overnight · {other.Count} other")
            .AppendLine();
        var html = new StringBuilder()
            .Append("<div style=\"color:#6b7280;font-size:13px;margin-bottom:12px;\">")
            .Append(WebUtility.HtmlEncode(period)).Append("</div>")
            .Append("<div style=\"font-size:17px;font-weight:700;color:#111827;margin-bottom:12px;\">")
            .Append(overnight.Count).Append(" overnight · ").Append(other.Count)
            .Append(" other human events</div>");

        var remaining = 200;
        void AppendSection(string title, string window, List<EventRecord> events)
        {
            body.AppendLine($"{title.ToUpperInvariant()} | {window} | {events.Count} event{(events.Count == 1 ? "" : "s")}");
            html.Append("<div style=\"background:#eff6ff;padding:9px 11px;margin-top:14px;color:#1e3a8a;font-size:14px;font-weight:700;\">")
                .Append(WebUtility.HtmlEncode(title)).Append(" · ").Append(events.Count)
                .Append("</div><div style=\"color:#6b7280;font-size:12px;margin:6px 0 8px;\">")
                .Append(WebUtility.HtmlEncode(window)).Append("</div>");
            if (events.Count == 0)
            {
                body.AppendLine("No human detections.").AppendLine();
                html.Append("<p style=\"color:#6b7280;font-size:13px;\">No human detections.</p>");
                return;
            }
            var shown = events.Take(remaining).ToList();
            remaining -= shown.Count;
            foreach (var e in shown)
            {
                var stamp = e.StartUtc.ToLocalTime().ToString("MMM d, HH:mm:ss");
                var duration = Math.Max(0, (e.EndUtc - e.StartUtc).TotalSeconds);
                var labels = string.Join(" + ", e.Labels);
                var ai = string.IsNullOrWhiteSpace(e.AiDescription) ? null
                    : string.Join(" ", e.AiDescription.Split((char[]?)null,
                        StringSplitOptions.RemoveEmptyEntries).Take(80));
                var url = EventLink(e);
                body.AppendLine($"{stamp} | {e.Camera} | {labels} | {duration:0}s");
                if (ai != null) body.AppendLine($"  AI: {ai}");
                if (url != null) body.AppendLine($"  Watch: {url}");
                body.AppendLine();

                html.Append("<div style=\"border-top:1px solid #e5e7eb;padding:11px 0;\">");
                if (url != null)
                    html.Append("<a href=\"").Append(WebUtility.HtmlEncode(url))
                        .Append("\" style=\"color:#1d4ed8;font-weight:700;text-decoration:underline;\">")
                        .Append(WebUtility.HtmlEncode(stamp)).Append("</a>");
                else
                    html.Append("<strong>").Append(WebUtility.HtmlEncode(stamp)).Append("</strong>");
                html.Append("<div style=\"color:#111827;font-size:13px;margin-top:3px;\">")
                    .Append(WebUtility.HtmlEncode(e.Camera)).Append(" · ")
                    .Append(WebUtility.HtmlEncode(labels)).Append($" · {duration:0}s")
                    .Append("</div>");
                if (ai != null)
                    html.Append("<div style=\"color:#4b5563;font-size:13px;margin-top:4px;\">")
                        .Append(WebUtility.HtmlEncode(ai)).Append("</div>");
                html.Append("</div>");
            }
            if (events.Count > shown.Count)
            {
                body.AppendLine($"... and {events.Count - shown.Count} more in Neolink.");
                html.Append("<p>").Append(events.Count - shown.Count).Append(" more events are in Neolink.</p>");
            }
        }

        AppendSection("Overnight alerts",
            $"{overnightStart:MMM d, h:mm tt} – {overnightEnd:MMM d, h:mm tt}", overnight);
        AppendSection("Other human events",
            $"{due.AddDays(-1):MMM d, h:mm tt} – {overnightStart:MMM d, h:mm tt}", other);

        var groups = records.GroupBy(e => e.Camera).OrderBy(g => g.Key).ToList();
        if (groups.Count > 0)
        {
            body.AppendLine("Camera totals:");
            html.Append("<div style=\"font-size:13px;font-weight:700;margin-top:16px;\">Camera totals</div>")
                .Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" ")
                .Append("style=\"margin-bottom:16px;border-collapse:collapse;font-size:13px;\">");
            foreach (var group in groups)
            {
                body.AppendLine($"{group.Key}: {group.Count()}");
                html.Append("<tr><td style=\"padding:4px 0;color:#374151;\">")
                    .Append(WebUtility.HtmlEncode(group.Key))
                    .Append("</td><td align=\"right\" style=\"padding:4px 0;font-weight:700;\">")
                    .Append(group.Count()).Append("</td></tr>");
            }
            html.Append("</table>");
        }
        if (allEventsLink != null)
        {
            body.AppendLine($"All events: {allEventsLink}");
            html.Append("<p><a href=\"").Append(WebUtility.HtmlEncode(allEventsLink))
                .Append("\" style=\"color:#1d4ed8;font-weight:700;\">Open all events</a></p>");
        }

        var alert = new Alert($"daily-digest:{dayKey}", false,
            $"Neolink: {overnight.Count} overnight, {other.Count} other human events through {due:MMM d, h:mm tt}",
            "Human events", body.ToString(), Channels: AlertChannels.Email,
            HtmlBody: html.ToString());
        if (!_notifier.Send(alert)) return;
        // Mark only after the notifier accepts the message. A delivery error is
        // logged by Notifier; it cannot be observed synchronously here.
        var temp = _sentPath + ".tmp";
        File.WriteAllText(temp, dayKey);
        File.Move(temp, _sentPath, overwrite: true);
    }
}
