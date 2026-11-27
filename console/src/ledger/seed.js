// The accounts every new simulation starts with, so the console is not empty.

export async function seed(ledger) {
  const open = async (name, normalBalance, allowNegative) => {
    const { body } = await ledger.openAccount({
      name,
      currency: 'USD',
      normalBalance,
      allowNegative,
    });
    return body.id;
  };

  // Where money enters and leaves. Debit-normal, and allowed below zero, because
  // the bank on the other side of it is not modeled here.
  const settlement = await open('ACH settlement', 'debit', true);
  const jordan = await open('Jordan checking', 'credit', false);
  const riley = await open('Riley savings', 'credit', false);

  await ledger.postTransaction(
    {
      description: 'Opening deposit',
      entries: [
        { accountId: settlement, direction: 'debit', amount: 184000 },
        { accountId: riley, direction: 'credit', amount: 184000 },
      ],
    },
    'seed-opening-deposit',
  );

  return { settlement, jordan, riley };
}
