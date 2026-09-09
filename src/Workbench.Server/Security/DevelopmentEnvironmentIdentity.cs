// Copyright (c) 2026 The White Stag Collection.

namespace Workbench.Server.Security;

public static class DevelopmentEnvironmentIdentity
{
    public static string GetSuffix(IConfiguration configuration, IHostEnvironment environment)
    {
        if (!environment.IsDevelopment() || configuration["Development:EnvironmentId"] is not { } id)
        {
            return "";
        }

        if (id.Length is < 1 or > 64 || !IsLetterOrDigit(id[0]) ||
            id.Any(character => !IsLetterOrDigit(character) && character != '-'))
        {
            throw new InvalidOperationException(
                "Development:EnvironmentId must contain 1 to 64 lowercase ASCII letters, digits or hyphens and start with a letter or digit.");
        }

        return "." + id;
    }

    private static bool IsLetterOrDigit(char character) => character is >= 'a' and <= 'z' or >= '0' and <= '9';
}
