using NeonLedger.Domain;

namespace NeonLedger.Tests;

public sealed class AccountBalanceTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(Direction.Credit, Direction.Credit, 250)]
    [InlineData(Direction.Credit, Direction.Debit, -250)]
    [InlineData(Direction.Debit, Direction.Debit, 250)]
    [InlineData(Direction.Debit, Direction.Credit, -250)]
    public void An_entry_on_the_normal_side_raises_the_balance_and_the_other_side_lowers_it(
        Direction normalBalance,
        Direction entry,
        long expected)
    {
        var account = Account.Open("Test", "USD", normalBalance, allowNegative: true, Now);

        Assert.Equal(expected, account.Apply(entry, 250));
        Assert.Equal(expected, account.Balance);
    }

    [Fact]
    public void A_balance_that_would_overflow_throws_instead_of_wrapping()
    {
        var account = Account.Open("Test", "USD", Direction.Credit, allowNegative: true, Now);
        account.Apply(Direction.Credit, long.MaxValue);

        Assert.Throws<OverflowException>(() => account.Apply(Direction.Credit, 1));
    }

    [Fact]
    public void Timestamps_are_cut_to_the_precision_the_database_keeps()
    {
        var precise = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero).AddTicks(1_234_567);

        var account = Account.Open("Test", "USD", Direction.Credit, allowNegative: false, precise);

        Assert.Equal(precise.AddTicks(-7), account.CreatedAt);
    }
}
