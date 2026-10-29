using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AppleCrash.Plugin.Automation;

public record MacroCommand(string Text, TimeSpan DelayBefore);

public static class ScriptCompiler
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly Regex InlineWait = new(@"\s*<wait\.(\d+(?:\.\d+)?)>\s*$", RegexOptions.CultureInvariant);
    private static readonly Regex Wait = new("^/wait\\s+(?:\"(\\d+(?:\\.\\d+)?)\"|(\\d+(?:\\.\\d+)?))$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static IReadOnlyList<MacroCommand> Compile(string script, bool requireSingleDice = false)
    {
        ArgumentNullException.ThrowIfNull(script);
        if (script.Length > 100_000)
            throw new ArgumentException("Script exceeds the 100,000-character input limit.", nameof(script));

        var commands = new List<MacroCommand>();
        var pending = TimeSpan.Zero;
        var total = TimeSpan.Zero;
        var diceCount = 0;
        foreach (var raw in script.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            ValidateCharacters(raw);
            var line = raw.Trim();
            if (line.Length == 0)
                continue;
            var suffix = InlineWait.Match(line);
            var after = TimeSpan.Zero;
            if (suffix.Success)
            {
                after = ParseWait(suffix.Groups[1].Value);
                line = line[..suffix.Index].Trim();
            }
            if (line.Contains("<wait", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Only one valid <wait.N> suffix is allowed per line.");

            var wait = Wait.Match(line);
            if (wait.Success)
            {
                var duration = ParseWait(wait.Groups[1].Success ? wait.Groups[1].Value : wait.Groups[2].Value);
                pending += duration;
                total += duration;
            }
            else if (line.Length != 0)
            {
                var text = line.StartsWith('/') ? line : "/p " + line;
                ValidateCommand(text);
                commands.Add(new MacroCommand(text, pending));
                pending = TimeSpan.Zero;
                if (IsDice(text))
                    diceCount++;
                if (commands.Count > 100)
                    throw new ArgumentException("A script may contain at most 100 outbound commands.");
            }
            pending += after;
            total += after;
            if (total > TimeSpan.FromSeconds(600))
                throw new ArgumentException("Total script waits may not exceed 600 seconds.");
        }
        if (requireSingleDice && (diceCount != 1 || commands.Count == 0 || !IsDice(commands[^1].Text)))
            throw new ArgumentException("A step must contain exactly one /dice party as its last non-wait command.");
        return commands.AsReadOnly();
    }

    internal static bool IsDice(string text) => text.Equals("/dice party", StringComparison.OrdinalIgnoreCase);

    internal static void ValidateCommand(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        ValidateCharacters(text);
        if (StrictUtf8.GetByteCount(text) > 500)
            throw new ArgumentException("An outbound command may not exceed 500 UTF-8 bytes.");
        var split = text.IndexOf(' ');
        var verb = (split < 0 ? text : text[..split]).ToLowerInvariant();
        var args = split < 0 ? "" : text[(split + 1)..];
        var valid = verb switch
        {
            "/p" or "/party" => !string.IsNullOrWhiteSpace(args),
            "/apple" or "/throw" or "/hum" => args.Length == 0 || args.Equals("motion", StringComparison.OrdinalIgnoreCase),
            "/dice" => args.Equals("party", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
        if (!valid || text.Contains("<wait", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only party chat, /apple, /throw, /hum (optionally motion), and /dice party are allowed.");
    }

    private static TimeSpan ParseWait(string value)
    {
        if (!decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds) || seconds < 0 || seconds > 60)
            throw new ArgumentException("Each wait must be between 0 and 60 seconds.");
        return TimeSpan.FromTicks(checked((long)(seconds * TimeSpan.TicksPerSecond)));
    }

    private static void ValidateCharacters(string text)
    {
        foreach (var character in text)
            if (char.IsControl(character) || character is '\u2028' or '\u2029')
                throw new ArgumentException("Commands must not contain control characters or embedded line separators.");
        try { _ = StrictUtf8.GetByteCount(text); }
        catch (EncoderFallbackException ex) { throw new ArgumentException("Commands must contain valid Unicode.", ex); }
    }
}
