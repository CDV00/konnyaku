using Konnyaku.Billing.Application;
using Konnyaku.Billing.Domain;

namespace Konnyaku.UnitTests;

public class RefundRuleTests
{
    private static readonly DateTimeOffset Now = new(2027, 1, 10, 3, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0, "Không")]
    [InlineData(5, "Năm")]
    [InlineData(10, "Mười")]
    [InlineData(15, "Mười lăm")]
    [InlineData(21, "Hai mươi mốt")]
    [InlineData(24, "Hai mươi tư")]
    [InlineData(105, "Một trăm linh năm")]
    [InlineData(1_500_000, "Một triệu năm trăm nghìn")]
    [InlineData(1_005_000, "Một triệu không trăm linh năm nghìn")]
    [InlineData(2_400_000, "Hai triệu bốn trăm nghìn")]
    [InlineData(1_000_000_000, "Một tỷ")]
    [InlineData(1_000_000_000_000, "Một nghìn tỷ")]
    [InlineData(12_050_300_000, "Mười hai tỷ không trăm năm mươi triệu ba trăm nghìn")]
    public void Amounts_read_in_Vietnamese(long amount, string expected) =>
        VietnameseNumberWords.Amount(amount, "VND").ShouldBe($"{expected} đồng");

    [Fact]
    public void Cents_are_read_after_the_unit() =>
        VietnameseNumberWords.Amount(12.5m, "USD").ShouldBe("Mười hai đô la Mỹ năm mươi xu");

    [Theory]
    [InlineData("Nguyễn Văn An", "Nguyễn V** A**")]
    [InlineData("Admin", "Admin")]
    [InlineData(null, "—")]
    public void Names_are_masked_on_stamps(string? name, string expected) => Masking.Name(name).ShouldBe(expected);

    [Fact]
    public void Unused_paid_lessons_are_refunded_at_unit_value_and_bonus_is_not()
    {
        var (order, account) = PaidOrder(price: 2_400_000m, paid: 24, bonus: 2);
        for (var i = 0; i < 5; i++)
        {
            account.Consume(1, Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2027, 1, 5), Now);
        }

        var quote = RefundQuote.For(order, order.RefundKind!.Value, account);
        quote.Kind.ShouldBe(RefundKind.Withdrawal);
        (quote.PaidCreditsLeft, quote.BonusCreditsLeft, quote.UnusedValue, quote.SuggestedAmount).ShouldBe((19m, 2m, 1_900_000m, 1_900_000m));
    }

    [Fact]
    public void Nothing_used_refunds_the_whole_amount_despite_rounding()
    {
        // 1,000,000 / 3 = 333,333.33 per lesson: three of them would lose 0.01.
        var (order, account) = PaidOrder(price: 1_000_000m, paid: 3, bonus: 0);
        RefundQuote.For(order, order.RefundKind!.Value, account).SuggestedAmount.ShouldBe(1_000_000m);
    }

    [Fact]
    public void Goodwill_credits_and_owed_lessons_are_not_refunded()
    {
        var (order, account) = PaidOrder(price: 1_000_000m, paid: 10, bonus: 0);
        account.Adjust(5, CreditKind.Paid, "Tặng thêm", Now);
        RefundQuote.For(order, order.RefundKind!.Value, account).SuggestedAmount.ShouldBe(1_000_000m);

        for (var i = 0; i < 17; i++)
        {
            account.Consume(1, Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2027, 1, 5), Now);
        }

        account.PaidCredits.ShouldBe(-2m);
        RefundQuote.For(order, order.RefundKind!.Value, account).SuggestedAmount.ShouldBe(0m);
    }

    [Fact]
    public void A_refund_closes_the_account_with_reversing_lines_and_the_order_once()
    {
        var (order, account) = PaidOrder(price: 2_400_000m, paid: 24, bonus: 2);
        var lines = account.CloseForRefund(null, "Nghỉ", Now);
        lines.Select(l => (l.EntryType, l.CreditKind, l.Credits)).ShouldBe(
            [(LedgerEntryType.Refund, CreditKind.Paid, -24m), (LedgerEntryType.Refund, CreditKind.Bonus, -2m)]);
        (account.PaidCredits, account.BonusCredits).ShouldBe((0m, 0m));

        order.MarkRefunded(2_400_001m, Now).Error!.Code.ShouldBe("billing.refund_amount_invalid");
        order.MarkRefunded(2_400_000m, Now).IsSuccess.ShouldBeTrue();
        order.MarkRefunded(1m, Now).Error!.Code.ShouldBe("billing.order_already_refunded");
    }

    private static (Order Order, CreditBalance Account) PaidOrder(decimal price, decimal paid, decimal bonus)
    {
        var package = Package.Create("P", new Dictionary<string, string> { ["vi"] = "Gói" }, paid, bonus, price, "VND").Value;
        var order = new Order(Guid.NewGuid(), package, null, null, Now);
        order.ReceivePayment(price, Now).Value.ShouldBeTrue();
        return (order, CreditBalance.Open(order, Now).Account);
    }
}
