using NeonLedger.Domain;

namespace NeonLedger.Ledger;

/// <summary>Checks that need no database: shape, limits, and that debits equal credits.</summary>
public static class PostTransactionValidator
{
    public static Dictionary<string, string[]> Validate(PostTransaction command)
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(command.Description))
        {
            errors["description"] = ["A description is required."];
        }
        else if (command.Description.Length > JournalTransaction.MaxDescriptionLength)
        {
            errors["description"] = [$"A description can be at most {JournalTransaction.MaxDescriptionLength} characters."];
        }

        if (command.Entries.Count < 2)
        {
            errors["entries"] = ["A transaction needs at least two entries."];
            return errors;
        }

        if (command.Entries.Count > LedgerService.MaxEntries)
        {
            errors["entries"] = [$"A transaction can have at most {LedgerService.MaxEntries} entries."];
            return errors;
        }

        long debits = 0;
        long credits = 0;
        for (var i = 0; i < command.Entries.Count; i++)
        {
            var entry = command.Entries[i];
            if (entry.AccountId == Guid.Empty)
            {
                errors[$"entries[{i}].accountId"] = ["An account id is required."];
            }

            if (entry.Amount is <= 0 or > LedgerService.MaxAmount)
            {
                errors[$"entries[{i}].amount"] = [$"An amount is a whole number of minor units between 1 and {LedgerService.MaxAmount}."];
                continue;
            }

            if (entry.Direction == Direction.Debit)
            {
                debits += entry.Amount;
            }
            else
            {
                credits += entry.Amount;
            }
        }

        if (errors.Count == 0 && debits != credits)
        {
            errors["entries"] = [$"Debits ({debits}) must equal credits ({credits})."];
        }

        return errors;
    }
}
