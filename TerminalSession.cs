using System.Text;

namespace Tractus.Ndi.ConfigTui;

internal sealed class TerminalSession : IDisposable
{
    private readonly Encoding _previousOutputEncoding;
    private bool _disposed;

    public TerminalSession(string title)
    {
        _previousOutputEncoding = Console.OutputEncoding;
        Console.OutputEncoding = Encoding.UTF8;
        Console.CursorVisible = false;
        Write("\x1b[?1049h"); // alternate screen
        Write("\x1b[?25l");
        Write("\x1b[?7l");
        Write("\x1b[?1000h\x1b[?1002h\x1b[?1006h"); // click, drag, SGR mouse
        SetTitle(title);
        Write("\x1b[2J\x1b[H");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Write("\x1b[0m");
        Write("\x1b[?1006l\x1b[?1002l\x1b[?1000l");
        Write("\x1b[?7h\x1b[?25h\x1b[?1049l");
        Console.OutputEncoding = _previousOutputEncoding;
        Console.CursorVisible = true;
    }

    private static void SetTitle(string title) =>
        Write($"\x1b]0;{title}\x07");

    private static void Write(string value) =>
        Console.Out.Write(value);
}
