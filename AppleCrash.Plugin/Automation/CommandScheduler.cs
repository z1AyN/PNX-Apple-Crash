using System;
using System.Collections.Generic;

namespace AppleCrash.Plugin.Automation;

public sealed class CommandScheduler
{
    private static readonly TimeSpan MinimumPacing = TimeSpan.FromMilliseconds(1200);
    private MacroCommand[] commands = [];
    private int next;
    private Guid? roundId;
    private DateTimeOffset delayOrigin;
    private DateTimeOffset? lastDispatch;
    private bool dispatching;
    private int generation;

    public bool IsBusy => next < commands.Length;
    public string Status { get; private set; } = "Idle";
    public bool AwaitingDice { get; private set; }
    public event Action? DiceSent;

    public void Start(IReadOnlyList<MacroCommand> commands, Guid? roundId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(commands);
        if (IsBusy || AwaitingDice || dispatching)
            throw new InvalidOperationException("An execution or unresolved dice attempt is already active.");
        if (commands.Count > 100)
            throw new ArgumentException("An execution may contain at most 100 commands.", nameof(commands));
        var snapshot = new MacroCommand[commands.Count];
        var total = TimeSpan.Zero;
        for (var i = 0; i < snapshot.Length; i++)
        {
            var command = commands[i] ?? throw new ArgumentException("Commands must not be null.", nameof(commands));
            ScriptCompiler.ValidateCommand(command.Text);
            // A compiled delay can combine several individually bounded waits.
            if (command.DelayBefore < TimeSpan.Zero || command.DelayBefore > TimeSpan.FromSeconds(600))
                throw new ArgumentException("Command delays must be between 0 and 600 seconds.", nameof(commands));
            total += command.DelayBefore;
            if (total > TimeSpan.FromSeconds(600))
                throw new ArgumentException("Total delays may not exceed 600 seconds.", nameof(commands));
            snapshot[i] = command;
        }
        this.commands = snapshot;
        this.roundId = roundId;
        next = 0;
        delayOrigin = now;
        generation++;
        Status = IsBusy ? "Running" : "Completed";
    }

    public void Tick(DateTimeOffset now, Guid? currentRoundId, Action<string> send)
    {
        ArgumentNullException.ThrowIfNull(send);
        if (dispatching || (!IsBusy && !AwaitingDice))
            return;
        if (roundId != currentRoundId)
        {
            Cancel("Round changed; pending execution discarded.");
            return;
        }
        if (!IsBusy)
            return;
        var command = commands[next];
        if (now - delayOrigin < command.DelayBefore || (lastDispatch.HasValue && now - lastDispatch.Value < MinimumPacing))
            return;

        // Submission may succeed before the adapter throws: consume first, never retry.
        next++;
        delayOrigin = now;
        lastDispatch = now;
        var dice = ScriptCompiler.IsDice(command.Text);
        if (dice)
            AwaitingDice = true;
        var dispatchedGeneration = generation;
        dispatching = true;
        try
        {
            send(command.Text);
            if (dice)
                DiceSent?.Invoke();
            if (generation == dispatchedGeneration)
                Status = IsBusy ? "Running" : AwaitingDice ? "Awaiting dice" : "Completed";
        }
        catch (Exception ex)
        {
            Cancel("Command dispatch failed; delivery is uncertain. Reconcile before restarting.");
            throw new InvalidOperationException("Command dispatch failed; delivery is uncertain and the queue was cancelled. Do not automatically retry.", ex);
        }
        finally
        {
            dispatching = false;
        }
    }

    public void Cancel(string reason)
    {
        commands = [];
        next = 0;
        AwaitingDice = false;
        generation++;
        Status = string.IsNullOrWhiteSpace(reason) ? "Cancelled" : "Cancelled: " + reason;
    }

    public void CompleteDice()
    {
        AwaitingDice = false;
        if (!IsBusy && Status == "Awaiting dice")
            Status = "Completed";
    }
}
