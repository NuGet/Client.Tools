using DotnetPackageSkills.Cli;

namespace DotnetPackageSkills.Tests;

public class PackageListingIndicatorTests
{
    [Theory]
    [InlineData(false, false, false, false, null, true, true)]
    [InlineData(false, false, false, false, "xterm-256color", false, true)]
    [InlineData(true, false, false, false, "xterm", true, false)]
    [InlineData(false, true, false, false, "xterm", true, false)]
    [InlineData(false, false, true, false, "xterm", true, false)]
    [InlineData(false, false, false, true, "xterm", true, false)]
    [InlineData(false, false, false, false, "dumb", true, false)]
    [InlineData(false, false, false, false, "UNKNOWN", true, false)]
    [InlineData(false, false, false, false, "vt100", true, false)]
    [InlineData(false, false, false, false, null, false, false)]
    [InlineData(false, false, false, false, "unsupported", false, false)]
    public void Animation_requires_interactive_streams_and_a_capable_non_CI_terminal(
        bool inputRedirected, bool outputRedirected, bool errorRedirected, bool ci,
        string? term, bool windows, bool expected)
    {
        Assert.Equal(expected, PackageListingIndicator.CanAnimate(
            inputRedirected, outputRedirected, errorRedirected, ci, term, windows));
    }

    [Fact]
    public void Frames_start_immediately_and_advance_every_100_milliseconds()
    {
        var terminal = new FakeTerminal();
        var clock = new ManualClock();
        using var indicator = new PackageListingIndicator(terminal, clock);

        Assert.Equal("| Reading package references...", terminal.Screen);
        Assert.Equal(TimeSpan.FromMilliseconds(100), clock.Timer.DueTime);
        Assert.Equal(TimeSpan.FromMilliseconds(100), clock.Timer.Period);
        foreach (var frame in "/-\\|")
        {
            clock.Timer.Tick();
            Assert.Equal($"{frame} Reading package references...", terminal.Screen);
        }

        Assert.All(terminal.Writes, write => Assert.All(write.Bytes, value => Assert.InRange(value, 32, 126)));
    }

    [Fact]
    public void Disposal_clears_only_the_status_and_prevents_queued_or_late_writes()
    {
        var terminal = new FakeTerminal();
        terminal.WriteLine("Keep existing output.");
        var state = terminal.CaptureState();
        var clock = new ManualClock();
        var indicator = new PackageListingIndicator(terminal, clock);
        clock.Timer.Tick();

        indicator.Dispose();
        var writes = terminal.Writes.Count;
        clock.Timer.Tick();
        indicator.Dispose();

        Assert.True(clock.Timer.IsDisposed);
        Assert.Equal(writes, terminal.Writes.Count);
        Assert.Equal("Keep existing output.", terminal.Screen.TrimEnd());
        Assert.Equal(1, terminal.CursorTop);
        Assert.Equal(state, terminal.CaptureState());
        Assert.Equal(0, terminal.ViewportClears);
        Assert.Equal(0, terminal.ScreenEntries);
        Assert.Empty(terminal.StyleEvents);
        Assert.Empty(terminal.EncodingChanges);
        Assert.Empty(terminal.KeysRead);
        Assert.Empty(terminal.InputTimeouts);
        Assert.False(terminal.ControlCWasEverTakenAsInput);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(8)]
    [InlineData(20)]
    public void Frames_fit_narrow_windows_without_wrapping(int width)
    {
        var terminal = new FakeTerminal(windowWidth: width);
        var clock = new ManualClock();
        var indicator = new PackageListingIndicator(terminal, clock);
        clock.Timer.Tick();

        Assert.All(terminal.Writes, write => Assert.True(write.Text.Length < width));
        Assert.Equal(0, terminal.CursorTop);
        indicator.Dispose();
        Assert.Equal(string.Empty, terminal.Screen.Trim());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_resize_clears_reflowed_rows_before_redrawing_or_finishing(bool redraw)
    {
        var terminal = new FakeTerminal(windowHeight: 8);
        terminal.WriteLine("History");
        var clock = new ManualClock();
        var indicator = new PackageListingIndicator(terminal, clock);
        terminal.ResizeNow(8, 10);
        // Model the extra rows a real terminal may create when it reflows the old frame.
        terminal.SetCursorPosition(0, 2);
        terminal.Write("package");
        terminal.SetCursorPosition(0, 3);
        terminal.Write("refs...");
        terminal.SetCursorPosition(0, 5);
        terminal.Write("Untouched");
        terminal.SetCursorPosition(0, 1);
        if (redraw)
        {
            clock.Timer.Tick();
            Assert.Contains("/ Read...", terminal.Screen);
            terminal.ResizeNow(8, 100);
            clock.Timer.Tick();
        }

        indicator.Dispose();

        var lines = terminal.Screen.Split(Environment.NewLine);
        Assert.Equal("History", lines[0].TrimEnd());
        Assert.All(lines.Skip(1).Take(4), line => Assert.Empty(line.Trim()));
        Assert.Equal("Untouched", lines[5].TrimEnd());
        Assert.Equal(1, terminal.CursorTop);
        Assert.Equal(0, terminal.ViewportClears);
    }

    [Fact]
    public void Repeated_lookups_start_with_a_fresh_frame_and_leave_no_status()
    {
        var terminal = new FakeTerminal();
        var clock = new ManualClock();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using (new PackageListingIndicator(terminal, clock))
            {
                Assert.Equal("| Reading package references...", terminal.Screen.TrimEnd());
                clock.Timer.Tick();
            }

            Assert.Equal(string.Empty, terminal.Screen.Trim());
            Assert.True(clock.Timer.IsDisposed);
        }
    }

    [Fact]
    public void Timer_failures_are_reported_on_disposal_and_do_not_escape_on_a_background_thread()
    {
        var terminal = new FakeTerminal();
        var clock = new ManualClock();
        var indicator = new PackageListingIndicator(terminal, clock);
        var failure = new IOException("Terminal write failed.");
        terminal.BeforeOperation = operation =>
        {
            if (operation == nameof(ITerminal.Write)) { throw failure; }
        };

        clock.Timer.Tick();
        Assert.Same(failure, Assert.Throws<IOException>(indicator.Dispose));
        Assert.True(clock.Timer.IsDisposed);
        Assert.Empty(terminal.Screen.Trim());
        clock.Timer.Tick();
    }

    [Fact]
    public async Task Disposal_waits_for_an_in_flight_frame_before_clearing()
    {
        var terminal = new FakeTerminal();
        var clock = new ManualClock();
        using var indicator = new PackageListingIndicator(terminal, clock);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        terminal.BeforeOperation = operation =>
        {
            if (operation == nameof(ITerminal.Write))
            {
                entered.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            }
        };
        var rendering = Task.Run(clock.Timer.Tick);
        Task? disposing = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            disposing = Task.Run(indicator.Dispose);
        }
        finally
        {
            release.Set();
            await rendering.WaitAsync(TimeSpan.FromSeconds(10));
            if (disposing is not null) { await disposing.WaitAsync(TimeSpan.FromSeconds(10)); }
        }

        var writes = terminal.Writes.Count;
        clock.Timer.Tick();
        Assert.Equal(writes, terminal.Writes.Count);
        Assert.Empty(terminal.Screen.Trim());
    }

    [Fact]
    public void A_terminal_can_write_to_the_supplied_error_writer_instead_of_stdout()
    {
        using var error = new StringWriter();
        var terminal = new SystemTerminal(error);

        terminal.Write("| Reading package references...");
        terminal.WriteLine();

        Assert.Equal($"| Reading package references...{Environment.NewLine}", error.ToString());
    }

    private sealed class ManualClock : TimeProvider
    {
        public ManualTimer Timer { get; private set; } = null!;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Timer = new ManualTimer(callback, state, dueTime, period);
            return Timer;
        }
    }

    private sealed class ManualTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) : ITimer
    {
        public TimeSpan DueTime { get; private set; } = dueTime;
        public TimeSpan Period { get; private set; } = period;
        public bool IsDisposed { get; private set; }

        public void Tick() => callback(state);

        public bool Change(TimeSpan newDueTime, TimeSpan newPeriod)
        {
            DueTime = newDueTime;
            Period = newPeriod;
            return !IsDisposed;
        }

        public void Dispose() => IsDisposed = true;

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
