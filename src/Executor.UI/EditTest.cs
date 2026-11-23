#if DEBUG
using Avalonia.Input;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Editing;
using Executor.UI.ViewModels;

namespace Executor.UI;

/// <summary>
/// Teste de integração do editor (EXECUTOR_EDITTEST=1): abre a janela de verdade fora da tela e "digita" no
/// editor real (TextInput e teclas), conferindo texto, cursor, desfazer e as opções. Só em Debug.
/// </summary>
public static class EditTest
{
    private static int _pass, _fail;

    private static void Check(string name, bool ok, string? detail = null)
    {
        if (ok) _pass++; else _fail++;
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(ok || detail is null ? "" : "  -> " + detail)}");
    }

    private static string Show(TextEditor ed) => ed.Text.Insert(Math.Min(ed.CaretOffset, ed.Text.Length), "|").Replace("\n", "\\n");

    private static void Set(TextEditor ed, string marked)
    {
        var caret = marked.IndexOf('|');
        ed.Text = marked.Remove(caret, 1);
        ed.CaretOffset = caret;
        ed.TextArea.ClearSelection();
    }

    private static void Type(TextEditor ed, string s)
    {
        foreach (var c in s) ed.TextArea.PerformTextInput(c.ToString());
    }

    private static void Key(TextEditor ed, Key key, KeyModifiers mods = KeyModifiers.None)
    {
        ed.TextArea.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = mods, Source = ed.TextArea,
        });
    }

    public static async Task RunAsync(MainWindow window)
    {
        await Task.Delay(600); // deixa o realce de sintaxe e o layout assentarem
        var ed = window.Editor;
        var vm = (MainViewModel)window.DataContext!;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            Set(ed, "|");
            Type(ed, "print(");
            Check("editor: ( fecha sozinho e o cursor fica no meio", Show(ed) == "print(|)", Show(ed));

            Set(ed, "|");
            Type(ed, "print(\"oi\")");
            Check("editor: digitar print(\"oi\") sai exato (pula os fechos)", Show(ed) == "print(\"oi\")|", Show(ed));

            Set(ed, "|");
            Type(ed, "local t = {a = [1], b = 'x'}");
            Check("editor: tabela com colchetes e aspas sai exata", Show(ed) == "local t = {a = [1], b = 'x'}|", Show(ed));

            Set(ed, "if x then|");
            Key(ed, Avalonia.Input.Key.Enter);
            Check("editor: Enter depois de then indenta e escreve o end", Show(ed) == "if x then\\n    |\\nend", Show(ed));

            Set(ed, "(|)");
            Key(ed, Avalonia.Input.Key.Back);
            Check("editor: Backspace no par vazio apaga os dois", Show(ed) == "|", Show(ed));

            Set(ed, "abc|");
            ed.TextArea.Selection = Selection.Create(ed.TextArea, 0, 3);
            ed.TextArea.PerformTextInput("(");
            Check("editor: seleção + ( envolve", ed.Text == "(abc)", ed.Text);

            Set(ed, "x = 1|");
            Key(ed, Avalonia.Input.Key.Oem2, KeyModifiers.Control);
            Check("editor: Ctrl+/ comenta (teclado US)", ed.Text == "-- x = 1", ed.Text);
            Key(ed, Avalonia.Input.Key.AbntC1, KeyModifiers.Control);
            Check("editor: Ctrl+/ de novo descomenta (ABNT2)", ed.Text == "x = 1", ed.Text);

            Set(ed, "if a then\n    x()\n    end|");
            Type(ed, " ");
            Check("editor: end digitado realinha com o if", ed.Text == "if a then\n    x()\nend ", ed.Text.Replace("\n", "\\n"));

            // desfazer: um Ctrl+Z desfaz o par inteiro
            Set(ed, "|");
            ed.Document.UndoStack.ClearAll();
            Type(ed, "(");
            Check("editor: ( gerou o par", ed.Text == "()", ed.Text);
            ed.Undo();
            Check("editor: um desfazer remove o par inteiro", ed.Text == "", $"«{ed.Text}»");

            // o texto do editor é o que a aba guarda
            Set(ed, "|");
            Type(ed, "foo(");
            Check("editor: a aba recebe o texto com o par", vm.SelectedTab!.Content == "foo()", vm.SelectedTab.Content);

            // opções
            vm.Settings.AutoPairs = false;
            Set(ed, "|");
            Type(ed, "(");
            Check("opção: fechar pares desligada insere só (", ed.Text == "(", ed.Text);
            vm.Settings.AutoPairs = true;
            vm.Settings.SmartBlocks = false;
            Set(ed, "if x then|");
            Key(ed, Avalonia.Input.Key.Enter);
            // (o Enter padrão vem dos atalhos da janela, que um evento simulado não dispara; o que importa é não termos escrito o end)
            Check("opção: blocos desligado = o app não escreve end nem indenta", ed.Text == "if x then", ed.Text.Replace("\n", "\\n"));
            vm.Settings.SmartBlocks = true;

            // acentos e texto comum não são afetados
            Set(ed, "|");
            Type(ed, "-- não é isso, né? ok");
            Check("editor: texto comum e acentos saem iguais", ed.Text == "-- não é isso, né? ok", ed.Text);
        });
        Console.WriteLine($"\nRESULTADO (editor real): {_pass} ok, {_fail} falha(s)");
    }
}
#endif
