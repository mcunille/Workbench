import { setupServer } from 'msw/node';
import { http, HttpResponse } from 'msw';

// Existing item fixtures have no acquisition. Acquisition scenarios override this default.
export const server = setupServer(
  http.get('*/api/items/:id/acquisition', () =>
    HttpResponse.json({ acquisition: null, itemVersion: 'AAAAAAAAAAA=' }),
  ),
);
