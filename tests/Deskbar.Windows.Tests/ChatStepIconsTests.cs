using Deskbar.App;
using Deskbar.Core.Tasks;

namespace Deskbar.Windows.Tests;

// The icons of a session's steps (ChatStepIcons, InboxMarks). The owner is colourblind: a kind is told by its shape.
public class ChatStepIconsTests
{
    [Fact]
    public void Every_step_kind_has_its_own_shape() => StaThread.Run(() =>
    {
        var shapes = Enum.GetValues<StepKind>().Select(kind => InboxMarks.ShapeOfIcon(ChatStepIcons.IconOf(kind))).ToArray();

        Assert.Equal(shapes.Length, shapes.Distinct().Count());
    });

    [Fact]
    public void A_failed_step_a_finished_step_and_the_fold_chevrons_are_shapes_of_their_own() => StaThread.Run(() =>
    {
        var names = new[] { "fail", "tick", "blank", "chevR", "chevD", "image" }.Concat(Enum.GetValues<StepKind>().Select(ChatStepIcons.IconOf)).ToArray();
        var shapes = names.Select(InboxMarks.ShapeOfIcon).ToArray();

        Assert.Equal(names.Distinct().Count(), shapes.Distinct().Count());
    });

    [Fact]
    public void A_row_reads_as_a_verb_and_the_label() =>
        Assert.Equal("Ran dotnet test", ChatStepIcons.TextOf(new ChatStep("s", StepKind.Run, "dotnet test", false, true, CSharpFunctionalExtensions.Maybe<DateTimeOffset>.None)));
}
