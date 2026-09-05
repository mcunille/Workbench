// Copyright (c) 2026 The White Stag Collection.

using Microsoft.AspNetCore.Identity;

namespace Workbench.Server.Identity;

public static class WorkbenchPasswordPolicy
{
    public const int MinimumLength = 14;
    public const int MaximumLength = 1024;
    public const int MinimumUniqueCharacters = 4;

    public static void Configure(PasswordOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.RequiredLength = MinimumLength;
        options.RequiredUniqueChars = MinimumUniqueCharacters;
        options.RequireDigit = true;
        options.RequireLowercase = true;
        options.RequireUppercase = true;
        options.RequireNonAlphanumeric = true;
    }

    public static void EnsureValid(string? password, string parameterName)
    {
        if (!IsWithinInputBounds(password) ||
            password!.Length < MinimumLength ||
            password.Distinct().Take(MinimumUniqueCharacters).Count() < MinimumUniqueCharacters ||
            !password.Any(char.IsDigit) ||
            !password.Any(char.IsLower) ||
            !password.Any(char.IsUpper) ||
            !password.Any(character => !char.IsLetterOrDigit(character)))
        {
            throw new ArgumentException("The password does not satisfy the Workbench password policy.", parameterName);
        }
    }

    public static bool IsWithinInputBounds(string? password) =>
        !string.IsNullOrEmpty(password) && password.Length <= MaximumLength;
}
