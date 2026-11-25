namespace Konnyaku.Application.Security;

/// <summary>Encrypts secrets kept in the database (TOTP keys), so a database dump alone cannot generate codes.</summary>
public interface ISecretProtector
{
    string Protect(byte[] secret);

    byte[] Unprotect(string protectedSecret);
}
