// Runs one script against the simulation and against a real Neon Ledger, and
// reports every place the two answered differently.
//
//   npm run parity                          (the ledger at http://localhost:5080)
//   npm run parity -- http://localhost:5081
//
// Ids, times and cursors differ by nature, so they are replaced or dropped
// before comparing. Everything else has to match: status codes, refusal codes,
// messages, balances, the order and content of events.

import { isDeepStrictEqual } from 'node:util';
import { createHttpLedger } from '../src/ledger/http.js';
import { runScript } from '../src/ledger/script.js';
import { createSimulatedLedger } from '../src/ledger/sim.js';

// Set by one side only, or different for reasons that are not rules.
const DROPPED = new Set([
  'traceId',
  'postedAt',
  'createdAt',
  'updatedAt',
  'occurredAt',
  'cursor',
  // The real ledger counts every account it holds, not only this script's.
  'accountsChecked',
]);
const UUID = /[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/gi;

/** Rewrites a run so that two faithful ledgers produce the same value. */
function normalize(run) {
  const names = new Map();
  const nameOf = (id) => {
    if (!names.has(id)) names.set(id, `<id ${String(names.size + 1)}>`);
    return names.get(id);
  };
  const entryIds = new Map();
  const walk = (value, key) => {
    if (typeof value === 'string') return value.replace(UUID, nameOf);
    // Entry ids are a sequence shared with everything else in the ledger.
    if (key === 'id' && typeof value === 'number') {
      if (!entryIds.has(value)) entryIds.set(value, `<entry ${String(entryIds.size + 1)}>`);
      return entryIds.get(value);
    }
    if (Array.isArray(value)) return value.map((item) => walk(item));
    if (value !== null && typeof value === 'object') {
      return Object.fromEntries(
        Object.entries(value)
          .filter(([name]) => !DROPPED.has(name))
          .map(([name, item]) => [name, walk(item, name)]),
      );
    }
    return value;
  };
  return walk(run);
}

function differences(simulated, real, path = '') {
  if (isDeepStrictEqual(simulated, real)) return [];
  const both = (check) => check(simulated) && check(real);
  if (both(Array.isArray) && simulated.length === real.length) {
    return simulated.flatMap((item, index) =>
      differences(item, real[index], `${path}[${String(index)}]`),
    );
  }
  if (both((value) => value !== null && typeof value === 'object' && !Array.isArray(value))) {
    return [...new Set([...Object.keys(simulated), ...Object.keys(real)])].flatMap((name) =>
      differences(simulated[name], real[name], path === '' ? name : `${path}.${name}`),
    );
  }
  return [{ path, simulated, real }];
}

const address = process.argv[2] ?? 'http://localhost:5080';
const real = createHttpLedger(address);
if ((await real.health()).status !== 200) {
  console.error(`No ledger answered at ${address}. Start one, then run this again.`);
  process.exit(2);
}

// A new tag each run, so the real ledger has never seen these idempotency keys.
const tag = `parity-${Date.now().toString(36)}`;
const fromSimulation = normalize(await runScript(createSimulatedLedger(), { tag }));
const fromLedger = normalize(await runScript(real, { tag }));

// Steps are compared by name, so a difference reads as a sentence.
const byName = (run) => ({
  ...run,
  steps: Object.fromEntries(run.steps.map(({ name, ...rest }) => [name, rest])),
});
const found = differences(byName(fromSimulation), byName(fromLedger));

if (found.length === 0) {
  const { steps, events, accounts } = fromSimulation;
  console.log(
    `The simulation and the ledger at ${address} agree: ${String(steps.length)} requests, ` +
      `${String(events.length)} events, ${String(accounts.length)} accounts, and the integrity report.`,
  );
} else {
  console.error(`${String(found.length)} differences between the simulation and ${address}:\n`);
  for (const { path, simulated, real: actual } of found) {
    console.error(`  ${path}`);
    console.error(`    simulation: ${JSON.stringify(simulated)}`);
    console.error(`    ledger:     ${JSON.stringify(actual)}\n`);
  }
  process.exit(1);
}
