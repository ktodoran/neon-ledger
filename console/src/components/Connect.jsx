import { useId, useState } from 'react';
import { localAddress } from '../ledger/http.js';

const DEFAULT_ADDRESS = 'http://localhost:5080';

const START = `docker compose up -d --wait
dotnet run --project src/NeonLedger.Api -- migrate
dotnet run --project src/NeonLedger.Api`;

/** Why a connection did not happen, in terms of what to do about it. */
function explain(address, status) {
  if (status === 0) {
    return `Nothing answered at ${address}. Either no ledger is running there, or it does not accept requests from this page (${window.location.origin}). The ledger lists the pages it accepts under Cors:AllowedOrigins.`;
  }
  return `The ledger at ${address} answered ${String(status)}. It is running but cannot reach its database.`;
}

/** The form for switching from the simulation to a ledger running on this machine. */
export function Connect({ busy, initial, onConnect, onClose }) {
  const id = useId();
  const [address, setAddress] = useState(initial?.address ?? DEFAULT_ADDRESS);
  const [error, setError] = useState(
    initial?.failed === undefined ? null : explain(initial.address, initial.failed),
  );

  async function submit(event) {
    event.preventDefault();
    const local = localAddress(address);
    if (local === null) {
      setError('Enter an http address on this machine, such as http://localhost:5080.');
      return;
    }
    const result = await onConnect(local);
    if (!result.connected) setError(explain(local, result.status));
  }

  return (
    <section className="connect" aria-labelledby={`${id}-title`}>
      <div className="connect-about">
        <h2 id={`${id}-title`}>Connect a real ledger</h2>
        <p>
          The console can send its requests to a Neon Ledger running on this machine in place of the
          simulation. Start one from the repository:
        </p>
        <pre>
          <code>{START}</code>
        </pre>
        <p className="hint">
          Only an address on this machine is accepted. The ledger has no authentication, so this
          page does not send requests anywhere else.
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
            aria-invalid={error !== null}
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
