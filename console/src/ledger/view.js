// What the console shows, read from the event feed.
//
// The journal and the payments table are not fetched as lists: they are built
// from the same feed another system would read to stay in step with the
// ledger. Each event carries the transaction or payment as the API returns it.

export function readFeed(events) {
  const transactions = [];
  const payments = new Map();
  for (const event of events) {
    if (event.type === 'transaction.posted') transactions.push(event.data);
    else if (event.type.startsWith('payment.')) payments.set(event.data.id, event.data);
  }
  return {
    // Newest first, as a person reads a statement.
    transactions: transactions.reverse(),
    payments: [...payments.values()].reverse(),
  };
}

/** A transaction's title for the journal: a reversal names what it reverses. */
export function titleOf(transaction, transactions) {
  if (transaction.reversesTransactionId === null) return transaction.description;
  const original = transactions.find((other) => other.id === transaction.reversesTransactionId);
  return original === undefined ? transaction.description : `Reversal: ${original.description}`;
}
