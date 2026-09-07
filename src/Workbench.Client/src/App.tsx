import { useCallback, useEffect, useState } from "react";
import { getSystem, type SystemInformation } from "./api/system";
import { TenantUsers } from "./features/admin/TenantUsers";
import { AuthProvider } from "./features/auth/AuthContext";
import { Recovery } from "./features/auth/Recovery";
import { Sessions } from "./features/auth/Sessions";
import { SignIn } from "./features/auth/SignIn";
import { useAuth } from "./features/auth/useAuth";
import { AddItem } from "./features/inventory/AddItem";
import { Collection, ItemDetails } from "./features/inventory/Collection";
import { AppearanceControl } from "./AppearanceControl";
import { useNavigation } from "./useNavigation";
import { DiscardDialog } from "./DiscardDialog";
import { Icon } from "./Icon";

function PublicAppearance() {
  return (
    <div className="appearance-bar">
      <AppearanceControl />
    </div>
  );
}
function Brand() {
  return (
    <div className="brand">
      <img src="/stag.svg" width="36" height="44" alt="" />
      <span className="wordmark">
        Workbench<small>The White Stag Collection</small>
      </span>
    </div>
  );
}
function SignedInApplication({ system }: { system: SystemInformation }) {
  const { identity, signOut, refresh } = useAuth();
  const navigation = useNavigation();
  const [signOutFailed, setSignOutFailed] = useState(false);
  const authLost = useCallback(() => {
    void refresh();
  }, [refresh]);
  if (!identity) return null;
  const canManageUsers = identity.permissions.includes("TenantUsersManage");
  const path = navigation.path;
  const collectionPath = path === "/" || path === "/inventory";
  return (
    <div className="app-shell">
      <a className="skip-link" href="#main">
        Skip to content
      </a>
      <header className="topbar">
        <Brand />
        <div className="identity-summary">
          <strong>{identity.tenantName}</strong>
          <AppearanceControl />
          <button
            className="quiet"
            type="button"
            onClick={() =>
              navigation.request(() => {
                void signOut().catch(() => setSignOutFailed(true));
              })
            }
          >
            Sign out
          </button>
        </div>
      </header>
      <div className="workspace-layout">
        <nav className="workspace-nav" aria-label="Workspace">
          <a
            href="/inventory"
            aria-current={
              path.startsWith("/inventory") || path === "/" ? "page" : undefined
            }
            onClick={navigation.follow}
          >
            <Icon name="inventory" />
            Inventory
          </a>
          <a
            href="/account"
            aria-current={path === "/account" ? "page" : undefined}
            onClick={navigation.follow}
          >
            <Icon name="account" />
            Account
          </a>
          {canManageUsers ? (
            <a
              href="/administration"
              aria-current={path === "/administration" ? "page" : undefined}
              onClick={navigation.follow}
            >
              <Icon name="administration" />
              Administration
            </a>
          ) : null}
        </nav>
        <main id="main" className="workspace">
          {signOutFailed ? (
            <p role="alert">We could not sign you out. Please try again.</p>
          ) : null}
          {collectionPath ? (
            <Collection
              key={path}
              follow={navigation.follow}
              onAuthLost={authLost}
            />
          ) : path === "/inventory/new" ? (
            <AddItem
              onDirtyChange={navigation.setDirty}
              onCancel={() => navigation.navigate("/inventory")}
              onAuthLost={authLost}
              onSaved={(item) => navigation.navigate(`/inventory/${item.id}`)}
            />
          ) : path.startsWith("/inventory/") ? (
            <ItemDetails
              key={path}
              id={path.slice("/inventory/".length)}
              follow={navigation.follow}
              onAuthLost={authLost}
            />
          ) : path === "/account" ? (
            <>
              <h1>Account</h1>
              <p className="lede">{identity.email}</p>
              <Sessions />
            </>
          ) : path === "/administration" && canManageUsers ? (
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
      <footer title={system.version}>
        Workbench {system.version.split("+")[0]}
      </footer>
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
function WorkbenchApplication() {
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
  if (systemFailed || status === "unavailable")
    return (
      <>
        <PublicAppearance />
        <main className="public-shell">
          <p role="alert">Workbench is temporarily unavailable.</p>
        </main>
      </>
    );
  if (status === "forbidden")
    return (
      <>
        <PublicAppearance />
        <main className="public-shell">
          <section className="auth-card">
            <h1>Access denied</h1>
            <p>Your account does not have access to this Workbench.</p>
          </section>
        </main>
      </>
    );
  if (status === "signed-out")
    return (
      <>
        <PublicAppearance />
        <main className="public-shell">
          <Brand />
          <SignIn />
        </main>
      </>
    );
  if (status === "loading" || !system || !identity)
    return (
      <>
        <PublicAppearance />
        <main className="public-shell">
          <p role="status">Loading Workbench…</p>
        </main>
      </>
    );
  return <SignedInApplication key={identity.userId} system={system} />;
}
export function App({
  recoveryToken = null,
}: {
  recoveryToken?: string | null;
}) {
  return (
    <>
      {window.location.pathname === "/recover" ? (
        <>
          <PublicAppearance />
          <Recovery token={recoveryToken} />
        </>
      ) : window.location.pathname === "/invite" ? (
        <>
          <PublicAppearance />
          <Recovery invitation token={recoveryToken} />
        </>
      ) : (
        <AuthProvider>
          <WorkbenchApplication />
        </AuthProvider>
      )}
    </>
  );
}
