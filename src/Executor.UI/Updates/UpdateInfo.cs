namespace Executor.UI.Updates;

/// <summary>Uma atualização já conferida (assinatura válida, canal certo, versão maior).</summary>
public sealed record UpdateInfo(
    Version Version,
    bool Mandatory,
    IReadOnlyList<string> Notes,
    Uri PackageUri,
    string Sha256,
    long Size,
    string Released,
    int Parts = 1)
{
    public string VersionText => Version.Build >= 0 ? $"{Version.Major}.{Version.Minor}.{Version.Build}" : Version.ToString();
}

public sealed class UpdateException(string message, Exception? inner = null) : Exception(message, inner);
