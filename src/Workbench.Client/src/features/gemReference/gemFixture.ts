import type { GemReferenceDetailResponse } from '../../api/gemReference';

// Synthetic assertions for UI tests; never installed as reference content.
export const referenceFixture: GemReferenceDetailResponse = {
  id: 'ruby', origin: 'workbench', commonName: 'Synthetic ruby', materialKind: 'mineral', group: null, species: 'Corundum', variety: 'Ruby', layer: 'workbenchReferenceCustomized', aliases: ['Synthetic red gem'], description: null, rowVersion: 'v1',
  retirement: { isRetired: false, explanation: null, redirectEntryId: null }, sourceAssertions: [],
  notableLocality: { place: 'Synthetic valley', scope: 'Exceptional quality has been reported in this locality.', reviewedOn: '2026-10-01', sourceAssertionId: 'shared-id' },
  effectiveFields: {
    commonName: { state: 'inherit', attribution: 'workbench', sources: [] },
    materialKind: { state: 'inherit', attribution: 'workbench', sources: [] },
    group: { state: 'clear', attribution: 'tenant', sources: [] },
    species: { state: 'inherit', attribution: 'workbench', sources: [{ id: 'shared-id', field: 'species', title: 'Synthetic taxonomy', publisher: 'Reference institute', url: 'https://example.test/taxonomy', citation: 'Synthetic classification citation', accessedOn: '2026-10-02', reviewedOn: '2026-10-01', attribution: 'workbench' }] },
    variety: { state: 'replace', attribution: 'tenant', sources: [] },
    aliases: { state: 'replace', attribution: 'tenant', sources: [] },
    description: { state: 'inherit', attribution: 'workbench', sources: [] },
    notableLocality: { state: 'inherit', attribution: 'workbench', sources: [{ id: 'shared-id', field: 'notableLocality', title: 'Synthetic locality report', publisher: 'Reference institute', url: 'https://example.test/locality', citation: 'Synthetic locality citation', accessedOn: null, reviewedOn: '2026-10-01', attribution: 'workbench' }] },
  },
};
