/** A response, as a status line and a sentence. Announced to screen readers when it changes. */
export function Outcome({ outcome, className = '' }) {
  return (
    <div className={`outcome ${className}`} role="status" aria-live="polite">
      {outcome && (
        <div className={`outcome-body tone-${outcome.tone}`}>
          <span className="outcome-label">{outcome.label}</span>
          <span className="outcome-message">{outcome.message}</span>
        </div>
      )}
    </div>
  );
}
