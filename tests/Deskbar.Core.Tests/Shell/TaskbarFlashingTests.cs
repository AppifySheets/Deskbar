using Deskbar.Core.Shell;

namespace Deskbar.Core.Tests.Shell;

// Deskbar holding Windows' "Show flashing on taskbar apps" off while it runs, and putting it back when it stops
// (docs/superpowers/specs/2026-11-18-taskbar-flashing-switch-design.md). Every rule is decided by the pure plan
// below; the App layer (TaskbarFlashingSwitch) only gathers the three facts and does what the step says.
//
// Facts read (Wanted, Remembered, FlashingOn), a step reads (Write, Remember, Forget).
public class TaskbarFlashingTests
{
    static TaskbarFlashingStep Plan(bool wanted, bool? remembered, bool flashingOn) =>
        TaskbarFlashing.Plan(new(wanted, remembered, flashingOn));

    [Fact]
    public void Taking_hold_remembers_that_flashing_was_on_and_turns_it_off() =>
        Assert.Equal(new(Write: false, Remember: true, Forget: false), Plan(true, null, true));

    // Someone who had flashing off before Deskbar: nothing to write, but "off" is remembered so that letting
    // go later does not turn it ON for them.
    [Fact]
    public void Taking_hold_of_a_switch_already_off_only_remembers_off() =>
        Assert.Equal(new(Write: null, Remember: false, Forget: false), Plan(true, null, false));

    // After a crash the marker is still there and the registry still says off. The remembered value is the
    // original and must survive: remembering again would record the 0 Deskbar wrote itself.
    [Fact]
    public void A_start_after_a_crash_keeps_what_was_remembered() =>
        Assert.Equal(new(Write: null, Remember: null, Forget: false), Plan(true, true, false));

    // Same, but flashing was switched back on in Settings while Deskbar was down.
    [Fact]
    public void A_start_after_a_crash_turns_it_off_again_without_remembering_anew() =>
        Assert.Equal(new(Write: false, Remember: null, Forget: false), Plan(true, true, true));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Not_wanted_and_nothing_remembered_touches_nothing(bool flashingOn) =>
        Assert.Equal(new(Write: null, Remember: null, Forget: false), Plan(false, null, flashingOn));

    [Fact]
    public void Letting_go_puts_flashing_back_on_and_forgets() =>
        Assert.Equal(new(Write: true, Remember: null, Forget: true), Plan(false, true, false));

    [Fact]
    public void Letting_go_leaves_it_off_for_someone_who_had_it_off() =>
        Assert.Equal(new(Write: null, Remember: null, Forget: true), Plan(false, false, false));

    // The registry already says what was remembered (turned back on by hand meanwhile): only forget.
    [Fact]
    public void Letting_go_writes_nothing_when_the_switch_is_already_as_remembered() =>
        Assert.Equal(new(Write: null, Remember: null, Forget: true), Plan(false, true, true));

    // The default, Petre 2026-11-19: "can you do a default for that if the taskbar is hidden?". With no choice
    // made it follows auto-hide; a choice made with Manage's box holds whatever auto-hide does.
    [Theory]
    [InlineData(null, true, true)]
    [InlineData(null, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    [InlineData(false, false, false)]
    public void With_no_choice_made_it_follows_auto_hide(bool? choice, bool autoHide, bool wanted) =>
        Assert.Equal(wanted, TaskbarFlashing.Wanted(choice, autoHide));

    // The suggestion under Manage's box: only while the box is unticked AND the taskbar is on auto-hide AND
    // Windows flashes, which is exactly when a flashing app holds the taskbar up.
    [Theory]
    [InlineData(false, true, true, true)]
    [InlineData(true, true, true, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, true, false, false)]
    public void The_note_shows_only_when_ticking_the_box_would_help(bool ticked, bool autoHide, bool flashingOn, bool shown) =>
        Assert.Equal(shown ? TaskbarFlashing.NoteText : "", TaskbarFlashing.Note(ticked, autoHide, flashingOn));
}
