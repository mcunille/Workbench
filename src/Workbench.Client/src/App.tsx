import { useEffect, useState } from 'react';
import { getSystem, type SystemInformation } from './api/system';
import { TenantUsers } from './features/admin/TenantUsers';
import { AuthProvider } from './features/auth/AuthContext';
import { Recovery } from './features/auth/Recovery';
import { Sessions } from './features/auth/Sessions';
import { SignIn } from './features/auth/SignIn';
import { useAuth } from './features/auth/useAuth';

const tenantUsersManage = 'TenantUsersManage';

function WorkbenchApplication() {
  const { identity, status, signOut } = useAuth();
  const [system, setSystem] = useState<SystemInformation>();
  const [systemFailed, setSystemFailed] = useState(false);

  useEffect(() => {
    let current = true;
    void getSystem().then(
      (result) => {
        if (current) setSystem(result);
      },
      () => {
        if (current) setSystemFailed(true);
      },
    );
    return () => {
      current = false;
    };
  }, []);

  if (systemFailed || status === 'unavailable') {
    return <main className="public-shell"><p role="alert">Workbench is temporarily unavailable.</p></main>;
  }

  if (status === 'forbidden') {
    return (
      <main className="public-shell">
        <section className="auth-card">
          <h1>Access denied</h1>
          <p>Your account does not have access to this Workbench.</p>
        </section>
      </main>
    );
  }

  if (status === 'signed-out') {
    return <main className="public-shell"><SignIn /></main>;
  }

  if (status === 'loading' || !system || !identity) {
    return <main className="public-shell"><p role="status">Loading Workbench…</p></main>;
  }

  const canManageUsers = identity.permissions.includes(tenantUsersManage);
  return (
    <div className="app-shell">
      <header className="topbar">
        <div>
          <p className="eyebrow">Portable operations workspace</p>
          <span className="wordmark">{system.name}</span>
        </div>
        <div className="identity-summary">
          <span><strong>{identity.tenantName}</strong><small>{identity.email}</small></span>
          <button className="secondary" type="button" onClick={() => void signOut()}>Sign out</button>
        </div>
      </header>
      <main className="workspace">
        <section className="welcome">
          <p className="eyebrow">Trusted session</p>
          <h1>Welcome to {identity.tenantName}</h1>
          <p className="lede">Your tenant is derived from your durable server session.</p>
        </section>
        <div className="panel-grid">
          <Sessions />
          {canManageUsers ? <TenantUsers /> : null}
        </div>
      </main>
      <footer>Workbench {system.version}</footer>
    </div>
  );
}

export function App({ recoveryToken = null }: { recoveryToken?: string | null }) {
  if (window.location.pathname === '/recover') return <Recovery token={recoveryToken} />;
  if (window.location.pathname === '/invite') return <Recovery invitation token={recoveryToken} />;
  return <AuthProvider><WorkbenchApplication /></AuthProvider>;
}
