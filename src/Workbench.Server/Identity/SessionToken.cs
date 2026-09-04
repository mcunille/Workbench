// Copyright (c) 2026 The White Stag Collection.

using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;

namespace Workbench.Server.Identity;

public static class SessionToken
{
    public const int EncodedLength = 43;
    private const int ByteLength = 32;

    public static string Create() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(ByteLength));

    public static byte[] Hash(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        var decoded = WebEncoders.Base64UrlDecode(token);
        if (decoded.Length != ByteLength)
        {
            throw new FormatException("The token has an invalid length.");
        }

        return SHA256.HashData(decoded);
    }

    public static bool TryHash(string? token, out byte[] hash)
    {
        hash = [];
        if (string.IsNullOrWhiteSpace(token) || token.Length != EncodedLength)
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
