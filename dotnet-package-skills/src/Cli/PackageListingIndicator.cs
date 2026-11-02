using System.Diagnostics;

namespace DotnetPackageSkills.Cli;

internal sealed class PackageListingIndicator : IDisposable
{
    private const string Frames = "|/-\\";
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(250);
    private readonly ITerminal _terminal;
    private readonly TextWriter _error;
    private readonly ITimer? _timer;
    private readonly object _gate = new();
    private readonly ConsoleCancelEventHandler? _cancelHandler;
    private Exception? _renderFailure;
    private bool _stopped;
    private bool _failureReported;
    private int _frame;
    private int _renderedCells;
    private int _renderedWidth;

    internal PackageListingIndicator(
        ITerminal terminal, TimeProvider timeProvider, bool handleControlC = false, TextWriter? error = null)
    {
        _terminal = terminal;
        _error = error ?? Console.Error;
        try
        {
            _terminal.BeginStatusLine();
            DrawFrame();
            _timer = timeProvider.CreateTimer(Tick, null, Interval, Interval);
            if (handleControlC)
            {
                _cancelHandler = (_, _) => Stop();
                Console.CancelKeyPress += _cancelHandler;
            }
        }
        catch (Exception failure) when (IsTerminalFailure(failure))
        {
            _renderFailure = failure;
            Stop();
        }
    }

    internal static IDisposable? Start(TextWriter error)
    {
        try
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

            return new PackageListingIndicator(
                new SystemTerminal(error), TimeProvider.System, handleControlC: true, error: error);
        }
        catch (Exception failure) when (IsTerminalFailure(failure))
        {
            WriteWarning(error, failure);
            return null;
        }
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
            catch (Exception failure) when (IsTerminalFailure(failure))
            {
                _renderFailure = failure;
            }
        }
    }

    private void DrawFrame()
    {
        var (width, height) = _terminal.GetWindowSize();
        if (width != _renderedWidth)
        {
            ClearFrame();
        }

        if (width <= 1 || height <= 0)
        {
            return;
        }

        if (_renderedCells == 0)
        {
            var text = Frames[_frame] + TerminalText.Clip(" Reading package references...", width - 2);
            _renderedCells = text.Length;
            _terminal.WriteStatusLine(text);
            _renderedWidth = width;
        }
        else
        {
            _terminal.WriteStatusLine(Frames[_frame].ToString());
        }

        _frame = (_frame + 1) % Frames.Length;
    }

    private void ClearFrame()
    {
        if (_renderedCells == 0)
        {
            return;
        }

        _terminal.ClearStatusLine(_renderedCells);
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
            _timer?.Dispose();
            if (_cancelHandler is not null)
            {
                Console.CancelKeyPress -= _cancelHandler;
            }

            try
            {
                ClearFrame();
            }
            catch (Exception failure) when (IsTerminalFailure(failure))
            {
                _renderFailure ??= failure;
            }
        }
    }

    public void Dispose()
    {
        Stop();
        _timer?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        lock (_gate)
        {
            if (_renderFailure is not null && !_failureReported)
            {
                _failureReported = true;
                WriteWarning(_error, _renderFailure);
            }
        }
    }

    private static bool IsTerminalFailure(Exception failure) =>
        failure is IOException or PlatformNotSupportedException or InvalidOperationException or ArgumentOutOfRangeException;

    private static void WriteWarning(TextWriter error, Exception failure)
    {
        var message = $"Warning: could not display package-reference progress: {TerminalText.Sanitize(failure.Message)}";
        try
        {
            error.WriteLine(message);
        }
        catch (Exception diagnosticFailure) when (IsTerminalFailure(diagnosticFailure))
        {
            Trace.TraceWarning("{0} (stderr unavailable: {1})", message, diagnosticFailure.Message);
        }
    }
}
