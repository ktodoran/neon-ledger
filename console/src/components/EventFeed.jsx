import { titleOf } from '../ledger/view.js';
import { Panel } from './Panel.jsx';

const SHOWN = 60;

const KIND = {
  'transaction.posted': 'good',
  'payment.created': 'held',
  'payment.settled': 'good',
  'payment.canceled': 'quiet',
  'payment.returned': 'warn',
};

export function EventFeed({ events, transactions }) {
  const shown = events.slice(-SHOWN).reverse();
  return (
    <Panel
      title="Event feed"
      note="In commit order. Another system reads this to stay in step."
      className="feed"
    >
      <div className="scroll" tabIndex={0} role="group" aria-label="Events">
        {shown.length === 0 && <p className="empty">No events yet.</p>}
        <ol className="feed-list">
          {shown.map((event) => (
            <li key={event.cursor}>
              <span className="cursor amount">{event.cursor}</span>
              <span className={`event-type tone-${KIND[event.type] ?? 'quiet'}`}>{event.type}</span>
              <span className="event-about">
                {event.type === 'transaction.posted'
                  ? titleOf(event.data, transactions)
                  : event.data.description}
              </span>
            </li>
          ))}
        </ol>
      </div>
    </Panel>
  );
}
