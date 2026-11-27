import { describe, expect, it } from 'vitest';
import { runScript } from '../src/ledger/script.js';
import { createSimulatedLedger } from '../src/ledger/sim.js';

// scripts/parity.js runs this script against a real ledger too and compares the
// two runs. That needs a server, so it has its own CI job. This test pins what
// the simulation answers, which is what the real ledger was last seen to answer.
describe('the parity script, against the simulation', () => {
  it('gets the expected answer to every request', async () => {
    const { steps } = await runScript(createSimulatedLedger(), { tag: 'test' });

    expect(
      steps.map((step) => [
        step.status,
        step.body?.code ?? step.body?.status ?? 'ok',
        step.replayed,
      ]),
    ).toEqual([
      [400, 'validation_failed', false],
      [201, 'ok', false],
      [201, 'ok', false],
      [201, 'ok', false],
      [201, 'ok', false],
      [201, 'ok', false],
      [201, 'ok', true],
      [422, 'idempotency_key_reused', false],
      [400, 'idempotency_key_required', false],
      [400, 'validation_failed', false],
      [400, 'validation_failed', false],
      [422, 'account_not_found', false],
      [422, 'currency_mismatch', false],
      [422, 'insufficient_funds', false],
      [422, 'insufficient_funds', true],
      [400, 'validation_failed', false],
      [201, 'pending', false],
      [201, 'pending', true],
      [422, 'insufficient_funds', false],
      [422, 'insufficient_funds', false],
      [409, 'invalid_payment_state', false],
      [200, 'settled', false],
      [200, 'settled', true],
      [409, 'invalid_payment_state', false],
      [409, 'invalid_payment_state', false],
      [400, 'validation_failed', false],
      [200, 'returned', false],
      [409, 'invalid_payment_state', false],
      [201, 'pending', false],
      [200, 'canceled', false],
      [409, 'invalid_payment_state', false],
      [201, 'settled', false],
      [409, 'invalid_payment_state', false],
      [422, 'insufficient_funds', false],
      [404, 'payment_not_found', false],
    ]);
  });

  it('leaves the books consistent, with the balances the script should end on', async () => {
    const { accounts, events, books } = await runScript(createSimulatedLedger(), { tag: 'test' });

    expect(books).toMatchObject({ consistent: true, debitsMinusCredits: 0 });
    expect(
      Object.fromEntries(accounts.map((account) => [account.name, account.available])),
    ).toEqual({ Settlement: 25000, Jordan: 21000, Riley: 4000, Euros: 0 });
    expect(events).toHaveLength(11);
  });
});
