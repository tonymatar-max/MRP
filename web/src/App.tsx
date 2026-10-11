import { Fragment, useState } from 'react';
import { Link, NavLink, Navigate, Route, Routes } from 'react-router-dom';
import { USE_MOCK } from './api/client';
import { useRun, useStatus } from './api/hooks';
import { AuthGate, useAuth } from './components/Auth';
import { RunProvider, useSelectedRun } from './components/RunContext';
import { AiDrawer, AiPanelProvider } from './components/AiAssistant';
import { BrandMark } from './components/ui';
import Calendar from './pages/Calendar';
import Capacity from './pages/Capacity';
import Dashboard from './pages/Dashboard';
import Exceptions from './pages/Exceptions';
import Forecasts from './pages/Forecasts';
import MasterData from './pages/MasterData';
import PlannedOrders from './pages/PlannedOrders';
import Settings from './pages/Settings';

const NAV: { to: string; label: string; end?: boolean; adminOnly?: boolean; group: string }[] = [
  { to: '/', label: 'Dashboard', end: true, group: 'Overview' },
  { to: '/planned-orders', label: 'Planned Orders', group: 'Planning' },
  { to: '/exceptions', label: 'Exceptions', group: 'Planning' },
  { to: '/capacity', label: 'Capacity (CRP)', group: 'Planning' },
  { to: '/calendar', label: 'Calendar', group: 'Planning' },
  { to: '/master-data', label: 'Master data', group: 'Data' },
  { to: '/forecasts', label: 'Forecasts', group: 'Data' },
  { to: '/settings', label: 'Settings', adminOnly: true, group: 'Configuration' },
];

const DISMISS_KEY = 'cobalt-mrp-memory-banner-dismissed';
const readDismissed = () => { try { return sessionStorage.getItem(DISMISS_KEY) === '1'; } catch { return false; } };

function StatusBanner() {
  const { isAdmin } = useAuth();
  const status = useStatus().data;
  const [dismissed, setDismissed] = useState(readDismissed);
  if (!status || status.storage !== 'memory' || dismissed) return null;
  const dismiss = () => { setDismissed(true); try { sessionStorage.setItem(DISMISS_KEY, '1'); } catch { /* ignore */ } };
  return (
    <div className="banner warn" role="alert">
      <span className="banner-ico" aria-hidden="true">⚠</span>
      <div className="banner-text">
        <strong>Runs are not saved.</strong>{' '}
        {isAdmin
          ? <>The Cobalt database is not configured, so everything is held in memory and lost on restart. <Link to="/settings">Configure the Cobalt database in Settings</Link>.</>
          : <>The Cobalt database is not configured. Ask an administrator to set it up in Settings.</>}
      </div>
      <button type="button" className="btn sm" onClick={dismiss}>Dismiss</button>
    </div>
  );
}

function Sidebar() {
  const { isAdmin } = useAuth();
  const { runId } = useSelectedRun();
  const run = useRun(runId).data;
  const counts: Record<string, number | undefined> = {
    '/planned-orders': run?.plannedOrders.length,
    '/exceptions': run?.exceptions.filter((e) => e.actionStatus !== 'Done').length,
  };
  let lastGroup = '';
  return (
    <aside className="sidebar">
      <nav className="nav" aria-label="Main">
        {NAV.filter((n) => !n.adminOnly || isAdmin).map((n) => {
          const label = n.group !== lastGroup ? <div className="nav-label">{n.group}</div> : null;
          lastGroup = n.group;
          return (
            <Fragment key={n.to}>
              {label}
              <NavLink to={n.to} end={n.end} className={({ isActive }) => `nav-item${isActive ? ' active' : ''}`}>
                <span>{n.label}</span>
                {counts[n.to] != null && <span className="count">{counts[n.to]}</span>}
              </NavLink>
            </Fragment>
          );
        })}
      </nav>
      <div className="foot">© {new Date().getFullYear()} Solvep · Cobalt product family</div>
    </aside>
  );
}

function Shell() {
  const { user, isAdmin, signOut } = useAuth();
  return (
    <RunProvider>
      <AiPanelProvider>
      <div className="shell">
        <header className="topbar">
          <div className="brand">
            <BrandMark />
            <div className="product-title"><strong>Cobalt</strong><span className="product-sub">MRP</span></div>
          </div>
          <div className="spacer" />
          <span className={`env-chip ${USE_MOCK ? 'mock' : ''}`}><span className="led" />{USE_MOCK ? 'Mock data' : 'Live API'}</span>
          <div className="user-btn">
            <div className="avatar" aria-hidden="true">{user.username.slice(0, 1).toUpperCase()}</div>
            <div className="who"><b>{user.username}</b><small>{user.role}</small></div>
          </div>
          <button type="button" className="btn sm signout" onClick={signOut}>Sign out</button>
        </header>
        <Sidebar />
        <main className="main">
          <StatusBanner />
          <Routes>
            <Route path="/" element={<Dashboard />} />
            <Route path="/planned-orders" element={<PlannedOrders />} />
            <Route path="/exceptions" element={<Exceptions />} />
            <Route path="/capacity" element={<Capacity />} />
            <Route path="/calendar" element={<Calendar />} />
            <Route path="/master-data" element={<MasterData />} />
            <Route path="/forecasts" element={<Forecasts />} />
            <Route path="/settings" element={isAdmin ? <Settings /> : <Navigate to="/" replace />} />
            <Route path="*" element={<Navigate to="/" replace />} />
          </Routes>
        </main>
        <AiDrawer />
      </div>
      </AiPanelProvider>
    </RunProvider>
  );
}

export default function App() {
  return (
    <AuthGate>
      <Shell />
    </AuthGate>
  );
}
