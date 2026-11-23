namespace Lazuli.UI.Services;

/// <summary>
/// Várias janelas do Roblox: o Roblox só abre se conseguir criar o mutex "ROBLOX_singletonMutex".
/// Enquanto o Lazuli mantém esse mutex aberto, as próximas janelas não encontram "instância única" e abrem normalmente.
/// Só vale para janelas abertas depois; se já existe um Roblox segurando o mutex, tenta de novo quando ele fechar.
/// </summary>
public static class MultiClient
{
    private const string MutexName = "ROBLOX_singletonMutex";
    private static Mutex? _held;

    public static bool IsHeld => _held is not null;

    /// <summary>Garante que o mutex está aberto neste processo. false = outro processo (o Roblox) já o segura.</summary>
    public static bool Enable()
    {
        if (_held is not null) return true;
        try
        {
            // criado já com posse (como as ferramentas de multi-instância fazem): o Roblox encontra o mutex ocupado e não fecha as outras janelas
            var m = new Mutex(true, MutexName, out var created);
            if (!created) { m.Dispose(); return false; }
            _held = m;
            return true;
        }
        catch { return false; }
    }

    public static void Disable()
    {
        if (_held is null) return;
        try { _held.ReleaseMutex(); } catch { /* outra thread ou já solto: fechar o handle basta */ }
        _held.Dispose();
        _held = null;
    }
}
