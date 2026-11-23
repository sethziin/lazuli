#if DEBUG
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Executor.Contracts;
using Executor.UI.Backends;
using Executor.UI.Mocks;
using Executor.UI.Services;
using Executor.UI.ViewModels;

namespace Executor.UI;

/// <summary>
/// Testes da arquitetura de backends (chamados pelo SelfTest, só em Debug; escrevem apenas dentro da pasta de teste).
/// Usam o backend de teste de verdade (backends\Executor.Backend.TestFixture, copiado para test-fixtures\fixture\ no build Debug),
/// então o carregamento passa pelo mesmo caminho dos plugins reais: manifesto → hashes → AssemblyLoadContext → fábrica.
/// </summary>
public static class BackendTests
{
    private static void Check(string name, bool ok, string? detail = null) => SelfTest.Check(name, ok, detail);

    private static void CopyDir(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var d in Directory.GetDirectories(src, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(d.Replace(src, dst));
        foreach (var f in Directory.GetFiles(src, "*", SearchOption.AllDirectories)) File.Copy(f, f.Replace(src, dst), true);
    }

    private static BackendEntry EntryAt(string dir) => new(dir, BackendManifest.Parse(File.ReadAllText(Path.Combine(dir, BackendManifest.FileName))));

    private static string? Throws(Action a)
    {
        try { a(); return null; }
        catch (Exception ex) { return ex.GetType().Name + ": " + ex.Message; }
    }

    private static void RewriteManifest(string dir, Func<string, string> edit)
    {
        var p = Path.Combine(dir, BackendManifest.FileName);
        File.WriteAllText(p, edit(File.ReadAllText(p)));
    }

    public static async Task RunAsync(string root)
    {
        var work = Path.Combine(root, "backend-tests");
        if (Directory.Exists(work)) Directory.Delete(work, true);
        Directory.CreateDirectory(work);

        var fixtureSrc = Path.Combine(AppContext.BaseDirectory, "test-fixtures", "fixture");
        Check("backends: fixture de teste foi copiado pelo build", File.Exists(Path.Combine(fixtureSrc, BackendManifest.FileName)), fixtureSrc);
        if (!Directory.Exists(fixtureSrc)) return;

        // ---------- manifesto ----------
        var good = File.ReadAllText(Path.Combine(fixtureSrc, BackendManifest.FileName));
        var m = BackendManifest.Parse(good);
        Check("manifesto: lê o do fixture", m.Id == "fixture" && m.ContractVersion == ContractInfo.Version && !m.RequiresAdmin && m.Files.ContainsKey(m.Assembly), m.Id);
        Check("manifesto: lista também o deps.json (hash)", m.Files.Keys.Any(k => k.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase)));
        Check("manifesto: não lista o Executor.Contracts nem o pdb", !m.Files.Keys.Any(k => k.StartsWith("Executor.Contracts") || k.EndsWith(".pdb")));

        var h = new string('a', 64);
        string Mk(string id = "x1", string asm = "a.dll", string files = "", string extra = "\"contractVersion\":1,") =>
            $"{{\"id\":\"{id}\",\"displayName\":\"X\",\"assembly\":\"{asm}\",\"factory\":\"T\",{extra}\"files\":{{{(files == "" ? $"\"a.dll\":\"{h}\"" : files)}}}}}";
        Check("manifesto: aceita um mínimo válido", Throws(() => BackendManifest.Parse(Mk())) is null);
        Check("manifesto: JSON quebrado é recusado", Throws(() => BackendManifest.Parse("{ nao eh json")) is { } e1 && e1.StartsWith("FormatException"));
        Check("manifesto: falta campo", Throws(() => BackendManifest.Parse("{\"id\":\"x1\"}")) is not null);
        Check("manifesto: id 'demo' é reservado", Throws(() => BackendManifest.Parse(Mk(id: "demo"))) is not null);
        Check("manifesto: id com maiúscula/espaço é recusado", Throws(() => BackendManifest.Parse(Mk(id: "Meu Backend"))) is not null);
        Check("manifesto: assembly fora da pasta é recusado", Throws(() => BackendManifest.Parse(Mk(asm: "../a.dll"))) is not null);
        Check("manifesto: caminho absoluto em files é recusado", Throws(() => BackendManifest.Parse(Mk(files: $"\"a.dll\":\"{h}\",\"C:/x.dll\":\"{h}\""))) is not null);
        Check("manifesto: SHA-256 curto é recusado", Throws(() => BackendManifest.Parse(Mk(files: "\"a.dll\":\"abc\""))) is not null);
        Check("manifesto: assembly precisa estar em files", Throws(() => BackendManifest.Parse(Mk(files: $"\"b.dll\":\"{h}\""))) is not null);
        Check("manifesto: sem contractVersion é recusado", Throws(() => BackendManifest.Parse(Mk(extra: ""))) is not null);

        // ---------- descoberta ----------
        var empty = Path.Combine(work, "vazio");
        Directory.CreateDirectory(empty);
        Check("descoberta: pasta inexistente = nenhum backend", BackendCatalog.Discover(Path.Combine(work, "nao-existe")).Backends.Count == 0);
        Check("descoberta: pasta vazia = nenhum backend e nenhum problema", BackendCatalog.Discover(empty) is { Backends.Count: 0, Problems.Count: 0 });

        var rootOk = Path.Combine(work, "ok", "backends");
        CopyDir(fixtureSrc, Path.Combine(rootOk, "fixture"));
        var d1 = BackendCatalog.Discover(rootOk);
        Check("descoberta: acha o fixture sem problemas", d1.Backends.Count == 1 && d1.Problems.Count == 0 && d1.Backends[0].Manifest.Id == "fixture");

        var rootBad = Path.Combine(work, "ruins", "backends");
        CopyDir(fixtureSrc, Path.Combine(rootBad, "a-bom"));
        Directory.CreateDirectory(Path.Combine(rootBad, "b-sem-manifesto"));
        Directory.CreateDirectory(Path.Combine(rootBad, "c-json-quebrado"));
        File.WriteAllText(Path.Combine(rootBad, "c-json-quebrado", BackendManifest.FileName), "{ quebrado");
        CopyDir(fixtureSrc, Path.Combine(rootBad, "d-contrato-novo"));
        RewriteManifest(Path.Combine(rootBad, "d-contrato-novo"), t => t.Replace("\"contractVersion\": 1", "\"contractVersion\": 99"));
        CopyDir(fixtureSrc, Path.Combine(rootBad, "e-id-repetido"));
        var d2 = BackendCatalog.Discover(rootBad);
        Check("descoberta: pastas ruins viram problemas, a boa continua", d2.Backends.Count == 1 && d2.Problems.Count == 4, $"{d2.Backends.Count}/{d2.Problems.Count}");
        Check("descoberta: contrato diferente é avisado com as duas versões", d2.Problems.Any(p => p.Message.Contains("v99") && p.Message.Contains($"v{ContractInfo.Version}")));
        Check("descoberta: id repetido é ignorado", d2.Problems.Any(p => p.Message.Contains("já existe")));

        // ---------- integridade ----------
        var okEntry = d1.Backends[0];
        Check("integridade: fixture íntegro passa", Throws(() => BackendCatalog.VerifyIntegrity(okEntry)) is null);

        var tamper = Path.Combine(work, "t-adulterado");
        CopyDir(Path.Combine(rootOk, "fixture"), tamper);
        var dllPath = Path.Combine(tamper, m.Assembly);
        var bytes = File.ReadAllBytes(dllPath);
        bytes[^1] ^= 0xFF;
        File.WriteAllBytes(dllPath, bytes);
        Check("integridade: 1 byte trocado é detectado", Throws(() => BackendCatalog.VerifyIntegrity(EntryAt(tamper))) is { } e2 && e2.Contains("Integridade"), Throws(() => BackendCatalog.VerifyIntegrity(EntryAt(tamper))));

        var missing = Path.Combine(work, "t-faltando");
        CopyDir(Path.Combine(rootOk, "fixture"), missing);
        File.Delete(Path.Combine(missing, m.Files.Keys.First(k => k.EndsWith(".deps.json"))));
        Check("integridade: arquivo listado ausente é detectado", Throws(() => BackendCatalog.VerifyIntegrity(EntryAt(missing))) is { } e3 && e3.Contains("ausente"));

        var extraDll = Path.Combine(work, "t-dll-solta");
        CopyDir(Path.Combine(rootOk, "fixture"), extraDll);
        File.WriteAllBytes(Path.Combine(extraDll, "solta.dll"), [1, 2, 3]);
        Check("integridade: DLL fora do manifesto é recusada", Throws(() => BackendCatalog.VerifyIntegrity(EntryAt(extraDll))) is { } e4 && e4.Contains("não listado"));

        var extraSub = Path.Combine(work, "t-dll-subpasta");
        CopyDir(Path.Combine(rootOk, "fixture"), extraSub);
        Directory.CreateDirectory(Path.Combine(extraSub, "nativos"));
        File.WriteAllBytes(Path.Combine(extraSub, "nativos", "x.EXE"), [1]);
        Check("integridade: binário solto em subpasta (qualquer caixa) é recusado", Throws(() => BackendCatalog.VerifyIntegrity(EntryAt(extraSub))) is { } e5 && e5.Contains("não listado"));

        var extraTxt = Path.Combine(work, "t-txt");
        CopyDir(Path.Combine(rootOk, "fixture"), extraTxt);
        File.WriteAllText(Path.Combine(extraTxt, "LEIA-ME.txt"), "oi");
        Check("integridade: arquivo que não é binário pode ficar na pasta", Throws(() => BackendCatalog.VerifyIntegrity(EntryAt(extraTxt))) is null);

        var wrongHash = Path.Combine(work, "t-hash-errado");
        CopyDir(Path.Combine(rootOk, "fixture"), wrongHash);
        RewriteManifest(wrongHash, t => t.Replace(m.Files[m.Assembly], new string('0', 64)));
        Check("integridade: hash do manifesto errado é detectado", Throws(() => BackendCatalog.VerifyIntegrity(EntryAt(wrongHash))) is { } e6 && e6.Contains("Integridade"));

        // ---------- carregamento ----------
        var loaded = BackendCatalog.Load(okEntry);
        Check("carga: Info vem do código do backend", loaded.Info.Id == "fixture" && loaded.Info.TargetName == "Alvo de teste" && loaded.Info.Capabilities == (BackendCapabilities.ListClients | BackendCapabilities.LogStream));
        Check("carga: fábrica declara a versão do contrato", loaded.Factory.ContractVersion == ContractInfo.Version);

        var ctx = new BackendContext(Path.GetFullPath(okEntry.Directory), Path.Combine(work, "data"), new Version(1, 2, 3));
        var session = loaded.Factory.Create(ctx);
        var asm = session.GetType().Assembly;
        Check("carga: sessão vem de um contexto de carga próprio (não o padrão)", AssemblyLoadContext.GetLoadContext(asm) is { } alc && alc != AssemblyLoadContext.Default && alc.Name == "backend:fixture");
        Check("carga: Executor.Contracts não foi duplicado (mesmo tipo dos dois lados)",
            AppDomain.CurrentDomain.GetAssemblies().Count(a => a.GetName().Name == "Executor.Contracts") == 1);
        Check("carga: a sessão enxerga a pasta do backend pelo contexto", session.StatusText.Contains(Path.GetFullPath(okEntry.Directory)));
        Check("carga: IsAttached começa falso", !session.IsAttached);
        var evt = new List<LogLine>();
        session.LogReceived += evt.Add;
        var att = await session.AttachAsync();
        Check("carga: Attach funciona e o evento de log chega", session.IsAttached && att[0].Text == "fixture: anexado" && evt.Count == 1);
        var run = await session.RunAsync("print(1)");
        Check("carga: Run devolve o texto previsto", run[0].Text == "fixture: 8 caracteres", run[0].Text);
        var runOn = await session.RunOnAsync("ab", [7]);
        Check("carga: RunOnAsync usa o padrão da interface (sem PerClientRun)", runOn[0].Text == "fixture: 2 caracteres");
        Check("carga: GetClients devolve o cliente do backend", session.GetClients() is [{ Pid: 1234, Attached: true }]);
        var kill = await session.KillAsync(null);
        Check("carga: KillAsync padrão avisa que não encerra o alvo", kill[0].Level == LogLevel.Warning);

        Check("carga: tipo inexistente em 'factory' é recusado",
            Throws(() => BackendCatalog.Load(Mutated("t-tipo", t => t.Replace("Executor.TestFixture.FixtureFactory", "Nao.Existe")))) is { } e7 && e7.Contains("não existe"));
        Check("carga: id do manifesto diferente do que o backend declara é recusado",
            Throws(() => BackendCatalog.Load(Mutated("t-id", t => t.Replace("\"id\": \"fixture\"", "\"id\": \"outro\"")))) is { } e8 && e8.Contains("não bate"));
        Check("carga: requiresAdmin diferente do que o backend declara é recusado",
            Throws(() => BackendCatalog.Load(Mutated("t-admin", t => t.Replace("\"requiresAdmin\": false", "\"requiresAdmin\": true")))) is { } e9 && e9.Contains("requiresAdmin"));
        Check("carga: backend adulterado não chega a carregar", Throws(() => BackendCatalog.Load(EntryAt(tamper))) is { } e10 && e10.Contains("Integridade"));

        BackendEntry Mutated(string name, Func<string, string> edit)
        {
            var dir = Path.Combine(work, name);
            CopyDir(Path.Combine(rootOk, "fixture"), dir);
            RewriteManifest(dir, edit);
            return EntryAt(dir);
        }

        // ---------- escolha do backend ----------
        var none = BackendDiscovery.Empty;
        var warn = new List<string>();
        Check("escolha: sem id e sem backends = demonstração", BackendHost.Pick(null, none, warn) is null && warn.Count == 0);
        Check("escolha: sem id e 1 backend = esse backend", BackendHost.Pick(null, d1, warn)?.Manifest.Id == "fixture");
        var two = new BackendDiscovery([okEntry, okEntry with { Manifest = okEntry.Manifest with { Id = "outro" } }], []);
        Check("escolha: sem id e 2 backends = demonstração (não adivinha)", BackendHost.Pick(null, two, warn) is null && warn.Count == 0);
        Check("escolha: 'demo' = demonstração, sem aviso", BackendHost.Pick("demo", d1, warn) is null && warn.Count == 0);
        Check("escolha: id explícito vence", BackendHost.Pick("outro", two, warn)?.Manifest.Id == "outro");
        Check("escolha: id que não existe = demonstração com aviso", BackendHost.Pick("sumiu", d1, warn) is null && warn.Count == 1 && warn[0].Contains("sumiu"));

        var host = new Version(1, 0, 0);
        var dataRoot = Path.Combine(work, "dados");
        var r1 = BackendHost.Resolve(null, rootOk, host, dataRoot);
        Check("Resolve: carrega o único backend instalado", r1.Info.Id == "fixture" && r1.Session.GetType().Name == "FixtureSession" && r1.Warnings.Count == 0);
        Check("Resolve: cria a pasta de dados do backend", Directory.Exists(Path.Combine(dataRoot, "fixture")));
        Check("Resolve: atualiza BackendHost.Current e o nome mostrado em Sobre", BackendHost.Current.Id == "fixture" && Updates.AppInfo.BackendName.Contains("Fixture de teste"));
        var r2 = BackendHost.Resolve("demo", rootOk, host, dataRoot);
        Check("Resolve('demo'): MockRunSession, mesmo com plugin instalado", r2.Session is MockRunSession && r2.Info.Id == "demo" && Updates.AppInfo.BackendName.Contains("demonstração"));
        var r3 = BackendHost.Resolve("sumiu", rootOk, host, dataRoot);
        Check("Resolve: id desconhecido cai na demonstração com aviso", r3.Session is MockRunSession && r3.Warnings.Any(w => w.Contains("sumiu")));
        var r4 = BackendHost.Resolve("fixture", Path.Combine(work, "ruins-2"), host, dataRoot);
        Check("Resolve: pasta de backends inexistente cai na demonstração", r4.Session is MockRunSession);
        var rootTamper = Path.Combine(work, "adulterado", "backends");
        CopyDir(tamper, Path.Combine(rootTamper, "fixture"));
        var r5 = BackendHost.Resolve("fixture", rootTamper, host, dataRoot);
        Check("Resolve: backend adulterado cai na demonstração e explica por quê", r5.Session is MockRunSession && r5.Warnings.Any(w => w.Contains("Integridade") && w.Contains("demonstração")));
        var r6 = BackendHost.Resolve(null, rootBad, host, dataRoot);
        Check("Resolve: avisos de pastas ignoradas chegam ao usuário", r6.Warnings.Count >= 4 && r6.Info.Id == "fixture");
        BackendHost.Resolve("demo", rootOk, host, dataRoot); // deixa o estado global como no início

        // ---------- configurações ----------
        var s = new AppSettings();
        Check("config: BackendId começa nulo (automático)", s.BackendId is null);
        s.BackendId = "xeno";
        var json = JsonSerializer.Serialize(s);
        Check("config: BackendId é gravado e lido", JsonSerializer.Deserialize<AppSettings>(json)?.BackendId == "xeno");
        Check("config: arquivo antigo sem BackendId continua válido", JsonSerializer.Deserialize<AppSettings>("{\"EditorFontSize\":15}") is { BackendId: null, EditorFontSize: 15 });
        s.CopyFrom(new AppSettings());
        Check("config: 'Restaurar padrões' não troca o backend", s.BackendId == "xeno");

        // ---------- o que a UI oferece ----------
        var vmDemo = new MainViewModel(new MockScriptLibrary(), new MockRunSession(), new AppSettings { RestoreTabs = false }) { PersistenceEnabled = false };
        var vmFix = new MainViewModel(new MockScriptLibrary(), session, new AppSettings { RestoreTabs = false }) { PersistenceEnabled = false, Backend = loaded.Info };
        Check("UI: demonstração oferece lista de clientes e encerrar", vmDemo.CanListClients && vmDemo.CanKill);
        Check("UI: backend sem KillTarget esconde o encerrar, com ListClients mostra a lista", !vmFix.CanKill && vmFix.CanListClients);
        int[]? Selected(MainViewModel vm)
        {
            vm.Clients.Add(new RobloxClient(55) { IsSelected = true });
            return (int[]?)typeof(MainViewModel).GetMethod("SelectedPids", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(vm, null);
        }
        Check("UI: com PerClientRun, os clientes marcados viram PIDs", Selected(vmDemo) is [55]);
        Check("UI: sem PerClientRun, executa em todos (não finge escolher)", Selected(vmFix) is null);

        // ---------- elevação ----------
        var table = new[] { (true, false, false, true), (true, false, true, false), (true, true, false, false), (true, true, true, false),
                            (false, false, false, false), (false, false, true, false), (false, true, false, false), (false, true, true, false) };
        Check("elevação: só relança se exige admin, não é admin e ainda não relançou",
            table.All(t => ElevationGate.ShouldRelaunch(t.Item1, t.Item2, t.Item3) == t.Item4));
        Check("elevação: IsAdmin não lança exceção", Throws(() => ElevationGate.IsAdmin()) is null);

        // ---------- o plugin Xeno de verdade (só existe no build do canal xeno) ----------
        var xenoDir = Path.Combine(AppContext.BaseDirectory, "backends");
        if (Directory.Exists(Path.Combine(xenoDir, "xeno")))
        {
            var dx = BackendCatalog.Discover(xenoDir);
            Check("xeno real: descoberto sem problemas", dx.Backends.Any(b => b.Manifest.Id == "xeno") && dx.Problems.Count == 0, string.Join("; ", dx.Problems.Select(p => p.Message)));
            var xe = dx.Backends.First(b => b.Manifest.Id == "xeno");
            Check("xeno real: integridade confere", Throws(() => BackendCatalog.VerifyIntegrity(xe)) is null);
            var lx = BackendCatalog.Load(xe);
            Check("xeno real: Info e capacidades", lx.Info is { Id: "xeno", RequiresAdmin: true } && lx.Info.Has(BackendCapabilities.KillTarget | BackendCapabilities.PerClientRun | BackendCapabilities.ListClients));
            var sx = lx.Factory.Create(new BackendContext(Path.GetFullPath(xe.Directory), Path.Combine(work, "data-xeno"), host));
            Check("xeno real: cria a sessão sem carregar o Xeno.dll", sx.StatusText.StartsWith("Xeno") && !sx.IsAttached);
        }
        else Console.WriteLine("SKIP  xeno real: build sem -p:Backend=xeno (sem backends\\xeno)");
    }
}
#endif
