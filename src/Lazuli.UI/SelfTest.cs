#if DEBUG
using System.Text.Json;
using Lazuli.UI.Editing;
using Lazuli.UI.Mocks;
using Lazuli.UI.Services;
using Lazuli.UI.ViewModels;

namespace Lazuli.UI;

/// <summary>
/// Autoteste da lógica (sem janela): LAZULI_SELFTEST=&lt;pasta temporária&gt; roda tudo e imprime PASS/FAIL.
/// Só existe em Debug. Escreve apenas dentro da pasta informada.
/// </summary>
public static class SelfTest
{
    private static int _pass, _fail;

    internal static void Check(string name, bool ok, string? detail = null)
    {
        if (ok) _pass++; else _fail++;
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(ok || detail is null ? "" : "  -> " + detail)}");
    }

    // ---- simuladores: aplicam o resultado das regras de edição a um texto com "|" marcando o cursor ----

    private static string Cut(string text, EditResult r) => text[..r.Offset] + r.Insert + text[(r.Offset + r.RemoveLength)..];

    /// <summary>Digita os caracteres um a um (como o editor faria): usa a regra, ou insere normal se ela devolver null.</summary>
    private static string Type(string marked, string typed, string unit = "    ", bool pairs = true, bool blocks = true)
    {
        var caret = marked.IndexOf('|');
        var text = marked.Remove(caret, 1);
        foreach (var ch in typed)
        {
            var r = LuaEditing.Typed(text, caret, caret, caret, ch, unit, pairs, blocks);
            if (r is null) { text = text.Insert(caret, ch.ToString()); caret++; }
            else { text = Cut(text, r); caret = r.CaretAfter; }
        }
        return text.Insert(caret, "|");
    }

    /// <summary>Enter: a regra de Lua, ou o Enter padrão do editor (mantém a indentação da linha).</summary>
    private static string Enter(string marked, string unit = "    ")
    {
        var caret = marked.IndexOf('|');
        var text = marked.Remove(caret, 1);
        var r = LuaEditing.Enter(text, caret, 0, unit);
        if (r is not null) return Cut(text, r).Insert(r.CaretAfter, "|");
        var ls = caret == 0 ? 0 : text.LastIndexOf('\n', caret - 1) + 1;
        var ws = new string(text[ls..].TakeWhile(c => c == ' ' || c == '\t').ToArray());
        return text.Insert(caret, "\n" + ws).Insert(caret + 1 + ws.Length, "|");
    }

    private static string Back(string marked)
    {
        var caret = marked.IndexOf('|');
        var text = marked.Remove(caret, 1);
        var r = LuaEditing.Backspace(text, caret, 0);
        return r is null ? "<padrão>" : Cut(text, r).Insert(r.CaretAfter, "|");
    }

    private static void Eq(string name, string actual, string expected) => Check(name, actual == expected,
        $"esperado «{expected.Replace("\n", "\\n")}» obtido «{actual.Replace("\n", "\\n")}»");

    private static void EditingTests()
    {
        // ---- pares ----
        Eq("pares: ( fecha sozinho", Type("|", "("), "(|)");
        Eq("pares: depois de nome", Type("print|", "("), "print(|)");
        Eq("pares: digitar ) por cima pula o fecho", Type("print(|)", ")"), "print()|");
        Eq("pares: ( antes de palavra não fecha", Type("|foo", "("), "(|foo");
        Eq("pares: aspas fecham", Type("x = |", "\""), "x = \"|\"");
        Eq("pares: aspas por cima pulam", Type("x = \"abc|\"", "\""), "x = \"abc\"|");
        Eq("pares: apóstrofo depois de letra não fecha", Type("it|", "'"), "it'|");
        Eq("pares: dentro de string não fecha (", Type("x = \"a|\"", "("), "x = \"a(|\"");
        Eq("pares: dentro de comentário não fecha (", Type("-- nota|", "("), "-- nota(|");
        Eq("pares: [ vira [[ ]] ao repetir", Type("[|]", "["), "[[|]]");
        Eq("pares: { fecha", Type("local t = |", "{"), "local t = {|}");
        Eq("pares: aspas dentro dos parênteses", Type("f(a, |)", "\""), "f(a, \"|\")");
        Eq("pares: aspas simples por cima pulam", Type("a = 'x|'", "'"), "a = 'x'|");
        Eq("pares: digita print(\"oi\") do zero", Type("|", "print(\"oi\")"), "print(\"oi\")|");
        Eq("pares: desligado não fecha", Type("|", "(", pairs: false), "(|");
        var wrap = LuaEditing.Typed("abc", 3, 0, 3, '(', "    ")!;
        Check("pares: seleção + ( envolve e mantém o miolo selecionado",
            Cut("abc", wrap) == "(abc)" && wrap.SelStartAfter == 1 && wrap.SelLengthAfter == 3);
        Check("pares: seleção + aspas envolve", Cut("abc", LuaEditing.Typed("abc", 3, 0, 3, '"', "    ")!) == "\"abc\"");
        Check("pares: seleção + letra não é com a regra", LuaEditing.Typed("abc", 3, 0, 3, 'x', "    ") is null);

        // ---- backspace ----
        Eq("backspace: () vazio apaga os dois", Back("(|)"), "|");
        Eq("backspace: \"\" vazio apaga os dois", Back("\"|\""), "|");
        Eq("backspace: '' vazio apaga os dois", Back("'|'"), "|");
        Eq("backspace: {} vazio apaga os dois", Back("a = {|}"), "a = |");
        Eq("backspace: par com conteúdo usa o padrão", Back("(a|)"), "<padrão>");
        Eq("backspace: fora de par usa o padrão", Back("abc|"), "<padrão>");

        // ---- realinhar end / else / elseif / until / fechos ----
        Eq("end: realinha com o if", Type("if x then\n    foo()\n    end|", " "), "if x then\n    foo()\nend |");
        Eq("end: aninhado volta ao nível certo", Type("function f()\n  if a then\n    if b then\n      x()\n      end|", " "),
            "function f()\n  if a then\n    if b then\n      x()\n    end |");
        Eq("else: realinha com o if", Type("if a then\n    x()\n    else|", " "), "if a then\n    x()\nelse |");
        Eq("elseif: realinha com o if", Type("if a then\n    x()\n    elseif|", " "), "if a then\n    x()\nelseif |");
        Eq("until: realinha com o repeat", Type("repeat\n    x()\n    until|", " "), "repeat\n    x()\nuntil |");
        Eq("} no começo da linha realinha", Type("local t = {\n    a = 1,\n    |", "}"), "local t = {\n    a = 1,\n}|");
        Eq("palavra parecida com end não mexe", Type("    endpoint|", " "), "    endpoint |");
        Eq("end já alinhado não muda", Type("if x then\n    foo()\nend|", " "), "if x then\n    foo()\nend |");
        Eq("blocos desligado não realinha", Type("if x then\n    foo()\n    end|", " ", blocks: false), "if x then\n    foo()\n    end |");

        // ---- Enter ----
        Eq("enter: if…then escreve o end", Enter("if x then|"), "if x then\n    |\nend");
        Eq("enter: if…then já fechado não repete o end", Enter("if x then|\nend"), "if x then\n    |\nend");
        Eq("enter: function escreve o end", Enter("function foo()|"), "function foo()\n    |\nend");
        Eq("enter: for…do escreve o end", Enter("for i = 1, 3 do|"), "for i = 1, 3 do\n    |\nend");
        Eq("enter: while…do escreve o end", Enter("while x do|"), "while x do\n    |\nend");
        Eq("enter: repeat só indenta (sem until)", Enter("repeat|"), "repeat\n    |");
        Eq("enter: {|} abre em três linhas", Enter("local t = {|}"), "local t = {\n    |\n}");
        Eq("enter: (|) abre em três linhas", Enter("print(|)"), "print(\n    |\n)");
        Eq("enter: end desalinhado é corrigido", Enter("if a then\n    x()\n    end|"), "if a then\n    x()\nend\n|");
        Eq("enter: linha comum mantém indentação", Enter("    x = 1|"), "    x = 1\n    |");
        Eq("enter: linha comum sem indentação", Enter("x = 1|"), "x = 1\n|");
        Eq("enter: dentro de string usa o padrão", Enter("s = \"abc|\""), "s = \"abc\n|\"");
        Eq("enter: respeita a indentação atual", Enter("  if x then|"), "  if x then\n      |\n  end");
        Eq("enter: else indenta o corpo", Enter("if a then\n    x()\nelse|\nend"), "if a then\n    x()\nelse\n    |\nend");
        Eq("enter: elseif…then não repete end", Enter("if a then\n    x()\nelseif b then|\nend"), "if a then\n    x()\nelseif b then\n    |\nend");
        Eq("enter: texto depois do cursor desce junto", Enter("if x then| foo()"), "if x then\n    |foo()");
        Eq("enter: function com parâmetros já fechada", Enter("function f(a)|\nend"), "function f(a)\n    |\nend");
        Eq("enter: function anônima escreve o end", Enter("local f = function(a)|"), "local f = function(a)\n    |\nend");
        Eq("enter: pcall(function()|) põe o end antes do )", Enter("pcall(function()|)"), "pcall(function()\n    |\nend)");
        Eq("enter: linha em branco usa o padrão", Enter("    |"), "    \n    |");
        Check("enter: com seleção não interfere", LuaEditing.Enter("if x then", 9, 3, "    ") is null);

        // ---- comentar ----
        Eq("comentar: uma linha", Cut("x = 1", LuaEditing.ToggleComment("x = 1", 0, 0)), "-- x = 1");
        Eq("descomentar: uma linha", Cut("-- x = 1", LuaEditing.ToggleComment("-- x = 1", 0, 0)), "x = 1");
        Eq("comentar: várias linhas com indentação e linha vazia",
            Cut("  a\n\n  b", LuaEditing.ToggleComment("  a\n\n  b", 0, 8)), "  -- a\n\n  -- b");
        Eq("descomentar: várias linhas", Cut("  -- a\n\n  -- b", LuaEditing.ToggleComment("  -- a\n\n  -- b", 0, 13)), "  a\n\n  b");
        Eq("comentar: misto comenta tudo", Cut("-- a\nb", LuaEditing.ToggleComment("-- a\nb", 0, 6)), "-- -- a\n-- b");
        Eq("comentar: seleção que termina no começo da próxima linha não a inclui",
            Cut("a\nb", LuaEditing.ToggleComment("a\nb", 0, 2)), "-- a\nb");
        Eq("comentar: CRLF preservado", Cut("a\r\nb", LuaEditing.ToggleComment("a\r\nb", 0, 4)), "-- a\r\n-- b");
        Eq("comentar: indentação desigual usa a menor", Cut("    a\n  b", LuaEditing.ToggleComment("    a\n  b", 0, 9)), "  --   a\n  -- b");

        // ---- leitor de Lua ----
        Check("leitor: dentro de string curta", LuaScan.StateAt("x = \"abc", 7) == LuaState.String);
        Check("leitor: depois de fechar a string", LuaScan.StateAt("x = \"abc\" y", 10) == LuaState.Code);
        Check("leitor: comentário de linha", LuaScan.StateAt("-- hi", 5) == LuaState.Comment);
        Check("leitor: comentário longo aberto", LuaScan.StateAt("--[[ a\nb ]] c", 8) == LuaState.Comment);
        Check("leitor: depois do comentário longo", LuaScan.StateAt("--[[ a\nb ]] c", 13) == LuaState.Code);
        Check("leitor: string longa com nível", LuaScan.StateAt("[==[ x ]] y ]==] z", 10) == LuaState.String);
        Check("leitor: aspas escapadas não fecham", LuaScan.StateAt("s = \"a\\\"b", 9) == LuaState.String);
        Check("leitor: Depth de bloco completo", LuaBlocks.Depth("function f() if x then y() end end") == 0);
        Check("leitor: Depth com bloco aberto", LuaBlocks.Depth("if a then") == 1);
        Check("leitor: Depth ignora strings e comentários", LuaBlocks.Depth("-- if\nx = 'end' .. \"function\"") == 0);
        Check("leitor: Depth de repeat/until e for/do", LuaBlocks.Depth("repeat x() until y for i=1,2 do end") == 0);
    }

    public static async Task<int> RunAsync(string root)
    {
        Directory.CreateDirectory(root);
        var libDir = Path.Combine(root, "Scripts");

        // ---------- Biblioteca em pasta ----------
        var lib = new FolderScriptLibrary(libDir);
        var all = lib.GetAll();
        Check("biblioteca: cria 4 exemplos", all.Count == 4, $"{all.Count}");
        Check("biblioteca: subpasta vira tag", all.Count(e => e.Tag == "utils") == 2);
        Check("biblioteca: entradas têm caminho", all.All(e => e.Path is not null && File.Exists(e.Path)));
        File.Delete(all[0].Path!);
        var lib2 = new FolderScriptLibrary(libDir);
        Check("biblioteca: não recria exemplos apagados", lib2.GetAll().Count == 3);

        // ---------- ViewModel ----------
        var settings = new AppSettings { RestoreTabs = false };
        var vm = new MainViewModel(lib2, new MockRunSession(), settings) { PersistenceEnabled = false };

        Check("abre com 1 aba (Main)", vm.Tabs.Count == 1 && vm.Tabs[0].IsMain);
        Check("console e scripts fechados por padrão", !vm.ConsoleOpen && !vm.LibraryOpen);

        // abrir arquivo
        var fa = Path.Combine(root, "a.lua");
        File.WriteAllText(fa, "print('a')\n");
        vm.OpenFromPath(fa);
        var ta = vm.SelectedTab!;
        Check("abrir: aba nova com título e conteúdo", ta.Title == "a.lua" && ta.Content == "print('a')\n" && ta.FilePath == Path.GetFullPath(fa),
            $"{ta.Title}|{ta.FilePath}");
        Check("abrir: aba limpa (sem bolinha)", !ta.IsDirty && !ta.ShowDirty);
        vm.OpenFromPath(fa);
        Check("abrir de novo: não duplica", vm.Tabs.Count == 2 && vm.SelectedTab == ta);
        vm.OpenFromPath(Path.Combine(root, "nao_existe.lua"));
        Check("abrir inexistente: não quebra nem cria aba", vm.Tabs.Count == 2);

        // sujar e salvar
        ta.Content = "print('editado')\n";
        Check("editar: marca como sujo", ta.IsDirty && ta.ShowDirty);
        Check("salvar: grava no disco", vm.WriteTab(ta, fa) && File.ReadAllText(fa) == "print('editado')\n");
        Check("salvar: volta a ficar limpo", !ta.IsDirty);
        var bomCheck = File.ReadAllBytes(fa);
        Check("salvar: UTF-8 sem BOM", !(bomCheck.Length >= 3 && bomCheck[0] == 0xEF && bomCheck[1] == 0xBB));
        var fb = Path.Combine(root, "novo nome.lua");
        vm.NewTabCommand.Execute(null);
        var tb = vm.SelectedTab!;
        tb.Content = "x = 1";
        Check("salvar como: aba assume o nome do arquivo", vm.WriteTab(tb, fb) && tb.Title == "novo nome.lua" && tb.FilePath == fb);
        Check("salvar em pasta inexistente: falha com segurança", !vm.WriteTab(tb, Path.Combine(root, "nada", "x.lua")));
        Check("salvar na biblioteca: recarrega lista", vm.WriteTab(tb, Path.Combine(libDir, "do_teste.lua")) && vm.FilteredLibrary.Any(e => e.Name == "do_teste.lua"));

        // fechar com confirmação
        vm.NewTabCommand.Execute(null);
        var tc = vm.SelectedTab!;
        tc.Content = "print('rascunho')";
        var before = vm.Tabs.Count;
        vm.CloseTabCommand.Execute(tc);
        Check("fechar sujo: pede confirmação e mantém a aba", vm.ConfirmOpen && vm.Tabs.Contains(tc) && vm.Tabs.Count == before);
        vm.ConfirmCancelCommand.Execute(null);
        Check("cancelar: nada fecha", !vm.ConfirmOpen && vm.Tabs.Contains(tc));
        vm.CloseTabCommand.Execute(tc);
        vm.ConfirmDiscardCommand.Execute(null);
        Check("descartar: fecha sem salvar", !vm.Tabs.Contains(tc) && !vm.ConfirmOpen);

        vm.NewTabCommand.Execute(null);
        var td = vm.SelectedTab!;
        td.Content = "keep";
        vm.SaveHandler = (t, _) => Task.FromResult(false); // usuário cancelou o diálogo de arquivo
        vm.CloseTabCommand.Execute(td);
        await vm.ConfirmSaveCommand.ExecuteAsync(null);
        Check("salvar cancelado: aba e confirmação continuam", vm.Tabs.Contains(td) && vm.ConfirmOpen);
        vm.SaveHandler = (t, _) => Task.FromResult(vm.WriteTab(t, Path.Combine(root, "d.lua")));
        await vm.ConfirmSaveCommand.ExecuteAsync(null);
        Check("salvar na confirmação: grava e fecha", !vm.Tabs.Contains(td) && !vm.ConfirmOpen && File.ReadAllText(Path.Combine(root, "d.lua")) == "keep");

        vm.NewTabCommand.Execute(null);
        var te = vm.SelectedTab!;
        vm.CloseTabCommand.Execute(te);
        Check("fechar aba vazia/limpa: fecha direto", !vm.Tabs.Contains(te) && !vm.ConfirmOpen);
        vm.CloseTabCommand.Execute(vm.Tabs[0]);
        Check("aba Main não fecha", vm.Tabs.Count > 0 && vm.Tabs[0].IsMain);
        Check("sempre sobra uma aba selecionada", vm.SelectedTab is not null && vm.Tabs.Contains(vm.SelectedTab));

        // renomear
        vm.NewTabCommand.Execute(null);
        var tr = vm.SelectedTab!;
        vm.BeginRenameCommand.Execute(tr);
        Check("renomear: entra em edição com o título atual", tr.IsRenaming && tr.EditTitle == tr.Title);
        tr.EditTitle = "   meu script  ";
        vm.CommitRename(tr);
        Check("renomear: confirma com trim", !tr.IsRenaming && tr.Title == "meu script");
        vm.BeginRenameCommand.Execute(tr);
        tr.EditTitle = "   ";
        vm.CommitRename(tr);
        Check("renomear: vazio é ignorado", tr.Title == "meu script");
        vm.BeginRenameCommand.Execute(tr);
        tr.EditTitle = "outro";
        vm.CancelRename(tr);
        Check("renomear: Esc cancela", !tr.IsRenaming && tr.Title == "meu script");
        vm.CommitRename(tr); // LostFocus depois de cancelar não pode aplicar nada
        Check("renomear: commit depois de cancelar não faz nada", tr.Title == "meu script");
        vm.BeginRenameCommand.Execute(vm.Tabs[0]);
        Check("renomear: aba Main não renomeia", !vm.Tabs[0].IsRenaming);

        // menu da aba: duplicar
        var idx = vm.Tabs.IndexOf(tr);
        vm.OpenTabMenu(tr, 100, 46);
        Check("menu: abre e libera edição", vm.TabMenuOpen && vm.MenuCanEdit);
        vm.MenuDuplicateCommand.Execute(null);
        Check("duplicar: cria cópia logo depois", !vm.TabMenuOpen && vm.Tabs[idx + 1].Title == "meu script (copy)" && vm.SelectedTab == vm.Tabs[idx + 1]);
        vm.OpenTabMenu(tr, 100, 46);
        vm.MenuDuplicateCommand.Execute(null);
        Check("duplicar de novo: nome único", vm.Tabs.Select(t => t.Title).Distinct().Count() == vm.Tabs.Count);
        vm.OpenTabMenu(vm.Tabs[0], 10, 46);
        Check("menu na aba Main: sem renomear/fechar", !vm.MenuCanEdit);
        vm.CloseTabMenuCommand.Execute(null);

        // fechar as outras
        var dirty = new ScriptTab("sujo", "conteudo", false, null, "") ;
        vm.Tabs.Add(dirty);
        vm.OpenTabMenu(tr, 100, 46);
        vm.MenuCloseOthersCommand.Execute(null);
        Check("fechar as outras: mantém a escolhida, a Main e a suja",
            vm.Tabs.Contains(tr) && vm.Tabs.Contains(dirty) && vm.Tabs[0].IsMain && vm.SelectedTab == tr && vm.Tabs.Count == 3,
            string.Join(",", vm.Tabs.Select(t => t.Title)));

        // ciclo de abas
        vm.SelectedTab = vm.Tabs[^1];
        vm.CycleTab(+1);
        Check("Ctrl+Tab: dá a volta no fim", vm.SelectedTab == vm.Tabs[0]);
        vm.CycleTab(-1);
        Check("Ctrl+Shift+Tab: dá a volta no início", vm.SelectedTab == vm.Tabs[^1]);

        // paleta de comandos
        vm.OpenPalette();
        var total = vm.PaletteItems.Count;
        Check("paleta: lista todos os comandos", vm.PaletteOpen && total >= 15 && vm.SelectedPaletteItem is not null);
        vm.PaletteQuery = "tab";
        Check("paleta: filtra por texto", vm.PaletteItems.Count > 0 && vm.PaletteItems.Count < total && vm.PaletteItems.All(p => p.Title.Contains("tab", StringComparison.OrdinalIgnoreCase)));
        vm.PaletteQuery = "zzzz";
        Check("paleta: sem resultado não quebra", vm.PaletteItems.Count == 0 && vm.SelectedPaletteItem is null);
        vm.MovePalette(+1); vm.RunPaletteItem();
        vm.PaletteQuery = "new script";
        var n0 = vm.Tabs.Count;
        vm.RunPaletteItem();
        Check("paleta: Enter executa o comando e fecha", !vm.PaletteOpen && vm.Tabs.Count == n0 + 1);
        vm.OpenPalette();
        var first = vm.SelectedPaletteItem;
        vm.MovePalette(-1);
        Check("paleta: seta para cima no topo dá a volta", vm.SelectedPaletteItem == vm.PaletteItems[^1] && vm.SelectedPaletteItem != first);
        vm.ClosePalette();

        // zoom
        settings.EditorFontSize = 13;
        vm.Zoom(+1); var z1 = settings.EditorFontSize;
        vm.Zoom(-1); vm.Zoom(-1); var z2 = settings.EditorFontSize;
        for (var i = 0; i < 20; i++) vm.Zoom(+1);
        var zmax = settings.EditorFontSize;
        for (var i = 0; i < 20; i++) vm.Zoom(-1);
        var zmin = settings.EditorFontSize;
        vm.ZoomReset();
        Check("zoom: sobe e desce pela lista de tamanhos", z1 == 14 && z2 == 12, $"{z1}/{z2}");
        Check("zoom: respeita os limites (10..22)", zmax == 22 && zmin == 10, $"{zmax}/{zmin}");
        Check("zoom: Ctrl+0 volta ao padrão", settings.EditorFontSize == 13);

        // executar e Output
        vm.ClearConsoleCommand.Execute(null);
        await vm.RunCommand.ExecuteAsync(null);
        Check("executar sem Attach: avisa e não executa", vm.Console.Any(c => c.Level == "Warning" && c.Text.Contains("Attach")) && vm.WarnCount == 1 && !vm.Console.Any(c => c.Text.Contains("[mock]")));
        await vm.AttachCommand.ExecuteAsync(null);
        Check("attach: conecta e habilita executar", vm.IsAttached && vm.CanRun);
        await vm.RunCommand.ExecuteAsync(null);
        Check("executar com Attach: roda e abre o Output", vm.Console.Any(c => c.Text.Contains("[mock]")) && vm.ConsoleOpen);
        var total2 = vm.Console.Count;
        vm.ToggleWarnCommand.Execute(null);
        Check("filtro: esconder avisos tira só avisos", vm.Console.Count < total2 && vm.Console.All(c => c.Level != "Warning") && vm.WarnCount >= 1);
        vm.ToggleWarnCommand.Execute(null);
        Check("filtro: reativar traz de volta, na ordem", vm.Console.Count == total2);
        vm.ToggleInfoCommand.Execute(null); vm.ToggleErrorCommand.Execute(null);
        Check("filtro: só avisos", vm.Console.Count > 0 && vm.Console.All(c => c.Level == "Warning"));
        vm.ToggleInfoCommand.Execute(null); vm.ToggleErrorCommand.Execute(null);
        Check("copiar: texto do Output tem hora e mensagem", vm.ConsoleText.Contains("Attached") && vm.ConsoleText.Contains("  "));
        vm.ClearConsoleCommand.Execute(null);
        Check("limpar Output: zera lista e contadores", vm.Console.Count == 0 && vm.WarnCount == 0);
        await vm.AttachCommand.ExecuteAsync(null);
        Check("detach: desconecta e bloqueia executar", !vm.IsAttached && !vm.CanRun);

        // sessão (serialização)
        var saved = new List<SavedTab> { new("Main", "m", true, null, null), new("a.lua", "novo", false, fa, "antigo") };
        var json = JsonSerializer.Serialize(saved);
        var back = JsonSerializer.Deserialize<List<SavedTab>>(json)!;
        Check("sessão: round-trip guarda caminho e último salvo", back[1].FilePath == fa && back[1].SavedContent == "antigo" && back[0].SavedContent is null);
        var legacy = JsonSerializer.Deserialize<List<SavedTab>>("[{\"Title\":\"Main\",\"Content\":\"x\",\"IsMain\":true}]")!;
        Check("sessão: arquivo antigo (sem os campos novos) ainda lê", legacy.Count == 1 && legacy[0].FilePath is null);
        var restored = new ScriptTab(back[1].Title, back[1].Content, false, back[1].FilePath, back[1].SavedContent);
        Check("sessão: aba restaurada continua marcada como suja", restored.IsDirty && restored.ShowDirty);
        var mainRest = new ScriptTab("Main", "texto", true);
        Check("aba Main sem arquivo nunca mostra bolinha", mainRest.IsDirty == false && !mainRest.ShowDirty);
        mainRest.Content = "mudou";
        Check("aba Main editada sem arquivo: sem bolinha", mainRest.IsDirty && !mainRest.ShowDirty);

        // geometria da janela nas configurações
        var s2 = new AppSettings();
        s2.CopyFrom(new AppSettings { WordWrap = true, WindowWidth = 1234 });
        Check("restaurar padrões: não mexe na geometria", s2.WordWrap && s2.WindowWidth == 0);
        var s3 = new AppSettings(); s3.CopyWindowFrom(new AppSettings { WindowWidth = 800, WindowHeight = 500, WindowX = 10, WindowY = 20, HasWindowPos = true, WindowMaximized = true });
        Check("geometria: CopyWindowFrom copia tudo", s3.WindowWidth == 800 && s3.WindowHeight == 500 && s3.WindowX == 10 && s3.WindowY == 20 && s3.HasWindowPos && s3.WindowMaximized);
        var jsonS = JsonSerializer.Serialize(new AppSettings { WindowWidth = 777, HasWindowPos = true });
        Check("geometria: entra no settings.json", JsonSerializer.Deserialize<AppSettings>(jsonS)!.WindowWidth == 777);
        var oldKey = JsonSerializer.Deserialize<AppSettings>("{\"OutputOnStartup\":true,\"EditorFontSize\":15}")!;
        Check("config antiga: chave removida é ignorada e Output fica fechado", !oldKey.ShowOutputOnStart && oldKey.EditorFontSize == 15);

        // biblioteca preguiçosa
        var lazyVm = new MainViewModel(new FolderScriptLibrary(libDir), new MockRunSession(), new AppSettings { RestoreTabs = false }) { PersistenceEnabled = false };
        Check("biblioteca preguiçosa: vazia até a barra abrir", lazyVm.FilteredLibrary.Count == 0);
        lazyVm.LibraryOpen = true;
        var expected = new FolderScriptLibrary(libDir).GetAll();
        Check("biblioteca preguiçosa: carrega ao abrir a barra", lazyVm.FilteredLibrary.Count == expected.Count && expected.Count > 0);
        Check("biblioteca: conteúdo não é lido na listagem, só o caminho", expected.All(e => e.Content == "" && e.Path is not null));
        lazyVm.OpenScriptCommand.Execute(lazyVm.FilteredLibrary[0]);
        Check("biblioteca: abrir lê o arquivo na hora", lazyVm.SelectedTab!.FilePath is not null && lazyVm.SelectedTab.Content.Length > 0);

        // teto do Output
        var logVm = new MainViewModel(lib2, new MockRunSession(), new AppSettings { RestoreTabs = false }) { PersistenceEnabled = false };
        for (var i = 1; i <= 1500; i++) logVm.Log(Lazuli.Contracts.LogLevel.Info, $"linha {i}");
        var logCount = logVm.Console.Count;
        Check("Output: nunca passa do teto + lote", logCount <= MainViewModel.MaxLogLines + 200 && logCount >= MainViewModel.MaxLogLines, $"{logCount}");
        Check("Output: guarda as mais recentes, descarta as antigas", logVm.Console[^1].Text == "linha 1500" && !logVm.Console.Any(c => c.Text == "linha 1"));
        Check("Output: ordem preservada depois da poda", logVm.Console.Zip(logVm.Console.Skip(1), (a, b) => int.Parse(a.Text[6..]) < int.Parse(b.Text[6..])).All(x => x));

        // modo de desempenho
        Check("desempenho: padrão é Automático", new AppSettings().PerformanceMode == "Auto");
        Check("desempenho: Leve liga o modo leve", Perf.IsLite(new AppSettings { PerformanceMode = "Lite" }));
        Check("desempenho: Normal desliga o modo leve", !Perf.IsLite(new AppSettings { PerformanceMode = "Normal" }));
        Check("desempenho: Automático segue o hardware", Perf.IsLite(new AppSettings { PerformanceMode = "Auto" }) == Perf.IsLowEndHardware());
        Check("desempenho: valor desconhecido cai em Automático", Perf.IsLite(new AppSettings { PerformanceMode = "xyz" }) == Perf.IsLowEndHardware());
        var perfJson = JsonSerializer.Serialize(new AppSettings { PerformanceMode = "Lite" });
        Check("desempenho: é salvo e lido", JsonSerializer.Deserialize<AppSettings>(perfJson)!.PerformanceMode == "Lite");
        Check("desempenho: config antiga (sem a chave) fica em Automático", JsonSerializer.Deserialize<AppSettings>("{\"EditorFontSize\":14}")!.PerformanceMode == "Auto");
        var sPerf = new AppSettings { PerformanceMode = "Lite" }; sPerf.CopyFrom(new AppSettings());
        Check("desempenho: restaurar padrões volta para Automático", sPerf.PerformanceMode == "Auto");

        EditingTests();

        // splash opcional
        Check("splash: ligada por padrão", new AppSettings().ShowSplash);
        var noKey = JsonSerializer.Deserialize<AppSettings>("{\"EditorFontSize\":14}")!;
        Check("splash: config antiga (sem a chave) mantém ligada", noKey.ShowSplash && noKey.EditorFontSize == 14);
        var offJson = JsonSerializer.Serialize(new AppSettings { ShowSplash = false });
        Check("splash: desligada é salva e lida", !JsonSerializer.Deserialize<AppSettings>(offJson)!.ShowSplash);
        var s4 = new AppSettings { ShowSplash = false };
        s4.CopyFrom(new AppSettings());
        Check("splash: restaurar padrões volta a ligar", s4.ShowSplash);

        // ---------- Backends (plugins) ----------
        await BackendTests.RunAsync(root);

        Console.WriteLine($"\nRESULTADO: {_pass} ok, {_fail} falha(s)");
        return _fail;
    }
}
#endif
