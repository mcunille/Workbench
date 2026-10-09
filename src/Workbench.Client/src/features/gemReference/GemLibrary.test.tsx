import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import * as api from '../../api/gemReference';
import { GemLibrary } from './GemLibrary';
import { GemLibraryMemory } from './gemLibraryMemory';

const ruby = { id: 'same', origin: 'workbench', commonName: 'Ruby', materialKind: 'mineral', group: 'Corundum', species: 'Corundum', variety: 'Ruby', layer: 'workbenchReference' };
const follow = vi.fn((event: React.MouseEvent<HTMLAnchorElement>) => event.preventDefault());
const lost = vi.fn();
beforeEach(() => {
  vi.spyOn(window, 'scrollTo').mockImplementation(() => {});
  vi.spyOn(api, 'browseGems').mockResolvedValue({ entries: [ruby], nextCursor: null });
});
afterEach(() => vi.restoreAllMocks());
function mount(memory = new GemLibraryMemory()) {
  return { memory, ...render(<GemLibrary memory={memory} follow={follow} onAuthLost={lost} />) };
}
function submit(value: string) {
  fireEvent.change(screen.getByLabelText('Search gems'), { target: { value } });
  fireEvent.click(screen.getByRole('button', { name: 'Search' }));
}

it('submits name/classification and filters, qualifies links and restores a traversal', async () => {
  // GIVEN both origins use one GUID and the tenant row needs review.
  vi.mocked(api.browseGems).mockResolvedValue({ entries: [ruby, { ...ruby, origin: 'tenant', commonName: 'Private ruby', layer: 'tenantEntry', needsReview: true }], nextCursor: null });
  const view = mount();
  // WHEN submitting a familiar name or classification and material/group filters.
  await screen.findByRole('link', { name: 'Ruby' });
  fireEvent.change(screen.getByLabelText('Material kind', { exact: true }), { target: { value: 'mineral' } });
  fireEvent.change(screen.getByLabelText('Group'), { target: { value: ' Corundum ' } });
  submit(' red & ruby ');
  await waitFor(() => expect(api.browseGems).toHaveBeenLastCalledWith({ query: 'red & ruby', materialKind: 'mineral', group: 'Corundum' }, expect.any(AbortSignal)));
  // THEN identities and review warnings are visible without validated classification on the invalid row.
  expect(screen.getByRole('link', { name: 'Ruby' })).toHaveAttribute('href', '/gem-reference/workbench/same');
  const privateLink = screen.getByRole('link', { name: 'Private ruby' });
  expect(privateLink).toHaveAttribute('href', '/gem-reference/tenant/same');
  expect(privateLink.closest('li')).toHaveTextContent('Needs review');
  expect(privateLink.closest('li')).not.toHaveTextContent('Corundum');
  expect(screen.getByText('Tenant entry')).toBeVisible();
  fireEvent.click(privateLink);
  view.unmount();
  mount(view.memory);
  expect(screen.getByLabelText('Search gems')).toHaveValue(' red & ruby ');
  expect(screen.getByLabelText('Group')).toHaveValue(' Corundum ');
  expect(screen.getByRole('link', { name: 'Private ruby' })).toHaveFocus();
});

it('retains loaded rows and retries the failed continuation cursor', async () => {
  // GIVEN the first page loaded and the next page fails once.
  vi.mocked(api.browseGems).mockResolvedValueOnce({ entries: [ruby], nextCursor: 'next' }).mockRejectedValueOnce(new api.GemReferenceApiError(503, null)).mockResolvedValueOnce({ entries: [{ ...ruby, id: 'emerald', commonName: 'Emerald' }], nextCursor: null });
  mount();
  await screen.findByRole('link', { name: 'Ruby' });
  // WHEN retrying the failed continuation.
  fireEvent.click(screen.getByRole('button', { name: 'Load more entries' }));
  fireEvent.click(await screen.findByRole('button', { name: 'Retry' }));
  // THEN prior content stays, the same opaque cursor is used and results append.
  await screen.findByRole('link', { name: 'Emerald' });
  expect(screen.getByRole('link', { name: 'Ruby' })).toBeVisible();
  expect(api.browseGems).toHaveBeenLastCalledWith({ cursor: 'next' }, expect.any(AbortSignal));
});

it('ignores older responses after a new search and clears filters explicitly', async () => {
  // GIVEN the original request remains unresolved even after cancellation.
  let resolveOld!: (page: api.GemReferencePageResponse) => void;
  vi.mocked(api.browseGems).mockImplementationOnce(() => new Promise(resolve => { resolveOld = resolve; })).mockResolvedValue({ entries: [{ ...ruby, commonName: 'Sapphire' }], nextCursor: null });
  mount();
  // WHEN a newer query completes first and the obsolete request later returns.
  submit('Sapphire');
  await screen.findByRole('link', { name: 'Sapphire' });
  await act(async () => resolveOld({ entries: [ruby], nextCursor: 'obsolete' }));
  // THEN obsolete content/cursors cannot replace current results.
  expect(screen.queryByRole('link', { name: 'Ruby' })).not.toBeInTheDocument();
  expect(screen.queryByRole('button', { name: 'Load more entries' })).not.toBeInTheDocument();
  fireEvent.click(screen.getByRole('button', { name: 'Clear filters' }));
  await waitFor(() => expect(api.browseGems).toHaveBeenLastCalledWith({}, expect.any(AbortSignal)));
  expect(screen.getByLabelText('Search gems')).toHaveValue('');
});

it('distinguishes an empty catalog, no matches, validation errors and ended authority', async () => {
  // GIVEN an empty catalog followed by no search matches, a validation error and an ended session.
  vi.mocked(api.browseGems).mockResolvedValueOnce({ entries: [], nextCursor: null }).mockResolvedValueOnce({ entries: [], nextCursor: null }).mockRejectedValueOnce(new api.GemReferenceApiError(400, { title: 'Review the material kind and group filters.' })).mockRejectedValueOnce(new api.GemReferenceApiError(401, null));
  const { memory } = mount();
  expect(screen.getByRole('status')).toHaveTextContent('Loading');
  await screen.findByText('No gem entries are available yet.');
  // WHEN searches produce different outcomes THEN each has its own recovery.
  submit('Pearl');
  await screen.findByText('No gems match these filters.');
  submit('Invalid');
  await screen.findByRole('alert');
  expect(screen.getByRole('alert')).toHaveTextContent('Review the material kind and group filters.');
  submit('Ended');
  await waitFor(() => expect(lost).toHaveBeenCalled());
  expect(memory.snapshot).toBeUndefined();
});
