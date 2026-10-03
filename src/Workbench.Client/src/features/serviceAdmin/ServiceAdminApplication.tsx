import { useCallback, useEffect, useState, type ReactNode } from 'react';
import { getServiceAdminIdentity, signInServiceAdmin, signOutServiceAdmin, type CurrentServiceAdminResponse } from '../../api/serviceAdmin';
import { Brand } from '../../Brand';
import { useNavigation } from '../../useNavigation';
import { ServiceAdminSignIn } from './ServiceAdminSignIn';
import { GemCatalog, GemDetail } from './GemCatalog';
import { GemEditor } from './GemEditor';
import { DiscardDialog } from '../../DiscardDialog';
import type { GemReferenceDraftResponse, GemReferenceDraftSelection } from '../../api/gemReferenceAdmin';
import './service-admin.css';

export function ServiceAdminApplication({ appearance }: { appearance: ReactNode }) {
  const navigation = useNavigation();
  const [identity, setIdentity] = useState<CurrentServiceAdminResponse | null>(null);
  const [status, setStatus] = useState<'loading' | 'signed-out' | 'signed-in' | 'unavailable'>('loading');
  const [attempt, setAttempt] = useState(0);
  const [signOutPending, setSignOutPending] = useState(false);
  const [signOutFailed, setSignOutFailed] = useState(false);
  const [selectedDrafts, setSelectedDrafts] = useState<GemReferenceDraftSelection[]>([]);
  const [writesSuspended, setWritesSuspended] = useState(false);
  const [sessionRevision, setSessionRevision] = useState(0);
  const { replace } = navigation;
  const { setDirty } = navigation;
  const authLost = useCallback(() => setWritesSuspended(true), []);
  const dirtyChanged = useCallback((dirty: boolean, uncertain = false) => setDirty(dirty, uncertain), [setDirty]);
  const draftSaved = useCallback((draft: GemReferenceDraftResponse) => {
    setSelectedDrafts((selection) => selection.map((item) => item.draftId === draft.id ? { ...item, expectedDraftRowVersion: draft.rowVersion } : item));
    replace(`/service-admin/gem-reference/drafts/${encodeURIComponent(draft.id)}`);
  }, [replace]);

  useEffect(() => {
    let current = true;
    void getServiceAdminIdentity().then((next) => {
      if (!current) return;
      setIdentity(next);
      setStatus(next ? 'signed-in' : 'signed-out');
      if (next && (window.location.pathname === '/service-admin' || window.location.pathname === '/service-admin/sign-in')) {
        replace('/service-admin/gem-reference');
      }
    }).catch(() => { if (current) setStatus('unavailable'); });
    return () => { current = false; };
  }, [attempt, replace]);

  async function signIn(email: string, password: string) {
    await signInServiceAdmin(email, password);
    const next = await getServiceAdminIdentity();
    if (!next) throw new Error('Service-admin identity was not confirmed.');
    if (identity && identity.accountId !== next.accountId) {
      setSelectedDrafts([]);
      setDirty(false, false);
      replace('/service-admin/gem-reference');
    }
    setIdentity(next);
    setStatus('signed-in');
    setWritesSuspended(false);
    setSessionRevision((value) => value + 1);
    if (navigation.path === '/service-admin' || navigation.path === '/service-admin/sign-in') replace('/service-admin/gem-reference');
  }

  async function signOut() {
    setSignOutPending(true);
    setSignOutFailed(false);
    try {
      await signOutServiceAdmin();
      setIdentity(null);
      setSelectedDrafts([]);
      setDirty(false, false);
      setStatus('signed-out');
      replace('/service-admin/sign-in');
    } catch {
      setSignOutFailed(true);
    } finally {
      setSignOutPending(false);
    }
  }

  if (status !== 'signed-in' || !identity) {
    return (
      <div className="sign-in-page">
        <div className="appearance-bar">{appearance}</div>
        <main className="sign-in-shell">
          {status === 'signed-out' ? <ServiceAdminSignIn signIn={signIn} /> : (
            <section className="auth-card">
              <Brand />
              {status === 'unavailable' ? <>
                <p role="alert">Service administration is temporarily unavailable.</p>
                <button type="button" onClick={() => { setStatus('loading'); setAttempt((value) => value + 1); }}>Retry</button>
              </> : <p role="status">Checking service-admin session…</p>}
            </section>
          )}
        </main>
      </div>
    );
  }

  return (
    <div className="app-shell service-admin-shell">
      <a className="skip-link" href="#main">Skip to content</a>
      <div className="workspace-layout">
        <nav className="workspace-nav service-admin-nav" aria-label="Service administration">
          <div className="navigation-heading"><Brand /></div>
          <a className="navigation-destination" href="/service-admin/gem-reference" aria-current="page" onClick={navigation.follow}>Gem reference</a>
          <p className="service-admin-identity">{identity.email}</p>
          {appearance}
          <button type="button" disabled={signOutPending} onClick={() => navigation.request(() => void signOut())}>{signOutPending ? 'Signing out…' : 'Sign out'}</button>
          {signOutFailed ? <p className="form-message error" role="alert">We could not sign you out. Try again.</p> : null}
        </nav>
        <div className="workspace-sheet">
          <main id="main" className="workspace">
            {writesSuspended ? <section className="gem-reauth" aria-label="Service-admin session recovery"><p role="alert">Your service-admin session ended. Your draft edits remain below. Sign in with the same account to continue.</p><ServiceAdminSignIn signIn={signIn} /></section> : null}
            {navigation.path === '/service-admin/gem-reference/new' || navigation.path.match(/^\/service-admin\/gem-reference\/drafts\/([^/]+)$/) || navigation.path.match(/^\/service-admin\/gem-reference\/entries\/([^/]+)\/edit$/) ? <GemEditor key={`${identity.accountId}:${navigation.viewId}`} draftId={navigation.path.includes('/drafts/') ? decodeURIComponent(navigation.path.split('/').at(-1)!) : undefined} entryId={navigation.path.endsWith('/edit') ? decodeURIComponent(navigation.path.split('/').at(-2)!) : undefined} onDirtyChange={dirtyChanged} onAuthLost={authLost} onSaved={draftSaved} writesSuspended={writesSuspended} sessionRevision={sessionRevision} />
              : navigation.path.match(/^\/service-admin\/gem-reference\/entries\/([^/]+)$/) ? <GemDetail key={navigation.path} entryId={decodeURIComponent(navigation.path.split('/').at(-1)!)} follow={navigation.follow} />
              : navigation.path === '/service-admin/gem-reference' ? <GemCatalog selected={selectedDrafts} onSelectionChange={setSelectedDrafts} follow={navigation.follow} />
                : <><h1>{navigation.path.endsWith('/review') ? 'Review drafts' : 'Gem draft'}</h1><a href="/service-admin/gem-reference" onClick={navigation.follow}>Back to gem reference</a></>}
          </main>
        </div>
      </div>
      {navigation.confirmation ? <DiscardDialog uncertain={navigation.uncertain} keep={navigation.keep} discard={navigation.discard} /> : null}
    </div>
  );
}
