import { buildGemOverrides, draftFromGem, newGemDraft, setGemFieldState } from './gemEditorDraft';
import { referenceFixture } from './gemFixture';

it('preserves retained invalid choices and strips displaced shared citations from replacements', () => {
  // GIVEN a retained clear requires review, plus a separate tenant description and citation.
  const draft = draftFromGem({ ...referenceFixture, species: null, description: 'My notes', needsReview: true, overrides: { species: { state: 'clear', value: null, sources: [] }, description: { state: 'replace', value: 'My notes', sources: [] } } });
  // WHEN replacing a previously inherited field THEN its shared citation is not copied.
  const replaced = setGemFieldState(draft, 'materialKind', 'replace');
  expect(buildGemOverrides(replaced)).toEqual({ species: { state: 'clear', value: null, sources: [] }, description: { state: 'replace', value: 'My notes', sources: [] }, materialKind: { state: 'replace', value: 'mineral', sources: [] } });
  // WHEN resetting a single choice THEN other unsaved choices survive.
  const reset = setGemFieldState(replaced, 'species', 'inherit');
  expect(buildGemOverrides(reset)).not.toHaveProperty('species');
  expect(buildGemOverrides(reset).description.value).toBe('My notes');
});

it('serializes aliases as an empty replacement and explicit clears without sources', () => {
  // GIVEN aliases deliberately replaced with no search terms and a sourced description.
  const draft = newGemDraft('stable');
  expect(draft.content.species).toBeNull();
  draft.content.description = 'Personal notes';
  draft.content.sources = [{ id: 'citation', field: 'description', title: 'Notebook', publisher: 'Owner', url: null, citation: 'Page 1', reviewedOn: '2026-10-01', accessedOn: null }];
  // WHEN replacing aliases and clearing description THEN empty aliases are not inheritance.
  const changed = setGemFieldState(setGemFieldState(draft, 'aliases', 'replace'), 'description', 'clear');
  expect(buildGemOverrides(changed)).toEqual({ aliases: { state: 'replace', value: [], sources: [] }, description: { state: 'clear', value: null, sources: [] } });
  expect(changed.content.id).toBe('stable');
});

it('keeps only tenant sources when converting effective data into editable content', () => {
  // GIVEN an effective entry includes both tenant and Workbench citations.
  const source = referenceFixture.effectiveFields!.species.sources[0];
  const draft = draftFromGem({ ...referenceFixture, origin: 'tenant', sourceAssertions: [source, { ...source, id: 'tenant-source', attribution: 'tenant' }] });
  // WHEN editing THEN an inherited citation cannot be resubmitted as a tenant assertion.
  expect(draft.content.sources.map(item => item.id)).toEqual(['tenant-source']);
  expect(draft.content.isRetired).toBe(false);
});
