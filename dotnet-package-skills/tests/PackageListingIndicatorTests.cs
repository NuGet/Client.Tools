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
    public void Frames_start_immediately_and_advance_every_second()
    {
        var terminal = new FakeTerminal();
        var clock = new ManualClock();
        using var indicator = new PackageListingIndicator(terminal, clock);

        Assert.Equal("| Reading package references...", terminal.Screen);
        Assert.Equal(TimeSpan.FromSeconds(1), clock.Timer.DueTime);
        Assert.Equal(TimeSpan.FromSeconds(1), clock.Timer.Period);
        foreach (var frame in "/-\\|")
        {
            clock.Timer.Tick();
            Assert.Equal($"{frame} Reading package references...", terminal.Screen);
        }

        Assert.All(terminal.Writes, write => Assert.All(write.Bytes, value => Assert.InRange(value, 32, 126)));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(20)]
    [InlineData(100)]
    public void Stable_viewports_update_only_the_spinner_without_clearing_or_rewriting_the_label(int width)
    {
        var terminal = new FakeTerminal(windowWidth: width);
        var clock = new ManualClock();
        using var indicator = new PackageListingIndicator(terminal, clock);
        var initialText = Assert.Single(terminal.Writes).Text;
        Assert.StartsWith("|", initialText);
        var clears = 0;
        terminal.BeforeOperation = operation =>
        {
            if (operation == nameof(ITerminal.ClearStatusLine)) { clears++; }
        };

        foreach (var frame in "/-\\|")
        {
            clock.Timer.Tick();
            Assert.Equal($"{frame}{initialText[1..]}", terminal.Screen);
        }

        Assert.Equal(0, clears);
        Assert.Equal([initialText, "/", "-", "\\", "|"], terminal.Writes.Select(write => write.Text));
        Assert.All(terminal.Writes.Skip(1), write =>
        {
            Assert.Equal(0, write.Left);
            Assert.Equal(0, write.Top);
        });
    }

    [Fact]
    public void A_width_resize_redraws_the_label_once_then_resumes_spinner_only_updates()
    {
        var terminal = new FakeTerminal();
        var clock = new ManualClock();
        using var indicator = new PackageListingIndicator(terminal, clock);

        terminal.ResizeNow(18, 10);
        clock.Timer.Tick();
        clock.Timer.Tick();
        terminal.ResizeNow(18, 100);
        clock.Timer.Tick();
        clock.Timer.Tick();

        Assert.Equal(
            ["| Reading package references...", "/ Read...", "-", "\\ Reading package references...", "|"],
            terminal.Writes.Select(write => write.Text));
        Assert.Equal("| Reading package references...", terminal.Screen.TrimEnd());
    }

    [Fact]
    public void A_height_only_resize_keeps_the_label_and_updates_only_the_spinner()
    {
        var terminal = new FakeTerminal();
        var clock = new ManualClock();
        using var indicator = new PackageListingIndicator(terminal, clock);

        terminal.ResizeNow(8, 100);
        clock.Timer.Tick();

        Assert.Equal(["| Reading package references...", "/"], terminal.Writes.Select(write => write.Text));
        Assert.Equal("/ Reading package references...", terminal.Screen);
    }

    [Fact]
    public void Shrinking_then_disposing_clears_hidden_buffer_cells_before_expanding()
    {
        var terminal = new FakeTerminal();
        terminal.WriteLine("Keep history.");
        var clock = new ManualClock();
        var indicator = new PackageListingIndicator(terminal, clock);
        terminal.SetCursorPosition(0, 2);
        terminal.Write("Keep following output.");
        terminal.SetCursorPosition(0, 1);

        terminal.ResizeNow(18, 10);
        indicator.Dispose();
        terminal.ResizeNow(18, 100);

        var lines = terminal.Screen.Split(Environment.NewLine);
        Assert.Equal("Keep history.", lines[0].TrimEnd());
        Assert.Empty(lines[1].Trim());
        Assert.Equal("Keep following output.", lines[2].TrimEnd());
    }

    [Fact]
    public void Starting_after_partial_output_allocates_a_fresh_status_line()
    {
        var terminal = new FakeTerminal();
        terminal.Write("Keep partial output.");
        var clock = new ManualClock();
        var indicator = new PackageListingIndicator(terminal, clock);

        Assert.StartsWith($"Keep partial output.{Environment.NewLine}| Reading", terminal.Screen);
        clock.Timer.Tick();
        indicator.Dispose();

        Assert.Equal("Keep partial output.", terminal.Screen.TrimEnd());
        Assert.Equal(1, terminal.CursorTop);
    }

    [Fact]
    public void Echoed_input_moving_the_cursor_does_not_redirect_frames_or_cleanup()
    {
        var terminal = new FakeTerminal();
        terminal.WriteLine("Keep history.");
        var clock = new ManualClock();
        var indicator = new PackageListingIndicator(terminal, clock);
        terminal.SetCursorPosition(0, 5);
        terminal.Write("Keep echoed input.");

        clock.Timer.Tick();
        indicator.Dispose();

        var lines = terminal.Screen.Split(Environment.NewLine);
        Assert.Equal("Keep history.", lines[0].TrimEnd());
        Assert.Empty(lines[1].Trim());
        Assert.Equal("Keep echoed input.", lines[5].TrimEnd());
    }

    [Fact]
    public void A_cursor_clamped_to_a_scrolled_history_row_does_not_move_the_status()
    {
        var terminal = new FakeTerminal();
        terminal.WriteLine("Keep history.");
        var clock = new ManualClock();
        var indicator = new PackageListingIndicator(terminal, clock);
        terminal.CursorTopOverride = 0;

        clock.Timer.Tick();
        indicator.Dispose();

        Assert.Equal("Keep history.", terminal.Screen.TrimEnd());
    }

    [Fact]
    public void Rendering_and_cleanup_do_not_query_the_cursor_position()
    {
        var terminal = new FakeTerminal();
        terminal.BeforeOperation = operation =>
        {
            if (operation == nameof(ITerminal.CursorTop))
            {
                throw new InvalidOperationException("Cursor queries may consume terminal input.");
            }
        };
        var clock = new ManualClock();
        var indicator = new PackageListingIndicator(terminal, clock);

        Assert.Equal("| Reading package references...", terminal.Screen);
        clock.Timer.Tick();
        indicator.Dispose();

        Assert.Empty(terminal.Screen.Trim());
    }

    [Fact]
    public void Relative_status_drawing_reserves_a_line_and_uses_no_cursor_position_queries()
    {
        using var output = new StringWriter();

        ConsoleViewport.BeginRelativeStatusLine(output);
        ConsoleViewport.WriteRelativeStatusLine(output, "| Reading package references...");
        ConsoleViewport.WriteRelativeStatusLine(output, "/");

        Assert.Equal(
            "\r\n\u001b7\u001b8| Reading package references...\u001b8\u001b8/\u001b8",
            output.ToString());
    }

    [Theory]
    [InlineData(100, 24, "\u001b8\x1b[31X\u001b8")]
    [InlineData(10, 24, "\u001b8\x1b[10X\x1b[1B\r\x1b[10X\x1b[1B\r\x1b[10X\x1b[1B\r\x1b[1X\u001b8")]
    [InlineData(10, 2, "\u001b8\x1b[10X\x1b[1B\r\x1b[10X\u001b8")]
    public void Relative_cleanup_erases_only_owned_cells_and_restores_the_parked_origin(
        int width, int height, string expected)
    {
        using var output = new StringWriter();

        ConsoleViewport.ClearRelativeStatusLine(output, 31, width, height);

        Assert.Equal(expected, output.ToString());
    }

    [Theory]
    [InlineData(nameof(ITerminal.BeginStatusLine))]
    [InlineData(nameof(ITerminal.WriteStatusLine))]
    public void Startup_failures_warn_once_without_starting_a_timer(string failingOperation)
    {
        var terminal = new FakeTerminal
        {
            BeforeOperation = operation =>
            {
                if (operation == failingOperation) { throw new IOException("Terminal unavailable."); }
            },
        };
        var clock = new ManualClock();
        using var error = new StringWriter();
        var indicator = new PackageListingIndicator(terminal, clock, error: error);

        indicator.Dispose();
        indicator.Dispose();

        Assert.Null(clock.Timer);
        Assert.Empty(terminal.Screen.Trim());
        Assert.Equal(
            $"Warning: could not display package-reference progress: Terminal unavailable.{Environment.NewLine}",
            error.ToString());
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
        terminal.ResizeBufferNow(10);
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
            terminal.ResizeBufferNow(100);
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
    public void Timer_failures_warn_once_on_disposal_without_aborting_the_lookup()
    {
        var terminal = new FakeTerminal();
        var clock = new ManualClock();
        using var error = new StringWriter();
        var indicator = new PackageListingIndicator(terminal, clock, error: error);
        var failure = new IOException("Terminal write failed.");
        terminal.BeforeOperation = operation =>
        {
            if (operation == nameof(ITerminal.Write)) { throw failure; }
        };

        clock.Timer.Tick();
        indicator.Dispose();
        Assert.True(clock.Timer.IsDisposed);
        Assert.Empty(terminal.Screen.Trim());
        clock.Timer.Tick();
        indicator.Dispose();

        Assert.Equal(
            $"Warning: could not display package-reference progress: {failure.Message}{Environment.NewLine}",
            error.ToString());
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
