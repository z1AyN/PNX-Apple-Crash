using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace AppleCrash.Plugin.Integration;

/// <summary>Minimal native chat bridge. Callers must pass scheduler-validated commands.</summary>
public static unsafe class ChatSender
{
    public static void Send(string command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        var ui = UIModule.Instance();
        if (ui is null)
            throw new InvalidOperationException("UIModule is unavailable; chat command was not sent.");

        var message = new Utf8String(command);
        try
        {
            ui->ProcessChatBoxEntry(&message);
        }
        finally
        {
            message.Dtor();
        }
    }
}
