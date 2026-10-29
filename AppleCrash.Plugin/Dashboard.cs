using System.Globalization;
using System.Numerics;
using System.Text.Json;
using AppleCrash.Core;
using AppleCrash.Plugin.Automation;
using Dalamud.Bindings.ImGui;

namespace AppleCrash.Plugin;

internal sealed class Dashboard(Plugin plugin)
{
    internal bool Open;
    private string name = "", world = "", playerId = "", bet = "0", amount = "0", reason = "";
    private string settingsJson = "", scriptPreview = "", path = "", multiplier = "1";
    private string confirmation = "";
    private Action? confirm;
    private int roll = 1;
    private bool nativeAcknowledgment;
    private int selectedStep;
    private string stepScript = "";
    private string stepThreshold = "", stepDelay = "10";

    internal void ResetSettings()
    {
        settingsJson = "";
        playerId = "";
        selectedStep = 0;
        stepScript = "";
    }

    internal void Draw()
    {
        if (!Open) return;
        ImGui.SetNextWindowSize(new Vector2(1000, 740), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Apple Crash###AppleCrashMain", ref Open)) { ImGui.End(); return; }
        try
        {
            var simulation = plugin.Simulation;
            if (ImGui.Checkbox("Isolated simulation (never sends game commands)", ref simulation))
                Ask("Switch to a separate ledger? Any current round must be paused first.", () => plugin.SetSimulation(simulation));
            ImGui.SameLine();
            ImGui.TextDisabled("/applecrash");
            if (plugin.Simulation) ImGui.TextColored(new Vector4(1, .8f, .2f, 1), "SIMULATION ONLY - FAKE BANK AND CHAT");
            if (!string.IsNullOrEmpty(plugin.Error))
            {
                ImGui.TextColored(new Vector4(1, .3f, .3f, 1), plugin.Error);
                if (ImGui.SmallButton("Dismiss error")) plugin.ClearError();
            }
            if (!string.IsNullOrEmpty(plugin.Notice)) ImGui.TextWrapped(plugin.Notice);
            var engine = plugin.Engine;
            if (engine == null)
            {
                ImGui.TextWrapped("Log into a character to open that host's ledger, or enable the isolated simulation above.");
                return;
            }
            ImGui.Text($"Ledger: {engine.State.HostId} | Macro: {plugin.QueueStatus}");
            if (engine.StorageError != null) ImGui.TextColored(new Vector4(1, .3f, .3f, 1), engine.StorageError);
            if (ImGui.BeginTabBar("Tabs"))
            {
                if (ImGui.BeginTabItem("Round")) { DrawRound(engine); ImGui.EndTabItem(); }
                if (ImGui.BeginTabItem("Bank")) { DrawBank(engine); ImGui.EndTabItem(); }
                if (ImGui.BeginTabItem("Macros & Settings")) { DrawSettings(engine); ImGui.EndTabItem(); }
                if (ImGui.BeginTabItem("History & Backup")) { DrawHistory(engine); ImGui.EndTabItem(); }
                if (ImGui.BeginTabItem("Diagnostics")) { DrawDiagnostics(); ImGui.EndTabItem(); }
                ImGui.EndTabBar();
            }
        }
        catch (Exception ex) { ImGui.TextWrapped($"UI error: {ex.Message}"); }
        finally
        {
            DrawConfirmation();
            ImGui.End();
        }
    }

    private void DrawRound(GameEngine engine)
    {
        var round = engine.State.Round;
        ImGui.Text($"State: {round?.Phase.ToString() ?? "Idle"}     Available cash-out: {round?.Multiplier ?? 1:0.##}x");
        if (round != null)
        {
            var totalStake = round.Participants.Aggregate(0m, (sum, p) => sum + p.Stake);
            var paid = round.Participants.Aggregate(0m, (sum, p) => sum + p.Payout);
            var exposure = round.Participants.Where(p => p.Status == ParticipantStatus.InPlay)
                .Aggregate(0m, (sum, p) => sum + decimal.Floor(p.Stake * round.Multiplier));
            ImGui.Text($"Stakes {totalStake:N0} | Paid {paid:N0} | Active cash-out liability {exposure:N0} gil");
            if (round.PendingStep != null)
                ImGui.Text($"Pending: {round.PendingStep.From:0.##} -> {round.PendingStep.To:0.##} | roll >= {round.PendingStep.Threshold}");
        }
        if (ImGui.Button("Start Round / Preview"))
        {
            var errors = engine.ValidateStart();
            var preview = string.Join("\n", engine.State.Players.Where(p => p.Enrolled && !p.Archived)
                .Select(p => $"{p.Name}@{p.World}: stake {p.PreferredBet:N0}; bank after {p.Balance - p.PreferredBet:N0}"));
            Ask(errors.Count == 0 ? "Commit ALL these stakes?\n" + preview : "Start blocked:\n" + string.Join("\n", errors),
                errors.Count == 0 ? plugin.StartRound : null);
        }
        ImGui.SameLine();
        if (ImGui.Button("Next Step (macro)")) plugin.Enqueue(plugin.NextStep);
        ImGui.SameLine();
        if (ImGui.Button("Arm For Manual Roll")) plugin.Enqueue(plugin.ArmManual);
        if (ImGui.Button("Pause / Cancel Macro")) Ask("Cancel pending commands and pause for explicit reconciliation?", () => plugin.Pause("Host paused from dashboard"));
        ImGui.SameLine();
        if (ImGui.Button("Finish Crash: finalize losses"))
            Ask("Finalize every remaining active stake as LOST and announce losses? Review missed STOPs first.", () => plugin.FinishCrash("Host finalized crash after review"));
        ImGui.Separator();
        ImGui.TextWrapped("Dice automation is fail-closed until verified in game. Confirm only the actual host roll for the armed step. No other player's dice counts.");
        ImGui.SetNextItemWidth(120);
        ImGui.InputInt("Observed host roll (0-999)", ref roll);
        ImGui.SameLine();
        if (ImGui.Button("Confirm Roll"))
        {
            var value = roll;
            Ask($"Record host roll {value} for the pending step? This can advance or freeze the round, and pays all at the final step.", () => plugin.ConfirmRoll(value));
        }
        ImGui.InputText("Correction / recovery reason", ref reason, 256);
        if (round?.Phase == RoundPhase.RecoveryPaused)
        {
            ImGui.TextWrapped("Recovered rounds never replay commands. Reconcile pending roll evidence before resuming. A pending step must be manually resolved.");
            if (ImGui.Button("Resume after reconciliation"))
            {
                var why = reason;
                Ask("Resume this round after reviewing balances, settlements and roll state?", () => engine.Resume(why));
            }
        }
        if (ImGui.Button("Refund remaining active stakes"))
        {
            var why = reason;
            Ask("Refund ONLY unsettled active stakes? Existing cash-outs remain credited. A reason is required.", () => engine.RefundActive(why));
        }
        ImGui.SetNextItemWidth(120);
        ImGui.InputText("Correct multiplier", ref multiplier, 30);
        ImGui.SameLine();
        if (ImGui.Button("Apply multiplier correction"))
        {
            var text = multiplier; var why = reason;
            Ask("Record multiplier correction? Existing payouts are not silently recalculated. Reason required.",
                () => engine.CorrectMultiplier(decimal.Parse(text, CultureInfo.InvariantCulture), why));
        }
        ImGui.Separator();
        if (round != null && ImGui.BeginTable("Participants", 6, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            foreach (var title in new[] { "Player", "Stake", "State", "Exit", "Payout", "Host action" }) ImGui.TableSetupColumn(title);
            ImGui.TableHeadersRow();
            foreach (var p in round.Participants)
            {
                ImGui.TableNextRow();
                Cell(0, $"{p.Name}@{p.World}"); Cell(1, $"{p.Stake:N0}"); Cell(2, p.Status.ToString());
                Cell(3, p.ExitMultiplier?.ToString("0.##") ?? "-"); Cell(4, $"{p.Payout:N0}");
                ImGui.TableSetColumnIndex(5);
                if (p.Status == ParticipantStatus.InPlay && ImGui.SmallButton($"Cash Out##{p.PlayerId}"))
                {
                    var id = p.PlayerId; var why = reason;
                    Ask($"Manually settle {p.Name} at {round.Multiplier:0.##}x? Reason required (e.g. missed STOP).",
                        () => plugin.CashOut(id, true, why));
                }
            }
            ImGui.EndTable();
        }
        if (plugin.PendingAnnouncements.Count > 0)
        {
            ImGui.Text($"Pending local announcements: {plugin.PendingAnnouncements.Count}");
            if (ImGui.Button("Send Pending Announcements")) plugin.Enqueue(plugin.FlushAnnouncements);
        }
    }

    private void DrawBank(GameEngine engine)
    {
        ImGui.TextWrapped("This ledger tracks obligations, not physical gil. Complete real trades yourself. Deposits and withdrawals require your confirmation; trade detection is not verified and is disabled.");
        ImGui.Text($"Total bank obligation: {engine.State.Players.Aggregate(0m, (sum, p) => sum + p.Balance):N0} gil");
        if (ImGui.Button("Import party roster")) plugin.Enqueue(plugin.ImportParty);
        ImGui.InputText("Character name", ref name, 80);
        ImGui.InputText("Home world (exact)", ref world, 60);
        if (ImGui.Button("Add player"))
        {
            var n = name; var w = world;
            plugin.Enqueue(() => engine.AddPlayer(n, w));
        }
        if (ImGui.BeginTable("Accounts", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY, new Vector2(0, 220)))
        {
            foreach (var title in new[] { "Player", "Bank", "Preferred bet", "Enrolled", "Edit" }) ImGui.TableSetupColumn(title);
            ImGui.TableHeadersRow();
            foreach (var p in engine.State.Players.Where(p => !p.Archived))
            {
                ImGui.TableNextRow(); Cell(0, $"{p.Name}@{p.World}"); Cell(1, $"{p.Balance:N0}"); Cell(2, $"{p.PreferredBet:N0}");
                ImGui.TableSetColumnIndex(3);
                var enrolled = p.Enrolled;
                if (ImGui.Checkbox($"##enroll{p.Id}", ref enrolled))
                {
                    var id = p.Id; var preferred = p.PreferredBet;
                    plugin.Enqueue(() => engine.ConfigurePlayer(id, preferred, enrolled));
                }
                ImGui.TableSetColumnIndex(4);
                if (ImGui.SmallButton($"Select##{p.Id}")) { playerId = p.Id; bet = p.PreferredBet.ToString(CultureInfo.InvariantCulture); }
            }
            ImGui.EndTable();
        }
        var selected = engine.State.Players.FirstOrDefault(p => p.Id == playerId);
        if (selected == null) { ImGui.TextDisabled("Select a player to edit bets/bank."); return; }
        ImGui.Text($"Selected: {selected.Name}@{selected.World}");
        ImGui.InputText("Preferred bet in gil", ref bet, 30);
        if (ImGui.Button("Save preferred bet"))
        {
            var id = selected.Id; var text = bet; var enrolled = selected.Enrolled;
            plugin.Enqueue(() => engine.ConfigurePlayer(id, ParseGil(text), enrolled));
        }
        ImGui.InputText("Amount in gil", ref amount, 30);
        ImGui.InputText("Required transaction reason / trade reference", ref reason, 256);
        if (ImGui.Button("Deposit (+)")) BankAction(engine, selected, "Deposit", 1);
        ImGui.SameLine();
        if (ImGui.Button("Withdrawal (-)")) BankAction(engine, selected, "Withdrawal", -1);
        ImGui.SameLine();
        if (ImGui.Button("Set exact bank (correction)")) BankAction(engine, selected, "Set exact balance", 0);
        if (ImGui.Button("Correct this round's payout to amount"))
        {
            var id = selected.Id; var text = amount; var why = reason;
            Ask("Replace this participant's payout amount using an audited compensating adjustment? No silent re-enrollment. Reason required.",
                () => engine.CorrectPayout(id, ParseGil(text), why));
        }
        ImGui.SameLine();
        if (ImGui.Button("Archive empty player"))
        {
            var id = selected.Id;
            Ask("Archive this player? Nonzero banks or unsettled stakes must be resolved first.", () => engine.ArchivePlayer(id));
        }
    }

    private void BankAction(GameEngine engine, PlayerAccount player, string kind, int direction)
    {
        var id = player.Id; var text = amount; var why = reason;
        Ask($"{kind} for {player.Name}@{player.World}: {text} gil? Verify the real trade. A reason is required.", () =>
        {
            var value = ParseGil(text);
            if (direction == 0) engine.SetBalance(id, value, why);
            else engine.AdjustBalance(id, checked(value * direction), why);
        });
    }

    private void DrawSettings(GameEngine engine)
    {
        ImGui.TextWrapped("Settings apply to the next round. Configure BOTH 1.4 transition thresholds before starting. Step scripts use {from}, {to}, {threshold}, {delay} only if expanded explicitly; enter actual values here to avoid ambiguity.");
        ImGui.TextWrapped("Native command sending uses version-sensitive client internals. Third-party plugins/automation may violate FFXIV terms. Keep it disabled until tested with a fake/tiny stake.");
        ImGui.Checkbox("I understand native sending / automation risk", ref nativeAcknowledgment);
        var enabled = engine.Settings.ChatSendingEnabled;
        if (ImGui.Checkbox("Enable outbound game chat / emotes / dice", ref enabled))
        {
            if (enabled && !nativeAcknowledgment) Ask("Acknowledge native-sending risk first.", null);
            else
            {
                var updated = JsonSerializer.Deserialize<PluginSettings>(JsonSerializer.Serialize(engine.Settings, Plugin.Json), Plugin.Json)!;
                updated.ChatSendingEnabled = enabled;
                plugin.Enqueue(() => engine.SaveSettings(updated));
                settingsJson = "";
            }
        }
        if (ImGui.Button("Post Rules")) plugin.Enqueue(() => plugin.RunScript(engine.Settings.RulesScript));
        ImGui.SameLine();
        if (ImGui.Button("Play Intro")) plugin.Enqueue(() => plugin.RunScript(engine.Settings.IntroScript));
        ImGui.Separator();
        var steps = engine.Settings.Steps;
        if (steps.Count > 0)
        {
            selectedStep = Math.Clamp(selectedStep, 0, steps.Count - 1);
            if (ImGui.BeginCombo("Edit step", $"{steps[selectedStep].From:0.##} -> {steps[selectedStep].To:0.##}"))
            {
                for (var i = 0; i < steps.Count; i++)
                    if (ImGui.Selectable($"{steps[i].From:0.##} -> {steps[i].To:0.##}", i == selectedStep)) { selectedStep = i; stepScript = ""; }
                ImGui.EndCombo();
            }
            var step = steps[selectedStep];
            if (stepScript == "")
            {
                stepScript = step.Script;
                stepThreshold = step.Threshold?.ToString() ?? "";
                stepDelay = step.DelaySeconds.ToString();
            }
            ImGui.InputText("Pass at or above (blank = unconfigured)", ref stepThreshold, 10);
            ImGui.InputText("Decision delay (seconds)", ref stepDelay, 10);
            ImGui.InputTextMultiline("Step macro", ref stepScript, 12000, new Vector2(-1, 135));
            if (ImGui.Button("Save step (and validate macro)"))
            {
                var index = selectedStep; var threshold = stepThreshold; var delay = stepDelay; var script = stepScript;
                plugin.Enqueue(() =>
                {
                    ScriptCompiler.Compile(script, true);
                    var updated = JsonSerializer.Deserialize<PluginSettings>(JsonSerializer.Serialize(engine.Settings, Plugin.Json), Plugin.Json)!;
                    updated.Steps[index].Threshold = string.IsNullOrWhiteSpace(threshold) ? null : int.Parse(threshold, CultureInfo.InvariantCulture);
                    updated.Steps[index].DelaySeconds = int.Parse(delay, CultureInfo.InvariantCulture);
                    updated.Steps[index].Script = script;
                    engine.SaveSettings(updated);
                });
                settingsJson = "";
            }
            ImGui.SameLine();
            if (ImGui.Button("Preview compiled commands"))
            {
                try { scriptPreview = string.Join("\n", ScriptCompiler.Compile(stepScript, true).Select(c => $"wait {c.DelayBefore.TotalSeconds:0.##}s: {c.Text}")); }
                catch (Exception ex) { scriptPreview = ex.Message; }
            }
            if (scriptPreview != "") ImGui.TextWrapped(scriptPreview);
        }
        if (ImGui.CollapsingHeader("Full settings JSON (rules, intro, templates, ladder)"))
        {
            if (settingsJson == "") settingsJson = JsonSerializer.Serialize(engine.Settings, Plugin.Json);
            ImGui.TextWrapped("Placeholders for payouts: {name}, {world}, {stake}, {multiplier}, {payout}, {profit}, {bank}. Adding {bank} exposes private balances in party chat.");
            ImGui.InputTextMultiline("##settingsjson", ref settingsJson, 65536, new Vector2(-1, 250));
            if (ImGui.Button("Validate and save full settings"))
            {
                var json = settingsJson;
                plugin.Enqueue(() =>
                {
                    var updated = JsonSerializer.Deserialize<PluginSettings>(json, Plugin.Json) ?? throw new InvalidOperationException("Invalid settings JSON.");
                    if (updated.ChatSendingEnabled && !nativeAcknowledgment) throw new InvalidOperationException("Acknowledge native sending first.");
                    ScriptCompiler.Compile(updated.RulesScript);
                    ScriptCompiler.Compile(updated.IntroScript);
                    foreach (var step in updated.Steps) if (step.Threshold != null) ScriptCompiler.Compile(step.Script, true);
                    engine.SaveSettings(updated);
                });
                stepScript = "";
            }
            ImGui.SameLine();
            if (ImGui.Button("Reload saved settings")) settingsJson = "";
        }
    }

    private void DrawHistory(GameEngine engine)
    {
        ImGui.TextWrapped("Exports contain private player balances and names. Keep backups secure. Import replaces this host ledger only after validation and confirmation; never merge by display name.");
        ImGui.InputText("Absolute JSON export / import path", ref path, 1024);
        if (ImGui.Button("Export ledger backup")) { var file = path; plugin.Enqueue(() => engine.Export(file)); }
        ImGui.SameLine();
        if (ImGui.Button("Import ledger backup"))
        {
            var file = path;
            Ask("Replace this host ledger with the validated export? Export the current ledger first. No active round may be running.", () => engine.Import(file));
        }
        if (ImGui.Button("Restore last-known-good backup"))
            Ask("Restore last-known-good backup? Review the resulting balances and paused round before playing. The most recent action may be absent.", engine.RestoreBackup);
        ImGui.Separator();
        ImGui.Text($"Audit entries: {engine.State.Audit.Count} (newest first; read-only)");
        ImGui.BeginChild("Audit", new Vector2(0, 0), false);
        foreach (var a in engine.State.Audit.AsEnumerable().Reverse().Take(500))
            ImGui.TextWrapped($"{a.Timestamp:u} | {a.Kind} | {a.PlayerId} | {a.Amount:N0} | {a.Reason}");
        ImGui.EndChild();
    }

    private void DrawDiagnostics()
    {
        ImGui.TextWrapped("Automatic dice attribution and completed-trade detection are not verified; host roll confirmation and manual bank entries are required. Unknown chat events never change balances.");
        ImGui.TextWrapped("These bounded traces are local. Financial history is stored separately in the ledger. Simulation uses a separate ledger and does not read live party STOPs.");
        ImGui.BeginChild("Diagnostics", new Vector2(0, 0), false);
        foreach (var line in plugin.Diagnostics) ImGui.TextWrapped(line);
        ImGui.EndChild();
    }

    private void Ask(string text, Action? action)
    {
        confirmation = text;
        confirm = action;
        ImGui.OpenPopup("Confirm Apple Crash action");
    }

    private void DrawConfirmation()
    {
        if (!ImGui.BeginPopupModal("Confirm Apple Crash action", ImGuiWindowFlags.AlwaysAutoResize)) return;
        ImGui.PushTextWrapPos(620);
        ImGui.TextWrapped(confirmation);
        ImGui.PopTextWrapPos();
        if (confirm != null && ImGui.Button("Confirm"))
        {
            plugin.Enqueue(confirm);
            confirm = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel / Close")) { confirm = null; ImGui.CloseCurrentPopup(); }
        ImGui.EndPopup();
    }

    private static void Cell(int column, string text) { ImGui.TableSetColumnIndex(column); ImGui.TextUnformatted(text); }
    private static long ParseGil(string text)
    {
        if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < 0)
            throw new InvalidOperationException("Enter a nonnegative whole gil amount without separators.");
        return value;
    }
}
