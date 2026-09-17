import { http, HttpResponse } from 'msw';
import { getSystem } from './system';
import { server } from '../test/server';

describe('getSystem', () => {
  it('returns the generated system response contract', async () => {
    server.use(
      http.get('*/api/beta/system', () =>
        HttpResponse.json({ name: 'Workbench', version: '1.2.3', apiRevision: 'beta-1' }),
      ),
    );

    await expect(getSystem()).resolves.toEqual({
      name: 'Workbench',
      version: '1.2.3',
      apiRevision: 'beta-1',
    });
  });
});
