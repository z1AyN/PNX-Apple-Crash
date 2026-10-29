using System.Text.RegularExpressions;
using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;

namespace AppleCrash.Plugin.Integration;

/// <summary>Converts API 15 chat events into stable, copied application values.</summary>
public sealed class ChatBridge : IDisposable
{
    private static readonly Regex SuggestedRollPattern = new(
        @"\b(?:roll(?:ed)?|dice)\D{0,20}(?<roll>\d{1,3})\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly IChatGui chat;
    private bool disposed;

    public ChatBridge(IChatGui chat)
    {
        this.chat = chat ?? throw new ArgumentNullException(nameof(chat));
        this.chat.ChatMessage += OnChatMessage;
        this.chat.LogMessage += OnLogMessage;
    }

    public event Action<string, string, string>? PartyMessage;
    public event Action<string>? Diagnostic;
    public event Action<int, string>? SuggestedRoll;

    // No current installed-client LogMessage ID/argument contract has been verified.
    public bool AutomaticDiceVerified => false;

    private void OnChatMessage(IHandleableChatMessage message)
    {
        if (message.LogKind != XivChatType.Party)
            return;

        var text = message.Message.TextValue;
        if (!TryGetIdentity(message.Sender, out var name, out var world))
        {
            Diagnostic?.Invoke("Party message ignored: sender had no structured PlayerPayload identity.");
            return;
        }

        PartyMessage?.Invoke(name, world, text);
        var match = SuggestedRollPattern.Match(text);
        if (match.Success && int.TryParse(match.Groups["roll"].Value, out var roll))
            SuggestedRoll?.Invoke(roll, text);
    }

    private void OnLogMessage(ILogMessage message)
    {
        // The installed API exposes IDs and typed parameters, but no verified dice ID/shape.
        // Keep this diagnostic-only so localized or unrelated log entries cannot settle a round.
        if (message.LogMessageId != 0)
            Diagnostic?.Invoke($"LogMessage observed (ID {message.LogMessageId}); automatic dice attribution remains disabled until an in-game sample verifies its ID and arguments.");
    }

    private static bool TryGetIdentity(SeString sender, out string name, out string world)
    {
        name = string.Empty;
        world = string.Empty;
        try
        {
            var player = sender.Payloads.OfType<PlayerPayload>().FirstOrDefault();
            if (player is null || string.IsNullOrWhiteSpace(player.PlayerName) || !player.World.IsValid)
                return false;

            name = player.PlayerName;
            world = player.World.Value.Name.ToString();
            return !string.IsNullOrWhiteSpace(world);
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        chat.ChatMessage -= OnChatMessage;
        chat.LogMessage -= OnLogMessage;
    }
}
