import '@fontsource-variable/inter';
import '@fontsource-variable/jetbrains-mono';
import '@fontsource-variable/sora';
import './styles.css';

import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { App } from './App.jsx';
import { localAddress } from './ledger/http.js';
import { createConsoleStore } from './store.js';

const store = createConsoleStore();

// ?ledger=http://localhost:5080 opens the connect panel with that address filled
// in. The key is typed there: it does not belong in a link.
const asked = new URLSearchParams(window.location.search).get('ledger');
const ledgerAddress = asked === null ? null : localAddress(asked);

void store.reset();

createRoot(document.getElementById('root')).render(
  <StrictMode>
    <App store={store} ledgerAddress={ledgerAddress} />
  </StrictMode>,
);
