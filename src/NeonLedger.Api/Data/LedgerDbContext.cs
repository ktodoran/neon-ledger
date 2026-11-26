using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using NeonLedger.Domain;
using NeonLedger.Idempotency;

namespace NeonLedger.Data;

public sealed class LedgerDbContext(DbContextOptions<LedgerDbContext> options) : DbContext(options)
{
    public DbSet<Account> Accounts => Set<Account>();

    public DbSet<JournalTransaction> Transactions => Set<JournalTransaction>();

    public DbSet<Entry> Entries => Set<Entry>();

    public DbSet<IdempotencyRecord> IdempotencyKeys => Set<IdempotencyRecord>();

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
                table.HasCheckConstraint("ck_accounts_balance_not_negative", "allow_negative OR balance >= 0");
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

        modelBuilder.Entity<IdempotencyRecord>(record =>
        {
            record.ToTable("idempotency_keys");
            record.HasKey(r => r.Key);
            record.Property(r => r.Key).HasMaxLength(IdempotencyRecord.MaxKeyLength);
            record.Property(r => r.Operation).HasMaxLength(64);
            record.Property(r => r.RequestHash).HasColumnType("char(64)");
        });
    }
}
