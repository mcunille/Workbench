// Copyright (c) 2026 The White Stag Collection.

using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;

namespace Workbench.Server.Identity;

public static class SessionToken
{
    public static string Create() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    public static byte[] Hash(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        return SHA256.HashData(WebEncoders.Base64UrlDecode(token));
    }

    public static bool TryHash(string? token, out byte[] hash)
    {
        hash = [];
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        try
        {
            hash = Hash(token);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
