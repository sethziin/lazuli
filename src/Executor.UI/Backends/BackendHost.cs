using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using Executor.Contracts;
using Executor.UI.Mocks;

namespace Executor.UI.Backends;

/// <summary>Resultado da escolha do backend no início do app.</summary>
/// <param name="Info">O backend que está valendo (o de demonstração, se nada mais carregou).</param>
/// <param name="Session">A sessão pronta para a UI.</param>
/// <param name="Warnings">Avisos para mostrar no Output (backend não achado, falha ao carregar, pastas ignoradas).</param>
/// <param name="Discovery">O que existe em backends\ (alimenta o seletor das Configurações).</param>
public sealed record BackendSelection(BackendInfo Info, IRunSession Session, IReadOnlyList<string> Warnings, BackendDiscovery Discovery);

/// <summary>
/// Escolha do backend. Acontece uma vez, no início do app (sem troca em execução).
/// Regra: id salvo → esse backend; sem id salvo → o único backend instalado, ou a demonstração se houver zero ou vários.
/// Qualquer falha cai na demonstração (<see cref="MockRunSession"/>), com um aviso.
/// </summary>
public static class BackendHost
{
    /// <summary>O backend em uso (para telas como Configurações → Sobre).</summary>
    public static BackendInfo Current { get; private set; } = BackendInfo.Demo;

    /// <summary>Descoberta feita no início (para o seletor das Configurações).</summary>
    public static BackendDiscovery Discovery { get; private set; } = BackendDiscovery.Empty;

    /// <summary>Decide qual backend usar, sem carregar nada. null = demonstração.</summary>
    public static BackendEntry? Pick(string? requestedId, BackendDiscovery discovery, ICollection<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(requestedId))
            return discovery.Backends.Count == 1 ? discovery.Backends[0] : null;
        if (requestedId == BackendInfo.DemoId) return null;
        var hit = discovery.Backends.FirstOrDefault(e => e.Manifest.Id == requestedId);
        if (hit is null) warnings.Add($"O backend \"{requestedId}\" not found in backends\\. Using demo.");
        return hit;
    }

    public static BackendSelection Resolve(string? requestedId, string root, Version hostVersion, string dataRoot)
    {
        var warnings = new List<string>();
        var discovery = BackendCatalog.Discover(root);
        warnings.AddRange(discovery.Problems.Select(p => p.Message));

        var pick = Pick(requestedId, discovery, warnings);
        BackendSelection result;
        if (pick is null)
            result = new BackendSelection(BackendInfo.Demo, new MockRunSession(), warnings, discovery);
        else
        {
            try
            {
                var loaded = BackendCatalog.Load(pick);
                var dataDir = Path.Combine(dataRoot, loaded.Info.Id);
                Directory.CreateDirectory(dataDir);
                var session = loaded.Factory.Create(new BackendContext(Path.GetFullPath(pick.Directory), dataDir, hostVersion));
                result = new BackendSelection(loaded.Info, session, warnings, discovery);
            }
            catch (Exception ex)
            {
                warnings.Add($"Couldn't load backend\"{pick.Manifest.Id}\": {ex.Message} Using demo.");
                result = new BackendSelection(BackendInfo.Demo, new MockRunSession(), warnings, discovery);
            }
        }
        Current = result.Info;
        Discovery = discovery;
        return result;
    }
}

/// <summary>
/// Um backend que exige administrador, com o app sem elevação: relança o app elevado (um pedido do UAC) e deixa este processo sair.
/// No build do canal xeno o manifesto já pede administrador; então este caminho não dispara nele.
/// </summary>
public static class ElevationGate
{
    public const string ElevatedFlag = "--elevated";

    /// <summary>Só decide; não faz nada. Não relança em laço: se já foi relançado e ainda não é admin, segue sem elevar.</summary>
    public static bool ShouldRelaunch(bool backendRequiresAdmin, bool isAdmin, bool alreadyRelaunched) =>
        backendRequiresAdmin && !isAdmin && !alreadyRelaunched;

    public static bool IsAdmin()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    /// <summary>Relança elevado. true = o novo processo foi iniciado (este deve sair). false = não relançou (UAC recusado ou erro).</summary>
    public static bool TryRelaunchElevated(string exePath, string workingDir, IEnumerable<string> args)
    {
        try
        {
            var psi = new ProcessStartInfo(exePath) { UseShellExecute = true, Verb = "runas", WorkingDirectory = workingDir };
            foreach (var a in args) psi.ArgumentList.Add(a);
            if (!psi.ArgumentList.Contains("--wait-pid")) { psi.ArgumentList.Add("--wait-pid"); psi.ArgumentList.Add(Environment.ProcessId.ToString()); }
            psi.ArgumentList.Add(ElevatedFlag);
            using var child = Process.Start(psi);
            return child is not null;
        }
        catch (Win32Exception) { return false; } // o usuário recusou o UAC (erro 1223) ou o Windows não deixou
        catch { return false; }
    }
}
