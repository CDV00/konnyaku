using Konnyaku.Scheduling.Domain;

namespace Konnyaku.UnitTests;

public class SchedulePlannerTests
{
    [Fact]
    public void Plans_weekly_sessions_in_the_class_time_zone_and_skips_holidays()
    {
        var holiday = new DateOnly(2027, 1, 7);
        var slots = SchedulePlanner.Plan(new DateOnly(2027, 1, 4), [2, 4], new TimeOnly(19, 0), 90, "Asia/Ho_Chi_Minh", 4,
            new HashSet<DateOnly> { holiday });

        slots.Select(s => s.LocalDate).ShouldBe(
            [new DateOnly(2027, 1, 5), new DateOnly(2027, 1, 12), new DateOnly(2027, 1, 14), new DateOnly(2027, 1, 19)]);
        slots[0].StartsAt.ShouldBe(new DateTimeOffset(2027, 1, 5, 12, 0, 0, TimeSpan.Zero));
        slots[0].EndsAt.ShouldBe(new DateTimeOffset(2027, 1, 5, 13, 30, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Local_start_time_stays_fixed_across_daylight_saving_change()
    {
        // UK clocks go forward on 28 March 2027: 19:00 London is 19:00 UTC before, 18:00 UTC after.
        var slots = SchedulePlanner.Plan(new DateOnly(2027, 3, 23), [2], new TimeOnly(19, 0), 60, "Europe/London", 2, new HashSet<DateOnly>());

        slots[0].StartsAt.ShouldBe(new DateTimeOffset(2027, 3, 23, 19, 0, 0, TimeSpan.Zero));
        slots[1].StartsAt.ShouldBe(new DateTimeOffset(2027, 3, 30, 18, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Wall_clock_time_skipped_by_dst_moves_after_the_jump()
    {
        var slot = SchedulePlanner.Slot(new DateOnly(2027, 3, 28), new TimeOnly(1, 30), 60, "Europe/London");
        slot.StartsAt.ShouldBe(new DateTimeOffset(2027, 3, 28, 1, 30, 0, TimeSpan.Zero));
    }
}
