// A ledger that lives in this browser tab.
//
// It answers the same requests as the Neon Ledger API with the same response
// shapes, status codes and refusal codes, so the console cannot tell the two
// apart. The rules are re-implemented here in JavaScript. The real ledger
// enforces them in C# and again in PostgreSQL, and is the one to trust where
// the two ever disagree.
//
// What it does not have is concurrency. Requests are handled one at a time, so
// a "race" here shows the outcome the real ledger reaches with row locks, not
// the mechanism.

const MAX_AMOUNT = 100_000_000_000_000;
const MAX_ENTRIES = 100;
const MAX_KEY_LENGTH = 255;
const MAX_NAME_LENGTH = 120;
const MAX_DESCRIPTION_LENGTH = 255;
const MAX_REASON_LENGTH = 64;
const AMOUNT_RULE = `An amount is a whole number of minor units between 1 and ${String(MAX_AMOUNT)}.`;

const TITLES = { 400: 'Bad request', 404: 'Not found', 409: 'Conflict', 422: 'Request refused' };
const TYPES = {
  400: 'https://tools.ietf.org/html/rfc9110#section-15.5.1',
  404: 'https://tools.ietf.org/html/rfc9110#section-15.5.5',
  409: 'https://tools.ietf.org/html/rfc9110#section-15.5.10',
  422: 'https://tools.ietf.org/html/rfc9110#section-15.5.21',
};

const copy = (value) => structuredClone(value);
const opposite = (direction) => (direction === 'debit' ? 'credit' : 'debit');
const isId = (value) => typeof value === 'string' && value !== '';
const isAmount = (value) => Number.isInteger(value) && value >= 1 && value <= MAX_AMOUNT;

function problem(status, code, detail) {
  return { status, body: { type: TYPES[status], title: TITLES[status], status, detail, code } };
}

function invalid(errors) {
  return {
    status: 400,
    body: {
      type: TYPES[400],
      title: 'One or more validation errors occurred.',
      status: 400,
      errors,
      code: 'validation_failed',
    },
  };
}

export function createSimulatedLedger({
  now = () => new Date(),
  newId = () => crypto.randomUUID(),
} = {}) {
  const accounts = new Map();
  const transactions = new Map();
  const payments = new Map();
  const reversals = new Map();
  const idempotency = new Map();
  const events = [];
  let entrySequence = 0;

  const timestamp = () => now().toISOString();
  const viewOf = (account) => ({ ...account, available: account.balance - account.held });

  function emit(type, data) {
    events.push({
      cursor: String(events.length + 1),
      type,
      occurredAt: timestamp(),
      data: copy(data),
    });
  }

  /**
   * Posts balanced lines, or refuses without changing anything. `release`
   * lifts a hold in the same step, which is how a pending payment settles.
   */
  function post(description, lines, { reverses = null, release = null } = {}) {
    for (const line of lines) {
      if (!accounts.has(line.accountId)) {
        return problem(422, 'account_not_found', `Account ${line.accountId} does not exist.`);
      }
    }
    const { currency } = accounts.get(lines[0].accountId);
    for (const line of lines) {
      const account = accounts.get(line.accountId);
      if (account.currency !== currency) {
        return problem(
          422,
          'currency_mismatch',
          `Account ${account.id} holds ${account.currency}; every entry in a transaction must be in one currency (${currency}).`,
        );
      }
    }

    const balances = new Map();
    for (const line of lines) {
      const account = accounts.get(line.accountId);
      const before = balances.get(account.id) ?? account.balance;
      const delta = line.direction === account.normalBalance ? line.amount : -line.amount;
      balances.set(account.id, before + delta);
    }
    // In id order, as the real ledger locks and checks them.
    for (const id of [...balances.keys()].sort()) {
      const account = accounts.get(id);
      const held = account.held - (release?.accountId === id ? release.amount : 0);
      if (balances.get(id) - held < 0 && !account.allowNegative) {
        return problem(
          422,
          'insufficient_funds',
          `Account ${id} does not have enough funds for this transaction.`,
        );
      }
    }

    const running = new Map();
    const entries = lines.map((line) => {
      const account = accounts.get(line.accountId);
      const before = running.get(account.id) ?? account.balance;
      const delta = line.direction === account.normalBalance ? line.amount : -line.amount;
      running.set(account.id, before + delta);
      entrySequence += 1;
      return {
        id: entrySequence,
        accountId: account.id,
        direction: line.direction,
        amount: line.amount,
        balanceAfter: before + delta,
      };
    });
    for (const [id, balance] of running) accounts.get(id).balance = balance;
    if (release !== null) accounts.get(release.accountId).held -= release.amount;

    const transaction = {
      id: newId(),
      description,
      currency,
      postedAt: timestamp(),
      reversesTransactionId: reverses,
      entries,
    };
    transactions.set(transaction.id, transaction);
    if (reverses !== null) reversals.set(reverses, transaction.id);
    emit('transaction.posted', transaction);
    return { status: 201, body: transaction };
  }

  /** Runs `action` once per key. The same key and request replays the stored response. */
  function once(key, scope, request, action) {
    if (typeof key !== 'string' || key.trim() === '') {
      return problem(
        400,
        'idempotency_key_required',
        'Send an Idempotency-Key header. A retry with the same key will not act twice.',
      );
    }
    if (key.length > MAX_KEY_LENGTH) {
      return problem(
        400,
        'idempotency_key_too_long',
        `An Idempotency-Key can be at most ${String(MAX_KEY_LENGTH)} characters.`,
      );
    }
    const fingerprint = JSON.stringify([scope, request]);
    const stored = idempotency.get(key);
    if (stored !== undefined) {
      return stored.fingerprint === fingerprint
        ? { ...copy(stored.response), replayed: true }
        : problem(
            422,
            'idempotency_key_reused',
            'This Idempotency-Key was already used for a different request.',
          );
    }
    const response = action();
    idempotency.set(key, { fingerprint, response: copy(response) });
    return response;
  }

  function validateTransaction(request) {
    const errors = {};
    const description = request?.description?.trim() ?? '';
    if (description === '') {
      errors.description = ['A description is required.'];
    } else if (description.length > MAX_DESCRIPTION_LENGTH) {
      errors.description = [
        `A description can be at most ${String(MAX_DESCRIPTION_LENGTH)} characters.`,
      ];
    }
    const entries = Array.isArray(request?.entries) ? request.entries : [];
    if (entries.length < 2) {
      errors.entries = ['A transaction needs at least two entries.'];
    } else if (entries.length > MAX_ENTRIES) {
      errors.entries = [`A transaction can have at most ${String(MAX_ENTRIES)} entries.`];
    } else {
      let debits = 0;
      let credits = 0;
      entries.forEach((entry, index) => {
        if (!isId(entry?.accountId)) {
          errors[`entries[${String(index)}].accountId`] = ['An account id is required.'];
        }
        if (entry?.direction !== 'debit' && entry?.direction !== 'credit') {
          errors[`entries[${String(index)}].direction`] = ['A direction is debit or credit.'];
        }
        if (!isAmount(entry?.amount)) {
          errors[`entries[${String(index)}].amount`] = [AMOUNT_RULE];
        } else if (entry.direction === 'debit') {
          debits += entry.amount;
        } else {
          credits += entry.amount;
        }
      });
      if (Object.keys(errors).length === 0 && debits !== credits) {
        errors.entries = [`Debits (${String(debits)}) must equal credits (${String(credits)}).`];
      }
    }
    return Object.keys(errors).length === 0 ? null : invalid(errors);
  }

  function savePayment(payment, type) {
    payment.updatedAt = timestamp();
    payments.set(payment.id, payment);
    emit(type, payment);
    return payment;
  }

  function transition(id, verb, allowed, act) {
    const payment = payments.get(id);
    if (payment === undefined) {
      return problem(404, 'payment_not_found', `Payment ${id} does not exist.`);
    }
    const refusal = allowed(payment);
    if (refusal !== null) {
      return problem(409, 'invalid_payment_state', `Payment ${id} cannot be ${verb}: ${refusal}.`);
    }
    return act(payment);
  }

  const notPending = (payment) =>
    payment.status === 'pending' ? null : `it is ${payment.status}, not pending`;

  return {
    mode: 'simulated',

    async listAccounts() {
      return { status: 200, body: [...accounts.values()].map(viewOf).map(copy) };
    },

    async openAccount(request) {
      const errors = {};
      const name = request?.name?.trim() ?? '';
      if (name === '') {
        errors.name = ['A name is required.'];
      } else if (name.length > MAX_NAME_LENGTH) {
        errors.name = [`A name can be at most ${String(MAX_NAME_LENGTH)} characters.`];
      }
      if (!/^[A-Z]{3}$/.test(request?.currency ?? '')) {
        errors.currency = ['A currency is a three-letter ISO 4217 code in capitals, such as USD.'];
      }
      if (request?.normalBalance !== 'debit' && request?.normalBalance !== 'credit') {
        errors.normalBalance = ['normalBalance is required: debit or credit.'];
      }
      if (Object.keys(errors).length > 0) return invalid(errors);
      const account = {
        id: newId(),
        name,
        currency: request.currency,
        normalBalance: request.normalBalance,
        allowNegative: request.allowNegative === true,
        balance: 0,
        held: 0,
        createdAt: timestamp(),
      };
      accounts.set(account.id, account);
      return { status: 201, body: copy(viewOf(account)) };
    },

    async postTransaction(request, key) {
      const refused = validateTransaction(request);
      if (refused !== null) return refused;
      return once(key, 'post_transaction', request, () =>
        copy(post(request.description.trim(), request.entries)),
      );
    },

    async createPayment(request, key) {
      const errors = {};
      const description = request?.description?.trim() ?? '';
      if (request?.rail !== 'instant' && request?.rail !== 'ach') {
        errors.rail = ['rail is required: instant or ach.'];
      }
      if (!isId(request?.sourceAccountId)) {
        errors.sourceAccountId = ['A source account id is required.'];
      }
      if (!isId(request?.destinationAccountId)) {
        errors.destinationAccountId = ['A destination account id is required.'];
      } else if (request.destinationAccountId === request.sourceAccountId) {
        errors.destinationAccountId = ['A payment needs two different accounts.'];
      }
      if (!isAmount(request?.amount)) {
        errors.amount = [AMOUNT_RULE];
      }
      if (description === '') {
        errors.description = ['A description is required.'];
      } else if (description.length > MAX_DESCRIPTION_LENGTH) {
        errors.description = [
          `A description can be at most ${String(MAX_DESCRIPTION_LENGTH)} characters.`,
        ];
      }
      if (Object.keys(errors).length > 0) return invalid(errors);

      return once(key, 'create_payment', request, () => {
        const { rail, sourceAccountId, destinationAccountId, amount } = request;
        const create = (currency) => ({
          id: newId(),
          rail,
          status: 'pending',
          sourceAccountId,
          destinationAccountId,
          amount,
          currency,
          description,
          settlementTransactionId: null,
          returnTransactionId: null,
          returnReason: null,
          createdAt: timestamp(),
          updatedAt: timestamp(),
        });

        if (rail === 'instant') {
          const posted = post(description, [
            { accountId: sourceAccountId, direction: 'debit', amount },
            { accountId: destinationAccountId, direction: 'credit', amount },
          ]);
          if (posted.status !== 201) return copy(posted);
          const payment = create(posted.body.currency);
          payment.status = 'settled';
          payment.settlementTransactionId = posted.body.id;
          payments.set(payment.id, payment);
          emit('payment.created', payment);
          return { status: 201, body: copy(savePayment(payment, 'payment.settled')) };
        }

        const source = accounts.get(sourceAccountId);
        if (source === undefined) {
          return problem(422, 'account_not_found', `Account ${sourceAccountId} does not exist.`);
        }
        const destination = accounts.get(destinationAccountId);
        if (destination === undefined) {
          return problem(
            422,
            'account_not_found',
            `Account ${destinationAccountId} does not exist.`,
          );
        }
        if (destination.currency !== source.currency) {
          return problem(
            422,
            'currency_mismatch',
            `Account ${destination.id} holds ${destination.currency}; every entry in a transaction must be in one currency (${source.currency}).`,
          );
        }
        if (source.balance - source.held < amount && !source.allowNegative) {
          return problem(
            422,
            'insufficient_funds',
            `Account ${source.id} does not have enough funds for this transaction.`,
          );
        }
        source.held += amount;
        return {
          status: 201,
          body: copy(savePayment(create(source.currency), 'payment.created')),
        };
      });
    },

    async settlePayment(id, key) {
      return once(key, 'settle_payment', { id }, () =>
        transition(id, 'settled', notPending, (payment) => {
          const posted = post(
            payment.description,
            [
              {
                accountId: payment.sourceAccountId,
                direction: 'debit',
                amount: payment.amount,
              },
              {
                accountId: payment.destinationAccountId,
                direction: 'credit',
                amount: payment.amount,
              },
            ],
            { release: { accountId: payment.sourceAccountId, amount: payment.amount } },
          );
          if (posted.status !== 201) return copy(posted);
          payment.status = 'settled';
          payment.settlementTransactionId = posted.body.id;
          return { status: 200, body: copy(savePayment(payment, 'payment.settled')) };
        }),
      );
    },

    async cancelPayment(id, key) {
      return once(key, 'cancel_payment', { id }, () =>
        transition(id, 'canceled', notPending, (payment) => {
          accounts.get(payment.sourceAccountId).held -= payment.amount;
          payment.status = 'canceled';
          return { status: 200, body: copy(savePayment(payment, 'payment.canceled')) };
        }),
      );
    },

    async returnPayment(id, reason, key) {
      const trimmed = typeof reason === 'string' ? reason.trim() : '';
      if (trimmed === '' || trimmed.length > MAX_REASON_LENGTH) {
        return invalid({
          reason: [
            `A reason of 1 to ${String(MAX_REASON_LENGTH)} characters is required, such as an ACH return code.`,
          ],
        });
      }
      return once(key, 'return_payment', { id, reason: trimmed }, () =>
        transition(
          id,
          'returned',
          (payment) =>
            payment.rail === 'instant'
              ? 'instant payments are final'
              : payment.status === 'settled'
                ? null
                : `it is ${payment.status}, not settled`,
          (payment) => {
            const original = transactions.get(payment.settlementTransactionId);
            const posted = post(
              `Reversal of ${original.id}`,
              original.entries.map((entry) => ({
                accountId: entry.accountId,
                direction: opposite(entry.direction),
                amount: entry.amount,
              })),
              { reverses: original.id },
            );
            if (posted.status !== 201) return copy(posted);
            payment.status = 'returned';
            payment.returnTransactionId = posted.body.id;
            payment.returnReason = trimmed;
            return {
              status: 200,
              body: copy(savePayment(payment, 'payment.returned')),
            };
          },
        ),
      );
    },

    async events(after = '0') {
      const from = Number(after) || 0;
      const items = events.slice(from).map(copy);
      return { status: 200, body: { items, nextCursor: String(from + items.length) } };
    },

    /** Recomputes every balance from the entries and every hold from the pending payments. */
    async integrity() {
      const fromEntries = new Map();
      let debitsMinusCredits = 0;
      for (const transaction of transactions.values()) {
        for (const entry of transaction.entries) {
          const account = accounts.get(entry.accountId);
          const delta = entry.direction === account.normalBalance ? entry.amount : -entry.amount;
          fromEntries.set(account.id, (fromEntries.get(account.id) ?? 0) + delta);
          debitsMinusCredits += entry.direction === 'debit' ? entry.amount : -entry.amount;
        }
      }
      const fromPayments = new Map();
      for (const payment of payments.values()) {
        if (payment.status !== 'pending') continue;
        fromPayments.set(
          payment.sourceAccountId,
          (fromPayments.get(payment.sourceAccountId) ?? 0) + payment.amount,
        );
      }
      const mismatches = [];
      const holdMismatches = [];
      for (const account of accounts.values()) {
        const balance = fromEntries.get(account.id) ?? 0;
        if (balance !== account.balance) {
          mismatches.push({
            accountId: account.id,
            storedBalance: account.balance,
            balanceFromEntries: balance,
          });
        }
        const held = fromPayments.get(account.id) ?? 0;
        if (held !== account.held) {
          holdMismatches.push({
            accountId: account.id,
            storedHeld: account.held,
            heldFromPayments: held,
          });
        }
      }
      return {
        status: 200,
        body: {
          consistent:
            debitsMinusCredits === 0 && mismatches.length === 0 && holdMismatches.length === 0,
          debitsMinusCredits,
          accountsChecked: accounts.size,
          mismatches,
          holdMismatches,
        },
      };
    },
  };
}
