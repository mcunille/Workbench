import { useState } from 'react';
import { fireEvent, render, screen } from '@testing-library/react';
import type { GemReferenceContent } from '../../api/gemReferenceAdmin';
import { GemSourceFields } from './GemSourceFields';

function Fields({ change }: { change: (content: GemReferenceContent) => void }) {
  const [content, setContent] = useState<GemReferenceContent>({ id: 'entry', commonName: 'Ruby', materialKind: 'mineral', group: null, species: null, variety: null, description: null, aliases: [], sources: [], notableLocality: null, isRetired: false, retirementExplanation: null, redirectEntryId: null });
  return <GemSourceFields content={content} onChange={(next) => { setContent(next); change(next); }} />;
}
it('keeps source identities and binds locality to the selected assertion and its review date', () => {
  // GIVEN an editor with two locality sources and optional locality.
  const change = vi.fn();
  render(<Fields change={change} />);
  fireEvent.click(screen.getByRole('button', { name: 'Add source' }));
  fireEvent.change(screen.getByLabelText('Source 1 field'), { target: { value: 'notableLocality' } });
  fireEvent.change(screen.getByLabelText('Source 1 title'), { target: { value: 'Myanmar handbook' } });
  fireEvent.change(screen.getByLabelText('Source 1 review date'), { target: { value: '2026-09-02' } });
  const sourceId = change.mock.lastCall![0].sources[0].id;
  fireEvent.click(screen.getByRole('button', { name: 'Add source' }));
  fireEvent.change(screen.getByLabelText('Source 2 field'), { target: { value: 'notableLocality' } });
  fireEvent.click(screen.getByLabelText('Include notable locality'));
  // WHEN locality chooses the first assertion THEN its identity and review date are preserved.
  fireEvent.change(screen.getByLabelText('Locality supporting source'), { target: { value: sourceId } });
  fireEvent.change(screen.getByLabelText('Locality place'), { target: { value: 'Mogok' } });
  expect(change.mock.lastCall![0]).toEqual(expect.objectContaining({ sources: expect.arrayContaining([expect.objectContaining({ id: sourceId })]), notableLocality: expect.objectContaining({ sourceAssertionId: sourceId, reviewedOn: '2026-09-02', place: 'Mogok' }) }));
  // WHEN that source date changes THEN locality follows the date of its selected evidence.
  fireEvent.change(screen.getByLabelText('Source 1 review date'), { target: { value: '2026-09-03' } });
  expect(change.mock.lastCall![0].notableLocality.reviewedOn).toBe('2026-09-03');
  // WHEN the selected source is removed THEN the locality citation is cleared for explicit correction.
  fireEvent.click(screen.getByRole('button', { name: 'Remove source 1' }));
  expect(change.mock.lastCall![0].notableLocality.sourceAssertionId).toBe('00000000-0000-0000-0000-000000000000');
  expect(screen.queryByLabelText('Source 2 title')).not.toBeInTheDocument();
});
