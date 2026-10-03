import { useCallback, useEffect, useState, type ReactNode } from 'react';
import { getServiceAdminIdentity, signInServiceAdmin, signOutServiceAdmin, type CurrentServiceAdminResponse } from '../../api/serviceAdmin';
import { Brand } from '../../Brand';
import { useNavigation } from '../../useNavigation';
import { ServiceAdminSignIn } from './ServiceAdminSignIn';
import { GemCatalog, GemDetail } from './GemCatalog';
import { GemEditor } from './GemEditor';
import { GemReview } from './GemReview';
import { readPendingPublication } from './publicationAttempt';
import { DiscardDialog } from '../../DiscardDialog';
import type { GemReferenceDraftResponse, GemReferenceDraftSelection, GemReferencePublishOutcome } from '../../api/gemReferenceAdmin';
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
  const [pendingPublication, setPendingPublication] = useState(false);
  const [published, setPublished] = useState<GemReferencePublishOutcome | null>(null);
  const { replace } = navigation;
  const { setDirty } = navigation;
  const authLost = useCallback(() => setWritesSuspended(true), []);
  const dirtyChanged = useCallback((dirty: boolean, uncertain = false) => setDirty(dirty, uncertain), [setDirty]);
  const publicationCompleted = useCallback((outcome: GemReferencePublishOutcome) => {
    const completed = new Set(outcome.review.map((entry) => entry.draftId));
    setSelectedDrafts((selection) => selection.filter((item) => !completed.has(item.draftId)));
    setPublished(outcome);
    setPendingPublication(false);
    setDirty(false, false);
    replace('/service-admin/gem-reference');
  }, [replace, setDirty]);
  function hasPending(accountId: string) {
    try { return !!readPendingPublication(accountId); } catch { return true; }
  }
  const draftSaved = useCallback((draft: GemReferenceDraftResponse) => {
    setSelectedDrafts((selection) => selection.map((item) => item.draftId === draft.id ? { ...item, expectedDraftRowVersion: draft.rowVersion } : item));
    replace(`/service-admin/gem-reference/drafts/${encodeURIComponent(draft.id)}`);
  }, [replace]);

  useEffect(() => {
    let current = true;
    void getServiceAdminIdentity().then((next) => {
      if (!current) return;
      setIdentity(next);
      setPendingPublication(next ? hasPending(next.accountId) : false);
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
      setPublished(null);
      setDirty(false, false);
      replace('/service-admin/gem-reference');
    }
    setIdentity(next);
    setPendingPublication(hasPending(next.accountId));
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
      setPublished(null);
      setPendingPublication(false);
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
            {pendingPublication && navigation.path !== '/service-admin/gem-reference/review' ? <p className="form-message"><a href="/service-admin/gem-reference/review" onClick={navigation.follow}>Resolve pending publication</a> before selecting another batch.</p> : null}
            {navigation.path === '/service-admin/gem-reference/review' ? <GemReview key={identity.accountId} accountId={identity.accountId} selection={selectedDrafts} onPublished={publicationCompleted} onEdit={(id) => navigation.navigate(`/service-admin/gem-reference/drafts/${encodeURIComponent(id)}`)} writesSuspended={writesSuspended} sessionRevision={sessionRevision} onAuthLost={authLost} onPendingChange={setPendingPublication} onDirtyChange={dirtyChanged} />
              : navigation.path === '/service-admin/gem-reference/new' || navigation.path.match(/^\/service-admin\/gem-reference\/drafts\/([^/]+)$/) || navigation.path.match(/^\/service-admin\/gem-reference\/entries\/([^/]+)\/edit$/) ? <GemEditor key={`${identity.accountId}:${navigation.viewId}`} draftId={navigation.path.includes('/drafts/') ? decodeURIComponent(navigation.path.split('/').at(-1)!) : undefined} entryId={navigation.path.endsWith('/edit') ? decodeURIComponent(navigation.path.split('/').at(-2)!) : undefined} onDirtyChange={dirtyChanged} onAuthLost={authLost} onSaved={draftSaved} writesSuspended={writesSuspended} sessionRevision={sessionRevision} />
              : navigation.path.match(/^\/service-admin\/gem-reference\/entries\/([^/]+)$/) ? <GemDetail key={navigation.path} entryId={decodeURIComponent(navigation.path.split('/').at(-1)!)} follow={navigation.follow} />
              : navigation.path === '/service-admin/gem-reference' ? <>{published ? <section className="gem-publication-result"><p role="status">Published {published.entries.length} shared {published.entries.length === 1 ? 'entry' : 'entries'}.</p><ul>{published.entries.map((entry, index) => <li key={entry.entryId}><a href={`/service-admin/gem-reference/entries/${encodeURIComponent(entry.entryId)}`} onClick={navigation.follow}>View published entry {index + 1}</a></li>)}</ul></section> : null}<GemCatalog selected={selectedDrafts} onSelectionChange={setSelectedDrafts} follow={navigation.follow} selectionLocked={pendingPublication || writesSuspended} /></>
                : <><h1>{navigation.path.endsWith('/review') ? 'Review drafts' : 'Gem draft'}</h1><a href="/service-admin/gem-reference" onClick={navigation.follow}>Back to gem reference</a></>}
          </main>
        </div>
      </div>
      {navigation.confirmation ? <DiscardDialog uncertain={navigation.uncertain} keep={navigation.keep} discard={navigation.discard} /> : null}
    </div>
  );
}
