// Copyright (c) 2026 The White Stag Collection.

using System.Text.Json;

namespace Workbench.Server.Gemology;

public sealed record GemReferenceFieldOverride(string State, JsonElement? Value,
    IReadOnlyList<GemReferenceSourceContent> Sources);
public sealed record GemReferenceEffectiveVersion(string? SharedRowVersion, string? TenantRowVersion);
public sealed record GemReferenceEffectiveField(string State, string Attribution,
    IReadOnlyList<GemReferenceSourceResponse> Sources);
public sealed record GemReferenceTenantUpdateRequest(GemReferenceContent Content, GemReferenceEffectiveVersion EffectiveVersion);
public sealed record GemReferenceOverridesRequest(IReadOnlyDictionary<string, GemReferenceFieldOverride> Overrides,
    GemReferenceEffectiveVersion EffectiveVersion);
public sealed record GemReferenceResetRequest(string? Field, GemReferenceEffectiveVersion EffectiveVersion);
public sealed record GemReferenceTenantVersionRequest(GemReferenceEffectiveVersion EffectiveVersion);

public sealed record GemReferenceTenantProblemResponse(int Status, string Title, string Code,
    IReadOnlyDictionary<string, string[]> Errors, GemReferenceDetailResponse? Current);
