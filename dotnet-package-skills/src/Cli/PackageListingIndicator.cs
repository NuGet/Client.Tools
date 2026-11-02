using System.Runtime.ExceptionServices;

namespace DotnetPackageSkills.Cli;

internal sealed class PackageListingIndicator : IDisposable
{
    private const string Frames = "|/-\\";
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(100);
    private readonly ITerminal _terminal;
    private readonly ITimer _timer;
    private readonly object _gate = new();
    private readonly ConsoleCancelEventHandler? _cancelHandler;
    private ExceptionDispatchInfo? _renderFailure;
    private bool _stopped;
    private int _frame;
    private int _renderedCells;

    internal PackageListingIndicator(ITerminal terminal, TimeProvider timeProvider, bool handleControlC = false)
    {
        _terminal = terminal;
        DrawFrame();
        _timer = timeProvider.CreateTimer(Tick, null, Interval, Interval);
        if (handleControlC)
        {
            _cancelHandler = (_, _) => Stop();
            Console.CancelKeyPress += _cancelHandler;
        }
    }

    internal static IDisposable? Start(TextWriter error)
    {
        if (!CanAnimate(
                Console.IsInputRedirected,
                Console.IsOutputRedirected,
                Console.IsErrorRedirected,
                IsEnabled("CI") || IsEnabled("TF_BUILD") || IsEnabled("GITHUB_ACTIONS"),
                Environment.GetEnvironmentVariable("TERM"),
                OperatingSystem.IsWindows()))
        {
            return null;
        }

        return new PackageListingIndicator(new SystemTerminal(error), TimeProvider.System, handleControlC: true);
    }

    internal static bool CanAnimate(
        bool inputRedirected, bool outputRedirected, bool errorRedirected, bool ci, string? term, bool windows) =>
        !ci && SystemTerminal.SupportsDynamicOutput(
            inputRedirected || outputRedirected || errorRedirected, term, windows);

    private static bool IsEnabled(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return !string.IsNullOrEmpty(value) && value != "0" &&
               !value.Equals("false", StringComparison.OrdinalIgnoreCase);
    }

    private void Tick(object? state)
    {
        lock (_gate)
        {
            if (_stopped || _renderFailure is not null)
            {
                return;
            }

            try
            {
                DrawFrame();
            }
            catch (Exception error)
            {
                // Propagate background rendering failures on the command thread, not the timer thread.
                _renderFailure = ExceptionDispatchInfo.Capture(error);
            }
        }
    }

    private void DrawFrame()
    {
        var (width, height) = _terminal.GetWindowSize();
        var top = _terminal.CursorTop;
        ClearFrame(width, height, top);
        if (width <= 1 || height <= 0)
        {
            return;
        }

        var text = TerminalText.Clip($"{Frames[_frame]} Reading package references...", width - 1);
        _terminal.SetCursorPosition(0, top);
        _terminal.Write(text);
        _renderedCells = text.Length;
        // Keep the cursor at the start so a resize preserves the beginning of the temporary line.
        _terminal.SetCursorPosition(0, top);
        _frame = (_frame + 1) % Frames.Length;
    }

    private void ClearFrame(int width, int height, int top)
    {
        if (_renderedCells == 0)
        {
            return;
        }

        // A narrower viewport can reflow the previous frame over more than one row.
        var rows = Math.Min(1 + (_renderedCells - 1) / Math.Max(1, width), height - top);
        for (var row = 0; row < rows; row++)
        {
            _terminal.SetCursorPosition(0, top + row);
            _terminal.ClearLine();
        }

        _terminal.SetCursorPosition(0, top);
        _renderedCells = 0;
    }

    private void Stop()
    {
        lock (_gate)
        {
            if (_stopped)
            {
                return;
            }

            _stopped = true;
            _timer.Dispose();
            if (_cancelHandler is not null)
            {
                Console.CancelKeyPress -= _cancelHandler;
            }

            try
            {
                var (width, height) = _terminal.GetWindowSize();
                ClearFrame(width, height, _terminal.CursorTop);
            }
            catch (Exception error)
            {
                _renderFailure ??= ExceptionDispatchInfo.Capture(error);
            }
        }
    }

    public void Dispose()
    {
        Stop();
        _timer.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _renderFailure?.Throw();
    }
}
