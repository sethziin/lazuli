using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using Executor.Contracts;

namespace Executor.UI.Backends;

public sealed record BackendEntry(string Directory, BackendManifest Manifest);

public sealed record BackendProblem(string Directory, string Message);

public sealed record BackendDiscovery(IReadOnlyList<BackendEntry> Backends, IReadOnlyList<BackendProblem> Problems)
{
    public static BackendDiscovery Empty { get; } = new([], []);
}

/// <summary>Um backend carregado: a fábrica e a descrição que o próprio código do backend declarou.</summary>
public sealed record LoadedBackend(BackendEntry Entry, IBackendFactory Factory, BackendInfo Info);

public sealed class BackendLoadException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Descobre, confere e carrega backends de <c>backends\&lt;id&gt;\</c>.
/// <para>
/// <b>Limite de segurança (leia):</b> a conferência de SHA-256 é verificação de <b>integridade</b>: pega arquivo corrompido,
/// incompleto ou trocado por engano, e impede que sobrem binários soltos na pasta do backend. Ela <b>não autentica</b> o
/// backend: o manifesto e os hashes ficam na mesma pasta, então quem consegue gravar ali consegue gerar um manifesto novo
/// (e também trocar o próprio Executor.exe). Um backend carregado roda com as permissões do app (administrador, se o
/// backend exigir). Instale só backends de origem confiável.
/// </para>
/// </summary>
public static class BackendCatalog
{
    public static string DefaultRoot => Path.Combine(Updates.AppInfo.AppDir, "backends");

    private static readonly string[] BinaryExtensions = [".dll", ".exe", ".sys", ".winmd"];

    /// <summary>Lê só os manifestos (não carrega nenhum código). Pastas com problema vão para <see cref="BackendDiscovery.Problems"/>.</summary>
    public static BackendDiscovery Discover(string root)
    {
        if (!Directory.Exists(root)) return BackendDiscovery.Empty;
        var found = new List<BackendEntry>();
        var problems = new List<BackendProblem>();
        var dirs = Directory.GetDirectories(root).OrderBy(d => d, StringComparer.OrdinalIgnoreCase);
        foreach (var dir in dirs)
        {
            var name = Path.GetFileName(dir);
            var file = Path.Combine(dir, BackendManifest.FileName);
            if (!File.Exists(file)) { problems.Add(new(dir, $"Folder \"{name}\" ignored: it has no {BackendManifest.FileName}.")); continue; }
            try
            {
                var m = BackendManifest.Parse(File.ReadAllText(file));
                if (m.ContractVersion != ContractInfo.Version)
                {
                    problems.Add(new(dir, $"Backend \"{m.Id}\" ignorado: usa o contrato v{m.ContractVersion} e este app espera v{ContractInfo.Version}."));
                    continue;
                }
                if (found.Any(e => e.Manifest.Id == m.Id))
                {
                    problems.Add(new(dir, $"Backend \"{m.Id}\" in \"{name}\" ignored: that id already exists in another folder."));
                    continue;
                }
                found.Add(new BackendEntry(dir, m));
            }
            catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
            {
                problems.Add(new(dir, $"Pasta \"{name}\" ignorada: {ex.Message}"));
            }
        }
        return new BackendDiscovery(found, problems);
    }

    /// <summary>Confere a integridade dos arquivos do backend. Lança <see cref="BackendLoadException"/> se algo não bater.</summary>
    public static void VerifyIntegrity(BackendEntry entry)
    {
        var dir = Path.GetFullPath(entry.Directory);
        var prefix = dir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var listed = new HashSet<string>(entry.Manifest.Files.Keys, StringComparer.OrdinalIgnoreCase);

        foreach (var (rel, expected) in entry.Manifest.Files)
        {
            var full = Path.GetFullPath(Path.Combine(dir, rel));
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new BackendLoadException($"Caminho fora da pasta do backend: {rel}");
            if (!File.Exists(full)) throw new BackendLoadException($"Missing file: {rel}");
            if (File.GetAttributes(full).HasFlag(FileAttributes.ReparsePoint)) throw new BackendLoadException($"Symbolic links aren't accepted: {rel}");
            string actual;
            try
            {
                using var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
                actual = Convert.ToHexString(SHA256.HashData(fs));
            }
            catch (IOException ex) { throw new BackendLoadException($"Couldn't read {rel}: {ex.Message}", ex); }
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new BackendLoadException($"Integrity check failed for {rel}: the file doesn't match the manifest.");
        }

        // nenhum binário pode estar na pasta sem constar no manifesto (uma DLL nativa solta seria carregada sem conferência)
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            if (!BinaryExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)) continue;
            var rel = Path.GetRelativePath(dir, f).Replace('\\', '/');
            if (!listed.Contains(rel)) throw new BackendLoadException($"Binary not listed in the manifest: {rel}");
        }
    }

    /// <summary>Confere a integridade e carrega o backend. Só o tipo apontado em "factory" é instanciado.</summary>
    public static LoadedBackend Load(BackendEntry entry)
    {
        VerifyIntegrity(entry);
        var assemblyPath = Path.GetFullPath(Path.Combine(entry.Directory, entry.Manifest.Assembly));
        try
        {
            var ctx = new PluginLoadContext($"backend:{entry.Manifest.Id}", assemblyPath);
            var asm = ctx.LoadFromAssemblyPath(assemblyPath);
            var type = asm.GetType(entry.Manifest.Factory, throwOnError: false)
                ?? throw new BackendLoadException($"Type \"{entry.Manifest.Factory}\" doesn't exist in {entry.Manifest.Assembly}.");
            if (!typeof(IBackendFactory).IsAssignableFrom(type) || type.IsAbstract)
                throw new BackendLoadException($"\"{entry.Manifest.Factory}\" doesn't implement IBackendFactory (or the backend shipped its own copy of Executor.Contracts).");
            if (Activator.CreateInstance(type) is not IBackendFactory factory)
                throw new BackendLoadException($"Couldn't create \"{entry.Manifest.Factory}\" (it needs a public parameterless constructor).");
            if (factory.ContractVersion != ContractInfo.Version)
                throw new BackendLoadException($"O backend foi feito para o contrato v{factory.ContractVersion}; o app espera v{ContractInfo.Version}.");
            var info = factory.Info;
            if (info.Id != entry.Manifest.Id)
                throw new BackendLoadException($"The backend id (\"{info.Id}\") doesn't match the manifest (\"{entry.Manifest.Id}\").");
            if (info.RequiresAdmin != entry.Manifest.RequiresAdmin)
                throw new BackendLoadException("The manifest's \"requiresAdmin\" doesn't match what the backend declares.");
            return new LoadedBackend(entry, factory, info);
        }
        catch (BackendLoadException) { throw; }
        catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or FileNotFoundException or TypeLoadException
                                       or ReflectionTypeLoadException or TargetInvocationException or MissingMethodException)
        {
            throw new BackendLoadException($"Falha ao carregar {entry.Manifest.Assembly}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Contexto de carga isolado por backend. O <c>Executor.Contracts</c> sempre vem do app (mesmo tipo dos dois lados);
    /// o resto é resolvido só dentro da pasta do backend, ou cai nas bibliotecas do .NET.
    /// </summary>
    private sealed class PluginLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;
        private readonly string _prefix;

        public PluginLoadContext(string name, string mainAssemblyPath) : base(name, isCollectible: false)
        {
            _resolver = new AssemblyDependencyResolver(mainAssemblyPath);
            _prefix = Path.GetDirectoryName(mainAssemblyPath)!.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        }

        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name == typeof(IRunSession).Assembly.GetName().Name) return null; // contrato: sempre o do app
            var path = _resolver.ResolveAssemblyToPath(name);
            return path is not null && Inside(path) ? LoadFromAssemblyPath(path) : null;
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            if (path is null)
            {
                var guess = Path.Combine(_prefix, unmanagedDllName);
                path = File.Exists(guess) ? guess : File.Exists(guess + ".dll") ? guess + ".dll" : null;
            }
            return path is not null && Inside(path) ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
        }

        private bool Inside(string path) => Path.GetFullPath(path).StartsWith(_prefix, StringComparison.OrdinalIgnoreCase);
    }
}
