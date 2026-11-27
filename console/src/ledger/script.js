// One script of requests, run against any ledger.
//
// It is how the simulation is held to the real thing: scripts/parity.js runs it
// against both and compares what came back, step by step. It only touches
// accounts it opens, so it is safe to run against a ledger that has other data.

const amountOf = (dollars) => Math.round(dollars * 100);

export async function runScript(ledger, { tag }) {
  const steps = [];
  const record = async (name, request) => {
    const result = await request;
    steps.push({
      name,
      status: result.status,
      replayed: result.replayed === true,
      body: result.body,
    });
    return result;
  };
  const key = (name) => `${tag}-${name}`;
  const start = (await ledger.events()).body.nextCursor;

  await record(
    'an account with nothing right about it is refused',
    ledger.openAccount({ name: ' ', currency: 'usd' }),
  );
  const open = async (name, normalBalance, allowNegative) =>
    (
      await record(
        `open ${name}`,
        ledger.openAccount({ name, currency: 'USD', normalBalance, allowNegative }),
      )
    ).body.id;
  const settlement = await open('Settlement', 'debit', true);
  const jordan = await open('Jordan', 'credit', false);
  const riley = await open('Riley', 'credit', false);
  const euros = (
    await record(
      'open an account in another currency',
      ledger.openAccount({ name: 'Euros', currency: 'EUR', normalBalance: 'credit' }),
    )
  ).body.id;

  const move = (from, to, dollars, description = 'Movement') => ({
    description,
    entries: [
      { accountId: from, direction: 'debit', amount: amountOf(dollars) },
      { accountId: to, direction: 'credit', amount: amountOf(dollars) },
    ],
  });
  const deposit = move(settlement, jordan, 250, 'Paycheck');

  await record('deposit', ledger.postTransaction(deposit, key('deposit')));
  await record('the same deposit again', ledger.postTransaction(deposit, key('deposit')));
  await record(
    'the same key with a different request',
    ledger.postTransaction(move(settlement, jordan, 1), key('deposit')),
  );
  await record('a write with no key', ledger.postTransaction(deposit, ''));
  await record(
    'debits and credits a cent apart',
    ledger.postTransaction(
      {
        description: 'Off by one cent',
        entries: [
          { accountId: settlement, direction: 'debit', amount: 1000 },
          { accountId: jordan, direction: 'credit', amount: 999 },
        ],
      },
      key('unbalanced'),
    ),
  );
  await record(
    'a transaction with one entry, no description and a zero amount',
    ledger.postTransaction(
      { description: '', entries: [{ accountId: jordan, direction: 'debit', amount: 0 }] },
      key('malformed'),
    ),
  );
  await record(
    'an account that does not exist',
    ledger.postTransaction(
      move(settlement, '00000000-0000-7000-8000-000000000000', 5),
      key('nobody'),
    ),
  );
  await record(
    'two currencies in one transaction',
    ledger.postTransaction(move(jordan, euros, 5), key('currencies')),
  );
  const overdraft = move(jordan, settlement, 999, 'Too much');
  await record('more than the balance', ledger.postTransaction(overdraft, key('overdraft')));
  await record('the refused request again', ledger.postTransaction(overdraft, key('overdraft')));

  const bill = {
    rail: 'ach',
    sourceAccountId: jordan,
    destinationAccountId: settlement,
    amount: amountOf(120),
    description: 'Electric bill',
  };
  await record(
    'a payment to the account it comes from, by no rail, for nothing',
    ledger.createPayment(
      {
        sourceAccountId: jordan,
        destinationAccountId: jordan,
        amount: 0,
        description: '',
      },
      key('bad-payment'),
    ),
  );
  const payment = (await record('an ACH payment', ledger.createPayment(bill, key('bill')))).body;
  await record('the same payment again', ledger.createPayment(bill, key('bill')));
  await record(
    'spending held money in a transaction',
    ledger.postTransaction(move(jordan, settlement, 200, 'Withdrawal'), key('spend-held')),
  );
  await record(
    'spending held money in a second payment',
    ledger.createPayment({ ...bill, amount: amountOf(200) }, key('second-bill')),
  );
  await record(
    'return a payment that has not settled',
    ledger.returnPayment(payment.id, 'R01', key('early-return')),
  );
  await record('settle', ledger.settlePayment(payment.id, key('settle')));
  await record('settle with the same key', ledger.settlePayment(payment.id, key('settle')));
  await record('settle with a new key', ledger.settlePayment(payment.id, key('settle-twice')));
  await record('cancel after settling', ledger.cancelPayment(payment.id, key('late-cancel')));
  await record('return with no reason', ledger.returnPayment(payment.id, ' ', key('no-reason')));
  await record('return', ledger.returnPayment(payment.id, 'R01', key('return')));
  await record('return twice', ledger.returnPayment(payment.id, 'R01', key('return-twice')));

  const second = (
    await record(
      'a second ACH payment',
      ledger.createPayment(
        { ...bill, description: 'Water bill', amount: amountOf(40) },
        key('water'),
      ),
    )
  ).body;
  await record('cancel', ledger.cancelPayment(second.id, key('cancel')));
  await record('settle after canceling', ledger.settlePayment(second.id, key('settle-canceled')));

  const instant = (
    await record(
      'an instant payment',
      ledger.createPayment(
        {
          rail: 'instant',
          sourceAccountId: jordan,
          destinationAccountId: riley,
          amount: amountOf(40),
          description: 'Dinner split',
        },
        key('dinner'),
      ),
    )
  ).body;
  await record(
    'return an instant payment',
    ledger.returnPayment(instant.id, 'R01', key('return-instant')),
  );
  await record(
    'an instant payment for more than is available',
    ledger.createPayment(
      {
        rail: 'instant',
        sourceAccountId: riley,
        destinationAccountId: jordan,
        amount: amountOf(500),
        description: 'Too generous',
      },
      key('too-generous'),
    ),
  );
  await record(
    'a payment that does not exist',
    ledger.settlePayment('00000000-0000-7000-8000-000000000000', key('nothing')),
  );

  const mine = new Set([settlement, jordan, riley, euros]);
  const accounts = (await ledger.listAccounts()).body.filter((account) => mine.has(account.id));
  const events = (await ledger.events(start)).body.items;
  const books = (await ledger.integrity()).body;

  return { steps, accounts, events, books };
}
