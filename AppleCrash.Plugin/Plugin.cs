using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using AppleCrash.Core;
using AppleCrash.Plugin.Automation;
using AppleCrash.Plugin.Integration;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace AppleCrash.Plugin;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager Commands { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IClientState Client { get; private set; } = null!;
    [PluginService] internal static IPartyList Party { get; private set; } = null!;
    [PluginService] internal static IChatGui Chat { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private readonly ConcurrentQueue<Action> actions = new();
    private readonly CommandScheduler scheduler = new();
    private readonly Queue<string> diagnostics = new();
    private readonly ChatBridge bridge;
    private readonly Dashboard dashboard;
    private readonly string dataDirectory;
    private string? attachedHost;
    private bool disposed;
    private DateTimeOffset? rollDeadline;
    private bool partyWasPresent;
    internal GameEngine? Engine { get; private set; }
    internal bool Simulation { get; private set; }
    internal string Error { get; private set; } = "";
    internal string Notice { get; private set; } = "";
    internal string QueueStatus => scheduler.Status;
    internal bool QueueBusy => scheduler.IsBusy;
    internal IReadOnlyCollection<string> Diagnostics => diagnostics;
    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public Plugin()
    {
        dataDirectory = Path.Combine(PluginInterface.ConfigDirectory.FullName, "ledgers");
        bridge = new ChatBridge(Chat);
        bridge.PartyMessage += OnParty;
        bridge.Diagnostic += text => actions.Enqueue(() => Record(text));
        dashboard = new Dashboard(this);
        Commands.AddHandler("/applecrash", new CommandInfo((_, _) => dashboard.Open = !dashboard.Open)
        { HelpMessage = "Open Apple Crash banking and round controls." });
        PluginInterface.UiBuilder.Draw += dashboard.Draw;
        PluginInterface.UiBuilder.OpenMainUi += Open;
        PluginInterface.UiBuilder.OpenConfigUi += Open;
        Framework.Update += Update;
        Record("Loaded. Native sending is opt-in. Dice requires host confirmation; trade detection is unavailable.");
    }

    private void Open() => dashboard.Open = true;
    internal void Enqueue(Action action) => actions.Enqueue(action);
    internal void ClearError() => Error = "";
    internal void Record(string text)
    {
        diagnostics.Enqueue($"{DateTimeOffset.Now:HH:mm:ss.fff} {text}");
        while (diagnostics.Count > 200) diagnostics.Dequeue();
    }

    private string? CurrentHost()
    {
        if (!Client.IsLoggedIn || !PlayerState.IsLoaded) return null;
        return $"{PlayerState.CharacterName}@{PlayerState.HomeWorld.Value.Name}";
    }

    private void Update(IFramework _)
    {
        if (disposed) return;
        try
        {
            var host = Simulation ? "SIMULATION@Sandbox" : CurrentHost();
            if (host != attachedHost)
            {
                scheduler.Cancel("Host changed or disconnected");
                rollDeadline = null;
                if (Engine?.State.Round?.Phase is RoundPhase.Running or RoundPhase.AwaitingRoll or RoundPhase.CrashReview)
                    Engine.Pause("Host changed or disconnected");
                Engine = null;
                attachedHost = host;
                if (host != null)
                {
                    Engine = new GameEngine(Simulation ? Path.Combine(dataDirectory, "sandbox") : dataDirectory, host);
                    dashboard.ResetSettings();
                    Record($"Attached ledger {host}");
                }
            }
            var partyPresent = Simulation || Party.Length > 0;
            if (partyWasPresent && !partyPresent && Engine?.State.Round?.Phase is RoundPhase.Running or RoundPhase.AwaitingRoll)
                Pause("Party session ended");
            partyWasPresent = partyPresent;
            while (actions.TryDequeue(out var action))
            {
                try { action(); }
                catch (Exception ex) { Fail(ex); }
            }
            if (Engine?.State.Round is { } round)
            {
                if (round.Phase is RoundPhase.CrashReview or RoundPhase.RecoveryPaused)
                    scheduler.Cancel("Round frozen");
                if (round.Phase == RoundPhase.Completed && scheduler.AwaitingDice)
                    scheduler.Cancel("All participants settled");
                scheduler.Tick(DateTimeOffset.UtcNow, round.Id, Send);
                if (rollDeadline is { } deadline && DateTimeOffset.UtcNow > deadline && round.Phase == RoundPhase.AwaitingRoll)
                    Pause("Roll attempt timed out: reconcile before resuming");
            }
            else scheduler.Tick(DateTimeOffset.UtcNow, null, Send);
        }
        catch (Exception ex)
        {
            scheduler.Cancel("Error; host review required");
            Fail(ex);
        }
    }

    private void Fail(Exception ex)
    {
        Error = ex.Message;
        Record($"ERROR {ex.Message}");
        Log.Error(ex, "Apple Crash action failed");
    }

    private void OnParty(string name, string world, string text)
    {
        // Copy callback data immediately, then serialize all game/ledger work on Framework.Update.
        actions.Enqueue(() =>
        {
            if (Simulation || Engine is null) return;
            Record($"Party {name}@{world}: {text}");
            var round = Engine.State.Round;
            if (round?.Phase is not (RoundPhase.Running or RoundPhase.AwaitingRoll)) return;
            if (ChatParser.ContainsStop(text))
            {
                var player = Engine.State.Players.FirstOrDefault(p =>
                    p.Name.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                    p.World.Equals(world, StringComparison.OrdinalIgnoreCase));
                if (player != null && round.Participants.Any(p => p.PlayerId == player.Id && p.Status == ParticipantStatus.InPlay))
                    CashOut(player.Id, false, "Party STOP");
            }
            // External macros are advisory until their host identity and transition can be reviewed.
            if (attachedHost == $"{name}@{world}" && ChatParser.TryTransition(text, out var from, out var to))
                Record($"Host macro observed {from} -> {to}; use Next Step / manual roll controls to arm safely.");
        });
    }

    internal void SetSimulation(bool enabled)
    {
        if (Engine?.State.Round?.Phase is RoundPhase.Running or RoundPhase.AwaitingRoll or RoundPhase.CrashReview)
            throw new InvalidOperationException("Pause the live round before switching ledgers.");
        scheduler.Cancel("Switching simulation");
        Simulation = enabled;
        Notice = enabled ? "SIMULATION: separate ledger; no commands can reach game chat." : "Live host ledger selected.";
    }

    internal void ImportParty()
    {
        var engine = RequireEngine();
        if (Simulation) throw new InvalidOperationException("Add fake players manually in simulation.");
        foreach (var member in Party)
        {
            var name = member.Name.TextValue;
            var world = member.World.Value.Name.ToString();
            if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(world) &&
                !engine.State.Players.Any(p => p.Name == name && p.World == world)) engine.AddPlayer(name, world);
        }
        Notice = "Party imported. Set bets and explicitly enroll players.";
    }

    internal GameEngine RequireEngine() => Engine ?? throw new InvalidOperationException("Log in or enable the isolated simulation ledger first.");

    internal void StartRound()
    {
        var engine = RequireEngine();
        if (scheduler.IsBusy) throw new InvalidOperationException("Wait for or cancel the current macro first.");
        engine.StartRound();
        Record("Round committed; stakes debited.");
        foreach (var p in engine.State.Round!.Participants)
            Announce($"{p.Name}@{p.World}: {p.Stake:N0} gil in play.");
        FlushAnnouncements();
    }

    internal void NextStep()
    {
        var engine = RequireEngine();
        if (scheduler.IsBusy || scheduler.AwaitingDice) throw new InvalidOperationException("An execution/roll is already pending.");
        var next = engine.Settings.Steps.FirstOrDefault(s => s.From == engine.State.Round?.Multiplier)
            ?? throw new InvalidOperationException("No next step is configured.");
        var commands = ScriptCompiler.Compile(next.Script, true);
        if (!Simulation && !engine.Settings.ChatSendingEnabled)
            throw new InvalidOperationException("Enable native sending in Settings, or use Arm For Manual Roll.");
        engine.ArmNextStep();
        scheduler.Start(commands, engine.State.Round!.Id, DateTimeOffset.UtcNow);
        rollDeadline = DateTimeOffset.UtcNow.AddMinutes(12);
        Notice = "Step armed. Confirm the host's actual roll using the roll control; parsing is not verified.";
    }

    internal void ArmManual()
    {
        if (scheduler.IsBusy) throw new InvalidOperationException("Cancel the macro before arming manually.");
        RequireEngine().ArmNextStep();
        rollDeadline = DateTimeOffset.UtcNow.AddMinutes(3);
        Record("Manual roll armed; enter the observed host roll once.");
    }

    internal void ConfirmRoll(int roll)
    {
        var engine = RequireEngine();
        if (scheduler.IsBusy) throw new InvalidOperationException("Wait until the macro has submitted its dice, or cancel and reconcile first.");
        var prior = engine.State.Round!.Participants.ToDictionary(p => p.PlayerId, p => p.Status);
        engine.AcceptRoll(roll);
        scheduler.CompleteDice();
        rollDeadline = null;
        Record($"Host confirmed roll {roll}; multiplier {engine.State.Round!.Multiplier}; {engine.State.Round.Phase}");
        foreach (var p in engine.State.Round.Participants.Where(p => p.Status == ParticipantStatus.CashedOut && prior[p.PlayerId] == ParticipantStatus.InPlay))
            Announce(Format(engine.Settings.WinTemplate, p));
        if (engine.State.Round.Phase == RoundPhase.CrashReview) scheduler.Cancel("Crash: automatic STOP frozen");
        FlushAnnouncements();
    }

    internal void CashOut(string id, bool manual, string reason)
    {
        var engine = RequireEngine();
        engine.CashOut(id, manual, reason);
        var participant = engine.State.Round!.Participants.Single(p => p.PlayerId == id);
        Announce(Format(engine.Settings.WinTemplate, participant));
        FlushAnnouncements();
    }

    internal void FinishCrash(string reason)
    {
        var engine = RequireEngine();
        var losses = engine.State.Round!.Participants.Where(p => p.Status == ParticipantStatus.InPlay).ToList();
        engine.FinishCrash(reason);
        scheduler.Cancel("Crash finalized");
        rollDeadline = null;
        foreach (var p in losses) Announce(Format(engine.Settings.LossTemplate, p));
        FlushAnnouncements();
    }

    internal void Pause(string reason)
    {
        scheduler.Cancel(reason);
        rollDeadline = null;
        RequireEngine().Pause(reason);
        Record($"Paused: {reason}");
    }

    internal void RunScript(string script)
    {
        var engine = RequireEngine();
        if (engine.State.Round?.Phase is RoundPhase.AwaitingRoll or RoundPhase.CrashReview or RoundPhase.RecoveryPaused)
            throw new InvalidOperationException("Do not run informational macros while a roll or review is pending.");
        if (script.Contains("/dice", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Dice can only be submitted through a step.");
        var compiled = ScriptCompiler.Compile(script);
        if (!Simulation && !engine.Settings.ChatSendingEnabled) throw new InvalidOperationException("Native sending is disabled.");
        scheduler.Start(compiled, engine.State.Round?.Id, DateTimeOffset.UtcNow);
    }

    private readonly Queue<string> pendingAnnouncements = new();
    internal IReadOnlyCollection<string> PendingAnnouncements => pendingAnnouncements;
    private void Announce(string text)
    {
        pendingAnnouncements.Enqueue(text);
        Record($"Announcement prepared: {text}");
    }

    internal void FlushAnnouncements()
    {
        if (pendingAnnouncements.Count == 0 || scheduler.IsBusy) return;
        if (!Simulation && Engine?.Settings.ChatSendingEnabled != true)
        {
            Notice = "Settlement saved. Announcements await native sending being enabled and an explicit Send Pending click.";
            return;
        }
        var script = string.Join('\n', pendingAnnouncements.Select(t => "/p " + t));
        var compiled = ScriptCompiler.Compile(script);
        scheduler.Start(compiled, Engine?.State.Round?.Id, DateTimeOffset.UtcNow);
        // Queue ownership passes to the scheduler. Its diagnostic trace records sends; never auto-retry uncertain sends.
        pendingAnnouncements.Clear();
    }

    private void Send(string command)
    {
        Record($"{(Simulation ? "SIMULATED" : "SEND")} {command}");
        if (Simulation) return;
        if (Engine?.Settings.ChatSendingEnabled != true || CurrentHost() != attachedHost)
            throw new InvalidOperationException("Sending disabled or host changed; command discarded.");
        ChatSender.Send(command);
    }

    private string Format(string template, RoundParticipant p)
    {
        var bank = Engine!.State.Players.Single(a => a.Id == p.PlayerId).Balance;
        return template.Replace("{name}", p.Name).Replace("{world}", p.World)
            .Replace("{stake}", p.Stake.ToString("N0", CultureInfo.InvariantCulture))
            .Replace("{multiplier}", (p.ExitMultiplier ?? Engine.State.Round!.Multiplier).ToString("0.##", CultureInfo.InvariantCulture))
            .Replace("{payout}", p.Payout.ToString("N0", CultureInfo.InvariantCulture))
            .Replace("{profit}", (p.Payout - p.Stake).ToString("N0", CultureInfo.InvariantCulture))
            .Replace("{bank}", bank.ToString("N0", CultureInfo.InvariantCulture));
    }

    public void Dispose()
    {
        disposed = true;
        scheduler.Cancel("Plugin unloaded");
        try
        {
            if (Engine?.State.Round?.Phase is RoundPhase.Running or RoundPhase.AwaitingRoll or RoundPhase.CrashReview)
                Engine.Pause("Plugin unloaded");
        }
        catch (Exception ex) { Log.Error(ex, "Could not persist unload pause; recovery still pauses on next load"); }
        Framework.Update -= Update;
        PluginInterface.UiBuilder.Draw -= dashboard.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= Open;
        PluginInterface.UiBuilder.OpenConfigUi -= Open;
        Commands.RemoveHandler("/applecrash");
        bridge.Dispose();
    }
}
