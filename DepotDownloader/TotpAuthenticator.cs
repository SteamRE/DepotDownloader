using System;
using System.Security.Cryptography;
using System.Threading.Tasks;
using SteamKit2.Authentication;

namespace DepotDownloader;

/// <summary>
/// Implementation of <see cref="IAuthenticator"/> that uses a TOTP Authenticator key to generate TOTP verification codes.
/// Falls back to the provided fallback authenticator.
/// </summary>
public class TotpAuthenticator : IAuthenticator
{
    private readonly string _totpKey;
    private readonly IAuthenticator _fallbackAuthenticator;

    public TotpAuthenticator(string totpKey, IAuthenticator fallbackAuthenticator)
    {
        _totpKey = totpKey;
        _fallbackAuthenticator = fallbackAuthenticator;
    }

    /// <inheritdoc />
    public Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect)
    {
        if (previousCodeWasIncorrect)
        {
            return _fallbackAuthenticator.GetDeviceCodeAsync(true);
        }

        var deviceCode = GetDeviceCode(_totpKey);
        return deviceCode != null ? Task.FromResult(deviceCode) : _fallbackAuthenticator.GetDeviceCodeAsync(false);
    }

    /// <inheritdoc />
    public Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect)
    {
        return _fallbackAuthenticator.GetEmailCodeAsync(email, previousCodeWasIncorrect);
    }

    /// <inheritdoc />
    public Task<bool> AcceptDeviceConfirmationAsync()
    {
        return _fallbackAuthenticator.AcceptDeviceConfirmationAsync();
    }

    // https://github.com/bitwarden/mobile/blob/7a65bf7fd7b44424073201c2c574d45b64b9ec9d/src/Core/Services/TotpService.cs
    private static string GetDeviceCode(string key)
    {
        const int Digits = 5;
        const int TotpDefaultTimer = 30;
        const string SteamChars = "23456789BCDFGHJKMNPQRTVWXY";

        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        var keyBytes = Util.DecodeBase32String(key);
        if (keyBytes == null || keyBytes.Length == 0)
        {
            return null;
        }
        var time = DateTimeOffset.Now.ToUnixTimeSeconds() / TotpDefaultTimer;
        var timeBytes = BitConverter.GetBytes(time);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(timeBytes, 0, timeBytes.Length);
        }

        var hash = new HMACSHA1(keyBytes).ComputeHash(timeBytes);

        if (hash.Length == 0)
        {
            return null;
        }

        var offset = hash[^1] & 0xf;
        var binary = ((hash[offset] & 0x7f) << 24) | ((hash[offset + 1] & 0xff) << 16) |
                     ((hash[offset + 2] & 0xff) << 8) | (hash[offset + 3] & 0xff);

        var otp = string.Empty;
        var fullCode = binary & 0x7fffffff;
        for (var i = 0; i < Digits; i++)
        {
            otp += SteamChars[fullCode % SteamChars.Length];
            fullCode = (int)Math.Truncate(fullCode / (double)SteamChars.Length);
        }
        return otp;
    }
}
