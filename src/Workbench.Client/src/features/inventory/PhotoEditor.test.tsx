import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { vi } from 'vitest';
import { PhotoEditor } from './PhotoEditor';
import { preparePhoto } from './preparePhoto';
import { putItemPhoto } from '../../api/items';
vi.mock('./preparePhoto', () => ({ preparePhoto: vi.fn() }));
vi.mock('../../api/items', () => ({
  putItemPhoto: vi.fn(),
  removeItemPhoto: vi.fn(),
  getPhoto: vi.fn(),
}));
it('keeps the prepared draft after an archive conflict without offering a fresh upload', async () => {
  // GIVEN a prepared image and another session archiving the record.
  const { ApiError } = await import('../../api/auth');
  vi.mocked(preparePhoto).mockResolvedValue(new Blob(['prepared']));
  vi.mocked(putItemPhoto).mockReset().mockRejectedValue(new ApiError(409));
  vi.stubGlobal(
    'URL',
    Object.assign(URL, {
      createObjectURL: vi.fn(() => 'blob:preview'),
      revokeObjectURL: vi.fn(),
    }),
  );
  const item = {
    id: 'item',
    name: 'Stone',
    notes: null,
    location: null,
    photo: null,
    version: 'old',
    createdAtUtc: '',
    archivedAtUtc: null as string | null,
  };
  const props = {
    onAuthLost: vi.fn(),
    onDirtyChange: vi.fn(),
    reload: vi.fn().mockResolvedValue(undefined),
  };
  const view = render(<PhotoEditor item={item} {...props} />);
  fireEvent.change(screen.getByLabelText('Choose photograph'), {
    target: { files: [new File(['image'], 'photo.jpg')] },
  });
  await screen.findByAltText('Prepared photograph preview');
  // WHEN a conflicting upload is followed by the current archived details.
  fireEvent.click(screen.getByRole('button', { name: 'Upload photograph' }));
  fireEvent.click(
    await screen.findByRole('button', { name: 'Reload current item' }),
  );
  await waitFor(() => expect(props.reload).toHaveBeenCalledOnce());
  view.rerender(
    <PhotoEditor
      item={{
        ...item,
        archivedAtUtc: '2026-09-07T01:00:00Z',
        version: 'new',
      }}
      {...props}
    />,
  );
  // THEN the draft remains available until explicitly discarded, and no mutation is offered.
  expect(screen.getByAltText('Prepared photograph preview')).toBeVisible();
  expect(
    screen.queryByRole('button', { name: 'Upload photograph' }),
  ).not.toBeInTheDocument();
  fireEvent.click(
    screen.getByRole('button', {
      name: 'Discard draft and view archived record',
    }),
  );
  expect(
    screen.queryByAltText('Prepared photograph preview'),
  ).not.toBeInTheDocument();
  vi.mocked(putItemPhoto).mockReset();
});
it('invalidates the cached photograph on success even when detail reload fails', async () => {
  // GIVEN a prepared upload and a successful server mutation followed by a failed refresh.
  vi.mocked(preparePhoto).mockResolvedValue(new Blob(['prepared']));
  vi.mocked(putItemPhoto).mockReset().mockResolvedValue({
    requestId: 'request',
    version: 'next',
    photoId: 'new',
  });
  vi.stubGlobal(
    'URL',
    Object.assign(URL, {
      createObjectURL: vi.fn(() => 'blob:preview'),
      revokeObjectURL: vi.fn(),
    }),
  );
  const invalidate = vi.fn();
  const reload = vi.fn(async () => {
    expect(invalidate).toHaveBeenCalledOnce();
    throw new Error('offline');
  });
  render(
    <PhotoEditor
      item={{
        id: 'item',
        name: 'Stone',
        notes: null,
        location: null,
        photo: null,
        version: 'old',
        createdAtUtc: '',
      }}
      onAuthLost={vi.fn()}
      onDirtyChange={vi.fn()}
      onPhotoChanged={invalidate}
      reload={reload}
    />,
  );
  // WHEN the collector uploads THEN the cache is invalidated before attempting detail refresh.
  fireEvent.change(screen.getByLabelText('Choose photograph'), {
    target: { files: [new File(['image'], 'photo.jpg')] },
  });
  await screen.findByAltText('Prepared photograph preview');
  fireEvent.click(screen.getByRole('button', { name: 'Upload photograph' }));
  await screen.findByRole('button', { name: 'Retry upload' });
  expect(invalidate).toHaveBeenCalledOnce();
  expect(reload).toHaveBeenCalledOnce();
  vi.mocked(putItemPhoto).mockReset();
});
it('previews locally and retries ambiguous uploads with the same command and bytes', async () => {
  // GIVEN a locally prepared photograph and an interrupted first upload.
  const blob = new Blob(['small'], { type: 'image/webp' });
  vi.mocked(preparePhoto).mockResolvedValue(blob);
  vi.mocked(putItemPhoto)
    .mockRejectedValueOnce(new TypeError('offline'))
    .mockResolvedValueOnce({ requestId: 'a', version: 'b', photoId: 'c' });
  const reload = vi.fn().mockResolvedValue(undefined);
  vi.stubGlobal(
    'URL',
    Object.assign(URL, {
      createObjectURL: vi.fn(() => 'blob:preview'),
      revokeObjectURL: vi.fn(),
    }),
  );
  render(
    <PhotoEditor
      item={{
        id: 'item',
        name: 'Stone',
        notes: null,
        location: null,
        createdAtUtc: '',
        version: 'original',
        photo: null,
      }}
      onAuthLost={vi.fn()}
      onDirtyChange={vi.fn()}
      reload={reload}
    />,
  );
  // WHEN selecting THEN preparation never uploads automatically.
  fireEvent.change(screen.getByLabelText('Choose photograph'), {
    target: { files: [new File(['large'], 'photo.jpg')] },
  });
  expect(
    await screen.findByAltText('Prepared photograph preview'),
  ).toBeVisible();
  expect(putItemPhoto).not.toHaveBeenCalled();
  // WHEN uploading and explicitly retrying THEN the command stays identical.
  fireEvent.click(screen.getByRole('button', { name: 'Upload photograph' }));
  fireEvent.click(
    await screen.findByRole('button', { name: 'Retry upload' }),
  );
  await waitFor(() => expect(reload).toHaveBeenCalledTimes(1));
  expect(vi.mocked(putItemPhoto).mock.calls[0]).toEqual(
    vi.mocked(putItemPhoto).mock.calls[1],
  );
  expect(vi.mocked(putItemPhoto).mock.calls[0][1]).toBe(blob);
});

it('requires reloading a conflict before a fresh explicit upload', async () => {
  // GIVEN a prepared image and another writer changing the item.
  vi.mocked(preparePhoto).mockResolvedValue(new Blob(['prepared']));
  const { ApiError } = await import('../../api/auth');
  vi.mocked(putItemPhoto).mockRejectedValue(new ApiError(409));
  const reload = vi.fn().mockResolvedValue(undefined);
  render(
    <PhotoEditor
      item={{
        id: 'item',
        name: 'Stone',
        notes: null,
        location: null,
        createdAtUtc: '',
        version: 'original',
        photo: null,
      }}
      onAuthLost={vi.fn()}
      onDirtyChange={vi.fn()}
      reload={reload}
    />,
  );
  fireEvent.change(screen.getByLabelText('Choose photograph'), {
    target: { files: [new File(['large'], 'photo.jpg')] },
  });
  await screen.findByAltText('Prepared photograph preview');
  // WHEN the server reports conflict THEN prevent another upload until reload.
  fireEvent.click(screen.getByRole('button', { name: 'Upload photograph' }));
  const reloadButton = await screen.findByRole('button', {
    name: 'Reload current item',
  });
  expect(
    screen.queryByRole('button', { name: 'Upload photograph' }),
  ).not.toBeInTheDocument();
  fireEvent.click(reloadButton);
  await screen.findByRole('button', { name: 'Upload photograph' });
  expect(reload).toHaveBeenCalledOnce();
});

it('confirms removal and keeps an ambiguous removal command for safe retry', async () => {
  // GIVEN an existing photo and an interrupted removal.
  const { removeItemPhoto, getPhoto } = await import('../../api/items');
  vi.mocked(getPhoto).mockResolvedValue(new Blob(['image']));
  vi.mocked(removeItemPhoto)
    .mockRejectedValueOnce(new TypeError('offline'))
    .mockResolvedValueOnce({ requestId: 'r', version: 'v', photoId: null });
  HTMLDialogElement.prototype.showModal = vi.fn();
  const reload = vi.fn().mockResolvedValue(undefined);
  const view = render(
    <PhotoEditor
      item={{
        id: 'item',
        name: 'Stone',
        notes: null,
        location: null,
        createdAtUtc: '',
        version: 'original',
        photo: {
          id: 'photo',
          thumbnailUrl: '/thumb',
          detailUrl: '/detail',
          width: 10,
          height: 10,
        },
      }}
      onAuthLost={vi.fn()}
      onDirtyChange={vi.fn()}
      reload={reload}
    />,
  );
  // WHEN asking to remove THEN nothing is sent before explicit confirmation.
  fireEvent.click(screen.getByRole('button', { name: 'Remove photograph' }));
  expect(removeItemPhoto).not.toHaveBeenCalled();
  fireEvent.click(
    screen.getByRole('button', { name: 'Confirm removal', hidden: true }),
  );
  // WHEN retrying THEN preserve the request id and version.
  fireEvent.click(
    await screen.findByRole('button', { name: 'Retry removal' }),
  );
  await waitFor(() => expect(reload).toHaveBeenCalledOnce());
  // AND the authoritative reload has no photograph, so announce that saved state.
  view.rerender(
    <PhotoEditor
      item={{
        id: 'item',
        name: 'Stone',
        notes: null,
        location: null,
        createdAtUtc: '',
        version: 'v',
        photo: null,
      }}
      onAuthLost={vi.fn()}
      onDirtyChange={vi.fn()}
      reload={reload}
    />,
  );
  expect(
    await screen.findByText('Current saved record has no photograph.'),
  ).toBeVisible();
  expect(vi.mocked(removeItemPhoto).mock.calls[0]).toEqual(
    vi.mocked(removeItemPhoto).mock.calls[1],
  );
});
