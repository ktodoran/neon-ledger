// @vitest-environment happy-dom
import { cleanup, render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it } from 'vitest';
import { App } from '../src/App.jsx';
import { createConsoleStore } from '../src/store.js';

afterEach(cleanup);

async function open() {
  const store = createConsoleStore();
  await store.reset();
  render(<App store={store} />);
  return { store, user: userEvent.setup() };
}

const panel = (name) => screen.getByRole('region', { name });
const account = (name) => screen.getByRole('heading', { name }).closest('li');
const outcomeIn = (region) => within(region).getByRole('status');

describe('the tour', () => {
  it('runs all six steps against the ledger and ends with consistent books', async () => {
    const { user } = await open();
    await user.click(screen.getByRole('button', { name: 'Start the tour' }));
    const tour = screen.getByRole('region', { name: 'Put money in' });
    const press = (name) => user.click(within(tour).getByRole('button', { name }));
    const said = () => within(tour).getByRole('status').textContent;

    await press('Deposit $250.00');
    expect(said()).toContain('201 Created');
    await press('Next: send it again');

    await press('Send it again with the same key');
    expect(said()).toContain('Replayed');
    expect(said()).toContain('Jordan still has $250.00');
    await press('Next: money on hold');

    await press('Create payment');
    expect(said()).toContain('Balance $250.00, held $120.00, available $130.00');
    expect(within(account('Jordan checking')).getByText('$130.00')).toBeTruthy();
    await press('Next: spend it twice');

    await press('Withdraw $200.00');
    expect(said()).toContain('422 insufficient_funds');
    await press('Next: settle it');

    await press('Settle it');
    expect(said()).toContain('Balance $130.00, held $0.00');
    await press('Return it');
    expect(said()).toContain('Returned · R01');
    expect(said()).toContain('Balance is back to $250.00');
    expect(within(panel('Journal')).getByText('Reversal: Electric bill')).toBeTruthy();
    await press('Next: check the books');

    await press('Check the books');
    expect(said()).toContain('Consistent');
    expect(said()).toContain('no mismatches in 3 accounts');
    await press('Finish the tour');
    expect(screen.getByRole('button', { name: 'Run the tour again' })).toBeTruthy();
  });

  it('shows what the ledger said when a step does not go as written', async () => {
    const { user } = await open();
    await user.click(screen.getByRole('button', { name: 'Start the tour' }));
    const tour = screen.getByRole('region', { name: 'Put money in' });
    const press = (name) => user.click(within(tour).getByRole('button', { name }));
    for (const name of [
      'Deposit $250.00',
      'Next: send it again',
      'Send it again with the same key',
      'Next: money on hold',
      'Create payment',
      'Next: spend it twice',
      'Withdraw $200.00',
      'Next: settle it',
    ]) {
      await press(name);
    }
    // The visitor cancels the tour's payment behind its back.
    await user.click(screen.getByRole('button', { name: 'Cancel Electric bill' }));

    await press('Settle it');

    expect(within(tour).getByRole('status').textContent).toContain('409 invalid_payment_state');
    expect(within(tour).getByRole('status').textContent).toContain('it is canceled, not pending');
  });
});

describe('moving money', () => {
  it('posts a deposit, and replays it when sent again with the same key', async () => {
    const { user } = await open();
    const composer = panel('Move money');

    await user.click(within(composer).getByRole('button', { name: 'Post deposit' }));
    expect(outcomeIn(composer).textContent).toContain('201 Created');
    expect(outcomeIn(composer).textContent).toContain('$250.00 on each side');

    await user.click(
      within(composer).getByRole('button', { name: 'Send it again with the same key' }),
    );
    expect(outcomeIn(composer).textContent).toContain('201 Created · replayed');
    expect(within(panel('Journal')).getAllByText('Paycheck')).toHaveLength(1);
    expect(within(account('Jordan checking')).getAllByText('$250.00')).toHaveLength(2);
  });

  it('gives a changed request a new key', async () => {
    const { user } = await open();
    const composer = panel('Move money');
    const key = within(composer).getByLabelText('Idempotency key');
    const first = key.value;

    await user.click(within(composer).getByRole('button', { name: 'Post deposit' }));
    expect(key.value).toBe(first);
    await user.type(within(composer).getByLabelText('Amount'), '0');

    expect(key.value).not.toBe(first);
    expect(
      within(composer).getByRole('button', { name: 'Send it again with the same key' }).disabled,
    ).toBe(true);
  });

  it('does not send an amount it cannot read', async () => {
    const { user, store } = await open();
    const composer = panel('Move money');
    const amount = within(composer).getByLabelText('Amount');

    await user.clear(amount);
    await user.type(amount, '12.345');
    await user.click(within(composer).getByRole('button', { name: 'Post deposit' }));

    expect(within(composer).getByRole('alert').textContent).toBe('Enter an amount such as 120.00.');
    expect(store.getSnapshot().events).toHaveLength(1);
  });

  it('holds an ACH payment, then gives the money back when it is canceled', async () => {
    const { user } = await open();
    const composer = panel('Move money');

    await user.click(within(composer).getByRole('radio', { name: 'Payment' }));
    await user.click(within(composer).getByRole('button', { name: 'Create payment' }));

    // Riley has the money, so the payment starts there.
    expect(outcomeIn(composer).textContent).toContain('$250.00 is held on Riley savings');
    const payments = panel('Payments');
    expect(within(payments).getByText('Pending')).toBeTruthy();
    expect(within(account('Riley savings')).getByText('$1,590.00')).toBeTruthy();

    await user.click(within(payments).getByRole('button', { name: 'Cancel Electric bill' }));
    expect(within(payments).getByText('Canceled')).toBeTruthy();
    expect(outcomeIn(payments).textContent).toContain('Nothing was posted');
    expect(within(payments).getByText('Final')).toBeTruthy();
  });
});

describe('trying to break it', () => {
  it('lets one of two racing withdrawals through', async () => {
    const { user, store } = await open();
    const trials = panel('Try to break it');

    await user.click(within(trials).getByRole('button', { name: 'Race them' }));

    expect(outcomeIn(trials).textContent).toContain('1 posted · 1 refused');
    expect(outcomeIn(trials).textContent).toContain('one after the other');
    expect(store.getSnapshot().books.consistent).toBe(true);
  });

  it('refuses an entry that is off by a cent', async () => {
    const { user } = await open();
    const trials = panel('Try to break it');

    await user.click(within(trials).getByRole('button', { name: 'Post it' }));

    expect(outcomeIn(trials).textContent).toContain('400 validation_failed');
    expect(outcomeIn(trials).textContent).toContain('Debits (1000) must equal credits (999).');
  });
});

describe('the rest of the console', () => {
  it('opens an account', async () => {
    const { user } = await open();
    const accounts = panel('Accounts');

    await user.click(within(accounts).getByRole('button', { name: 'Open account' }));
    await user.type(within(accounts).getByLabelText('Account name'), 'Sam checking');
    await user.click(within(accounts).getByRole('button', { name: 'Open account' }));

    expect(account('Sam checking')).toBeTruthy();
  });

  it('starts over with a new ledger', async () => {
    const { user, store } = await open();
    await user.click(within(panel('Move money')).getByRole('button', { name: 'Post deposit' }));
    expect(store.getSnapshot().events).toHaveLength(2);

    await user.click(screen.getByRole('button', { name: 'Start over' }));

    expect(store.getSnapshot().events).toHaveLength(1);
    expect(within(panel('Move money')).getByRole('status').textContent).toBe('');
  });
});
