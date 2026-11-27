import { describe, expect, it } from 'vitest';
import { balanceOf, deposit, openBooks, withdrawal } from './helpers.js';

describe('posting', () => {
  it('moves money between two accounts and reports the balance after each entry', async () => {
    const books = await openBooks();
    const { status, body } = await books.ledger.postTransaction(
      deposit(books, books.jordan, 25000),
      'k1',
    );

    expect(status).toBe(201);
    expect(
      body.entries.map((entry) => [entry.direction, entry.amount, entry.balanceAfter]),
    ).toEqual([
      ['debit', 25000, 25000],
      ['credit', 25000, 25000],
    ]);
    expect(await balanceOf(books.ledger, books.jordan)).toEqual({
      balance: 25000,
      held: 0,
      available: 25000,
    });
  });

  it('refuses entries that do not balance, by one cent', async () => {
    const books = await openBooks();
    const request = deposit(books, books.jordan, 10000);
    request.entries[1].amount = 9999;

    const { status, body } = await books.ledger.postTransaction(request, 'k1');

    expect(status).toBe(400);
    expect(body.code).toBe('validation_failed');
    expect(body.errors.entries[0]).toBe('Debits (10000) must equal credits (9999).');
    expect((await books.ledger.events()).body.items).toHaveLength(0);
  });

  it('refuses to overdraw an account that may not go negative, and lets one that may', async () => {
    const books = await openBooks();
    await books.ledger.postTransaction(deposit(books, books.jordan, 25000), 'k1');

    const refused = await books.ledger.postTransaction(
      withdrawal(books, books.jordan, 99900),
      'k2',
    );

    expect([refused.status, refused.body.code]).toEqual([422, 'insufficient_funds']);
    expect((await balanceOf(books.ledger, books.jordan)).balance).toBe(25000);
    // The settlement account went to 25000 on its debit side with nothing behind it.
    expect((await balanceOf(books.ledger, books.settlement)).balance).toBe(25000);
  });

  it('refuses an account that does not exist, and a request with no key', async () => {
    const books = await openBooks();
    const unknown = await books.ledger.postTransaction(deposit(books, 'nobody', 100), 'k1');
    const keyless = await books.ledger.postTransaction(deposit(books, books.jordan, 100), '');

    expect([unknown.status, unknown.body.code]).toEqual([422, 'account_not_found']);
    expect([keyless.status, keyless.body.code]).toEqual([400, 'idempotency_key_required']);
  });
});

describe('idempotency', () => {
  it('acts once for a key, and says the second response is a replay', async () => {
    const books = await openBooks();
    const request = deposit(books, books.jordan, 25000);

    const first = await books.ledger.postTransaction(request, 'deposit-0001');
    const second = await books.ledger.postTransaction(request, 'deposit-0001');

    expect(first.replayed).toBeUndefined();
    expect(second.replayed).toBe(true);
    expect(second.body).toEqual(first.body);
    expect((await balanceOf(books.ledger, books.jordan)).balance).toBe(25000);
  });

  it('replays a refusal too', async () => {
    const books = await openBooks();
    const request = withdrawal(books, books.jordan, 500);

    await books.ledger.postTransaction(request, 'k1');
    await books.ledger.postTransaction(deposit(books, books.jordan, 25000), 'k2');
    const again = await books.ledger.postTransaction(request, 'k1');

    // Jordan could afford it now, but this key already has its answer.
    expect([again.status, again.body.code, again.replayed]).toEqual([
      422,
      'insufficient_funds',
      true,
    ]);
  });

  it('refuses a key that comes back with a different request', async () => {
    const books = await openBooks();
    await books.ledger.postTransaction(deposit(books, books.jordan, 25000), 'k1');
    const other = await books.ledger.postTransaction(deposit(books, books.jordan, 100), 'k1');

    expect([other.status, other.body.code]).toEqual([422, 'idempotency_key_reused']);
  });
});

describe('payments', () => {
  async function funded() {
    const books = await openBooks();
    await books.ledger.postTransaction(deposit(books, books.jordan, 25000), 'fund');
    const bill = {
      rail: 'ach',
      sourceAccountId: books.jordan,
      destinationAccountId: books.settlement,
      amount: 12000,
      description: 'Electric bill',
    };
    return { ...books, bill };
  }

  it('holds the money for a pending payment without moving it', async () => {
    const books = await funded();
    const { status, body } = await books.ledger.createPayment(books.bill, 'bill-1');

    expect([status, body.status]).toEqual([201, 'pending']);
    expect(await balanceOf(books.ledger, books.jordan)).toEqual({
      balance: 25000,
      held: 12000,
      available: 13000,
    });
  });

  it('refuses to spend held money, in a transaction or in a second payment', async () => {
    const books = await funded();
    await books.ledger.createPayment(books.bill, 'bill-1');

    const spend = await books.ledger.postTransaction(withdrawal(books, books.jordan, 20000), 'w');
    const second = await books.ledger.createPayment({ ...books.bill, amount: 20000 }, 'bill-2');

    expect(spend.body.code).toBe('insufficient_funds');
    expect(second.body.code).toBe('insufficient_funds');
  });

  it('settles a pending payment, then returns it by reversing the settlement', async () => {
    const books = await funded();
    const { body: payment } = await books.ledger.createPayment(books.bill, 'bill-1');

    const settled = await books.ledger.settlePayment(payment.id, 'settle');
    expect([settled.status, settled.body.status]).toEqual([200, 'settled']);
    expect(await balanceOf(books.ledger, books.jordan)).toEqual({
      balance: 13000,
      held: 0,
      available: 13000,
    });

    const returned = await books.ledger.returnPayment(payment.id, 'R01', 'return');
    expect([returned.body.status, returned.body.returnReason]).toEqual(['returned', 'R01']);
    expect((await balanceOf(books.ledger, books.jordan)).balance).toBe(25000);

    const types = (await books.ledger.events()).body.items.map((event) => event.type);
    expect(types).toEqual([
      'transaction.posted',
      'payment.created',
      'transaction.posted',
      'payment.settled',
      'transaction.posted',
      'payment.returned',
    ]);
    const reversal = (await books.ledger.events()).body.items[4].data;
    expect(reversal.reversesTransactionId).toBe(settled.body.settlementTransactionId);
  });

  it('cancels a pending payment and gives the held money back', async () => {
    const books = await funded();
    const { body: payment } = await books.ledger.createPayment(books.bill, 'bill-1');

    const canceled = await books.ledger.cancelPayment(payment.id, 'cancel');

    expect(canceled.body.status).toBe('canceled');
    expect((await balanceOf(books.ledger, books.jordan)).held).toBe(0);
  });

  it('only moves a payment forward', async () => {
    const books = await funded();
    const { body: payment } = await books.ledger.createPayment(books.bill, 'bill-1');
    await books.ledger.cancelPayment(payment.id, 'cancel');

    const settle = await books.ledger.settlePayment(payment.id, 'settle');
    const back = await books.ledger.returnPayment(payment.id, 'R01', 'return');
    const missing = await books.ledger.settlePayment('nothing', 'settle-2');

    expect([settle.status, settle.body.code]).toEqual([409, 'invalid_payment_state']);
    expect([back.status, back.body.code]).toEqual([409, 'invalid_payment_state']);
    expect([missing.status, missing.body.code]).toEqual([404, 'payment_not_found']);
  });

  it('settles an instant payment at once, and treats it as final', async () => {
    const books = await funded();
    const { body: payment } = await books.ledger.createPayment(
      { ...books.bill, rail: 'instant', destinationAccountId: books.riley, amount: 4000 },
      'p2p',
    );

    expect(payment.status).toBe('settled');
    expect((await balanceOf(books.ledger, books.riley)).balance).toBe(4000);
    const back = await books.ledger.returnPayment(payment.id, 'R01', 'return');
    expect(back.body.detail).toContain('instant payments are final');
  });
});

describe('the books', () => {
  it('stay consistent through everything above', async () => {
    const books = await openBooks();
    await books.ledger.postTransaction(deposit(books, books.jordan, 25000), 'a');
    await books.ledger.postTransaction(withdrawal(books, books.jordan, 99900), 'b');
    const { body: payment } = await books.ledger.createPayment(
      {
        rail: 'ach',
        sourceAccountId: books.jordan,
        destinationAccountId: books.settlement,
        amount: 12000,
        description: 'Electric bill',
      },
      'c',
    );
    const pending = (await books.ledger.integrity()).body;
    await books.ledger.settlePayment(payment.id, 'd');
    await books.ledger.returnPayment(payment.id, 'R01', 'e');
    const after = (await books.ledger.integrity()).body;

    expect(pending).toMatchObject({
      consistent: true,
      debitsMinusCredits: 0,
      accountsChecked: 3,
    });
    expect(after).toMatchObject({ consistent: true, mismatches: [], holdMismatches: [] });
  });

  it('give out copies, so nothing a caller does to a response changes the ledger', async () => {
    const books = await openBooks();
    const { body } = await books.ledger.postTransaction(deposit(books, books.jordan, 25000), 'a');
    body.entries[1].amount = 1;
    const accounts = (await books.ledger.listAccounts()).body;
    accounts[1].balance = 0;

    expect((await books.ledger.integrity()).body.consistent).toBe(true);
    expect((await balanceOf(books.ledger, books.jordan)).balance).toBe(25000);
  });
});
