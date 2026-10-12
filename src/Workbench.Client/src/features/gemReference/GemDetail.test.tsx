import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import * as api from '../../api/gemReference';
import { GemDetail } from './GemDetail';
import { referenceFixture } from './gemFixture';

const lost = vi.fn();
const follow = (event: React.MouseEvent<HTMLAnchorElement>) => event.preventDefault();
beforeEach(() => { lost.mockClear(); vi.spyOn(api, 'getGem').mockResolvedValue(referenceFixture); });
afterEach(() => vi.restoreAllMocks());
function mount() { return render(<GemDetail id="ruby" origin="workbench" follow={follow} onAuthLost={lost} />); }
function field(name: string) { return screen.getByText(name, { selector: 'dt', exact: true }).parentElement!; }

it('renders field provenance, explicit clears, missing values and source dates', async () => {
  // GIVEN shared assertions, tenant replacements and a cleared optional field.
  // AND the locality's designated supporting citation is second, not the first field source.
  const selectedSource = referenceFixture.effectiveFields!.notableLocality.sources[0];
  vi.mocked(api.getGem).mockResolvedValue({ ...referenceFixture, effectiveFields: { ...referenceFixture.effectiveFields, notableLocality: { ...referenceFixture.effectiveFields!.notableLocality, sources: [{ ...selectedSource, id: 'additional', title: 'Additional locality context' }, selectedSource] } } });
  mount();
  expect(screen.getByRole('status')).toHaveTextContent('Loading');
  // WHEN opening the origin-qualified effective detail.
  await screen.findByRole('heading', { name: 'Synthetic ruby' });
  // THEN each claim retains its own attribution and limits rather than borrowing shared sources.
  expect(api.getGem).toHaveBeenCalledWith('ruby', 'workbench', expect.any(AbortSignal));
  expect(field('Species')).toHaveTextContent('Corundum');
  expect(field('Species')).toHaveTextContent('Workbench reference');
  expect(field('Group')).toHaveTextContent('Cleared by your tenant');
  expect(field('Variety')).toHaveTextContent('Tenant-authored · no sources supplied');
  expect(within(field('Description')).getByText('Not recorded')).toHaveAccessibleDescription('No assertion is recorded for this field; this does not establish absence or a measured zero.');
  expect(screen.getByText('Synthetic red gem')).toBeVisible();
  expect(screen.getByText(/does not establish the origin of an individual specimen/)).toBeVisible();
  expect(screen.getByRole('link', { name: 'Synthetic taxonomy' })).toHaveAttribute('rel', 'noopener noreferrer');
  const localityLink = within(field('Notable locality')).getByRole('link', { name: 'Supporting sources' });
  const target = document.getElementById(localityLink.getAttribute('href')!.slice(1));
  expect(target).toHaveAccessibleName('Notable locality · Synthetic locality report');
  expect(target).toHaveTextContent('Reviewed 2026-10-01');
  expect(screen.getByRole('region', { name: 'Sources' })).toHaveTextContent('Accessed 2026-10-02');
});

it('groups identical bibliography while retaining each assertion, date and attribution', async () => {
  // GIVEN two shared fields cite identical bibliography with distinct assertion IDs and dates.
  const source = referenceFixture.effectiveFields!.species.sources[0];
  vi.mocked(api.getGem).mockResolvedValue({ ...referenceFixture, effectiveFields: {
    commonName: { state: 'inherit', attribution: 'workbench', sources: [{ ...source, id: 'name', field: 'commonName', reviewedOn: '2026-10-03', accessedOn: '2026-10-04' }] },
    species: referenceFixture.effectiveFields!.species,
    // AND tenant attribution and a different URL must each keep separate bibliography.
    variety: { state: 'replace', attribution: 'tenant', sources: [{ ...source, id: 'tenant', field: 'variety', attribution: 'tenant' }] },
    description: { state: 'inherit', attribution: 'workbench', sources: [{ ...source, id: 'different', field: 'description', url: 'https://example.test/different' }] },
  } });
  mount();
  // WHEN reading THEN only genuinely identical metadata is shared.
  await screen.findByRole('heading', { name: 'Synthetic ruby' });
  const sources = screen.getByRole('region', { name: 'Sources' });
  expect(within(sources).getAllByText('Synthetic classification citation')).toHaveLength(3);
  for (const [label, reviewed, accessed] of [['Common name', '2026-10-03', '2026-10-04'], ['Species', '2026-10-01', '2026-10-02']]) {
    const link = within(field(label)).getByRole('link', { name: 'Supporting sources' });
    const target = document.getElementById(link.getAttribute('href')!.slice(1));
    expect(target).toHaveAccessibleName(`${label} · Synthetic taxonomy`);
    expect(target).toHaveTextContent(`Reviewed ${reviewed} · Accessed ${accessed}`);
    expect(target?.closest('.reference-source-group')).toHaveTextContent('Workbench source');
    expect(target?.closest('.reference-source-group')).not.toHaveTextContent('Tenant source');
  }
  expect(within(sources).getByText('Tenant source · Reference institute')).toBeVisible();
  expect(within(sources).getAllByRole('link', { name: 'Synthetic taxonomy' }).map(link => link.getAttribute('href'))).toContain('https://example.test/different');
});

it('keeps an invalid entry visible with reasons but withholds taxonomy claims', async () => {
  // GIVEN shared corrections have invalidated a retained tenant override.
  vi.mocked(api.getGem).mockResolvedValue({ ...referenceFixture, needsReview: true, reviewReasons: { species: ['A mineral requires a species.'] } });
  mount();
  // WHEN reading THEN identity/reasons remain available without a classification assertion.
  await screen.findByRole('heading', { name: 'Synthetic ruby' });
  expect(screen.getByRole('region', { name: 'Needs review' })).toHaveTextContent('A mineral requires a species.');
  expect(screen.queryByText('Corundum', { exact: true })).not.toBeInTheDocument();
  expect(screen.queryByText('Species', { selector: 'dt', exact: true })).not.toBeInTheDocument();
  expect(screen.queryByText('Material kind', { selector: 'dt', exact: true })).not.toBeInTheDocument();
});

it.each(['javascript:alert(1)', 'data:text/html,test', 'https://user:secret@example.test/source', '/relative'])('keeps unsafe source %s as readable text', async url => {
  // GIVEN an untrusted URL alongside an independently attributed tenant citation.
  vi.mocked(api.getGem).mockResolvedValue({ ...referenceFixture, effectiveFields: { variety: { state: 'replace', attribution: 'tenant', sources: [{ ...referenceFixture.effectiveFields!.species.sources[0], field: 'variety', attribution: 'tenant', url }] } } });
  mount();
  // WHEN viewing sources THEN text survives and no active unsafe link is emitted.
  await screen.findByRole('heading', { name: 'Synthetic taxonomy' });
  expect(screen.queryByRole('link', { name: 'Synthetic taxonomy' })).not.toBeInTheDocument();
  expect(screen.getByText('Synthetic classification citation')).toBeVisible();
  expect(screen.getByRole('region', { name: 'Sources' })).toHaveTextContent('Tenant source');
});

it('explains absent non-mineral taxonomy and retirement with a qualified redirect', async () => {
  // GIVEN a retired tenant-authored organic entry without invented species.
  vi.mocked(api.getGem).mockResolvedValue({ ...referenceFixture, origin: 'tenant', materialKind: 'organic', species: null, variety: null, layer: 'tenantEntry', retirement: { isRetired: true, explanation: 'Use the corrected identity.', redirectEntryId: 'replacement' }, effectiveFields: { species: { state: 'replace', attribution: 'tenant', sources: [] } } });
  mount();
  // WHEN reading THEN absence and retirement remain explicit.
  await screen.findByText('Use the corrected identity.');
  expect(field('Species')).toHaveTextContent('Mineral species is not required for this material kind');
  expect(screen.getByRole('link', { name: 'View replacement entry' })).toHaveAttribute('href', '/gem-reference/workbench/replacement');
});

it('distinguishes missing details, transient retry and ended authority', async () => {
  // GIVEN a missing entry, a transient failure, a successful retry and an ended session.
  vi.mocked(api.getGem).mockRejectedValueOnce(new api.GemReferenceApiError(404, null)).mockRejectedValueOnce(new api.GemReferenceApiError(503, null)).mockResolvedValueOnce(referenceFixture).mockRejectedValueOnce(new api.GemReferenceApiError(403, null));
  const first = mount();
  await screen.findByText('Gem reference not found.');
  first.unmount();
  const second = mount();
  // WHEN retrying THEN content returns without claiming that the failed load was empty.
  fireEvent.click(await screen.findByRole('button', { name: 'Retry' }));
  await screen.findByRole('heading', { name: 'Synthetic ruby' });
  second.unmount();
  mount();
  // AND ended authority is handed to the tenant authentication flow.
  await waitFor(() => expect(lost).toHaveBeenCalledOnce());
});
