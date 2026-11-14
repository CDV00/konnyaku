using Konnyaku.Learning.Domain;

namespace Konnyaku.UnitTests;

public class AttendancePolicyTests
{
    private static readonly DateTimeOffset Start = new(2027, 1, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = Start.AddMinutes(90);

    [Theory]
    [InlineData(0, 90, AttendanceStatus.Present)]
    [InlineData(5, 90, AttendanceStatus.Present)]
    [InlineData(15, 90, AttendanceStatus.Late)]
    [InlineData(0, 45, AttendanceStatus.LeftEarly)]
    [InlineData(0, 20, AttendanceStatus.Absent)]
    public void Classifies_presence_from_the_meeting_log(int joinedMinute, int presentMinutes, AttendanceStatus expected) =>
        AttendancePolicy.Classify(Start, End, Start.AddMinutes(joinedMinute), presentMinutes * 60).ShouldBe(expected);

    [Fact]
    public void Never_joined_is_absent() => AttendancePolicy.Classify(Start, End, null, 0).ShouldBe(AttendanceStatus.Absent);

    [Theory]
    [InlineData(AttendanceStatus.Present, true)]
    [InlineData(AttendanceStatus.Absent, true)]
    [InlineData(AttendanceStatus.ExcusedAbsent, false)]
    public void Only_excused_absence_keeps_the_lesson(AttendanceStatus status, bool billable) =>
        AttendancePolicy.IsBillable(status).ShouldBe(billable);
}
