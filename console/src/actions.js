// The requests the console sends, each as a function of the ledger it is sent to.

/** A transaction of two entries: debit `from`, credit `to`. */
export const move =
  ({ from, to, amount, memo }, key) =>
  (ledger) =>
    ledger.postTransaction(
      {
        description: memo,
        entries: [
          { accountId: from, direction: 'debit', amount },
          { accountId: to, direction: 'credit', amount },
        ],
      },
      key,
    );

export const pay =
  ({ rail, from, to, amount, memo }, key) =>
  (ledger) =>
    ledger.createPayment(
      { rail, sourceAccountId: from, destinationAccountId: to, amount, description: memo },
      key,
    );

export const settle = (id, key) => (ledger) => ledger.settlePayment(id, key);
export const cancel = (id, key) => (ledger) => ledger.cancelPayment(id, key);
export const giveBack = (id, reason, key) => (ledger) => ledger.returnPayment(id, reason, key);
