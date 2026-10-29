using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AppleCrash.Core;

namespace AppleCrash.Plugin;

/// <summary>Rebuildable settlement notices. Uncertain sends require explicit host review, never automatic retry.</summary>
internal sealed class AnnouncementOutbox
{
    internal sealed class Entry
    {
        public string Id { get; set; } = "";
        public string Text { get; set; } = "";
        public string Status { get; set; } = "Pending";
    }

    private readonly string path;
    private List<Entry> entries;
    internal IReadOnlyList<Entry> Entries => entries;

    internal AnnouncementOutbox(string directory, string host)
    {
        Directory.CreateDirectory(directory);
        path = Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(host))) + ".announcements.json");
        entries = File.Exists(path)
            ? JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(path)) ?? throw new InvalidDataException("Invalid announcement outbox")
            : [];
        foreach (var entry in entries.Where(e => e.Status == "Sending")) entry.Status = "Uncertain";
        Save();
    }

    internal void Reconcile(LedgerState state, Func<RoundState, RoundParticipant, bool, string> format)
    {
        var changed = false;
        foreach (var round in state.History.Concat(state.Round == null ? [] : new[] { state.Round }))
        foreach (var p in round.Participants)
        {
            Add($"{round.Id}/{p.PlayerId}/stake", $"{p.Name}@{p.World}: {p.Stake:N0} gil in play.");
            if (p.Status is ParticipantStatus.CashedOut or ParticipantStatus.Lost)
                Add($"{round.Id}/{p.PlayerId}/{p.Status}", format(round, p, p.Status == ParticipantStatus.CashedOut));
        }
        if (changed) Save();
        void Add(string id, string text)
        {
            if (entries.Any(e => e.Id == id)) return;
            entries.Add(new Entry { Id = id, Text = text });
            changed = true;
        }
    }

    internal void BeforeSend(string command)
    {
        var text = command.StartsWith("/p ", StringComparison.Ordinal) ? command[3..] : "";
        var entry = entries.FirstOrDefault(e => e.Status == "Queued" && e.Text == text);
        if (entry == null) return;
        entry.Status = "Sending";
        Save();
    }

    internal void AfterSend(string command)
    {
        var text = command.StartsWith("/p ", StringComparison.Ordinal) ? command[3..] : "";
        var entry = entries.FirstOrDefault(e => e.Status == "Sending" && e.Text == text);
        if (entry == null) return;
        entry.Status = "Sent";
        Save();
    }

    internal void Queue(IEnumerable<Entry> selected)
    {
        foreach (var entry in selected) entry.Status = "Queued";
        Save();
    }

    internal void CancelQueued()
    {
        var changed = false;
        foreach (var entry in entries)
        {
            if (entry.Status == "Queued") { entry.Status = "Pending"; changed = true; }
            else if (entry.Status == "Sending") { entry.Status = "Uncertain"; changed = true; }
        }
        if (changed) Save();
    }

    internal void ReviewUncertain(bool resend)
    {
        foreach (var entry in entries.Where(e => e.Status == "Uncertain")) entry.Status = resend ? "Pending" : "Sent";
        Save();
    }

    private void Save()
    {
        var temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(stream, entries, Plugin.Json);
            stream.Flush(true);
        }
        if (File.Exists(path)) File.Replace(temporary, path, path + ".bak");
        else File.Move(temporary, path);
    }
}
