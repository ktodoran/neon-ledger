import { describe, expect, it } from 'vitest';
import { createHttpLedger, localAddress } from '../src/ledger/http.js';

/** A fetch that answers from a list and remembers what it was asked. */
function fakeFetch(...answers) {
  const calls = [];
  const fetch = async (url, options) => {
    calls.push({ url, ...options });
    const answer = answers.shift();
    if (answer instanceof Error) throw answer;
    const { status = 200, body = null, headers = {} } = answer;
    return {
      status,
      headers: new Headers(headers),
      text: async () => (body === null ? '' : JSON.stringify(body)),
    };
  };
  return { fetch, calls };
}

describe('the addresses the console will connect to', () => {
  it.each([
    ['http://localhost:5080', 'http://localhost:5080'],
    [' http://127.0.0.1:5080/ ', 'http://127.0.0.1:5080'],
    ['http://[::1]:5080/v1/accounts', 'http://[::1]:5080'],
  ])('accepts %j', (text, origin) => {
    expect(localAddress(text)).toBe(origin);
  });

  it.each([
    'https://ledger.example.com',
    'http://192.168.1.20:5080',
    'http://localhost.example.com:5080',
    'https://localhost:5080',
    'localhost:5080',
    '',
  ])('refuses %j', (text) => {
    expect(localAddress(text)).toBeNull();
  });
});

describe('a ledger over HTTP', () => {
  it('sends a write with its key, and reads the response', async () => {
    const { fetch, calls } = fakeFetch({ status: 201, body: { id: 't1' } });
    const ledger = createHttpLedger('http://localhost:5080', { fetch });

    const result = await ledger.postTransaction({ description: 'Deposit', entries: [] }, 'k1');

    expect(result).toEqual({ status: 201, body: { id: 't1' } });
    expect(calls[0]).toEqual({
      url: 'http://localhost:5080/v1/transactions',
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'Idempotency-Key': 'k1' },
      body: '{"description":"Deposit","entries":[]}',
    });
  });

  it('says when the ledger marked a response as a replay', async () => {
    const { fetch } = fakeFetch({
      status: 201,
      body: { id: 't1' },
      headers: { 'Idempotent-Replayed': 'true' },
    });
    const ledger = createHttpLedger('http://localhost:5080', { fetch });

    expect((await ledger.settlePayment('p1', 'k1')).replayed).toBe(true);
  });

  it('reports a request that could not be delivered as an answer, not an exception', async () => {
    const { fetch } = fakeFetch(new TypeError('Failed to fetch'));
    const ledger = createHttpLedger('http://localhost:5080', { fetch });

    const result = await ledger.integrity();

    expect(result.status).toBe(0);
    expect(result.body.code).toBe('ledger_unreachable');
  });

  it('reads every page of accounts', async () => {
    const { fetch, calls } = fakeFetch(
      { body: { items: [{ id: 'a' }, { id: 'b' }], nextAfter: 'b' } },
      { body: { items: [{ id: 'c' }], nextAfter: null } },
    );
    const ledger = createHttpLedger('http://localhost:5080', { fetch });

    const result = await ledger.listAccounts();

    expect(result.body.map((account) => account.id)).toEqual(['a', 'b', 'c']);
    expect(calls.map((call) => call.url)).toEqual([
      'http://localhost:5080/v1/accounts?limit=200',
      'http://localhost:5080/v1/accounts?limit=200&after=b',
    ]);
  });

  it('reads events from a cursor until a page comes back short, and keeps the last cursor', async () => {
    const full = Array.from({ length: 200 }, (_, index) => ({ cursor: `1-${String(index)}` }));
    const { fetch, calls } = fakeFetch(
      { body: { items: full, nextCursor: '1-199' } },
      { body: { items: [{ cursor: '2-0' }], nextCursor: '2-0' } },
    );
    const ledger = createHttpLedger('http://localhost:5080', { fetch });

    const result = await ledger.events('0-9');

    expect(result.body.items).toHaveLength(201);
    expect(result.body.nextCursor).toBe('2-0');
    expect(calls.map((call) => call.url)).toEqual([
      'http://localhost:5080/v1/events?limit=200&after=0-9',
      'http://localhost:5080/v1/events?limit=200&after=1-199',
    ]);
  });

  it('gives up on a list when a page fails', async () => {
    const { fetch } = fakeFetch(
      { body: { items: [{ id: 'a' }], nextAfter: 'a' } },
      new TypeError('Failed to fetch'),
    );
    const ledger = createHttpLedger('http://localhost:5080', { fetch });

    expect((await ledger.listAccounts()).status).toBe(0);
  });
});
