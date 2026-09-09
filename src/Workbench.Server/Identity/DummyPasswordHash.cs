// Copyright (c) 2026 The White Stag Collection.

using Microsoft.AspNetCore.Identity;

namespace Workbench.Server.Identity;

public sealed class DummyPasswordHash
{
    private readonly Lock _gate = new();
    private string? _hash;

    public string GetOrCreate(IPasswordHasher<WorkbenchUser> hasher, WorkbenchUser user)
    {
        lock (_gate)
        {
            // Retain only the hash, never the request-scoped hasher or user.
            return _hash ??= hasher.HashPassword(user, "dummy-password-never-accepted");
        }
    }
}
