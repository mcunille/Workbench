import { StrictMode, useState } from 'react';
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import * as api from '../../api/gemReferenceAdmin';
import { GemCatalog, GemDetail } from './GemCatalog';

vi.mock('../../api/gemReferenceAdmin', () => ({ browseSharedGems: vi.fn(), listGemDrafts: vi.fn(), getSharedGem: vi.fn() }));
const follow = vi.fn();
const entry = { id: 'ruby', commonName: 'Ruby', materialKind: 'Mineral', group: 'Corundum', species: 'Corundum', variety: 'Ruby', layer: 'Shared' };
function draft(id: string): api.GemReferenceDraftResponse {
  return { id, entryId: 'ruby', rowVersion: `version-${id}`, expectedPublishedRowVersion: 'published-v1', content: { id: 'ruby', commonName: `Draft ${id}`, materialKind: 'Mineral', group: null, species: null, variety: null, description: null, aliases: [], sources: [], notableLocality: null, isRetired: false, retirementExplanation: null, redirectEntryId: null }, createdBy: 'admin', updatedBy: 'admin', createdAtUtc: '2026-10-01T12:00:00Z', updatedAtUtc: '2026-10-02T12:00:00Z', errors: {} };
}
function Catalog({ onChange = vi.fn() }: { onChange?: (value: api.GemReferenceDraftSelection[]) => void }) {
  const [selected, setSelected] = useState<api.GemReferenceDraftSelection[]>([]);
  return <GemCatalog selected={selected} onSelectionChange={(value) => { setSelected(value); onChange(value); }} follow={follow} />;
}
beforeEach(() => {
  vi.resetAllMocks();
  vi.mocked(api.browseSharedGems).mockResolvedValue({ entries: [], nextCursor: null });
  vi.mocked(api.listGemDrafts).mockResolvedValue({ drafts: [], nextCursor: null });
});

describe('shared catalog', () => {
  it('ignores failure from a superseded effect request during lifecycle replay', async () => {
    // GIVEN a discarded initial request and a successful replacement request.
    let rejectDiscarded!: (error: Error) => void;
    vi.mocked(api.browseSharedGems).mockImplementationOnce(() => new Promise((_resolve, reject) => { rejectDiscarded = reject; }))
      .mockResolvedValue({ entries: [entry], nextCursor: null });
    render(<StrictMode><Catalog /></StrictMode>);
    await screen.findByRole('link', { name: 'Ruby' });
    // WHEN the discarded request fails after the active request finishes.
    await act(async () => { rejectDiscarded(new Error('Aborted')); });
    // THEN the active result stays successful and no false retry error appears.
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Ruby' })).toBeVisible();
  });

  it('distinguishes failed loads from empty content and retries', async () => {
    // GIVEN the catalog service initially fails and later returns an empty catalog.
    vi.mocked(api.browseSharedGems).mockRejectedValueOnce(new Error('Unavailable'));
    render(<Catalog />);
    // WHEN loading fails THEN retry is offered without claiming the catalog is empty.
    expect(await screen.findByRole('alert')).toHaveTextContent('could not load');
    expect(screen.queryByText('No published entries yet.')).not.toBeInTheDocument();
    // WHEN the admin retries THEN the confirmed empty state offers creation.
    fireEvent.click(screen.getByRole('button', { name: 'Retry catalog' }));
    expect(await screen.findByText('No published entries yet.')).toBeVisible();
    expect(screen.getByRole('link', { name: 'New draft' })).toHaveAttribute('href', '/service-admin/gem-reference/new');
  });

  it('uses explicit cursor pagination without eagerly loading more entries', async () => {
    // GIVEN a catalog page with another page available.
    vi.mocked(api.browseSharedGems).mockResolvedValueOnce({ entries: [entry], nextCursor: 'next-page' })
      .mockResolvedValue({ entries: [{ ...entry, id: 'sapphire', commonName: 'Sapphire' }], nextCursor: null });
    render(<Catalog />);
    expect(await screen.findByRole('link', { name: 'Ruby' })).toHaveAttribute('href', '/service-admin/gem-reference/entries/ruby');
    expect(api.browseSharedGems).toHaveBeenCalledTimes(1);
    // WHEN more entries are requested THEN the opaque cursor reaches the server and both pages remain visible.
    fireEvent.click(screen.getByRole('button', { name: 'Load more entries' }));
    expect(await screen.findByRole('link', { name: 'Sapphire' })).toBeVisible();
    expect(screen.getByRole('link', { name: 'Ruby' })).toBeVisible();
    expect(api.browseSharedGems).toHaveBeenLastCalledWith(expect.objectContaining({ cursor: 'next-page' }), expect.any(AbortSignal));
  });

  it('retains loaded rows and retries the same continuation after a page failure', async () => {
    // GIVEN a loaded catalog page and a failed continuation.
    vi.mocked(api.browseSharedGems).mockResolvedValueOnce({ entries: [entry], nextCursor: 'next-page' })
      .mockRejectedValueOnce(new Error('Unavailable'))
      .mockResolvedValue({ entries: [{ ...entry, id: 'sapphire', commonName: 'Sapphire' }], nextCursor: null });
    render(<Catalog />);
    await screen.findByRole('link', { name: 'Ruby' });
    fireEvent.click(screen.getByRole('button', { name: 'Load more entries' }));
    await screen.findByRole('alert');
    expect(screen.getByRole('link', { name: 'Ruby' })).toBeVisible();
    // WHEN retrying THEN the same cursor appends the next page exactly once.
    fireEvent.click(screen.getByRole('button', { name: 'Retry catalog' }));
    await screen.findByRole('link', { name: 'Sapphire' });
    expect(screen.getAllByRole('link', { name: 'Ruby' })).toHaveLength(1);
    expect(screen.getAllByRole('link', { name: 'Sapphire' })).toHaveLength(1);
    expect(vi.mocked(api.browseSharedGems).mock.calls.slice(1).map(([query]) => query.cursor)).toEqual(['next-page', 'next-page']);
  });

  it('ignores an outdated response after a new server search', async () => {
    // GIVEN an initial search that responds after the newer query.
    let oldResponse!: (value: api.GemReferencePageResponse) => void;
    vi.mocked(api.browseSharedGems).mockImplementationOnce(() => new Promise((resolve) => { oldResponse = resolve; }))
      .mockResolvedValue({ entries: [{ ...entry, id: 'sapphire', commonName: 'Sapphire' }], nextCursor: null });
    render(<Catalog />);
    // WHEN a new query is submitted and then the old response arrives.
    fireEvent.change(screen.getByLabelText('Search shared gems'), { target: { value: 'Sapphire' } });
    fireEvent.click(screen.getByRole('button', { name: 'Search' }));
    await screen.findByRole('link', { name: 'Sapphire' });
    await act(async () => { oldResponse({ entries: [entry], nextCursor: null }); });
    // THEN the new result remains authoritative rather than being overwritten.
    expect(screen.queryByRole('link', { name: 'Ruby' })).not.toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Sapphire' })).toBeVisible();
  });

  it('retains selected draft IDs and versions across pages and limits the batch to fifty', async () => {
    // GIVEN fifty saved drafts and one further draft on another cursor page.
    vi.mocked(api.listGemDrafts).mockResolvedValueOnce({ drafts: Array.from({ length: 50 }, (_, index) => draft(String(index))), nextCursor: 'draft-next' })
      .mockResolvedValue({ drafts: [draft('50')], nextCursor: null });
    const onChange = vi.fn();
    render(<Catalog onChange={onChange} />);
    fireEvent.click(screen.getByRole('button', { name: 'Drafts' }));
    await screen.findByLabelText('Select Draft 0');
    // WHEN fifty drafts are selected and another page is loaded.
    for (let index = 0; index < 50; index += 1) fireEvent.click(screen.getByLabelText(`Select Draft ${index}`));
    fireEvent.click(screen.getByRole('button', { name: 'Load more drafts' }));
    // THEN further selection is disabled and the exact saved versions are available for review.
    expect(await screen.findByLabelText('Select Draft 50')).toBeDisabled();
    expect(screen.getByLabelText('Select Draft 0')).toBeChecked();
    expect(onChange).toHaveBeenLastCalledWith(Array.from({ length: 50 }, (_, index) => ({ draftId: String(index), expectedDraftRowVersion: `version-${index}` })));
    expect(api.listGemDrafts).toHaveBeenLastCalledWith('draft-next');
    expect(screen.getByRole('link', { name: 'Review 50 drafts' })).toHaveAttribute('href', '/service-admin/gem-reference/review');
    // WHEN a selection is cleared THEN the remaining draft becomes selectable.
    fireEvent.click(screen.getByLabelText('Select Draft 0'));
    expect(screen.getByLabelText('Select Draft 50')).toBeEnabled();
  });

  it('shows a failed draft load with retry instead of an empty draft list', async () => {
    // GIVEN saved-draft retrieval fails.
    vi.mocked(api.listGemDrafts).mockRejectedValueOnce(new Error('Unavailable')).mockResolvedValue({ drafts: [draft('1')], nextCursor: null });
    render(<Catalog />);
    // WHEN the drafts view is opened THEN its error offers its own retry.
    fireEvent.click(screen.getByRole('button', { name: 'Drafts' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('could not load');
    // WHEN retry succeeds THEN persisted draft content is shown.
    fireEvent.click(screen.getByRole('button', { name: 'Retry drafts' }));
    expect(await screen.findByRole('link', { name: 'Draft 1' })).toHaveAttribute('href', '/service-admin/gem-reference/drafts/1');
  });
});

describe('published detail', () => {
  it('shows retired content, taxonomy, safe claim sources, review dates and available drafts', async () => {
    // GIVEN a retired published entry with one safe link and unsafe source URLs.
    const source = { id: 'source', field: 'description', title: 'Reference', publisher: 'Institute', url: 'https://example.test/reference', citation: 'Volume 1', accessedOn: '2026-09-01', reviewedOn: '2026-09-02', attribution: 'Shared' };
    vi.mocked(api.getSharedGem).mockResolvedValue({ ...entry, rowVersion: 'published-v1', aliases: ['Red corundum'], description: 'A red variety.', notableLocality: { place: 'Mogok', scope: 'Historic deposits', reviewedOn: '2026-09-03', sourceAssertionId: 'source' }, retirement: { isRetired: true, explanation: 'Replaced classification', redirectEntryId: 'replacement' }, sourceAssertions: [source, { ...source, id: 'unsafe', title: 'Unsafe source', url: 'javascript:alert(1)' }, { ...source, id: 'userinfo', title: 'Userinfo source', url: 'https://user:pass@example.test/' }] });
    vi.mocked(api.listGemDrafts).mockResolvedValue({ drafts: [draft('existing')], nextCursor: null });
    // WHEN its stable detail URL is opened.
    render(<GemDetail entryId="ruby" follow={follow} />);
    // THEN read-only content includes retirement, supporting sources and explicit editing destinations.
    expect(await screen.findByRole('heading', { name: 'Ruby' })).toBeVisible();
    expect(screen.getByText('Retired')).toBeVisible();
    expect(screen.getByText('Replaced classification')).toBeVisible();
    expect(screen.getByText('Red corundum')).toBeVisible();
    expect(screen.getByText('Mogok')).toBeVisible();
    const link = screen.getByRole('link', { name: 'Reference' });
    expect(link).toHaveAttribute('rel', 'noopener noreferrer');
    expect(link).toHaveAttribute('target', '_blank');
    expect(screen.queryByRole('link', { name: 'Unsafe source' })).not.toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'Userinfo source' })).not.toBeInTheDocument();
    expect(screen.getAllByText('2026-09-02', { selector: 'time' })).toHaveLength(3);
    await waitFor(() => expect(screen.getByRole('link', { name: 'Open Draft existing' })).toHaveAttribute('href', '/service-admin/gem-reference/drafts/existing'));
    expect(screen.getByRole('link', { name: 'Edit entry' })).toHaveAttribute('href', '/service-admin/gem-reference/entries/ruby/edit');
  });
});
