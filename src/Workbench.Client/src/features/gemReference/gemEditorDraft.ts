import type { GemReferenceContent, GemReferenceDetailResponse, GemReferenceFieldOverride } from '../../api/gemReference';

export type GemEditorDraft = { content: GemReferenceContent; overrides: Record<string, GemReferenceFieldOverride> };
export function newGemDraft(id: string): GemEditorDraft {
  return { content: { id, materialKind: 'mineral', commonName: '', species: null, group: null, variety: null, description: null, aliases: [], sources: [], notableLocality: null, isRetired: false, retirementExplanation: null, redirectEntryId: null }, overrides: {} };
}
export function draftFromGem(entry: GemReferenceDetailResponse): GemEditorDraft {
  const { id, materialKind, commonName, species, group, variety, description, aliases, notableLocality } = entry;
  const sources = entry.origin === 'tenant' ? entry.sourceAssertions.filter(source => source.attribution === 'tenant') : Object.values(entry.overrides ?? {}).flatMap(choice => choice.sources);
  return structuredClone({ content: { id, materialKind, commonName, species, group, variety, description, aliases, notableLocality, sources, isRetired: false, retirementExplanation: null, redirectEntryId: null }, overrides: entry.overrides ?? {} });
}
export function setGemFieldState(draft: GemEditorDraft, field: string, state: 'inherit' | 'replace' | 'clear'): GemEditorDraft {
  return { ...draft, content: { ...draft.content, sources: draft.content.sources.filter(source => source.field !== field) }, overrides: { ...draft.overrides, [field]: { state, value: state === 'replace' ? draft.content[field as keyof GemReferenceContent] : null, sources: [] } } };
}
export function buildGemOverrides(draft: GemEditorDraft): Record<string, GemReferenceFieldOverride> {
  return Object.fromEntries(Object.entries(draft.overrides).filter(([, choice]) => choice.state !== 'inherit').map(([field, choice]) => [field, {
    state: choice.state,
    value: choice.state === 'replace' ? draft.content[field as keyof GemReferenceContent] : null,
    sources: choice.state === 'replace' ? draft.content.sources.filter(source => source.field === field) : [],
  }]));
}
