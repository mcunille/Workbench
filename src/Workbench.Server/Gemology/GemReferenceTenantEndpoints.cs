// Copyright (c) 2026 The White Stag Collection.

using System.Security.Claims;
using System.Text.Json;
using Workbench.Server.Http;

namespace Workbench.Server.Gemology;

public sealed class GemReferenceTenantWriteMetadata;

public static class GemReferenceTenantEndpoints
{
    public static IEndpointRouteBuilder MapGemReferenceTenantWrites(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/beta/gem-reference").WithTags("Gem reference").RequireAuthorization();

        Write<GemReferenceContent>(group.MapPost("/tenant-entries", async (HttpContext http, GemReferenceTenantService service, CancellationToken ct) =>
            await HandleAsync<GemReferenceContent>(http, content => ContentShape(content),
                content => service.CreateAsync(Actor(http), content, ct), ct)));
        Write<GemReferenceTenantUpdateRequest>(group.MapPut("/tenant-entries/{id:guid}", async (Guid id, HttpContext http, GemReferenceTenantService service, CancellationToken ct) =>
            await HandleAsync<GemReferenceTenantUpdateRequest>(http, request => Shape(request.Content, request.EffectiveVersion),
                request => service.UpdateAsync(Actor(http), id, request.Content, request.EffectiveVersion, ct), ct)));
        Write<GemReferenceOverridesRequest>(group.MapPut("/{id:guid}/overrides", async (Guid id, HttpContext http, GemReferenceTenantService service, CancellationToken ct) =>
            await HandleAsync<GemReferenceOverridesRequest>(http, request => OverrideShape(request),
                request => service.SaveOverridesAsync(Actor(http), id, request.Overrides, request.EffectiveVersion, ct), ct)));
        Write<GemReferenceResetRequest>(group.MapPost("/{id:guid}/reset", async (Guid id, HttpContext http, GemReferenceTenantService service, CancellationToken ct) =>
            await HandleAsync<GemReferenceResetRequest>(http, request => VersionShape(request.EffectiveVersion),
                request => service.ResetAsync(Actor(http), id, request.Field, request.EffectiveVersion, ct), ct)));
        foreach (var (path, archived) in new[] { ("archive", true), ("restore", false) })
            Write<GemReferenceTenantVersionRequest>(group.MapPost("/tenant-entries/{id:guid}/" + path, async (Guid id, HttpContext http, GemReferenceTenantService service, CancellationToken ct) =>
                await HandleAsync<GemReferenceTenantVersionRequest>(http, request => VersionShape(request.EffectiveVersion),
                    request => service.SetArchivedAsync(Actor(http), id, archived, request.EffectiveVersion, ct), ct)));
        return endpoints;
    }
    private static void Write<T>(RouteHandlerBuilder route) where T : class => route
        .WithMetadata(WorkbenchAntiforgeryMetadata.Instance, new GemReferenceTenantWriteMetadata()).Accepts<T>("application/json")
        .Produces<GemReferenceDetailResponse>().Produces<GemReferenceTenantProblemResponse>(400, "application/problem+json")
        .Produces<GemReferenceTenantProblemResponse>(403, "application/problem+json")
        .Produces<GemReferenceTenantProblemResponse>(404, "application/problem+json")
        .Produces<GemReferenceTenantProblemResponse>(409, "application/problem+json").ProducesProblem(413);
    private static Guid Actor(HttpContext http) => Guid.Parse(http.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private static IResult Problem(int status, string code, IReadOnlyDictionary<string, string[]> errors, GemReferenceDetailResponse? current = null) =>
        Results.Json(new GemReferenceTenantProblemResponse(status, "Review the tenant gem reference request.", code, errors, current),
            statusCode: status, contentType: "application/problem+json");
    private static Dictionary<string, string[]> Error(string field) => new() { [field] = ["Review this request field."] };
    private static async Task<IResult> HandleAsync<T>(HttpContext http, Func<T, Dictionary<string, string[]>> validate,
        Func<T, Task<GemReferenceTenantWriteResult>> write, CancellationToken ct) where T : class
    {
        T? request;
        try
        {
            if (!http.Request.HasJsonContentType()) return Problem(400, "validation_failed", Error("body"));
            request = await http.Request.ReadFromJsonAsync<T>(ct);
        }
        catch (Exception error) when (error is JsonException or BadHttpRequestException)
        { return Problem(400, "validation_failed", Error("body")); }
        if (request is null) return Problem(400, "validation_failed", Error("body"));
        var errors = validate(request);
        if (errors.Count != 0) return Problem(400, "validation_failed", errors);
        var result = await write(request);
        return result.Success ? Results.Ok(result.Detail) : Problem(result.StatusCode, result.Code, result.Errors, result.Current);
    }
    private static Dictionary<string, string[]> ContentShape(GemReferenceContent? content)
    {
        if (content is null) return Error("content");
        var errors = new Dictionary<string, string[]>();
        if (content.Aliases is null || content.Aliases.Count > 20 || content.Aliases.Any(alias => alias is null)) errors["aliases"] = ["Provide at most 20 non-null aliases."];
        if (content.Sources is null || content.Sources.Count > 64 || content.Sources.Any(source => source is null)) errors["sources"] = ["Provide at most 64 non-null sources."];
        return errors;
    }
    private static Dictionary<string, string[]> Shape(GemReferenceContent? content, GemReferenceEffectiveVersion? version)
    {
        var errors = ContentShape(content);
        foreach (var error in VersionShape(version)) errors[error.Key] = error.Value;
        return errors;
    }
    private static Dictionary<string, string[]> OverrideShape(GemReferenceOverridesRequest request)
    {
        var errors = VersionShape(request.EffectiveVersion);
        if (request.Overrides is null || request.Overrides.Count > 8 || request.Overrides.Any(pair =>
                pair.Value is null || pair.Value.Sources is null || pair.Value.Sources.Any(source => source is null)) ||
            request.Overrides.Values.Sum(choice => choice.Sources.Count) > 64) errors["overrides"] = ["Provide up to eight field choices with at most 64 non-null sources."];
        return errors;
    }
    private static Dictionary<string, string[]> VersionShape(GemReferenceEffectiveVersion? version)
    {
        static bool Valid(string? value)
        {
            if (value is null) return true;
            if (value.Length != 12) return false;
            Span<byte> bytes = stackalloc byte[8];
            return Convert.TryFromBase64String(value, bytes, out var count) && count == 8;
        }
        return version is null || !Valid(version.SharedRowVersion) || !Valid(version.TenantRowVersion)
            ? Error("effectiveVersion") : [];
    }
}
