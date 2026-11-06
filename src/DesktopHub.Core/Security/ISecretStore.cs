namespace DesktopHub.Core.Security;

/// <summary>
/// Armazenamento de segredos (tokens OAuth, chaves de API). A implementação deve cifrar em repouso
/// — nunca texto puro. Valores de segredos jamais devem ser registrados em logs.
/// </summary>
public interface ISecretStore
{
    void Save(string name, string value);

    string? Read(string name);

    bool Delete(string name);
}
