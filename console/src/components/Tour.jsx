import { useState } from 'react';
import { TOUR } from '../tour.js';
import { Outcome } from './Outcome.jsx';

const INTRO = { phase: 'intro' };

function Dots({ step }) {
  return (
    <ol className="tour-dots" aria-hidden="true">
      {TOUR.map((entry, index) => (
        <li
          key={entry.title}
          className={index < step ? 'is-done' : index === step ? 'is-current' : undefined}
        />
      ))}
    </ol>
  );
}

/**
 * The guided tour. Starting it starts a new ledger, so every step begins from
 * a state the tour knows.
 */
export function Tour({ store, mode, busy, act, onStart }) {
  const [tour, setTour] = useState(INTRO);

  async function start() {
    const cast = await onStart();
    setTour(
      cast === null
        ? { phase: 'intro', failed: true }
        : { phase: 'running', step: 0, done: 0, outcome: null, cast, memory: {} },
    );
  }

  if (tour.phase !== 'running') {
    const finished = tour.phase === 'finished';
    return (
      <section className="tour tour-idle" aria-labelledby="tour-title">
        <div>
          <p className="eyebrow">{finished ? 'Tour finished' : 'Guided tour'}</p>
          <h2 id="tour-title">
            {finished ? 'The ledger is yours now' : 'Six claims, each one checked in front of you'}
          </h2>
          <p className="tour-body">
            {finished
              ? 'Everything below still works. Move money yourself, or try to break the books.'
              : 'Deposit money, retry a request, hold funds, overspend, settle and return a payment, then check that the books still balance. About two minutes.'}
            {!finished &&
              mode === 'connected' &&
              ' On this ledger the tour opens three new accounts and leaves the rest alone.'}
          </p>
          {tour.failed && (
            <Outcome
              outcome={{
                label: 'Not started',
                tone: 'warn',
                message:
                  'The ledger did not open the tour’s accounts. Check that it is still running, then try again.',
              }}
            />
          )}
        </div>
        <button type="button" className="btn btn-primary" onClick={start} disabled={busy}>
          {finished ? 'Run the tour again' : 'Start the tour'}
        </button>
      </section>
    );
  }

  const step = TOUR[tour.step];
  const action = step.actions[tour.done];
  const last = tour.step === TOUR.length - 1;

  async function perform() {
    const outcome = await act(() => action.run({ store, cast: tour.cast, memory: tour.memory }));
    setTour((current) => ({
      ...current,
      done: current.done + 1,
      outcome,
      look: action.look ?? step.look,
    }));
  }

  function advance() {
    setTour((current) =>
      last
        ? { phase: 'finished' }
        : { ...current, step: current.step + 1, done: 0, outcome: null, look: null },
    );
  }

  return (
    <section className="tour" aria-labelledby="tour-title">
      <div className="tour-top">
        <p className="eyebrow">
          Tour · step {tour.step + 1} of {TOUR.length}
        </p>
        <Dots step={tour.step} />
        <button type="button" className="btn btn-quiet btn-small" onClick={() => setTour(INTRO)}>
          Leave the tour
        </button>
      </div>
      <h2 id="tour-title">{step.title}</h2>
      <p className="tour-body">{step.body}</p>
      <div className="row">
        {action ? (
          <button type="button" className="btn btn-primary" onClick={perform} disabled={busy}>
            {action.label}
          </button>
        ) : (
          <button type="button" className="btn btn-primary" onClick={advance}>
            {step.next}
          </button>
        )}
      </div>
      <Outcome outcome={tour.outcome} />
      {tour.look && <p className="tour-look">Where to look: {tour.look}</p>}
    </section>
  );
}
