// A real Neon Ledger, over HTTP, as the caller its API key belongs to.
//
// The same interface as the simulation, and the same result shape:
// { status, body } with `replayed: true` when the ledger says the response is a
// replay. A request the browser could not deliver comes back as status 0 with
// the code `ledger_unreachable`, so the console has one way to show a failure.

const PAGE = 200;

/**
 * The addresses the console will connect to: a ledger on this machine, and
 * nothing else. The ledger has no authentication, and this page should not be
 * a way to send requests to somebody else's server.
 */
export function localAddress(text) {
  let url;
  try {
    url = new URL(text.trim());
  } catch {
    return null;
  }
  const local = ['localhost', '127.0.0.1', '[::1]'].includes(url.hostname);
  return local && url.protocol === 'http:' ? url.origin : null;
}

export function createHttpLedger(
  baseUrl,
  { key: apiKey = null, fetch = globalThis.fetch.bind(globalThis) } = {},
) {
  async function send(method, path, { body, key } = {}) {
    const headers = {};
    if (apiKey !== null) headers.Authorization = `Bearer ${apiKey}`;
    if (body !== undefined) headers['Content-Type'] = 'application/json';
    if (key !== undefined) headers['Idempotency-Key'] = key;
    try {
      const response = await fetch(`${baseUrl}${path}`, {
        method,
        headers,
        body: body === undefined ? undefined : JSON.stringify(body),
      });
      const text = await response.text();
      const result = { status: response.status, body: text === '' ? null : JSON.parse(text) };
      if (response.headers.get('Idempotent-Replayed') === 'true') result.replayed = true;
      return result;
    } catch {
      return {
        status: 0,
        body: {
          code: 'ledger_unreachable',
          detail: `The ledger at ${baseUrl} did not answer.`,
        },
      };
    }
  }

  /** Follows a paged list to its end. `next` returns the path of the page after this one, or null. */
  async function all(first, next) {
    const items = [];
    let last = null;
    for (let path = first; path !== null; path = next(last.body)) {
      last = await send('GET', path);
      if (last.status !== 200) return last;
      items.push(...last.body.items);
    }
    return { status: 200, body: items, last: last.body };
  }

  return {
    mode: 'connected',
    address: baseUrl,

    health: () => send('GET', '/health'),

    listAccounts() {
      return all(`/v1/accounts?limit=${String(PAGE)}`, (page) =>
        page.nextAfter === null
          ? null
          : `/v1/accounts?limit=${String(PAGE)}&after=${page.nextAfter}`,
      );
    },

    openAccount: (request) => send('POST', '/v1/accounts', { body: request }),

    postTransaction: (request, key) => send('POST', '/v1/transactions', { body: request, key }),

    createPayment: (request, key) => send('POST', '/v1/payments', { body: request, key }),

    settlePayment: (id, key) =>
      send('POST', `/v1/payments/${encodeURIComponent(id)}/settle`, { key }),

    cancelPayment: (id, key) =>
      send('POST', `/v1/payments/${encodeURIComponent(id)}/cancel`, { key }),

    returnPayment: (id, reason, key) =>
      send('POST', `/v1/payments/${encodeURIComponent(id)}/return`, { body: { reason }, key }),

    /** Every event after a cursor, read page by page until a page comes back short. */
    async events(after = null) {
      const pathFrom = (cursor) =>
        `/v1/events?limit=${String(PAGE)}${cursor === null ? '' : `&after=${encodeURIComponent(cursor)}`}`;
      const read = await all(pathFrom(after), (page) =>
        page.items.length < PAGE ? null : pathFrom(page.nextCursor),
      );
      return read.status === 200
        ? { status: 200, body: { items: read.body, nextCursor: read.last.nextCursor } }
        : read;
    },

    integrity: () => send('GET', '/v1/integrity'),
  };
}
