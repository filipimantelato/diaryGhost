using System.Security.Cryptography;
using System.Text;
using DesktopHub.Core.Security;
using DesktopHub.Infrastructure.Persistence;

namespace DesktopHub.Infrastructure.Security;

/// <summary>
/// Segredos cifrados com DPAPI no escopo do usuário atual: só esta conta do Windows, nesta máquina,
/// consegue decifrar. O blob cifrado fica no SQLite; o texto puro nunca é gravado em disco.
/// </summary>
public sealed class DpapiSecretStore(SqliteDatabase database, TimeProvider time) : ISecretStore
{
    // Entropia adicional: impede que outro app do mesmo usuário decifre por acidente via DPAPI genérico.
    private static readonly byte[] Entropy = "DesktopHub.Secrets.v1"u8.ToArray();

    public void Save(string name, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);

        var blob = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser);
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();

        using var connection = database.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO secrets (name, protected_blob, created_at_ms, updated_at_ms) VALUES ($name, $blob, $now, $now)
            ON CONFLICT(name) DO UPDATE SET protected_blob = excluded.protected_blob, updated_at_ms = excluded.updated_at_ms;
            """;
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$blob", blob);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.ExecuteNonQuery();
    }

    public string? Read(string name)
    {
        using var connection = database.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT protected_blob FROM secrets WHERE name = $name;";
        cmd.Parameters.AddWithValue("$name", name);
        if (cmd.ExecuteScalar() is not byte[] blob)
        {
            return null;
        }
        // CryptographicException propaga de propósito: indica perfil de usuário diferente ou dado adulterado,
        // e o chamador (integração) deve tratar como "desconectado — reautenticar".
        return Encoding.UTF8.GetString(ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.CurrentUser));
    }

    public bool Delete(string name)
    {
        using var connection = database.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM secrets WHERE name = $name;";
        cmd.Parameters.AddWithValue("$name", name);
        return cmd.ExecuteNonQuery() > 0;
    }
}
