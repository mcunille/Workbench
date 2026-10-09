import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { App } from '../../App';
import { ServiceAdminApplication } from './ServiceAdminApplication';
import * as admin from '../../api/serviceAdmin';
import * as tenant from '../../api/auth';
import * as gems from '../../api/gemReferenceAdmin';
import { readPendingPublication, writePendingPublication } from './publicationAttempt';

vi.mock('../../api/serviceAdmin', async (original) => ({
  ...await original<typeof import('../../api/serviceAdmin')>(),
  getServiceAdminIdentity: vi.fn(), signInServiceAdmin: vi.fn(), signOutServiceAdmin: vi.fn(),
}));
vi.mock('../../api/auth', async (importOriginal) => ({
  ...await importOriginal<typeof import('../../api/auth')>(), getCurrentIdentity: vi.fn(),
}));
vi.mock('../../api/system', () => ({ getSystem: vi.fn().mockResolvedValue({ name: 'Workbench', version: '1.0.0' }) }));
vi.mock('../../api/gemReferenceAdmin', async (original) => ({
  ...await original<typeof import('../../api/gemReferenceAdmin')>(),
  browseSharedGems: vi.fn().mockResolvedValue({ entries: [], nextCursor: null }),
  listGemDrafts: vi.fn().mockResolvedValue({ drafts: [], nextCursor: null }),
  getGemDraft: vi.fn(), getSharedGem: vi.fn(), saveGemDraft: vi.fn(),
  reviewGemDrafts: vi.fn(), publishGemDrafts: vi.fn(), getGemPublication: vi.fn(),
}));

const savedDraft: gems.GemReferenceDraftResponse = { id: 'draft', entryId: 'entry', rowVersion: 'draft-v1', expectedPublishedRowVersion: null, content: { id: 'entry', materialKind: 'organic', commonName: 'Amber', group: null, species: null, variety: null, description: null, aliases: [], sources: [], notableLocality: null, isRetired: false, retirementExplanation: null, redirectEntryId: null }, createdBy: 'admin', updatedBy: 'admin', createdAtUtc: '2026-10-01T00:00:00Z', updatedAtUtc: '2026-10-01T00:00:00Z', errors: {} };
const originalShowModal = Object.getOwnPropertyDescriptor(HTMLDialogElement.prototype, 'showModal');

beforeEach(() => {
  Object.defineProperty(HTMLDialogElement.prototype, 'showModal', { configurable: true, value(this: HTMLDialogElement) { this.setAttribute('open', ''); } });
  vi.clearAllMocks();
  sessionStorage.clear();
  vi.mocked(gems.listGemDrafts).mockResolvedValue({ drafts: [], nextCursor: null });
  vi.mocked(admin.getServiceAdminIdentity).mockResolvedValue(null);
  vi.mocked(tenant.getCurrentIdentity).mockResolvedValue({ userId: 'tenant', email: 'tenant@example.test', tenantName: 'Tenant', permissions: ['TenantAccess'] });
  vi.mocked(gems.getGemDraft).mockResolvedValue(savedDraft);
  vi.mocked(gems.getSharedGem).mockRejectedValue(new gems.GemReferenceAdminApiError(404, {}));
  window.history.replaceState(null, '', '/service-admin/gem-reference');
});
afterEach(() => {
  if (originalShowModal) Object.defineProperty(HTMLDialogElement.prototype, 'showModal', originalShowModal);
  else Reflect.deleteProperty(HTMLDialogElement.prototype, 'showModal');
  window.history.replaceState(null, '', '/');
});

describe('service-admin entry', () => {
  it('clears catalog state and selected versions when recovery confirms a different account', async () => {
    // GIVEN selected drafts and a retained search belonging to the original account.
    vi.mocked(admin.getServiceAdminIdentity).mockResolvedValueOnce({ accountId: 'admin', email: 'first@example.test' })
      .mockResolvedValue({ accountId: 'other', email: 'other@example.test' });
    vi.mocked(gems.browseSharedGems).mockResolvedValue({ entries: [], nextCursor: null });
    vi.mocked(gems.listGemDrafts).mockResolvedValue({ drafts: [savedDraft], nextCursor: null });
    vi.mocked(admin.signOutServiceAdmin).mockRejectedValueOnce(new admin.ServiceAdminApiError(401));
    vi.mocked(admin.signInServiceAdmin).mockResolvedValue();
    render(<ServiceAdminApplication appearance={null} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Drafts' }));
    fireEvent.click(await screen.findByLabelText('Select Amber'));
    fireEvent.click(screen.getByRole('button', { name: 'Published catalog' }));
    fireEvent.change(screen.getByLabelText('Search shared gems'), { target: { value: 'private search' } });
    fireEvent.click(screen.getByRole('button', { name: 'Sign out' }));
    await screen.findByRole('region', { name: 'Service-admin session recovery' });
    // WHEN a different account signs in THEN its library starts without the former selection or search.
    fireEvent.change(screen.getByLabelText('Email'), { target: { value: 'other@example.test' } });
    fireEvent.change(screen.getByLabelText('Password'), { target: { value: 'synthetic-password' } });
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    await screen.findByText('other@example.test');
    expect(screen.getByLabelText('Search shared gems')).toHaveValue('');
    fireEvent.click(screen.getByRole('button', { name: 'Drafts' }));
    expect(await screen.findByLabelText('Select Amber')).not.toBeChecked();
    expect(screen.getByText('0 of 50 drafts selected')).toBeVisible();
  });
  it.each([
    ['catalog initial', 401], ['catalog continuation', 403], ['drafts initial', 403],
    ['drafts continuation', 401], ['detail', 401], ['detail drafts', 403], ['sign out', 401], ['sign out', 403],
  ] as const)('recovers %s authorization failure (%s) and refetches retained browsing state', async (path, status) => {
    // GIVEN an authenticated browser with a selected saved version and a search.
    vi.mocked(admin.getServiceAdminIdentity).mockResolvedValue({ accountId: 'admin', email: 'curator@example.test' });
    vi.mocked(admin.signInServiceAdmin).mockResolvedValue();
    vi.mocked(gems.browseSharedGems).mockResolvedValue({ entries: [], nextCursor: null });
    vi.mocked(gems.listGemDrafts).mockResolvedValue({ drafts: [savedDraft], nextCursor: 'next' });
    const detail = { id: 'entry', commonName: 'Published amber', materialKind: 'organic', group: null, species: null, variety: null, layer: 'Shared', rowVersion: 'published-v1', aliases: [], description: null, notableLocality: null, sourceAssertions: [], retirement: { isRetired: false, explanation: null, redirectEntryId: null } };
    vi.mocked(gems.getSharedGem).mockResolvedValue(detail);
    render(<ServiceAdminApplication appearance={null} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Drafts' }));
    fireEvent.click(await screen.findByLabelText('Select Amber'));
    fireEvent.click(screen.getByRole('button', { name: 'Published catalog' }));
    fireEvent.change(screen.getByLabelText('Search shared gems'), { target: { value: 'amber' } });
    fireEvent.click(screen.getByRole('button', { name: 'Search' }));
    await screen.findByText('No shared entries match this search.');
    const denied = new gems.GemReferenceAdminApiError(status, {});
    // WHEN a read or logout discovers that the session ended.
    if (path.startsWith('drafts')) {
      vi.mocked(gems.listGemDrafts).mockRejectedValueOnce(denied);
      if (path.endsWith('continuation')) {
        vi.mocked(gems.listGemDrafts).mockReset().mockResolvedValueOnce({ drafts: [savedDraft], nextCursor: 'next' }).mockRejectedValueOnce(denied).mockResolvedValue({ drafts: [savedDraft], nextCursor: null });
      }
      fireEvent.click(screen.getByRole('button', { name: 'Drafts' }));
      if (path.endsWith('continuation')) fireEvent.click(await screen.findByRole('button', { name: 'Load more drafts' }));
    } else if (path.startsWith('catalog')) {
      vi.mocked(gems.browseSharedGems).mockReset().mockRejectedValueOnce(denied).mockResolvedValue({ entries: [], nextCursor: null });
      if (path.endsWith('continuation')) {
        vi.mocked(gems.browseSharedGems).mockReset().mockResolvedValueOnce({ entries: [], nextCursor: 'next' }).mockRejectedValueOnce(denied).mockResolvedValue({ entries: [], nextCursor: null });
      }
      fireEvent.change(screen.getByLabelText('Search shared gems'), { target: { value: 'retained search' } });
      fireEvent.click(screen.getByRole('button', { name: 'Search' }));
      if (path.endsWith('continuation')) fireEvent.click(await screen.findByRole('button', { name: 'Load more entries' }));
    } else if (path.startsWith('detail')) {
      if (path === 'detail') vi.mocked(gems.getSharedGem).mockRejectedValueOnce(denied);
      else vi.mocked(gems.listGemDrafts).mockRejectedValueOnce(denied);
      window.history.pushState(null, '', '/service-admin/gem-reference/entries/entry');
      fireEvent.popState(window);
    } else {
      vi.mocked(admin.signOutServiceAdmin).mockRejectedValueOnce(new admin.ServiceAdminApiError(status));
      fireEvent.click(screen.getByRole('button', { name: 'Sign out' }));
    }
    // THEN recovery retains the browsing route and selection through a failed login.
    await screen.findByRole('region', { name: 'Service-admin session recovery' });
    const retainedPath = window.location.pathname;
    vi.mocked(admin.signInServiceAdmin).mockRejectedValueOnce(new Error('Rejected'));
    const login = () => {
      fireEvent.change(screen.getByLabelText('Email'), { target: { value: 'curator@example.test' } });
      fireEvent.change(screen.getByLabelText('Password'), { target: { value: 'synthetic-password' } });
      fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    };
    login();
    await screen.findByText(/We could not sign you in/);
    expect(window.location.pathname).toBe(retainedPath);
    const catalogReads = vi.mocked(gems.browseSharedGems).mock.calls.length;
    const draftReads = vi.mocked(gems.listGemDrafts).mock.calls.length;
    const detailReads = vi.mocked(gems.getSharedGem).mock.calls.length;
    // WHEN the same account signs in THEN reads refresh and the saved selection remains reviewable.
    login();
    await waitFor(() => expect(screen.queryByRole('region', { name: 'Service-admin session recovery' })).not.toBeInTheDocument());
    if (path.startsWith('detail')) {
      await waitFor(() => expect(vi.mocked(gems.getSharedGem).mock.calls.length).toBeGreaterThan(detailReads));
      await waitFor(() => expect(vi.mocked(gems.listGemDrafts).mock.calls.length).toBeGreaterThan(draftReads));
    } else if (path.startsWith('drafts')) await waitFor(() => expect(vi.mocked(gems.listGemDrafts).mock.calls.length).toBeGreaterThan(draftReads));
    else await waitFor(() => expect(vi.mocked(gems.browseSharedGems).mock.calls.length).toBeGreaterThan(catalogReads));
    if (path.startsWith('detail')) expect(await screen.findByRole('heading', { name: 'Published amber' })).toBeVisible();
    else {
      if (!path.startsWith('drafts')) expect(screen.getByLabelText('Search shared gems')).toHaveValue(path.startsWith('catalog') ? 'retained search' : 'amber');
      else expect(screen.getByRole('button', { name: 'Drafts' })).toHaveAttribute('aria-pressed', 'true');
    }
    if (path.startsWith('detail')) fireEvent.click(screen.getByRole('link', { name: 'Back to gem reference' }));
    if (!path.startsWith('drafts')) fireEvent.click(screen.getByRole('button', { name: 'Drafts' }));
    expect(await screen.findByLabelText('Select Amber')).toBeChecked();
    vi.mocked(gems.reviewGemDrafts).mockResolvedValue({ entries: [] });
    fireEvent.click(screen.getByRole('link', { name: 'Review 1 draft' }));
    await waitFor(() => expect(gems.reviewGemDrafts).toHaveBeenCalledWith([{ draftId: 'draft', expectedDraftRowVersion: 'draft-v1' }]));
  });
  it('restores a rejected recovered batch to the shell selection before repairing its draft', async () => {
    // GIVEN a review-route reload with an infrastructure rejection and no in-memory selection.
    const draftId = '22222222-2222-4222-8222-222222222222';
    const pending = { requestId: '11111111-1111-4111-8111-111111111111', drafts: [{ draftId, expectedDraftRowVersion: 'draft-v1' }] };
    writePendingPublication('admin', pending);
    window.history.replaceState(null, '', '/service-admin/gem-reference/review');
    vi.mocked(admin.getServiceAdminIdentity).mockResolvedValue({ accountId: 'admin', email: 'curator@example.test' });
    vi.mocked(gems.getGemPublication).mockResolvedValue({ requestId: pending.requestId, code: 'infrastructure_failure', entries: [], review: [] });
    vi.mocked(gems.getGemDraft).mockResolvedValue({ ...savedDraft, id: draftId });
    vi.mocked(gems.saveGemDraft).mockResolvedValue({ ...savedDraft, id: draftId, rowVersion: 'draft-v2', content: { ...savedDraft.content, commonName: 'Repaired amber' } });
    render(<ServiceAdminApplication appearance={null} />);
    await screen.findByText(/Publication was rejected/);
    // WHEN the recovered draft is repaired and navigation is requested before saving.
    fireEvent.click(screen.getByRole('button', { name: 'Edit draft' }));
    await screen.findByLabelText('Common name');
    fireEvent.change(screen.getByLabelText('Common name'), { target: { value: 'Repaired amber' } });
    fireEvent.click(screen.getByRole('link', { name: 'Gem reference' }));
    // THEN keeping edits preserves the recovered draft and its unsaved repair.
    expect(await screen.findByRole('dialog', { name: 'Discard changes?' })).toBeVisible();
    fireEvent.click(screen.getByRole('button', { name: 'Keep editing' }));
    expect(window.location.pathname).toBe(`/service-admin/gem-reference/drafts/${draftId}`);
    expect(screen.getByLabelText('Common name')).toHaveValue('Repaired amber');
    // WHEN the repair is confirmed saved THEN the shell retains the selection with the new version.
    fireEvent.click(screen.getByRole('button', { name: 'Save draft' }));
    await screen.findByText('Draft saved. Publication requires review.');
    await waitFor(() => expect(screen.getByRole('button', { name: 'Save draft' })).toBeEnabled());
    fireEvent.click(screen.getByRole('link', { name: 'Gem reference' }));
    fireEvent.click(screen.getByRole('button', { name: 'Drafts' }));
    expect(screen.getByText('1 of 50 drafts selected')).toBeVisible();
    vi.mocked(gems.reviewGemDrafts).mockResolvedValue({ entries: [] });
    fireEvent.click(screen.getByRole('link', { name: 'Review 1 draft' }));
    await waitFor(() => expect(gems.reviewGemDrafts).toHaveBeenCalledWith([{ draftId, expectedDraftRowVersion: 'draft-v2' }]));
  });
  it('publishes a catalog selection and refreshes the library while leaving unselected drafts', async () => {
    // GIVEN two saved drafts and a service-admin session.
    const selectedId = '22222222-2222-4222-8222-222222222222';
    const selected = { ...savedDraft, id: selectedId };
    const other = { ...savedDraft, id: '33333333-3333-4333-8333-333333333333', content: { ...savedDraft.content, commonName: 'Ruby' } };
    const review = { draftId: selectedId, entryId: 'entry', isStale: false, errors: {}, changes: [{ field: 'commonName', before: null, after: 'Amber' }] };
    vi.mocked(admin.getServiceAdminIdentity).mockResolvedValue({ accountId: 'admin', email: 'curator@example.test' });
    vi.mocked(gems.listGemDrafts).mockResolvedValue({ drafts: [selected, other], nextCursor: null });
    vi.mocked(gems.reviewGemDrafts).mockResolvedValue({ entries: [review] });
    vi.mocked(gems.publishGemDrafts).mockImplementation(async (request) => { vi.mocked(gems.listGemDrafts).mockResolvedValue({ drafts: [other], nextCursor: null }); return { requestId: request.requestId, code: 'published', entries: [{ entryId: 'entry', rowVersion: 'v2' }], review: [review] }; });
    render(<ServiceAdminApplication appearance={null} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Drafts' }));
    fireEvent.click(await screen.findByLabelText('Select Amber'));
    // WHEN the selected draft is reviewed and explicitly confirmed THEN publication returns to the refreshed library.
    fireEvent.click(screen.getByRole('link', { name: 'Review 1 draft' }));
    await screen.findByText('Amber');
    fireEvent.click(screen.getByLabelText('I confirm these reviewed changes should be published.'));
    fireEvent.click(screen.getByRole('button', { name: 'Publish 1 draft' }));
    expect(await screen.findByText('Published 1 shared entry.')).toHaveAttribute('role', 'status');
    expect(screen.getByRole('link', { name: 'View published entry 1' })).toHaveAttribute('href', '/service-admin/gem-reference/entries/entry');
    fireEvent.click(screen.getByRole('button', { name: 'Drafts' }));
    expect(await screen.findByLabelText('Select Ruby')).toBeEnabled();
    expect(screen.queryByLabelText('Select Amber')).not.toBeInTheDocument();
    expect(screen.getByText('0 of 50 drafts selected')).toBeVisible();
  });
  it('keeps a pending receipt account-scoped through sign-out and sign-in by another admin', async () => {
    // GIVEN an uncertain publication from the original account on the library route.
    const pending = { requestId: '11111111-1111-4111-8111-111111111111', drafts: [{ draftId: '22222222-2222-4222-8222-222222222222', expectedDraftRowVersion: 'v1' }] };
    writePendingPublication('admin', pending);
    vi.mocked(gems.listGemDrafts).mockResolvedValue({ drafts: [savedDraft], nextCursor: null });
    vi.mocked(admin.getServiceAdminIdentity).mockResolvedValueOnce({ accountId: 'admin', email: 'curator@example.test' }).mockResolvedValue({ accountId: 'other', email: 'other@example.test' });
    vi.mocked(admin.signOutServiceAdmin).mockResolvedValue(); vi.mocked(admin.signInServiceAdmin).mockResolvedValue();
    render(<ServiceAdminApplication appearance={null} />);
    expect(await screen.findByRole('link', { name: 'Resolve pending publication' })).toBeVisible();
    fireEvent.click(screen.getByRole('button', { name: 'Drafts' }));
    expect(await screen.findByLabelText('Select Amber')).toBeDisabled();
    fireEvent.click(screen.getByRole('button', { name: 'Sign out' }));
    await screen.findByRole('heading', { name: 'Service-admin sign in' });
    expect(screen.queryByRole('link', { name: 'Resolve pending publication' })).not.toBeInTheDocument();
    // WHEN a different admin signs in THEN the old account's receipt remains recoverable only by its owner.
    fireEvent.change(screen.getByLabelText('Email'), { target: { value: 'other@example.test' } });
    fireEvent.change(screen.getByLabelText('Password'), { target: { value: 'synthetic-password' } });
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    await screen.findByText('other@example.test');
    expect(screen.queryByRole('link', { name: 'Resolve pending publication' })).not.toBeInTheDocument();
    expect(readPendingPublication('admin')).toEqual(pending);
    expect(gems.getGemPublication).not.toHaveBeenCalled();
  });
  it.each(['admin', 'other-admin'])('keeps expired-session edits only for the original account (%s)', async (accountId) => {
    // GIVEN an authenticated editor with local changes and a revoked session.
    vi.mocked(admin.getServiceAdminIdentity).mockResolvedValueOnce({ accountId: 'admin', email: 'curator@example.test' })
      .mockResolvedValue({ accountId, email: 'curator@example.test' });
    vi.mocked(admin.signInServiceAdmin).mockResolvedValue();
    vi.mocked(gems.saveGemDraft).mockRejectedValue(new gems.GemReferenceAdminApiError(401, {}));
    window.history.replaceState(null, '', '/service-admin/gem-reference/drafts/draft');
    render(<ServiceAdminApplication appearance={null} />);
    await screen.findByLabelText('Common name');
    fireEvent.change(screen.getByLabelText('Common name'), { target: { value: 'Unsaved amber' } });
    // WHEN saving discovers expiry THEN reauthentication appears while the editor remains mounted.
    fireEvent.click(screen.getByRole('button', { name: 'Save draft' }));
    await screen.findByRole('heading', { name: 'Service-admin sign in' });
    expect(screen.getByLabelText('Common name')).toHaveValue('Unsaved amber');
    expect(screen.getByRole('button', { name: 'Save draft' })).toBeDisabled();
    // WHEN an admin signs in THEN the same account refreshes versions; a different account starts without those edits.
    fireEvent.change(screen.getByLabelText('Email'), { target: { value: 'curator@example.test' } });
    fireEvent.change(screen.getByLabelText('Password'), { target: { value: 'synthetic-password' } });
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    if (accountId === 'admin') {
      await waitFor(() => expect(screen.getByRole('button', { name: 'Save draft' })).toBeEnabled());
      expect(screen.getByLabelText('Common name')).toHaveValue('Unsaved amber');
      expect(gems.getGemDraft).toHaveBeenCalledTimes(2);
      expect(gems.getSharedGem).toHaveBeenCalledWith('entry');
    } else {
      await screen.findByRole('heading', { name: 'Gem reference' });
      expect(screen.queryByLabelText('Common name')).not.toBeInTheDocument();
      expect(window.location.pathname).toBe('/service-admin/gem-reference');
    }
  });

  it('uses the existing discard decision before leaving a dirty editor or signing out', async () => {
    // GIVEN local edits in an authenticated new draft.
    vi.mocked(admin.getServiceAdminIdentity).mockResolvedValue({ accountId: 'admin', email: 'curator@example.test' });
    vi.mocked(admin.signOutServiceAdmin).mockResolvedValue();
    window.history.replaceState(null, '', '/service-admin/gem-reference/new');
    render(<ServiceAdminApplication appearance={null} />);
    fireEvent.change(await screen.findByLabelText('Common name'), { target: { value: 'Unsaved' } });
    // WHEN library navigation is requested THEN keeping edits preserves the form and route.
    fireEvent.click(screen.getByRole('link', { name: 'Gem reference' }));
    expect(await screen.findByRole('dialog')).toHaveTextContent('Discard changes?');
    fireEvent.click(screen.getByRole('button', { name: 'Keep editing' }));
    expect(screen.getByLabelText('Common name')).toHaveValue('Unsaved');
    expect(window.location.pathname).toBe('/service-admin/gem-reference/new');
    // WHEN signing out is requested THEN only explicit discard performs logout.
    fireEvent.click(screen.getByRole('button', { name: 'Sign out' }));
    await screen.findByRole('dialog');
    expect(admin.signOutServiceAdmin).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: 'Discard changes' }));
    await screen.findByRole('heading', { name: 'Service-admin sign in' });
    expect(admin.signOutServiceAdmin).toHaveBeenCalledOnce();
  });
  it('tenant_cookie_does_not_authenticate_admin', async () => {
    // GIVEN a valid tenant session and an absent service-admin session.
    // WHEN the browser opens a direct service-admin route.
    render(<App />);
    // THEN dedicated admin sign-in renders without ever consulting tenant identity.
    expect(await screen.findByRole('heading', { name: 'Service-admin sign in' })).toBeVisible();
    expect(tenant.getCurrentIdentity).not.toHaveBeenCalled();
    expect(admin.getServiceAdminIdentity).toHaveBeenCalled();
  });

  it('both_cookies_use_admin_identity', async () => {
    // GIVEN both identities are available in the browser.
    vi.mocked(admin.getServiceAdminIdentity).mockResolvedValue({ accountId: 'admin', email: 'curator@example.test' });
    // WHEN a direct service-admin route is opened.
    render(<App />);
    // THEN only the independent admin identity and shared-work navigation appear.
    expect(await screen.findByText('curator@example.test')).toBeVisible();
    expect(tenant.getCurrentIdentity).not.toHaveBeenCalled();
    expect(screen.queryByText('tenant@example.test')).not.toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'Inventory' })).not.toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Gem reference' })).toHaveAttribute('href', '/service-admin/gem-reference');
  });

  it('failed_login_keeps_email', async () => {
    // GIVEN a signed-out admin and a rejected sign-in attempt.
    vi.mocked(admin.signInServiceAdmin).mockRejectedValue(new Error('Rejected'));
    render(<ServiceAdminApplication appearance={<span>Appearance</span>} />);
    await screen.findByRole('heading', { name: 'Service-admin sign in' });
    // WHEN the admin enters credentials and submits.
    fireEvent.change(screen.getByLabelText('Email'), { target: { value: 'curator@example.test' } });
    fireEvent.change(screen.getByLabelText('Password'), { target: { value: 'incorrect' } });
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    // THEN a visible error leaves the entered email ready to retry.
    expect(await screen.findByRole('alert')).toHaveTextContent('could not sign you in');
    expect(screen.getByLabelText('Email')).toHaveValue('curator@example.test');
    expect(screen.getByRole('button', { name: 'Sign in' })).toBeEnabled();
  });

  it('rechecks admin identity after sign-in and preserves the requested URL', async () => {
    // GIVEN a deep admin URL requiring authentication.
    vi.mocked(admin.signInServiceAdmin).mockResolvedValue();
    vi.mocked(admin.getServiceAdminIdentity).mockResolvedValueOnce(null)
      .mockResolvedValue({ accountId: 'admin', email: 'curator@example.test' });
    window.history.replaceState(null, '', '/service-admin/gem-reference/drafts/draft-id');
    render(<ServiceAdminApplication appearance={null} />);
    await screen.findByRole('heading', { name: 'Service-admin sign in' });
    // WHEN credentials are accepted.
    fireEvent.change(screen.getByLabelText('Email'), { target: { value: 'curator@example.test' } });
    fireEvent.change(screen.getByLabelText('Password'), { target: { value: 'synthetic-password' } });
    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    // THEN verified admin identity unlocks the shell at the requested URL.
    expect(await screen.findByText('curator@example.test')).toBeVisible();
    expect(admin.signInServiceAdmin).toHaveBeenCalledWith('curator@example.test', 'synthetic-password');
    expect(window.location.pathname).toBe('/service-admin/gem-reference/drafts/draft-id');
  });

  it('offers retry when admin identity is unavailable', async () => {
    // GIVEN an unavailable identity service which recovers.
    vi.mocked(admin.getServiceAdminIdentity).mockRejectedValueOnce(new Error('Unavailable'));
    render(<ServiceAdminApplication appearance={null} />);
    // WHEN the initial identity check fails and the admin retries.
    expect(await screen.findByRole('alert')).toHaveTextContent('temporarily unavailable');
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
    // THEN the dedicated sign-in form appears after the successful check.
    expect(await screen.findByRole('heading', { name: 'Service-admin sign in' })).toBeVisible();
  });

  it('keeps the authenticated shell on failed logout and signs out after a successful retry', async () => {
    // GIVEN an admin session and a transient logout failure.
    vi.mocked(admin.getServiceAdminIdentity).mockResolvedValue({ accountId: 'admin', email: 'curator@example.test' });
    vi.mocked(admin.signOutServiceAdmin).mockRejectedValueOnce(new Error('Unavailable')).mockResolvedValue();
    render(<ServiceAdminApplication appearance={null} />);
    await screen.findByText('curator@example.test');
    // WHEN logout fails.
    fireEvent.click(screen.getByRole('button', { name: 'Sign out' }));
    // THEN identity remains visible and the error allows another attempt.
    expect(await screen.findByRole('alert')).toHaveTextContent('could not sign you out');
    expect(screen.getByText('curator@example.test')).toBeVisible();
    await waitFor(() => expect(screen.getByRole('button', { name: 'Sign out' })).toBeEnabled());
    // WHEN logout succeeds THEN admin sign-in replaces the shared shell.
    fireEvent.click(screen.getByRole('button', { name: 'Sign out' }));
    expect(await screen.findByRole('heading', { name: 'Service-admin sign in' })).toBeVisible();
    expect(window.location.pathname).toBe('/service-admin/sign-in');
  });
});
