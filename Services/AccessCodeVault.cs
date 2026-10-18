using Windows.Security.Credentials;

namespace TrayCer.Services;

/// <summary>
/// Printer secrets (API keys, LAN access codes, passwords, check codes) in the Windows credential
/// vault, one entry per printer keyed by its id. Everything here is short, well under the vault's
/// ~1,200-character limit.
/// </summary>
internal static class AccessCodeVault
{
    private const string Resource = "TrayCer.Printer";

    public static string? Get(string printerId)
    {
        try
        {
            PasswordCredential credential = new PasswordVault().Retrieve(Resource, printerId);
            credential.RetrievePassword();
            return string.IsNullOrEmpty(credential.Password) ? null : credential.Password;
        }
        catch
        {
            // Nothing stored (Retrieve throws), or the vault is unavailable.
            return null;
        }
    }

    /// <summary>Stores the secret, or removes it when <paramref name="secret"/> is empty.</summary>
    public static void Set(string printerId, string? secret)
    {
        Remove(printerId);
        if (string.IsNullOrWhiteSpace(secret))
        {
            return;
        }

        try
        {
            new PasswordVault().Add(new PasswordCredential(Resource, printerId, secret.Trim()));
        }
        catch
        {
            // The vault refused it; the printer will report an auth failure the user can fix.
        }
    }

    public static void Remove(string printerId)
    {
        try
        {
            PasswordVault vault = new();
            vault.Remove(vault.Retrieve(Resource, printerId));
        }
        catch
        {
            // Nothing stored.
        }
    }
}
