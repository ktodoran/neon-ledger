using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using NeonLedger.Domain;
using NeonLedger.Events;
using NeonLedger.Idempotency;
using NeonLedger.Payments;

namespace NeonLedger.Data;

public sealed class LedgerDbContext(DbContextOptions<LedgerDbContext> options) : DbContext(options)
{
    public DbSet<Account> Accounts => Set<Account>();

    public DbSet<JournalTransaction> Transactions => Set<JournalTransaction>();

    public DbSet<Entry> Entries => Set<Entry>();

    public DbSet<IdempotencyRecord> IdempotencyKeys => Set<IdempotencyRecord>();

    public DbSet<EventRecord> Events => Set<EventRecord>();

    public DbSet<Payment> Payments => Set<Payment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Stored as 'debit' / 'credit' so the SQL in the migrations and the integrity check can read them.
        var direction = new ValueConverter<Direction, string>(
            value => value == Direction.Debit ? "debit" : "credit",
            value => value == "debit" ? Direction.Debit : Direction.Credit);

        modelBuilder.Entity<Account>(account =>
        {
            account.ToTable("accounts", table =>
            {
                table.HasCheckConstraint("ck_accounts_available_not_negative", "allow_negative OR balance - held >= 0");
                table.HasCheckConstraint("ck_accounts_held_not_negative", "held >= 0");
                table.HasCheckConstraint("ck_accounts_normal_balance", "normal_balance IN ('debit', 'credit')");
                table.HasCheckConstraint("ck_accounts_currency", "currency ~ '^[A-Z]{3}$'");
            });
            account.HasKey(a => a.Id);
            account.Property(a => a.Id).ValueGeneratedNever();
            account.Property(a => a.Name).HasMaxLength(Account.MaxNameLength);
            account.Property(a => a.Currency).HasColumnType("char(3)");
            account.Property(a => a.NormalBalance).HasConversion(direction).HasMaxLength(6);
        });

        modelBuilder.Entity<JournalTransaction>(transaction =>
        {
            transaction.ToTable("transactions");
            transaction.HasKey(t => t.Id);
            transaction.Property(t => t.Id).ValueGeneratedNever();
            transaction.Property(t => t.Description).HasMaxLength(JournalTransaction.MaxDescriptionLength);
            transaction.Property(t => t.Currency).HasColumnType("char(3)");

            // A transaction can be reversed at most once.
            transaction.HasIndex(t => t.ReversesTransactionId).IsUnique();
            transaction.HasOne<JournalTransaction>()
                .WithOne()
                .HasForeignKey<JournalTransaction>(t => t.ReversesTransactionId)
                .OnDelete(DeleteBehavior.Restrict);

            transaction.HasMany(t => t.Entries)
                .WithOne()
                .HasForeignKey(e => e.TransactionId)
                .OnDelete(DeleteBehavior.Restrict);
            transaction.Navigation(t => t.Entries).HasField("_entries");
        });

        modelBuilder.Entity<Entry>(entry =>
        {
            entry.ToTable("entries", table =>
            {
                table.HasCheckConstraint("ck_entries_amount_positive", "amount > 0");
                table.HasCheckConstraint("ck_entries_direction", "direction IN ('debit', 'credit')");
            });
            entry.HasKey(e => e.Id);
            entry.Property(e => e.Id).UseIdentityAlwaysColumn();
            entry.Property(e => e.Direction).HasConversion(direction).HasMaxLength(6);
            entry.HasOne<Account>()
                .WithMany()
                .HasForeignKey(e => e.AccountId)
                .OnDelete(DeleteBehavior.Restrict);

            // Serves "entries for this account, newest first".
            entry.HasIndex(e => new { e.AccountId, e.Id }).IsDescending(false, true);
        });

        modelBuilder.Entity<Payment>(payment =>
        {
            payment.ToTable("payments", table =>
            {
                table.HasCheckConstraint("ck_payments_amount_positive", "amount > 0");
                table.HasCheckConstraint("ck_payments_rail", "rail IN ('instant', 'ach')");
                table.HasCheckConstraint("ck_payments_status", "status IN ('pending', 'settled', 'canceled', 'returned')");
                table.HasCheckConstraint("ck_payments_distinct_accounts", "source_account_id <> destination_account_id");

                // A row can only say what its status allows it to say.
                table.HasCheckConstraint(
                    "ck_payments_settlement_matches_status",
                    "(settlement_transaction_id IS NOT NULL) = (status IN ('settled', 'returned'))");
                table.HasCheckConstraint(
                    "ck_payments_return_matches_status",
                    "(return_transaction_id IS NOT NULL) = (status = 'returned') AND (return_reason IS NOT NULL) = (status = 'returned')");
                table.HasCheckConstraint("ck_payments_instant_is_final", "rail <> 'instant' OR status = 'settled'");
            });
            payment.HasKey(p => p.Id);
            payment.Property(p => p.Id).ValueGeneratedNever();
            payment.Property(p => p.Rail).HasConversion(Lowercase<PaymentRail>()).HasMaxLength(16);
            payment.Property(p => p.Status).HasConversion(Lowercase<PaymentStatus>()).HasMaxLength(16);
            payment.Property(p => p.Currency).HasColumnType("char(3)");
            payment.Property(p => p.Description).HasMaxLength(Payment.MaxDescriptionLength);
            payment.Property(p => p.ReturnReason).HasMaxLength(Payment.MaxReturnReasonLength);
            payment.HasOne<Account>().WithMany().HasForeignKey(p => p.SourceAccountId).OnDelete(DeleteBehavior.Restrict);
            payment.HasOne<Account>().WithMany().HasForeignKey(p => p.DestinationAccountId).OnDelete(DeleteBehavior.Restrict);
            payment.HasOne<JournalTransaction>().WithMany().HasForeignKey(p => p.SettlementTransactionId).OnDelete(DeleteBehavior.Restrict);
            payment.HasOne<JournalTransaction>().WithMany().HasForeignKey(p => p.ReturnTransactionId).OnDelete(DeleteBehavior.Restrict);

            // Serves "what is this account holding funds for", which the integrity check asks.
            payment.HasIndex(p => new { p.SourceAccountId, p.Status });
        });

        modelBuilder.Entity<EventRecord>(record =>
        {
            record.ToTable("events");
            record.HasKey(e => e.Id);
            record.Property(e => e.Id).UseIdentityAlwaysColumn();

            // The id of the writing transaction, as a number the feed can compare and sort.
            record.Property(e => e.Txid).HasDefaultValueSql("(pg_current_xact_id()::text::bigint)");
            record.Property(e => e.Type).HasMaxLength(EventRecord.MaxTypeLength);
            record.Property(e => e.Payload).HasColumnType("jsonb");
            record.HasIndex(e => new { e.Txid, e.Id });
        });

        modelBuilder.Entity<IdempotencyRecord>(record =>
        {
            record.ToTable("idempotency_keys");
            record.HasKey(r => r.Key);
            record.Property(r => r.Key).HasMaxLength(IdempotencyRecord.MaxKeyLength);
            record.Property(r => r.Operation).HasMaxLength(64);
            record.Property(r => r.RequestHash).HasColumnType("char(64)");
        });
    }

    /// <summary>Stores an enum as its lowercase name, so the SQL constraints and triggers can read it.</summary>
    private static ValueConverter<TEnum, string> Lowercase<TEnum>()
        where TEnum : struct, Enum =>
        new(value => value.ToString().ToLowerInvariant(), value => Enum.Parse<TEnum>(value, true));
}
