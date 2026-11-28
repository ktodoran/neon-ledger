// @vitest-environment happy-dom
import { cleanup, render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it } from 'vitest';
import { App } from '../src/App.jsx';
import { createSimulatedLedger } from '../src/ledger/sim.js';
import { createConsoleStore } from '../src/store.js';

// A stand-in for a real ledger: the simulation's rules behind the connected
// ledger's face. What the console does differently when connected is decided
// by `mode`, so this exercises all of it without a server.
function standIn({ answers = true } = {}) {
  const ledger = createSimulatedLedger();
  let checks = 0;
  return {
    ...ledger,
    mode: 'connected',
    address: 'http://localhost:5080',
    health: async () => (answers ? { status: 200, body: { status: 'ok' } } : { status: 0 }),
    integrity: () => {
      checks += 1;
      return ledger.integrity();
    },
    checks: () => checks,
  };
}

const stores = [];
afterEach(() => {
  cleanup();
  for (const store of stores.splice(0)) store.stop();
});

async function open(remote = standIn()) {
  const store = createConsoleStore({ connectTo: () => remote });
  stores.push(store);
  await store.reset();
  render(<App store={store} />);
  return { store, remote, user: userEvent.setup() };
}

async function connect(user, address = 'http://localhost:5080') {
  await user.click(screen.getByRole('button', { name: 'Connect a real ledger' }));
  const field = screen.getByLabelText('Ledger address');
  await user.clear(field);
  await user.type(field, address);
  await user.click(screen.getByRole('button', { name: 'Connect' }));
}

const panel = (name) => screen.getByRole('region', { name });
const accountNames = () =>
  within(panel('Accounts'))
    .getAllByRole('heading', { level: 3 })
    .map((heading) => heading.textContent);

describe('connecting to a real ledger', () => {
  it('shows what that ledger holds, which may be nothing', async () => {
    const { user } = await open();
    await connect(user);

    expect(screen.getByText('Connected to localhost:5080')).toBeTruthy();
    expect(screen.getByText('Books not checked yet')).toBeTruthy();
    expect(within(panel('Accounts')).queryAllByRole('heading', { level: 3 })).toHaveLength(0);
    expect(within(panel('Move money')).getByText(/takes a settlement account/)).toBeTruthy();
  });

  it('will not connect to an address that is not on this machine', async () => {
    const { user, store } = await open();
    await connect(user, 'https://ledger.example.com');

    expect(screen.getByRole('alert').textContent).toContain('on this machine');
    expect(store.getSnapshot().mode).toBe('simulated');
  });

  it('stays on the simulation, and says why, when nothing answers', async () => {
    const { user, store } = await open(standIn({ answers: false }));
    await connect(user);

    expect(screen.getByRole('alert').textContent).toContain(
      'Nothing answered at http://localhost:5080',
    );
    expect(screen.getByRole('alert').textContent).not.toContain('permission');
    expect(store.getSnapshot().mode).toBe('simulated');
    expect(screen.getByText('Simulated in this browser')).toBeTruthy();
  });

  it('mentions the browser’s permission prompt when the page is served over https', async () => {
    window.happyDOM.setURL('https://ktodoran.github.io/neon-ledger/');
    try {
      const { user } = await open(standIn({ answers: false }));
      await connect(user);

      expect(screen.getByRole('alert').textContent).toContain('allow it and connect again');
    } finally {
      window.happyDOM.setURL('http://localhost:3000/');
    }
  });

  it('opens the tour its own accounts and leaves the others alone', async () => {
    const { user, remote } = await open();
    await remote.openAccount({ name: 'Jordan checking', currency: 'USD', normalBalance: 'credit' });
    await connect(user);
    expect(accountNames()).toEqual(['Jordan checking']);

    await user.click(screen.getByRole('button', { name: 'Start the tour' }));

    expect(accountNames()).toEqual([
      'Jordan checking 2',
      'Riley savings',
      'Jordan checking',
      'ACH settlement',
    ]);
    const tour = screen.getByRole('region', { name: 'Put money in' });
    await user.click(within(tour).getByRole('button', { name: 'Deposit $250.00' }));
    expect(within(tour).getByRole('status').textContent).toContain('201 Created');
    const cards = within(panel('Accounts')).getAllByRole('listitem');
    expect(within(cards[0]).getAllByText('$250.00')).toHaveLength(2);
    expect(within(cards[2]).getAllByText('$0.00')).toHaveLength(3);
  });

  it('checks the books when asked, not after every request', async () => {
    const { user, remote } = await open();
    await connect(user);
    await user.click(screen.getByRole('button', { name: 'Start the tour' }));
    const tour = screen.getByRole('region', { name: 'Put money in' });
    await user.click(within(tour).getByRole('button', { name: 'Deposit $250.00' }));
    expect(remote.checks()).toBe(0);

    await user.click(screen.getByRole('button', { name: 'Check the books' }));

    expect(remote.checks()).toBe(1);
    expect(screen.getByText(/Books balance, checked at/)).toBeTruthy();
  });

  it('goes back to a new simulation on disconnect', async () => {
    const { user, store } = await open();
    await connect(user);

    await user.click(screen.getByRole('button', { name: 'Disconnect' }));

    expect(store.getSnapshot().mode).toBe('simulated');
    expect(accountNames()).toEqual(['Jordan checking', 'Riley savings', 'ACH settlement']);
  });
});
