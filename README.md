# Neon Ledger

A double-entry ledger for payment systems, as a small HTTP service: C# on .NET 10, EF Core, PostgreSQL.

It keeps accounts and the transactions between them, and it is built around the four things a ledger has to get right:

- **Money is never created or destroyed.** Every transaction's debits equal its credits.
- **A retry never pays twice.** Writes carry an idempotency key, and a repeat returns the first response.
- **Two payments cannot spend the same money.** Concurrent postings against one account queue; they do not race.
- **History is not edited.** Posted transactions are append-only, and a mistake is corrected by posting a reversal.

I spent four years building payment and digital banking systems in C#. None of this code or design comes from an employer; it is written from scratch to show how I approach the problem.

## Status

This is the ledger core, and it is complete for what it claims below. It is not a payments product: there is no authentication, no pending or held funds, and no payment lifecycle (see [Known limits](#known-limits) and [Roadmap](#roadmap)). It has not run in production.

## The rules, and where each one is enforced

The important rules are enforced twice: in the application, where a violation becomes a clear error, and in PostgreSQL, where it holds for every writer. The database tests write SQL directly, around the application, to prove the second layer on its own.

| Rule | Application | Database | Proved by |
|---|---|---|---|
| Debits equal credits | Validated before posting | Deferred constraint trigger, checked at commit | `DatabaseRuleTests`, `PostTransactionValidatorTests` |
| No overdraft on accounts that forbid it | Checked under a row lock | `CHECK (allow_negative OR balance >= 0)` | `PostingTests`, `ConcurrencyTests`, `DatabaseRuleTests` |
| No double spend under concurrency | `SELECT … FOR UPDATE` on every account in the transaction | Row locks | `ConcurrencyTests` |
| No deadlock between opposing transfers | Accounts locked in id order | | `ConcurrencyTests` |
| A retry posts once | Idempotency key committed with the write | Primary key on the key | `IdempotencyTests` |
| History is append-only | No update or delete paths exist | Triggers reject `UPDATE`, `DELETE`, `TRUNCATE` | `DatabaseRuleTests` |
| A transaction is reversed at most once | Checked under a lock on the original | Unique index | `ReversalTests` |
| Stored balances match the entries | Balance and entries change in one database transaction | | `IntegrityTests` |

Two of the concurrency tests were checked the hard way: with the lock ordering removed, the opposing-transfers test fails, and with the row lock removed, the double-spend test fails.

## Run it

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download) and Docker.

```bash
docker compose up -d --wait                          # PostgreSQL on localhost:5433
dotnet run --project src/NeonLedger.Api -- migrate   # create the schema
dotnet run --project src/NeonLedger.Api              # http://localhost:5080
```

The OpenAPI document is at `http://localhost:5080/openapi/v1.json`.

### A deposit, a retry and a refusal

Amounts are whole numbers of minor units: `25000` in USD is $250.00.

Open a settlement account (where money enters the ledger from) and a customer account:

```bash
curl -s -X POST localhost:5080/v1/accounts -H 'Content-Type: application/json' \
  -d '{"name":"ACH settlement","currency":"USD","normalBalance":"debit","allowNegative":true}'

curl -s -X POST localhost:5080/v1/accounts -H 'Content-Type: application/json' \
  -d '{"name":"Jordan checking","currency":"USD","normalBalance":"credit"}'
```

Post a deposit, using the two ids from the responses:

```bash
curl -s -X POST localhost:5080/v1/transactions \
  -H 'Content-Type: application/json' -H 'Idempotency-Key: deposit-0001' \
  -d '{
    "description": "Incoming ACH deposit",
    "entries": [
      {"accountId": "<settlement id>", "direction": "debit",  "amount": 25000},
      {"accountId": "<customer id>",   "direction": "credit", "amount": 25000}
    ]
  }'
```

```json
{
  "id": "01a216ca-9ae9-7ede-89f1-0af132e48208",
  "description": "Incoming ACH deposit",
  "currency": "USD",
  "postedAt": "2026-11-26T07:38:26.409989+00:00",
  "reversesTransactionId": null,
  "entries": [
    {"id": 1, "accountId": "01a216ca-9807-…", "direction": "debit",  "amount": 25000, "balanceAfter": 25000},
    {"id": 2, "accountId": "01a216ca-993e-…", "direction": "credit", "amount": 25000, "balanceAfter": 25000}
  ]
}
```

Send the same request again and you get the same body back with an `Idempotent-Replayed: true` header. Nothing is posted a second time.

Try to move more than the customer has and the ledger refuses, with a `code` a client can branch on:

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.21",
  "title": "Request refused",
  "status": 422,
  "detail": "Account 01a216ca-993e-… does not have enough funds for this transaction.",
  "code": "insufficient_funds"
}
```

## API

| Method and path | What it does |
|---|---|
| `POST /v1/accounts` | Open an account: `name`, `currency`, `normalBalance` (`debit` or `credit`), `allowNegative`. |
| `GET /v1/accounts/{id}` | The account and its current balance. |
| `GET /v1/accounts/{id}/entries?limit=&before=` | The account's entries, newest first, each with the balance after it. Keyset pagination. |
| `POST /v1/transactions` | Post a transaction of two or more entries. Requires `Idempotency-Key`. |
| `GET /v1/transactions/{id}` | A transaction and its entries. |
| `POST /v1/transactions/{id}/reversal` | Post the mirror image of a transaction. Requires `Idempotency-Key`. |
| `GET /v1/integrity` | Recompute every balance from the entries and report any that disagree. |
| `GET /health` | Whether the service can reach its database. |

A transaction can have more than two entries, so one payment can carry its fee:

```json
{
  "description": "Bill payment with fee",
  "entries": [
    {"accountId": "<payer>",       "direction": "debit",  "amount": 5150},
    {"accountId": "<biller>",      "direction": "credit", "amount": 5000},
    {"accountId": "<fee revenue>", "direction": "credit", "amount": 150}
  ]
}
```

**Normal balance.** An account's balance grows on its normal side. Customer funds are a liability of the institution holding them, so customer accounts are credit-normal; cash and settlement accounts are debit-normal. That is why a deposit debits the settlement account and credits the customer, and both balances go up.

**Idempotency.** The key is stored with a hash of the request. The same key with the same request replays the stored response, success or refusal. The same key with a different request is refused (`idempotency_key_reused`). A request that fails validation never reaches the ledger and does not use up its key.

**Errors.** Every error is an RFC 9457 problem document. Refusals by the ledger carry a `code`: `insufficient_funds`, `account_not_found`, `currency_mismatch`, `already_reversed`, `reversal_not_reversible`, `transaction_not_found`, `idempotency_key_required`, `idempotency_key_too_long`, `idempotency_key_reused`, `validation_failed`.

## Design decisions

**Balances are stored, and the entries are still the truth.** Each account row carries its balance, updated in the same database transaction as the entries that change it, so reading a balance is one row. Each entry also records the balance after it, which gives a statement for free and makes any break in the chain visible. `GET /v1/integrity` recomputes everything from the entries and names the accounts that disagree.

**Pessimistic locks, taken in a fixed order.** A posting locks every account it touches with `SELECT … FOR UPDATE`, sorted by id, then checks funds and writes. Payments against one account are serialized by the database, and two transfers in opposite directions queue instead of deadlocking. Optimistic concurrency with retries would also work; I chose locks because a ledger's hot rows are contended by design, and under contention retries turn into wasted work and unpredictable latency.

**The idempotency key commits with the write.** The key row is inserted first, in the same database transaction as the posting. A concurrent request with the same key blocks on the key's primary key until the first one commits, then reads its stored response. If the first one crashes, its transaction rolls back and takes the key with it, so the retry runs for real. There is no window where the money has moved and the key is not recorded, or the reverse.

**The database defends its own invariants.** The balanced-transaction rule is a deferred constraint trigger and append-only history is enforced by triggers, both written by hand in the migration. An application bug, a script or a second service sharing the database cannot commit an unbalanced transaction or rewrite history.

**EF Core, with SQL where it matters.** EF Core does the mapping, the migrations and the ordinary queries. The statements where the exact SQL is the point (the row locks, the idempotency insert, the integrity check) are written out.

**One project, folders by concern.** `Domain`, `Ledger`, `Idempotency`, `Data`, `Api`. It is a small service, and splitting it into several assemblies would add ceremony without adding a boundary anything depends on.

## Tests

```bash
dotnet test
```

Most tests drive the real application over HTTP, in process, against a real PostgreSQL. By default the run starts a throwaway PostgreSQL container, so Docker is the only requirement. To use a server you already have, set `NEON_LEDGER_TEST_POSTGRES` to a connection string for a user that may create databases. Each test class gets its own freshly migrated database.

CI runs formatting, a build with warnings as errors, and the tests on every push.

### Changing the schema

```bash
dotnet tool restore
dotnet ef migrations add <Name> --project src/NeonLedger.Api --output-dir Data/Migrations
```

The server never migrates on start. Apply migrations with `dotnet run --project src/NeonLedger.Api -- migrate`.

## Known limits

- **No authentication or authorization.** Anyone who can reach the service can post. It needs to sit behind something that authenticates callers until that is built.
- **A busy account is a queue.** Every posting that touches an account waits for the one before it. That is correct for a customer account and a real ceiling for an account every transaction touches, such as a single settlement account. The usual answers are to shard such accounts or to batch their entries; neither is built.
- **One currency per transaction.** There is no foreign exchange. Currency codes are checked for shape, not against the ISO 4217 list.
- **No pending or held funds.** A posting is final when it commits. Authorizations, holds and settlement delays are not modeled.
- **Idempotency keys are kept forever** and are global, not scoped to a caller. There is no expiry job.
- **The integrity check reads every entry.** It is an operational tool for a schedule, not something to call per request.
- **No events.** Nothing is published when a transaction posts.

## Roadmap

1. **Payment lifecycles on top of the ledger.** Holds and settlement for payments that take days and can come back (ACH-style: pending, posted, returned), next to instant transfers that are final at once.
2. **An outbox**, so that "a transaction posted" can be published reliably from the same database transaction.
3. **Caller authentication**, with idempotency keys scoped to the caller.

## License

MIT. See [LICENSE](LICENSE).
