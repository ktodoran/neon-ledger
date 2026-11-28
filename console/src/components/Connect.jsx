import { useId, useState } from 'react';
import { localAddress } from '../ledger/http.js';

const DEFAULT_ADDRESS = 'http://localhost:5080';

const START = `docker compose up -d --wait
dotnet run --project src/NeonLedger.Api -- migrate
dotnet run --project src/NeonLedger.Api -- keys create console
dotnet run --project src/NeonLedger.Api`;

/** Why a connection did not happen, in terms of what to do about it. */
function explain(address, status) {
  if (status === 401) {
    return `The ledger at ${address} did not accept that key. Paste the whole key, starting with nl_, as “keys create” printed it. A revoked key does not work either.`;
  }
  if (status !== 0) {
    return `The ledger at ${address} answered ${String(status)}. It is running but cannot reach its database.`;
  }
  const reasons = `Nothing answered at ${address}. Either no ledger is running there, or it does not accept requests from this page (${window.location.origin}). The ledger lists the pages it accepts under Cors:AllowedOrigins.`;
  // A page served over https has to be allowed onto the local network first,
  // and an attempt made while the browser is still asking fails like any other.
  return window.location.protocol === 'https:'
    ? `${reasons} If your browser has just asked for permission to reach your local network, allow it and connect again.`
    : reasons;
}

/** The form for switching from the simulation to a ledger running on this machine. */
export function Connect({ busy, initialAddress, onConnect, onClose }) {
  const id = useId();
  const [address, setAddress] = useState(initialAddress ?? DEFAULT_ADDRESS);
  // Held here while it is typed, and handed to the ledger client on connect.
  // It is never written to the address bar or to browser storage.
  const [key, setKey] = useState('');
  const [error, setError] = useState(null);

  async function submit(event) {
    event.preventDefault();
    const local = localAddress(address);
    if (local === null) {
      setError('Enter an http address on this machine, such as http://localhost:5080.');
      return;
    }
    if (key.trim() === '') {
      setError('Paste an API key. The ledger refuses every request without one.');
      return;
    }
    const result = await onConnect(local, key.trim());
    if (!result.connected) setError(explain(local, result.status));
  }

  return (
    <section className="connect" aria-labelledby={`${id}-title`}>
      <div className="connect-about">
        <h2 id={`${id}-title`}>Connect a real ledger</h2>
        <p>
          The console can send its requests to a Neon Ledger running on this machine in place of the
          simulation. Start one from the repository, and create a key for the console:
        </p>
        <pre>
          <code>{START}</code>
        </pre>
        <p className="hint">
          The third command prints an API key, once. The console only connects to an address on this
          machine, so the key is not sent anywhere else, and it is forgotten when this page closes.
        </p>
        {window.location.protocol === 'https:' && (
          <p className="hint">
            From this public page, Chrome and Edge first ask for permission to reach your local
            network. Safari blocks the request altogether: run the console locally there.
          </p>
        )}
      </div>
      <form onSubmit={submit} noValidate>
        <div className="field">
          <label htmlFor={id}>Ledger address</label>
          <input
            id={id}
            className="mono"
            value={address}
            onChange={(event) => setAddress(event.target.value)}
            autoComplete="off"
            spellCheck="false"
            aria-describedby={error ? `${id}-error` : undefined}
          />
        </div>
        <div className="field">
          <label htmlFor={`${id}-key`}>API key</label>
          <input
            id={`${id}-key`}
            className="mono"
            type="password"
            value={key}
            onChange={(event) => setKey(event.target.value)}
            placeholder="nl_…"
            autoComplete="off"
            spellCheck="false"
            aria-describedby={error ? `${id}-error` : undefined}
          />
        </div>
        {error && (
          <p className="field-error" id={`${id}-error`} role="alert">
            {error}
          </p>
        )}
        <div className="row">
          <button type="submit" className="btn btn-primary" disabled={busy}>
            Connect
          </button>
          <button type="button" className="btn btn-quiet" onClick={onClose}>
            Stay on the simulation
          </button>
        </div>
      </form>
    </section>
  );
}
