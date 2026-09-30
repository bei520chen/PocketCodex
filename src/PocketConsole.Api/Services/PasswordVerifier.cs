using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using PocketConsole.Api.Options;

namespace PocketConsole.Api.Services;

public sealed class PasswordVerifier(IOptions<SecurityOptions> options)
{
    public bool Verify(string? password)
    {
        if (string.IsNullOrEmpty(options.Value.Password) || string.IsNullOrEmpty(password)) return false;
        var expected = SHA256.HashData(Encoding.UTF8.GetBytes(options.Value.Password));
        var actual = SHA256.HashData(Encoding.UTF8.GetBytes(password));
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}
