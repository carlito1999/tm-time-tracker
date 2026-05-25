using System.Security.Cryptography;
using System.Text;

namespace TmTimeTracker.Platform;

public interface ITokenProtector
{
    byte[] Protect(string plaintext);
    string Unprotect(byte[] ciphertext);
}

public sealed class DpapiTokenProtector : ITokenProtector
{
    public byte[] Protect(string plaintext) =>
        ProtectedData.Protect(Encoding.UTF8.GetBytes(plaintext), null, DataProtectionScope.CurrentUser);

    public string Unprotect(byte[] ciphertext) =>
        Encoding.UTF8.GetString(
            ProtectedData.Unprotect(ciphertext, null, DataProtectionScope.CurrentUser));
}
