import React from 'react';
import ReactDOM from 'react-dom/client';
import { BrowserRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import App from './App';
import { LangProvider } from './i18n';
import { applyTheme, readTheme } from './components/theme';
import './styles/theme.css';
import './styles/auth-settings.css';
import './styles/calendar.css';
import './styles/wizard.css';
import './styles/masterdata.css';
import './styles/ai.css';
import './styles/rtl.css';

applyTheme(readTheme());   // before the first paint, so a saved dark choice does not flash light

const qc = new QueryClient({ defaultOptions: { queries: { refetchOnWindowFocus: false, retry: (n: number, e: unknown) => (e as { status?: number })?.status !== 401 && n < 1 } } });

ReactDOM.createRoot(document.getElementById('root')!).render(
  <React.StrictMode>
    <QueryClientProvider client={qc}>
      <BrowserRouter>
        <LangProvider><App /></LangProvider>
      </BrowserRouter>
    </QueryClientProvider>
  </React.StrictMode>,
);
