using System.Text;
using System.Threading;

namespace Tractus.Ndi.ConfigTui;

internal sealed record TerminalInput(ConsoleKeyInfo? Key, TerminalMouseEvent? Mouse)
{
    public static TerminalInput FromKey(ConsoleKeyInfo key) => new(key, null);

    public static TerminalInput FromMouse(TerminalMouseEvent mouse) => new(null, mouse);
}

internal sealed record TerminalMouseEvent(int X, int Y, MouseButton Button, bool Pressed);

internal enum MouseButton
{
    Left,
    Middle,
    Right,
    WheelUp,
    WheelDown,
    Other
}

internal static class TerminalInputReader
{
    public static TerminalInput Read() => ReadAvailable();

    public static TerminalInput? TryRead(TimeSpan timeout)
    {
        if (!WaitForKey(timeout))
        {
            return null;
        }

        return ReadAvailable();
    }

    private static TerminalInput ReadAvailable()
    {
        var first = Console.ReadKey(intercept: true);
        if (first.Key == ConsoleKey.Escape && first.KeyChar == '\x1b')
        {
            var sequence = ReadEscapeSequence();
            if (sequence.Length > 1 && TryParseMouse(sequence, out var mouse))
            {
                return TerminalInput.FromMouse(mouse);
            }
        }

        return TerminalInput.FromKey(first);
    }

    private static bool WaitForKey(TimeSpan timeout)
    {
        var sleepMs = 25;
        var waitedMs = 0;
        var timeoutMs = Math.Max(1, (int)timeout.TotalMilliseconds);
        while (waitedMs < timeoutMs)
        {
            if (Console.KeyAvailable)
            {
                return true;
            }

            Thread.Sleep(sleepMs);
            waitedMs += sleepMs;
        }

        return Console.KeyAvailable;
    }

    private static string ReadEscapeSequence()
    {
        var sequence = new StringBuilder("\x1b");
        for (var wait = 0; wait < 8; wait++)
        {
            Thread.Sleep(1);
            while (Console.KeyAvailable)
            {
                var next = Console.ReadKey(intercept: true);
                sequence.Append(next.KeyChar);
                if (next.KeyChar is 'M' or 'm' or '~')
                {
                    return sequence.ToString();
                }
            }
        }

        return sequence.ToString();
    }

    private static bool TryParseMouse(string sequence, out TerminalMouseEvent mouse)
    {
        mouse = new TerminalMouseEvent(0, 0, MouseButton.Other, Pressed: false);
        if (!sequence.StartsWith("\x1b[<", StringComparison.Ordinal) || sequence.Length < 7)
        {
            return false;
        }

        var final = sequence[^1];
        if (final is not ('M' or 'm'))
        {
            return false;
        }

        var parts = sequence[3..^1].Split(';');
        if (parts.Length != 3 ||
            !int.TryParse(parts[0], out var code) ||
            !int.TryParse(parts[1], out var x) ||
            !int.TryParse(parts[2], out var y))
        {
            return false;
        }

        var button = DecodeButton(code);
        mouse = new TerminalMouseEvent(Math.Max(0, x - 1), Math.Max(0, y - 1), button, final == 'M');
        return true;
    }

    private static MouseButton DecodeButton(int code)
    {
        if ((code & 64) == 64)
        {
            return (code & 1) == 0 ? MouseButton.WheelUp : MouseButton.WheelDown;
        }

        return (code & 3) switch
        {
            0 => MouseButton.Left,
            1 => MouseButton.Middle,
            2 => MouseButton.Right,
            _ => MouseButton.Other
        };
    }
}

internal sealed record HitTarget(int X, int Y, int Width, int Height, HitAction Action, int Index = -1)
{
    public bool Contains(int x, int y) =>
        x >= X && x < X + Width && y >= Y && y < Y + Height;
}

internal enum HitAction
{
    MenuItem,
    Select,
    Back,
    Apply,
    Cancel
}
