using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DotnetPackageSkills.Cli;

internal static class ConsoleViewport
{
    public static (int Width, int Height) Size()
    {
        if (!OperatingSystem.IsWindows())
        {
            return (Console.WindowWidth, Console.WindowHeight);
        }

        var buffer = ReadBuffer();
        return (buffer.Window.Right - buffer.Window.Left + 1, buffer.Window.Bottom - buffer.Window.Top + 1);
    }

    public static void SetCursorPosition(int left, int top, TextWriter? output = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            if (output is null)
            {
                Console.SetCursorPosition(left, top);
            }
            else
            {
                output.Write($"\x1b[{top + 1};{left + 1}H");
            }

            return;
        }

        var buffer = ReadBuffer();
        var position = new Coordinate
        {
            X = (short)Math.Clamp(buffer.Window.Left + left, buffer.Window.Left, buffer.Window.Right),
            Y = (short)Math.Clamp(buffer.Window.Top + top, buffer.Window.Top, buffer.Window.Bottom),
        };
        if (!SetConsoleCursorPosition(GetStdHandle(-11), position))
        {
            throw ConsoleError("Could not position the interactive terminal cursor.");
        }
    }

    public static void Clear()
    {
        if (OperatingSystem.IsWindows())
        {
            ClearWindows();
        }
        else
        {
            Console.Write("\x1b[2J\x1b[H");
        }
    }

    [SupportedOSPlatform("windows")]
    public static int BeginStatusLine(TextWriter output)
    {
        var buffer = ReadBuffer(GetStdHandle(-12));
        if (buffer.Cursor.X != 0)
        {
            output.WriteLine();
            buffer = ReadBuffer(GetStdHandle(-12));
        }

        return buffer.Cursor.Y;
    }

    [SupportedOSPlatform("windows")]
    public static void WriteStatusLine(int top, string text)
    {
        if (!WriteConsoleOutputCharacter(GetStdHandle(-12), text, (uint)text.Length,
                new Coordinate { Y = (short)top }, out var written))
        {
            throw ConsoleError("Could not write the temporary terminal line.");
        }

        if (written != text.Length)
        {
            throw new IOException("Could not write the complete temporary terminal line.");
        }
    }

    [SupportedOSPlatform("windows")]
    public static void ClearStatusLine(int top, int cells)
    {
        var handle = GetStdHandle(-12);
        var buffer = ReadBuffer(handle);
        if (top < 0 || top >= buffer.Size.Y)
        {
            throw new IOException("The temporary terminal line is no longer in the console buffer.");
        }

        // Buffer cells can remain hidden beyond the viewport after a classic-console resize.
        var count = (uint)Math.Min(cells, buffer.Size.X * (buffer.Size.Y - top));
        if (!FillConsoleOutputCharacter(handle, ' ', count, new Coordinate { Y = (short)top }, out var written))
        {
            throw ConsoleError("Could not clear the temporary terminal line.");
        }

        if (written != count)
        {
            throw new IOException("Could not clear the complete temporary terminal line.");
        }
    }

    public static void BeginRelativeStatusLine(TextWriter output) => output.Write("\r\n\u001b7");

    public static void WriteRelativeStatusLine(TextWriter output, string text) =>
        output.Write($"\u001b8{text}\u001b8");

    public static void ClearRelativeStatusLine(TextWriter output, int cells, int width, int height)
    {
        output.Write("\u001b8");
        for (var row = 0; cells > 0 && row < height; row++)
        {
            if (row > 0)
            {
                output.Write("\x1b[1B\r");
            }

            var count = Math.Min(cells, Math.Max(1, width));
            output.Write($"\x1b[{count}X");
            cells -= count;
        }

        output.Write("\u001b8");
    }

    [SupportedOSPlatform("windows")]
    private static void ClearWindows()
    {
        var output = GetStdHandle(-11);
        var buffer = ReadBuffer();

        // Clear cells in place. Printing blank lines instead pushes stale picker frames
        // into scrollback, where the terminal can rewrap them independently after a resize.
        var width = (uint)(buffer.Window.Right - buffer.Window.Left + 1);
        for (var row = (int)buffer.Window.Top; row <= buffer.Window.Bottom; row++)
        {
            var position = new Coordinate { X = buffer.Window.Left, Y = (short)row };
            if (!FillConsoleOutputCharacter(output, ' ', width, position, out _) ||
                !FillConsoleOutputAttribute(output, buffer.Attributes, width, position, out _))
            {
                throw ConsoleError("Could not clear the interactive terminal viewport.");
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static ScreenBufferInfo ReadBuffer(nint? output = null)
    {
        if (!GetConsoleScreenBufferInfo(output ?? GetStdHandle(-11), out var buffer))
        {
            throw ConsoleError("Could not read the interactive terminal viewport.");
        }

        return buffer;
    }

    private static IOException ConsoleError(string message) =>
        new(message, new Win32Exception(Marshal.GetLastPInvokeError()));

    [StructLayout(LayoutKind.Sequential)]
    private struct Coordinate
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRectangle
    {
        public short Left;
        public short Top;
        public short Right;
        public short Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ScreenBufferInfo
    {
        public Coordinate Size;
        public Coordinate Cursor;
        public ushort Attributes;
        public WindowRectangle Window;
        public Coordinate MaximumWindowSize;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GetStdHandle(int handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetConsoleScreenBufferInfo(nint output, out ScreenBufferInfo info);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleCursorPosition(nint output, Coordinate position);

    [DllImport("kernel32.dll", EntryPoint = "FillConsoleOutputCharacterW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FillConsoleOutputCharacter(
        nint output, char character, uint length, Coordinate position, out uint written);

    [DllImport("kernel32.dll", EntryPoint = "WriteConsoleOutputCharacterW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteConsoleOutputCharacter(
        nint output, string text, uint length, Coordinate position, out uint written);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FillConsoleOutputAttribute(
        nint output, ushort attributes, uint length, Coordinate position, out uint written);
}
