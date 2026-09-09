import {
  useCallback,
  useEffect,
  useLayoutEffect,
  useRef,
  useState,
  type MouseEvent,
  type ReactNode,
} from 'react';
import { getSystem, type SystemInformation } from './api/system';
import { TenantUsers } from './features/admin/TenantUsers';
import { AuthProvider } from './features/auth/AuthContext';
import { Recovery } from './features/auth/Recovery';
import { Sessions } from './features/auth/Sessions';
import { SignIn } from './features/auth/SignIn';
import { useAuth } from './features/auth/useAuth';
import { AddItem } from './features/inventory/AddItem';
import { Collection, ItemDetails } from './features/inventory/Collection';
import { CollectionMemory } from './features/inventory/collectionMemory';
import { ExportMemory } from './features/inventory/exportMemory';
import { ExportRecords } from './features/inventory/ExportRecords';
import { AppearanceControl } from './AppearanceControl';
import { useNavigation } from './useNavigation';
import { DiscardDialog } from './DiscardDialog';
import { Icon } from './Icon';
import { readAppearance } from './appearance';
import { Brand } from './Brand';

function PublicAppearance({ children }: { children: ReactNode }) {
  return <div className="appearance-bar">{children}</div>;
}
function SignedInApplication({
  system,
  appearance,
}: {
  system: SystemInformation;
  appearance: ReactNode;
}) {
  const { identity, signOut, refresh } = useAuth();
  const navigation = useNavigation();
  const [signOutFailed, setSignOutFailed] = useState(false);
  const [userMenuOpen, setUserMenuOpen] = useState(false);
  const [navigationCollapsed, setNavigationCollapsed] = useState(false);
  const userMenu = useRef<HTMLDivElement>(null);
  const userMenuTrigger = useRef<HTMLButtonElement>(null);
  useEffect(() => {
    if (!userMenuOpen) return;
    function dismiss(event: Event) {
      // A confirmation dialog returns focus to its invoking profile action.
      if (event.target instanceof Element && event.target.closest('dialog')) return;
      if (event.target instanceof Node && !userMenu.current?.contains(event.target)) {
        setUserMenuOpen(false);
      }
    }
    document.addEventListener('pointerdown', dismiss);
    document.addEventListener('focusin', dismiss);
    return () => {
      document.removeEventListener('pointerdown', dismiss);
      document.removeEventListener('focusin', dismiss);
    };
  }, [userMenuOpen]);
  const [collectionMemory] = useState(() => new CollectionMemory());
  const [archiveMemory] = useState(() => new CollectionMemory());
  const [exportMemory] = useState(() => new ExportMemory());
  useLayoutEffect(() => () => exportMemory.dispose(), [exportMemory]);
  const [origins] = useState(
    () => new Map<string, 'active' | 'archived'>(),
  );
  function followFromCollection(event: MouseEvent<HTMLAnchorElement>) {
    const normalClick =
      event.button === 0 &&
      !event.metaKey &&
      !event.ctrlKey &&
      !event.shiftKey &&
      !event.altKey;
    const destination = event.currentTarget.pathname;
    navigation.follow(event);
    if (
      normalClick &&
      /^\/inventory\/[^/]+$/.test(destination) &&
      destination !== '/inventory/archive' &&
      destination !== '/inventory/export' &&
      destination !== '/inventory/new'
    )
      origins.set(
        window.history.state.workbenchEntryId,
        navigation.path === '/inventory/archive' ? 'archived' : 'active',
      );
  }
  const authLost = useCallback(() => {
    void refresh();
  }, [refresh]);
  if (!identity) return null;
  const canManageUsers = identity.permissions.includes(
    'TenantUsersManage',
  );
  const path = navigation.path;
  const archivePath = path === '/inventory/archive';
  const collectionPath =
    path === '/' || path === '/inventory' || archivePath;
  return (
    <div className="app-shell">
      <a className="skip-link" href="#main">
        Skip to content
      </a>
      <div className={`workspace-layout${navigationCollapsed ? ' navigation-collapsed' : ''}`}>
        <nav className="workspace-nav" aria-label="Workspace">
          <div className="navigation-heading">
            <Brand />
            <button
              className="quiet navigation-toggle"
              type="button"
              aria-label={navigationCollapsed ? 'Expand navigation' : 'Collapse navigation'}
              aria-expanded={!navigationCollapsed}
              title={navigationCollapsed ? 'Expand navigation' : 'Collapse navigation'}
              onClick={() => {
                setUserMenuOpen(false);
                setNavigationCollapsed((collapsed) => !collapsed);
              }}
            >
              <Icon name="menu" />
            </button>
          </div>
          <a
            className="navigation-destination"
            title={navigationCollapsed ? 'Inventory' : undefined}
            href="/inventory"
            aria-current={
              path.startsWith('/inventory') || path === '/'
                ? 'page'
                : undefined
            }
            onClick={navigation.follow}
          >
            <Icon name="inventory" />
            <span className="navigation-label">Inventory</span>
          </a>
          <div className="navigation-secondary">
            {canManageUsers ? (
              <a
                className="navigation-destination"
                title={navigationCollapsed ? 'Administration' : undefined}
                href="/administration"
                aria-current={path === '/administration' ? 'page' : undefined}
                onClick={navigation.follow}
              >
                <Icon name="administration" />
                <span className="navigation-label">Administration</span>
              </a>
            ) : null}
          </div>
          <div
            ref={userMenu}
            className="user-menu"
            onKeyDown={(event) => {
              if (event.key === 'Escape') {
                setUserMenuOpen(false);
                userMenuTrigger.current?.focus();
              }
            }}
          >
            {/* Keep the appearance subscription active while the disclosure is hidden. */}
            <div id="user-actions" className="user-actions" hidden={!userMenuOpen}>
              <div className="profile-heading">
                <strong>{identity.tenantName}</strong>
                <span>{identity.email ?? 'Account'}</span>
              </div>
              <a
                className="profile-row"
                href="/account"
                aria-current={path === '/account' ? 'page' : undefined}
                onClick={navigation.follow}
              >
                <Icon name="account" />
                <span>Account</span>
                <Icon name="chevron" />
              </a>
              {appearance}
              <button
                className="quiet profile-row"
                type="button"
                onClick={() => navigation.request(() => {
                  void signOut().catch(() => setSignOutFailed(true));
                })}
              >
                <Icon name="sign-out" />
                <span>Sign out</span>
              </button>
              <div className="nav-version" title={system.version}>
                Workbench {system.version.split('+')[0]}
              </div>
            </div>
            <button
              ref={userMenuTrigger}
              className="quiet user-menu-trigger"
              type="button"
              aria-label="User menu"
              title={navigationCollapsed ? 'Account and appearance' : undefined}
              aria-describedby="user-email"
              aria-expanded={userMenuOpen}
              aria-controls={userMenuOpen ? 'user-actions' : undefined}
              onClick={() => setUserMenuOpen((open) => !open)}
            >
              <span className="user-avatar" aria-hidden="true">{identity.email?.slice(0, 1).toUpperCase() ?? 'W'}</span>
              <span className="user-menu-identity">
                <span id="user-email" className="user-email" title={identity.email ?? undefined}>{identity.email ?? 'Account'}</span>
              </span>
              <Icon name="chevron" />
            </button>
          </div>
        </nav>
        <main id="main" className="workspace">
          {signOutFailed ? (
            <p role="alert">
              We could not sign you out. Please try again.
            </p>
          ) : null}
          {collectionPath ? (
            <Collection
              key={path}
              memory={archivePath ? archiveMemory : collectionMemory}
              archived={archivePath}
              follow={followFromCollection}
              onAuthLost={authLost}
            />
          ) : path === '/inventory/export' ? (
            <ExportRecords memory={exportMemory} follow={navigation.follow} onAuthLost={authLost} />
          ) : path === '/inventory/new' ? (
            <AddItem
              onDirtyChange={navigation.setDirty}
              onCancel={() => navigation.navigate('/inventory')}
              onAuthLost={authLost}
              onSaved={(item) => {
                collectionMemory.invalidate();
                navigation.navigate(`/inventory/${item.id}`);
              }}
            />
          ) : path.startsWith('/inventory/') ? (
            <ItemDetails
              key={path}
              id={path.slice('/inventory/'.length)}
              memory={collectionMemory}
              archiveMemory={archiveMemory}
              origin={origins.get(navigation.entryId)}
              onDirtyChange={navigation.setDirty}
              follow={navigation.follow}
              onAuthLost={authLost}
            />
          ) : path === '/account' ? (
            <>
              <h1>Account</h1>
              <p className="lede">{identity.email}</p>
              <Sessions />
            </>
          ) : path === '/administration' && canManageUsers ? (
            <>
              <h1>Administration</h1>
              <TenantUsers />
            </>
          ) : (
            <>
              <h1>Page not found</h1>
              <a href="/inventory" onClick={navigation.follow}>
                Back to collection
              </a>
            </>
          )}
        </main>
      </div>
      {navigation.confirmation ? (
        <DiscardDialog
          uncertain={navigation.uncertain}
          keep={navigation.keep}
          discard={navigation.discard}
        />
      ) : null}
    </div>
  );
}
function WorkbenchApplication({ appearance, menuAppearance }: { appearance: ReactNode; menuAppearance: ReactNode }) {
  const { identity, status } = useAuth();
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
  if (systemFailed || status === 'unavailable')
    return (
      <>
        <PublicAppearance>{appearance}</PublicAppearance>
        <main className="public-shell">
          <section className="auth-card">
            <Brand />
            <p role="alert">Workbench is temporarily unavailable.</p>
          </section>
        </main>
      </>
    );
  if (status === 'forbidden')
    return (
      <>
        <PublicAppearance>{appearance}</PublicAppearance>
        <main className="public-shell">
          <section className="auth-card">
            <Brand />
            <h1>Access denied</h1>
            <p>Your account does not have access to this Workbench.</p>
          </section>
        </main>
      </>
    );
  if (status === 'signed-out')
    return (
      <div className="sign-in-page">
        <PublicAppearance>{appearance}</PublicAppearance>
        <main className="sign-in-shell">
          <SignIn />
        </main>
      </div>
    );
  if (status === 'loading' || !system || !identity)
    return (
      <>
        <PublicAppearance>{appearance}</PublicAppearance>
        <main className="public-shell">
          <section className="auth-card">
            <Brand />
            <p role="status">Loading Workbench…</p>
          </section>
        </main>
      </>
    );
  return (
    <SignedInApplication
      key={JSON.stringify([identity.userId, identity.tenantName])}
      system={system}
      appearance={menuAppearance}
    />
  );
}
export function App({
  recoveryToken = null,
}: {
  recoveryToken?: string | null;
}) {
  const [preference, setPreference] = useState(readAppearance);
  const appearance = (
    <AppearanceControl
      preference={preference}
      setPreference={setPreference}
    />
  );
  return (
    <>
      {window.location.pathname === '/recover' ? (
        <div className="sign-in-page">
          <PublicAppearance>{appearance}</PublicAppearance>
          <Recovery token={recoveryToken} />
        </div>
      ) : window.location.pathname === '/invite' ? (
        <>
          <PublicAppearance>{appearance}</PublicAppearance>
          <Recovery invitation token={recoveryToken} />
        </>
      ) : (
        <AuthProvider>
          <WorkbenchApplication appearance={appearance} menuAppearance={
            <AppearanceControl preference={preference} setPreference={setPreference} variant="menu" />
          } />
        </AuthProvider>
      )}
    </>
  );
}
