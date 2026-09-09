import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { vi } from 'vitest';
import { server } from '../../test/server';
import { ItemDetails } from './Collection';

const item = {
  id: 'stone',
  name: 'Stone',
  notes: null,
  location: null,
  photo: null,
  version: 'old',
  createdAtUtc: '2026-01-01T00:00:00Z',
  archivedAtUtc: null as string | null,
};
const acquisition = {
  id: 'origin',
  method: 'Gift',
  source: 'Aunt May',
  year: 1998,
  month: null,
  day: null,
  notes: 'Family collection',
  version: 'a1',
};
function setup() {
  const onDirtyChange = vi.fn();
  const onAuthLost = vi.fn();
  render(
    <ItemDetails
      id="stone"
      onDirtyChange={onDirtyChange}
      onAuthLost={onAuthLost}
      follow={vi.fn()}
    />,
  );
  return { onDirtyChange, onAuthLost };
}
beforeEach(() => {
  server.use(
    http.get('*/api/items/stone', () => HttpResponse.json(item)),
    http.get('*/api/items/stone/acquisition', () =>
      HttpResponse.json({ acquisition: null, itemVersion: 'old' }),
    ),
    http.get('*/api/auth/antiforgery', () =>
      HttpResponse.json({ requestToken: 'csrf' }),
    ),
  );
});

it('adds a gift with unknown date and source without inventing facts or another item', async () => {
  // GIVEN a saved item without acquisition context.
  const commands: Record<string, unknown>[] = [];
  server.use(
    http.post('*/api/items/stone/acquisition', async ({ request }) => {
      const body = (await request.json()) as Record<string, unknown>;
      commands.push(body);
      return HttpResponse.json(
        {
          acquisition: {
            ...acquisition,
            source: null,
            year: null,
            notes: null,
          },
          itemVersion: 'next',
        },
        { status: 201 },
      );
    }),
  );
  const { onDirtyChange } = setup();
  // WHEN adding only the known method.
  fireEvent.click(
    await screen.findByRole('button', { name: 'Add acquisition' }),
  );
  expect(screen.getByLabelText('Acquisition method')).toHaveValue('');
  fireEvent.change(screen.getByLabelText('Acquisition method'), {
    target: { value: 'Gift' },
  });
  expect(screen.getByLabelText('Gift from (optional)')).toHaveValue('');
  expect(screen.getByRole('button', { name: 'Edit details' })).toBeDisabled();
  expect(screen.getByLabelText('Choose photograph')).toBeDisabled();
  fireEvent.click(screen.getByRole('button', { name: 'Save acquisition' }));
  // THEN the request preserves unknowns, and current context replaces the form.
  await screen.findByRole('button', { name: 'Edit acquisition' });
  expect(commands).toEqual([
    expect.objectContaining({
      expectedItemVersion: 'old',
      method: 'Gift',
      source: null,
      year: null,
      month: null,
      day: null,
      notes: null,
    }),
  ]);
  expect(commands[0].creationRequestId).toEqual(expect.any(String));
  expect(onDirtyChange).toHaveBeenLastCalledWith(false, false);
});

it('retries an uncertain creation with the same UUID, versions and frozen input', async () => {
  // GIVEN a lost create response.
  const commands: unknown[] = [];
  server.use(
    http.post('*/api/items/stone/acquisition', async ({ request }) => {
      commands.push(await request.json());
      return commands.length === 1
        ? HttpResponse.error()
        : HttpResponse.json({ acquisition, itemVersion: 'next' });
    }),
  );
  setup();
  fireEvent.click(
    await screen.findByRole('button', { name: 'Add acquisition' }),
  );
  fireEvent.change(screen.getByLabelText('Acquisition method'), {
    target: { value: 'Gift' },
  });
  fireEvent.change(screen.getByLabelText('Gift from (optional)'), {
    target: { value: 'Aunt May' },
  });
  // WHEN the response is lost THEN prevent editing the uncertain command.
  fireEvent.click(screen.getByRole('button', { name: 'Save acquisition' }));
  await screen.findByText(/could not confirm/);
  expect(screen.getByLabelText('Gift from (optional)')).toBeDisabled();
  fireEvent.click(screen.getByRole('button', { name: 'Retry save' }));
  // THEN a retry does not create another request identity.
  await screen.findByRole('button', { name: 'Edit acquisition' });
  expect(commands).toHaveLength(2);
  expect(commands[1]).toEqual(commands[0]);
});

it('retains conflicting edits through a failed read and starts reconciliation from current values', async () => {
  // GIVEN existing context and another session changing it.
  let reads = 0;
  const commands: Record<string, unknown>[] = [];
  server.use(
    http.get('*/api/items/stone/acquisition', () => {
      reads++;
      if (reads === 2) return HttpResponse.error();
      return HttpResponse.json({
        acquisition:
          reads === 1
            ? acquisition
            : { ...acquisition, source: 'New source', version: 'a2' },
        itemVersion: reads === 1 ? 'old' : 'new',
      });
    }),
    http.put('*/api/items/stone/acquisition/origin', async ({ request }) => {
      commands.push((await request.json()) as Record<string, unknown>);
      return HttpResponse.json(
        { code: 'acquisition_version_conflict' },
        { status: 409 },
      );
    }),
  );
  setup();
  fireEvent.click(
    await screen.findByRole('button', { name: 'Edit acquisition' }),
  );
  fireEvent.change(screen.getByLabelText('Gift from (optional)'), {
    target: { value: 'My draft' },
  });
  // WHEN saving with a stale version and failing to refresh THEN preserve input with no unlocked save.
  fireEvent.click(screen.getByRole('button', { name: 'Save acquisition' }));
  await screen.findByText(/could not load the current acquisition/);
  expect(screen.getByText('My draft')).toBeVisible();
  expect(
    screen.queryByRole('button', { name: 'Save acquisition' }),
  ).not.toBeInTheDocument();
  fireEvent.click(
    screen.getByRole('button', { name: 'Retry loading current acquisition' }),
  );
  fireEvent.click(
    await screen.findByRole('button', { name: 'Review my edits' }),
  );
  // THEN fresh values and versions require deliberate edits, even for a second conflict.
  expect(screen.getByLabelText('Gift from (optional)')).toHaveValue(
    'New source',
  );
  expect(screen.getByText('My draft')).toBeVisible();
  fireEvent.click(screen.getByRole('button', { name: 'Save acquisition' }));
  await screen.findByRole('button', { name: 'Review my edits' });
  expect(commands[1]).toMatchObject({
    expectedItemVersion: 'new',
    expectedAcquisitionVersion: 'a2',
    source: 'New source',
  });
});

it('distinguishes a failed acquisition load from an empty acquisition', async () => {
  // GIVEN a read failure WHEN details open THEN no Add acquisition action is offered.
  server.use(
    http.get('*/api/items/stone/acquisition', () => HttpResponse.error()),
  );
  setup();
  await screen.findByText(/could not load acquisition context/);
  expect(
    screen.queryByRole('button', { name: 'Add acquisition' }),
  ).not.toBeInTheDocument();
  // WHEN retry succeeds THEN creation becomes available.
  server.use(
    http.get('*/api/items/stone/acquisition', () =>
      HttpResponse.json({ acquisition: null, itemVersion: 'old' }),
    ),
  );
  fireEvent.click(
    screen.getByRole('button', { name: 'Retry loading acquisition' }),
  );
  await screen.findByRole('button', { name: 'Add acquisition' });
});

it('shows retained context on archived items without mutation controls', async () => {
  // GIVEN an archived item WHEN loading context THEN retain its precise year and provenance.
  server.use(
    http.get('*/api/items/stone', () =>
      HttpResponse.json({ ...item, archivedAtUtc: '2026-01-02T00:00:00Z' }),
    ),
    http.get('*/api/items/stone/acquisition', () =>
      HttpResponse.json({ acquisition, itemVersion: 'old' }),
    ),
  );
  setup();
  expect(await screen.findByText('Aunt May')).toBeVisible();
  expect(screen.getByText('1998')).toBeVisible();
  expect(
    screen.queryByRole('button', { name: /^(Add|Edit) acquisition$/ }),
  ).not.toBeInTheDocument();
});

it('focuses server validation, retains fields, and preserves partial date precision', async () => {
  // GIVEN authoritative date validation rejecting a future year.
  const commands: Record<string, unknown>[] = [];
  server.use(
    http.post('*/api/items/stone/acquisition', async ({ request }) => {
      commands.push((await request.json()) as Record<string, unknown>);
      return HttpResponse.json(
        { errors: { year: ['The acquired date cannot be in the future.'] } },
        { status: 400 },
      );
    }),
  );
  setup();
  fireEvent.click(
    await screen.findByRole('button', { name: 'Add acquisition' }),
  );
  fireEvent.change(screen.getByLabelText('Acquisition method'), {
    target: { value: 'Inheritance' },
  });
  fireEvent.change(screen.getByLabelText('Date precision'), {
    target: { value: 'Year' },
  });
  fireEvent.change(screen.getByLabelText('Year'), {
    target: { value: '9999' },
  });
  // WHEN saving THEN no artificial month/day is sent, and correction stays editable.
  fireEvent.click(screen.getByRole('button', { name: 'Save acquisition' }));
  await screen.findByText('The acquired date cannot be in the future.');
  expect(screen.getByLabelText('Year')).toHaveFocus();
  expect(screen.getByLabelText('Year')).toHaveValue(9999);
  expect(screen.getByLabelText('Year')).toBeEnabled();
  expect(commands[0]).toMatchObject({
    year: 9999,
    month: null,
    day: null,
    source: null,
  });
});

it('cancels unsent acquisition input and restores focus without writing', async () => {
  // GIVEN an unsent draft WHEN cancelling THEN the item remains without an acquisition.
  const write = vi.fn(() => HttpResponse.error());
  server.use(http.post('*/api/items/stone/acquisition', write));
  setup();
  fireEvent.click(
    await screen.findByRole('button', { name: 'Add acquisition' }),
  );
  fireEvent.change(screen.getByLabelText('Acquisition method'), {
    target: { value: 'Trade' },
  });
  fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));
  await waitFor(() =>
    expect(
      screen.getByRole('button', { name: 'Add acquisition' }),
    ).toHaveFocus(),
  );
  expect(write).not.toHaveBeenCalled();
});

it('requires an explicit method and the components of the chosen precision before sending', async () => {
  // GIVEN no facts selected WHEN saving THEN focus a useful method error without a request.
  const write = vi.fn(() => HttpResponse.error());
  server.use(http.post('*/api/items/stone/acquisition', write));
  setup();
  fireEvent.click(
    await screen.findByRole('button', { name: 'Add acquisition' }),
  );
  fireEvent.click(screen.getByRole('button', { name: 'Save acquisition' }));
  expect(screen.getByLabelText('Acquisition method')).toHaveFocus();
  expect(write).not.toHaveBeenCalled();
  // WHEN exact precision lacks a day THEN require that fact or a less precise selection.
  fireEvent.change(screen.getByLabelText('Acquisition method'), {
    target: { value: 'Unknown' },
  });
  fireEvent.change(screen.getByLabelText('Date precision'), {
    target: { value: 'Exact date' },
  });
  fireEvent.change(screen.getByLabelText('Year'), {
    target: { value: '2000' },
  });
  fireEvent.change(screen.getByLabelText('Month'), { target: { value: '2' } });
  fireEvent.click(screen.getByRole('button', { name: 'Save acquisition' }));
  expect(screen.getByLabelText('Day')).toHaveFocus();
  expect(write).not.toHaveBeenCalled();
});

it('retains a draft when another session archives and cannot reconcile into a write', async () => {
  // GIVEN active context WHEN another session archives before our save.
  server.use(
    http.get('*/api/items/stone/acquisition', () =>
      HttpResponse.json({ acquisition, itemVersion: 'old' }),
    ),
    http.put('*/api/items/stone/acquisition/origin', () =>
      HttpResponse.json({ code: 'item_archived' }, { status: 409 }),
    ),
  );
  setup();
  fireEvent.click(
    await screen.findByRole('button', { name: 'Edit acquisition' }),
  );
  fireEvent.change(screen.getByLabelText('Gift from (optional)'), {
    target: { value: 'Keep this draft' },
  });
  server.use(
    http.get('*/api/items/stone', () =>
      HttpResponse.json({
        ...item,
        archivedAtUtc: '2026-01-02T00:00:00Z',
        version: 'archived',
      }),
    ),
  );
  fireEvent.click(screen.getByRole('button', { name: 'Save acquisition' }));
  // THEN retain the draft, with no fresh-token save against the archived item.
  await screen.findByText(/This item is archived and read-only/);
  expect(screen.getByText('Keep this draft')).toBeVisible();
  expect(
    screen.queryByRole('button', { name: 'Review my edits' }),
  ).not.toBeInTheDocument();
  fireEvent.click(screen.getByRole('button', { name: 'Use saved record' }));
  await screen.findByRole('button', { name: 'Restore to collection' });
  expect(
    screen.queryByRole('button', { name: 'Edit acquisition' }),
  ).not.toBeInTheDocument();
});

it('refreshes descriptive fields with their version after acquisition replay', async () => {
  // GIVEN a creation replay returning context after another session also renamed the item.
  server.use(
    http.post('*/api/items/stone/acquisition', () => {
      server.use(
        http.get('*/api/items/stone', () =>
          HttpResponse.json({
            ...item,
            name: 'Current name',
            version: 'newest',
          }),
        ),
      );
      return HttpResponse.json({ acquisition, itemVersion: 'newer' });
    }),
  );
  setup();
  fireEvent.click(
    await screen.findByRole('button', { name: 'Add acquisition' }),
  );
  fireEvent.change(screen.getByLabelText('Acquisition method'), {
    target: { value: 'Gift' },
  });
  // WHEN creation succeeds THEN never pair the stale name with a fresh item version.
  fireEvent.click(screen.getByRole('button', { name: 'Save acquisition' }));
  await screen.findByRole('heading', { name: 'Current name' });
  fireEvent.click(screen.getByRole('button', { name: 'Edit details' }));
  expect(screen.getByLabelText('Name')).toHaveValue('Current name');
});

it('delegates authentication loss without retaining an enabled save flow', async () => {
  // GIVEN an expired session WHEN context loading is rejected THEN notify the private-state owner.
  server.use(
    http.get(
      '*/api/items/stone/acquisition',
      () => new HttpResponse(null, { status: 401 }),
    ),
  );
  const { onAuthLost } = setup();
  await waitFor(() => expect(onAuthLost).toHaveBeenCalledOnce());
  expect(
    screen.queryByRole('button', { name: 'Add acquisition' }),
  ).not.toBeInTheDocument();
});

it.each(['2020.5', '10000', '2147483648'])(
  'keeps an invalid year editable without sending it: %s',
  async (year) => {
    // GIVEN a numeric value that cannot represent an acquired year.
    const write = vi.fn(() =>
      HttpResponse.json({ acquisition, itemVersion: 'next' }),
    );
    server.use(http.post('*/api/items/stone/acquisition', write));
    setup();
    fireEvent.click(
      await screen.findByRole('button', { name: 'Add acquisition' }),
    );
    fireEvent.change(screen.getByLabelText('Acquisition method'), {
      target: { value: 'Gift' },
    });
    fireEvent.change(screen.getByLabelText('Date precision'), {
      target: { value: 'Year' },
    });
    fireEvent.change(screen.getByLabelText('Year'), {
      target: { value: year },
    });
    // WHEN saving THEN no unbindable command is sent or frozen as uncertain.
    fireEvent.click(screen.getByRole('button', { name: 'Save acquisition' }));
    await screen.findByText('Enter a whole year from 1 to 9999.');
    expect(screen.getByLabelText('Year')).toBeEnabled();
    expect(screen.getByLabelText('Year')).toHaveFocus();
    expect(write).not.toHaveBeenCalled();
  },
);

it('keeps input editable after a definite bad request without field details', async () => {
  // GIVEN a definitive binding rejection without a field-error body.
  server.use(
    http.post(
      '*/api/items/stone/acquisition',
      () => new HttpResponse(null, { status: 400 }),
    ),
  );
  setup();
  fireEvent.click(
    await screen.findByRole('button', { name: 'Add acquisition' }),
  );
  fireEvent.change(screen.getByLabelText('Acquisition method'), {
    target: { value: 'Trade' },
  });
  // WHEN the response arrives THEN allow correction, without calling the rejected save uncertain.
  fireEvent.click(screen.getByRole('button', { name: 'Save acquisition' }));
  await screen.findByText(
    'The request could not be accepted. Check the entered facts and try again.',
  );
  expect(screen.getByLabelText('Acquisition method')).toBeEnabled();
  expect(
    screen.queryByRole('button', { name: 'Retry save' }),
  ).not.toBeInTheDocument();
});
