using AppleCrash.Plugin.Automation;

var tests = new (string Name, Action Run)[]
{
    ("Compiler supports bounded waits, party text and whitelist", () =>
    {
        var commands = ScriptCompiler.Compile("hello\n/wait \"2\"\n/apple motion <wait.3>\n/throw\n/hum\n/party ready\n/dice party\n/wait 1", true);
        Equal(6, commands.Count);
        Equal("/p hello", commands[0].Text);
        Equal(TimeSpan.FromSeconds(2), commands[1].DelayBefore);
        Equal(TimeSpan.FromSeconds(3), commands[2].DelayBefore);
    }),
    ("Rejects arbitrary commands, invalid waits and controls", () =>
    {
        foreach (var script in new[] { "/echo nope", "/wait 61", "/wait \"1\" extra", "/p x\0", "/p x\t", "/p x\u2028/dice party", "/dice", "/dice say", "/apple target", "/p x <wait.bad>", "/p x <wait.1> injected", "/p x <wait.1> <wait.2>", "\ud800" })
            Throws<ArgumentException>(() => ScriptCompiler.Compile(script));
        Throws<ArgumentException>(() => ScriptCompiler.Compile("/p safe\n/logout"));
    }),
    ("Compiler enforces byte, count and total wait limits", () =>
    {
        Equal(1, ScriptCompiler.Compile(new string('x', 497)).Count);
        Throws<ArgumentException>(() => ScriptCompiler.Compile(new string('x', 498)));
        Throws<ArgumentException>(() => ScriptCompiler.Compile(new string('\u00e9', 249)));
        Equal(100, ScriptCompiler.Compile(string.Join('\n', Enumerable.Repeat("x", 100))).Count);
        Throws<ArgumentException>(() => ScriptCompiler.Compile(string.Join('\n', Enumerable.Repeat("x", 101))));
        Equal(0, ScriptCompiler.Compile(string.Join('\n', Enumerable.Repeat("/wait 60", 10))).Count);
        Throws<ArgumentException>(() => ScriptCompiler.Compile(string.Join('\n', Enumerable.Repeat("/wait 60", 11))));
    }),
    ("Single dice must be exactly one and last nonwait command", () =>
    {
        foreach (var script in new[] { "", "hello", "/dice party\n/dice party", "/dice party\nafter" })
            Throws<ArgumentException>(() => ScriptCompiler.Compile(script, true));
        Equal(1, ScriptCompiler.Compile("/dice party <wait.1>\n/wait 2", true).Count);
    }),
    ("Pacing and wait deadlines use actual dispatch time without catchup", () =>
    {
        var scheduler = new CommandScheduler();
        var sent = new List<string>();
        var now = DateTimeOffset.UnixEpoch;
        scheduler.Start(ScriptCompiler.Compile("/wait 2\none <wait.3>\ntwo\nthree"), null, now);
        scheduler.Tick(now.AddSeconds(1), null, sent.Add);
        Equal(0, sent.Count);
        scheduler.Tick(now.AddSeconds(2), null, sent.Add);
        scheduler.Tick(now.AddSeconds(4), null, sent.Add);
        Equal(1, sent.Count);
        scheduler.Tick(now.AddSeconds(10), null, sent.Add);
        scheduler.Tick(now.AddSeconds(10), null, sent.Add);
        scheduler.Tick(now.AddSeconds(11.199), null, sent.Add);
        Equal(2, sent.Count);
        scheduler.Tick(now.AddMilliseconds(11200), null, sent.Add);
        Equal(3, sent.Count);
        Equal(false, scheduler.IsBusy);
    }),
    ("Cancellation and stale round discard queued work", () =>
    {
        var scheduler = new CommandScheduler();
        var now = DateTimeOffset.UnixEpoch;
        var round = Guid.NewGuid();
        scheduler.Start(ScriptCompiler.Compile("/wait 5\nhello"), round, now);
        scheduler.Cancel("disconnect");
        scheduler.Tick(now.AddHours(1), round, _ => throw new Exception("Unexpected dispatch"));
        scheduler.Start(ScriptCompiler.Compile("hello"), round, now);
        scheduler.Tick(now, Guid.NewGuid(), _ => throw new Exception("Unexpected stale dispatch"));
        Equal(false, scheduler.IsBusy);
        Equal(true, scheduler.Status.Contains("Round changed"));
    }),
    ("Duplicate start rejected and dice latch precedes send", () =>
    {
        var scheduler = new CommandScheduler();
        var now = DateTimeOffset.UnixEpoch;
        var commands = ScriptCompiler.Compile("/dice party", true);
        var events = 0;
        scheduler.DiceSent += () => events++;
        scheduler.Start(commands, null, now);
        Throws<InvalidOperationException>(() => scheduler.Start(commands, null, now));
        scheduler.Tick(now, null, _ => { Equal(true, scheduler.AwaitingDice); Equal(0, events); });
        Equal(1, events);
        Equal(false, scheduler.IsBusy);
        Equal(true, scheduler.AwaitingDice);
        Throws<InvalidOperationException>(() => scheduler.Start(commands, null, now));
        scheduler.CompleteDice();
        Equal(false, scheduler.AwaitingDice);
        scheduler.Start(commands, null, now);
        scheduler.Tick(now, null, _ => throw new Exception("Pacing not preserved"));
        scheduler.Cancel("stop");
        Equal(false, scheduler.AwaitingDice);
    }),
    ("Uncertain send cancels, propagates and never resends", () =>
    {
        var scheduler = new CommandScheduler();
        var now = DateTimeOffset.UnixEpoch;
        var attempts = 0;
        var events = 0;
        scheduler.DiceSent += () => events++;
        scheduler.Start(ScriptCompiler.Compile("/dice party\nafter"), null, now);
        var error = Throws<InvalidOperationException>(() => scheduler.Tick(now, null, _ => { attempts++; throw new Exception("bridge failed"); }));
        Equal(true, error.Message.Contains("uncertain"));
        Equal("bridge failed", error.InnerException!.Message);
        scheduler.Tick(now.AddHours(1), null, _ => attempts++);
        Equal(1, attempts);
        Equal(0, events);
        Equal(false, scheduler.IsBusy);
        Equal(false, scheduler.AwaitingDice);
    }),
    ("Scheduler validates and snapshots external input", () =>
    {
        var scheduler = new CommandScheduler();
        var now = DateTimeOffset.UnixEpoch;
        foreach (var command in new[] { new MacroCommand("/p x\n/logout", TimeSpan.Zero), new MacroCommand("/logout", TimeSpan.Zero), new MacroCommand("/p x", TimeSpan.FromSeconds(-1)) })
            Throws<ArgumentException>(() => scheduler.Start(new[] { command }, null, now));
        var list = new List<MacroCommand> { new("/p original", TimeSpan.Zero) };
        scheduler.Start(list, null, now);
        list[0] = new("/logout", TimeSpan.Zero);
        scheduler.Tick(now, null, text => Equal("/p original", text));
    }),
    ("Reentrant Tick cannot resend or prematurely dispatch next command", () =>
    {
        var scheduler = new CommandScheduler();
        var now = DateTimeOffset.UnixEpoch;
        var count = 0;
        scheduler.Start(ScriptCompiler.Compile("one\ntwo"), null, now);
        scheduler.Tick(now, null, _ => { count++; scheduler.Tick(now.AddHours(1), null, _ => count++); });
        Equal(1, count);
        scheduler.Tick(now.AddMilliseconds(1200), null, _ => count++);
        Equal(2, count);
    }),
    ("Combined waits remain bounded and fractional waits are deterministic", () =>
    {
        var commands = ScriptCompiler.Compile("/wait 60\n/wait 60\none <wait.0.5>\ntwo");
        Equal(TimeSpan.FromSeconds(120), commands[0].DelayBefore);
        Equal(TimeSpan.FromMilliseconds(500), commands[1].DelayBefore);
        var scheduler = new CommandScheduler();
        var now = DateTimeOffset.UnixEpoch;
        var count = 0;
        scheduler.Start(commands, null, now);
        scheduler.Tick(now.AddSeconds(119), null, _ => count++);
        Equal(0, count);
        scheduler.Tick(now.AddSeconds(120), null, _ => count++);
        Equal(1, count);
        Throws<ArgumentException>(() => new CommandScheduler().Start(new[] { new MacroCommand("/p x", TimeSpan.FromSeconds(601)) }, null, now));
        Throws<ArgumentException>(() => new CommandScheduler().Start(new[] { new MacroCommand("/p x", TimeSpan.FromSeconds(400)), new MacroCommand("/p y", TimeSpan.FromSeconds(400)) }, null, now));
    }),
    ("Cancel inside callback preserves cancellation and discards queue", () =>
    {
        var scheduler = new CommandScheduler();
        var now = DateTimeOffset.UnixEpoch;
        scheduler.Start(ScriptCompiler.Compile("one\ntwo"), null, now);
        scheduler.Tick(now, null, _ => scheduler.Cancel("callback stop"));
        Equal(false, scheduler.IsBusy);
        Equal("Cancelled: callback stop", scheduler.Status);
        scheduler.Tick(now.AddDays(1), null, _ => throw new Exception("Cancelled command was sent"));
    }),
    ("Stale round clears completed dice latch without firing again", () =>
    {
        var scheduler = new CommandScheduler();
        var now = DateTimeOffset.UnixEpoch;
        var round = Guid.NewGuid();
        var count = 0;
        scheduler.Start(ScriptCompiler.Compile("/dice party"), round, now);
        scheduler.Tick(now, round, _ => count++);
        Equal(true, scheduler.AwaitingDice);
        scheduler.Tick(now.AddSeconds(2), null, _ => count++);
        Equal(1, count);
        Equal(false, scheduler.AwaitingDice);
    }),
};

var failed = 0;
foreach (var test in tests)
{
    try { test.Run(); Console.WriteLine("PASS " + test.Name); }
    catch (Exception ex) { failed++; Console.Error.WriteLine("FAIL " + test.Name + ": " + ex); }
}
Console.WriteLine($"{tests.Length - failed}/{tests.Length} tests passed.");
return failed == 0 ? 0 : 1;

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"Expected {expected}, got {actual}.");
}

static T Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T ex) { return ex; }
    throw new Exception($"Expected {typeof(T).Name}.");
}
