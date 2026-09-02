import { http, HttpResponse } from 'msw';
import { getSystem } from './system';
import { server } from '../test/server';

describe('getSystem', () => {
  it('returns the generated system response contract', async () => {
    server.use(
      http.get('*/api/system', () =>
        HttpResponse.json({ name: 'Workbench', version: '1.2.3' }),
      ),
    );

    await expect(getSystem()).resolves.toEqual({
      name: 'Workbench',
      version: '1.2.3',
    });
  });
});
