namespace MERRICK.DatabaseContext.Handlers;

/// <summary>
///     Generates and hashes passwords in the legacy Heroes Of Newerth SRP format used by both portal registration and master-server authentication.
/// </summary>
public static class SRPPasswordHandlers
{
    # region Secure Remote Password Magic Strings

    // Thank you, Anton Romanov (aka Theli), for making these values public: https://github.com/theli-ua/pyHoNBot/blob/cabde31b8601c1ca55dc10fcf663ec663ec0eb71/hon/masterserver.py#L37.
    private const string MagicStringOne = "[!~esTo0}";
    private const string MagicStringTwo = "taquzaph_?98phab&junaj=z=kuChusu";

    # endregion

    /// <summary>
    ///     Generates the 64-character lowercase hexadecimal password hash expected by the legacy client.
    /// </summary>
    public static string ComputeSRPPasswordHash(string password, string salt, bool passwordIsHashed = false)
    {
        string passwordHash = passwordIsHashed ? password : Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(password))).ToLowerInvariant();

        string magickedPasswordHash = passwordHash + salt + MagicStringOne;
        string magickedPasswordHashHash = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(magickedPasswordHash))).ToLowerInvariant();
        string magickedMagickedPasswordHashHash = magickedPasswordHashHash + MagicStringTwo;

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(magickedMagickedPasswordHashHash))).ToLowerInvariant();
    }

    /// <summary>
    ///     Generates a cryptographically random 64-character lowercase hexadecimal salt.
    /// </summary>
    public static string GenerateSRPPasswordSalt()
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
}
