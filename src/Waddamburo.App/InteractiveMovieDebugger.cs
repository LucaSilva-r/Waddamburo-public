using System.Collections.Concurrent;
using Waddamburo.Lumen.Runtime;
using Waddamburo.Platform.Sdl;

internal sealed class InteractiveMovieDebugger
{
    private readonly LumenPlayer _player;
    private readonly ConcurrentQueue<string> _commands = new();
    private readonly KeyValuePair<string, int>[] _labels;
    private SdlKeyboardSnapshot _previousKeyboard = SdlKeyboardSnapshot.Empty;

    public InteractiveMovieDebugger(LumenPlayer player)
    {
        _player = player;
        _labels = [.. player.Labels.OrderBy(label => label.Value).ThenBy(label => label.Key, StringComparer.Ordinal)];
        printHelp();
        var inputThread = new Thread(readCommands) { IsBackground = true, Name = "Lumen debug console" };
        inputThread.Start();
    }

    public void Tick(SdlKeyboardSnapshot keyboard)
    {
        while (_commands.TryDequeue(out var command))
            execute(command);
        for (var index = 0; index < Math.Min(9, _labels.Length); index++)
        {
            var key = SdlKeyboardKey.Digit1 + index;
            if (keyboard.IsDown(key) && !_previousKeyboard.IsDown(key))
                gotoLabel(index);
        }
        _previousKeyboard = keyboard;
        _player.Advance(LumenInputAdapter.CreateSnapshot(keyboard));
    }

    private void readCommands()
    {
        while (Console.ReadLine() is { } line)
            _commands.Enqueue(line);
    }

    private void execute(string rawCommand)
    {
        var command = rawCommand.Trim();
        if (command.Length == 0)
            return;
        try
        {
            if (command is "help" or "?")
                printHelp();
            else if (command == "callbacks")
                printCallbacks();
            else if (command is "labels" or "states")
                printLabels();
            else if (command.StartsWith("state ", StringComparison.Ordinal)
                && int.TryParse(command.AsSpan(6), out var state))
                gotoLabel(state - 1);
            else
            {
                var expression = command.StartsWith("invoke ", StringComparison.Ordinal)
                    ? command[7..]
                    : command;
                if (!expression.Contains('|'))
                    expression = string.Join('|', expression.Split(' ', StringSplitOptions.RemoveEmptyEntries));
                var invocation = CallbackInvocation.ParseExpression(expression);
                if (!_player.TryInvokeCallback(invocation.Name, invocation.Arguments))
                    Console.WriteLine($"Callback '{invocation.Name}' is not registered or could not complete.");
                else
                    Console.WriteLine($"Invoked Lumen callback: {invocation.Name}");
            }
        }
        catch (Exception exception) when (exception is ArgumentException or KeyNotFoundException)
        {
            Console.WriteLine($"Debug command rejected: {exception.Message}");
        }
    }

    private void gotoLabel(int index)
    {
        if ((uint)index >= (uint)Math.Min(9, _labels.Length))
        {
            Console.WriteLine($"State must be between 1 and {Math.Min(9, _labels.Length)}.");
            return;
        }
        var label = _labels[index];
        _player.GotoLabel(label.Key, play: true);
        Console.WriteLine($"State {index + 1}: {label.Key} (frame {label.Value})");
    }

    private void printHelp()
    {
        Console.WriteLine("Lumen debug controls:");
        printLabels();
        printCallbacks();
        Console.WriteLine("  keyboard 1-9 or terminal 'state N' switches a root label");
        Console.WriteLine("  terminal: invoke Callback|n:1|b:true|s:text|null|undefined");
        Console.WriteLine("  terminal: callbacks, labels, help");
    }

    private void printLabels()
    {
        Console.WriteLine("Root states:");
        foreach (var (label, index) in _labels.Take(9).Select((label, index) => (label, index)))
            Console.WriteLine($"  {index + 1}: {label.Key} (frame {label.Value})");
    }

    private void printCallbacks()
    {
        Console.WriteLine("Exposed callbacks:");
        foreach (var callback in _player.Callbacks)
            Console.WriteLine($"  {callback.Name}({string.Join(", ", callback.Parameters)})");
    }
}
