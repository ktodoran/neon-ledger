import { useId } from 'react';

/** A titled section of the console. */
export function Panel({ title, note, action, className = '', children }) {
  const id = useId();
  return (
    <section className={`panel ${className}`} aria-labelledby={id}>
      <header className="panel-head">
        <div>
          <h2 id={id}>{title}</h2>
          {note && <p className="panel-note">{note}</p>}
        </div>
        {action}
      </header>
      {children}
    </section>
  );
}
