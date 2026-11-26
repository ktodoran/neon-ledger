using NeonLedger.Domain;
using NeonLedger.Ledger;

namespace NeonLedger.Tests;

public sealed class PostTransactionValidatorTests
{
    private static readonly Guid A = Guid.NewGuid();
    private static readonly Guid B = Guid.NewGuid();
    private static readonly Guid C = Guid.NewGuid();

    [Fact]
    public void A_balanced_transaction_has_no_errors()
    {
        var command = new PostTransaction(
            "Split",
            [new(A, Direction.Debit, 1_000), new(B, Direction.Credit, 600), new(C, Direction.Credit, 400)]);

        Assert.Empty(PostTransactionValidator.Validate(command));
    }

    [Fact]
    public void An_unbalanced_transaction_reports_both_totals()
    {
        var command = new PostTransaction("Off by one", [new(A, Direction.Debit, 1_000), new(B, Direction.Credit, 999)]);

        var errors = PostTransactionValidator.Validate(command);

        Assert.Equal(["Debits (1000) must equal credits (999)."], errors["entries"]);
    }

    [Fact]
    public void Two_debits_do_not_balance_each_other()
    {
        var command = new PostTransaction("Same side", [new(A, Direction.Debit, 500), new(B, Direction.Debit, 500)]);

        Assert.Contains("entries", PostTransactionValidator.Validate(command).Keys);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_description_is_required(string description)
    {
        var command = new PostTransaction(description, [new(A, Direction.Debit, 1), new(B, Direction.Credit, 1)]);

        Assert.Contains("description", PostTransactionValidator.Validate(command).Keys);
    }

    [Fact]
    public void A_description_longer_than_the_column_is_refused()
    {
        var command = new PostTransaction(
            new string('x', JournalTransaction.MaxDescriptionLength + 1),
            [new(A, Direction.Debit, 1), new(B, Direction.Credit, 1)]);

        Assert.Contains("description", PostTransactionValidator.Validate(command).Keys);
    }

    [Fact]
    public void Each_bad_entry_is_reported_by_its_position()
    {
        var command = new PostTransaction(
            "Bad entries",
            [new(Guid.Empty, Direction.Debit, 100), new(B, Direction.Credit, 0), new(C, Direction.Credit, LedgerService.MaxAmount + 1)]);

        var errors = PostTransactionValidator.Validate(command);

        Assert.Equal(["entries[0].accountId", "entries[1].amount", "entries[2].amount"], errors.Keys.Order());
    }

    [Fact]
    public void More_entries_than_the_limit_is_refused()
    {
        var entries = Enumerable.Range(0, LedgerService.MaxEntries + 2)
            .Select(i => new EntryLine(A, i % 2 == 0 ? Direction.Debit : Direction.Credit, 1))
            .ToList();

        Assert.Contains("entries", PostTransactionValidator.Validate(new PostTransaction("Too many", entries)).Keys);
    }
}
