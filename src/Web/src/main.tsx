import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { BrowserRouter } from 'react-router-dom';
import App from './App';
import { api } from './api/client';
import { AuthProvider } from './auth/AuthProvider';
import { configureFormatting } from './format';
import './index.css';

const container = document.getElementById('root');

if (!container) {
  throw new Error('No #root element to mount into.');
}

// Currency and locale come from the server, so they are fetched before the first paint rather
// than rendered wrong and corrected. If the call fails the API is down and there is no data to
// format anyway — the app renders with its defaults and surfaces the real failure on its own.
try {
  configureFormatting(await api.config());
} catch (cause) {
  console.warn('Could not load /api/config; formatting falls back to defaults.', cause);
}

createRoot(container).render(
  <StrictMode>
    <BrowserRouter>
      <AuthProvider>
        <App />
      </AuthProvider>
    </BrowserRouter>
  </StrictMode>,
);
