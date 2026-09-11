import { setupServer } from 'msw/node';
import { http, HttpResponse } from 'msw';

// Existing item fixtures have no acquisition. Acquisition scenarios override this default.
export const server = setupServer(
  // Existing acquisition fixtures predate documents; document scenarios override this empty list.
  http.get('*/api/items/:id/acquisition/:acquisitionId/documents', () =>
    HttpResponse.json({ documents: [], itemVersion: 'AAAAAAAAAAA=', acquisitionVersion: 'AAAAAAAAAAA=' }),
  ),
  http.get('*/api/items/:id/acquisition', () =>
    HttpResponse.json({ acquisition: null, itemVersion: 'AAAAAAAAAAA=' }),
  ),
);
