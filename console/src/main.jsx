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

// ?ledger=http://localhost:5080 opens the console already connected. If nothing
// answers there, it opens on the simulation with the reason on screen.
async function open() {
  await store.reset();
  const asked = new URLSearchParams(window.location.search).get('ledger');
  const address = asked === null ? null : localAddress(asked);
  if (address === null) return null;
  const result = await store.connect(address);
  return result.connected ? null : { address, failed: result.status };
}

const failedConnection = await open();

createRoot(document.getElementById('root')).render(
  <StrictMode>
    <App store={store} failedConnection={failedConnection} />
  </StrictMode>,
);
