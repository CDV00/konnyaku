using Konnyaku.Learning.Domain;
using Konnyaku.Scheduling.Domain;

namespace Konnyaku.UnitTests;

public class SessionSeatTests
{
    [Theory]
    [InlineData("Online", null, 12, 0)]
    [InlineData("Offline", null, 0, 12)]
    [InlineData("Offline", 8, 0, 8)]
    [InlineData("Hybrid", 8, 12, 8)]
    [InlineData("Hybrid", 20, 12, 12)]
    public void Seats_are_the_class_size_plus_makeups_offline_capped_by_the_room(string mode, int? room, int online, int offline) =>
        SessionSeat.CapacityFor(mode, 10, 2, room).ShouldBe((online, offline));

    [Fact]
    public void Free_seats_never_go_below_zero()
    {
        var seat = new SessionSeat(Guid.NewGuid(), 2, 0, 1);
        seat.Free(SeatMode.Online).ShouldBe(2);
        seat.Free(SeatMode.Offline).ShouldBe(0);
        seat.Resize(1, 0, 0);
        seat.FreeMakeup.ShouldBe(0);
    }

    [Fact]
    public void Only_a_regular_booked_seat_can_be_postponed()
    {
        var now = DateTimeOffset.UtcNow;
        var regular = new SessionBooking(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), SeatMode.Online, BookingKind.Regular, null, now);
        regular.Postpone(now).IsSuccess.ShouldBeTrue();
        regular.Status.ShouldBe(BookingStatus.Postponed);
        regular.Postpone(now).Error!.Code.ShouldBe("scheduling.booking_not_postponable");

        var makeup = new SessionBooking(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), SeatMode.Online, BookingKind.Makeup, null, now);
        makeup.Postpone(now).IsSuccess.ShouldBeFalse();
    }

    [Theory]
    [InlineData("Makeup", AttendanceStatus.Present, AttendanceStatus.Makeup)]
    [InlineData("Makeup", AttendanceStatus.Late, AttendanceStatus.Makeup)]
    [InlineData("Makeup", AttendanceStatus.Absent, AttendanceStatus.Absent)]
    [InlineData("Regular", AttendanceStatus.Present, AttendanceStatus.Present)]
    public void A_makeup_learner_who_came_is_recorded_as_makeup(string kind, AttendanceStatus status, AttendanceStatus expected) =>
        AttendancePolicy.ForBooking(kind, status).ShouldBe(expected);

    [Fact]
    public void An_entitlement_is_open_until_used_declined_or_expired()
    {
        var now = DateTimeOffset.UtcNow;
        var right = new MakeupEntitlement(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), now.AddDays(30), now);
        right.IsOpen(now).ShouldBeTrue();
        right.IsOpen(now.AddDays(31)).ShouldBeFalse();
        right.Use(Guid.NewGuid());
        right.EnsureOpen(now).Error!.Code.ShouldBe("scheduling.entitlement_used");
        right.Release();
        right.Decline(now);
        right.EnsureOpen(now).Error!.Code.ShouldBe("scheduling.entitlement_expired");
    }
}
