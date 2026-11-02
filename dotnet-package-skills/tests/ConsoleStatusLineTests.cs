using DotnetPackageSkills.Cli;

namespace DotnetPackageSkills.Tests;

public class ConsoleStatusLineTests
{
    private const string Label = "| Reading package references...";

    [Theory]
    [InlineData(30, true)]
    [InlineData(30, false)]
    [InlineData(10, true)]
    [InlineData(10, false)]
    [InlineData(1, true)]
    [InlineData(1, false)]
    public void Reflowed_history_moves_the_origin_and_retains_only_the_remaining_owned_cells(int width, bool wraps)
    {
        var history = Rows("Keep-history-" + new string('h', 70) + "-end", width);
        var status = wraps ? Rows(Label, width) : Label[..Math.Min(width, Label.Length)].PadRight(width);
        var buffer = history + status + Rows("Keep following output.", width);

        var origin = ConsoleViewport.FindStatusLine(buffer, width, Label);

        Assert.Equal(history.Length, origin.Offset);
        Assert.Equal(wraps ? Label.Length : Math.Min(width, Label.Length), origin.Cells);
        Assert.True(origin.Offset + origin.Cells <= history.Length + status.Length);
    }

    [Fact]
    public void Reflow_can_move_the_status_to_a_nonzero_column()
    {
        var buffer = ("Keep wrapped history. " + Label).PadRight(100);

        Assert.Equal(("Keep wrapped history. ".Length, Label.Length),
            ConsoleViewport.FindStatusLine(buffer, 100, Label));
    }

    [Theory]
    [InlineData(100)]
    [InlineData(10)]
    public void Ambiguous_status_text_is_not_mistaken_for_owned_output(int width)
    {
        var buffer = Rows(Label, width) + Rows("Keep other history.", width) + Rows(Label, width);

        Assert.Throws<IOException>(() => ConsoleViewport.FindStatusLine(buffer, width, Label));
    }

    [Fact]
    public void A_full_history_match_and_a_truncated_status_match_are_ambiguous()
    {
        var buffer = Rows(Label, 10) + Label[..10];

        Assert.Throws<IOException>(() => ConsoleViewport.FindStatusLine(buffer, 10, Label));
    }

    [Fact]
    public void Missing_status_text_is_not_replaced_at_an_old_coordinate()
    {
        var buffer = Rows("Keep history.", 30) + Rows("Keep echoed input.", 30);

        Assert.Throws<IOException>(() => ConsoleViewport.FindStatusLine(buffer, 30, Label));
    }

    private static string Rows(string text, int width) =>
        string.Concat(text.Chunk(width).Select(row => new string(row).PadRight(width)));
}
