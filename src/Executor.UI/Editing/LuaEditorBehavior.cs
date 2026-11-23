using Avalonia.Input;
using AvaloniaEdit;
using AvaloniaEdit.Editing;

namespace Executor.UI.Editing;

/// <summary>
/// Liga as regras de <see cref="LuaEditing"/> ao editor: fecha ( [ { e aspas, pula o fecho, apaga o par vazio,
/// envolve a seleção, Enter/end/indentação de Lua e Ctrl+/ para comentar.
/// </summary>
public sealed class LuaEditorBehavior
{
    private readonly TextEditor _ed;
    private readonly Func<string> _unit;
    private readonly Func<bool> _pairs;
    private readonly Func<bool> _blocks;

    public LuaEditorBehavior(TextEditor editor, Func<string> indentUnit, Func<bool> pairs, Func<bool> blocks)
    {
        _ed = editor; _unit = indentUnit; _pairs = pairs; _blocks = blocks;
        _ed.TextArea.TextEntering += OnEntering;
        // túnel: roda antes do tratamento padrão de Enter/Backspace do editor
        _ed.TextArea.AddHandler(InputElement.KeyDownEvent, OnKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    private static bool IsBlockWord(string s) => s is "end" or "else" or "elseif" or "until";

    private (int Start, int End) GetSelection()
    {
        var area = _ed.TextArea;
        var sel = area.Selection;
        if (sel.IsEmpty) return (area.Caret.Offset, area.Caret.Offset);
        return (sel.SurroundingSegment.Offset, sel.SurroundingSegment.EndOffset);
    }

    private void Apply(EditResult r)
    {
        var doc = _ed.Document;
        var area = _ed.TextArea;
        doc.BeginUpdate();
        try { doc.Replace(r.Offset, r.RemoveLength, r.Insert); }
        finally { doc.EndUpdate(); }
        area.Caret.Offset = r.CaretAfter;
        if (r.SelLengthAfter > 0)
            area.Selection = Selection.Create(area, r.SelStartAfter, r.SelStartAfter + r.SelLengthAfter);
        else
            area.ClearSelection();
        area.Caret.BringCaretToView();
    }

    private void OnEntering(object? sender, TextInputEventArgs e)
    {
        if (e.Text is not { Length: 1 } s) return;
        var ch = s[0];
        var area = _ed.TextArea;
        var doc = _ed.Document;
        var special = "([{)]}\"'".IndexOf(ch) >= 0;

        if (special)
        {
            if (!_pairs() && !_blocks()) return;
        }
        else
        {
            // letras e números (quase toda digitação) saem aqui sem tocar no texto todo
            if (!_blocks() || LuaEditing.IsWordChar(ch)) return;
            var line = doc.GetLineByOffset(area.Caret.Offset);
            var before = doc.GetText(line.Offset, area.Caret.Offset - line.Offset).Trim();
            if (!IsBlockWord(before)) return;
        }

        var (ss, se) = GetSelection();
        var r = LuaEditing.Typed(doc.Text, area.Caret.Offset, ss, se, ch, _unit(), _pairs(), _blocks());
        if (r is null) return;
        e.Handled = true;
        Apply(r);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var area = _ed.TextArea;
        var doc = _ed.Document;

        if (e.KeyModifiers == KeyModifiers.None && e.Key == Key.Back && _pairs())
        {
            var (ss, se) = GetSelection();
            if (LuaEditing.Backspace(doc.Text, area.Caret.Offset, se - ss) is { } r) { Apply(r); e.Handled = true; }
            return;
        }

        if (e.KeyModifiers == KeyModifiers.None && e.Key == Key.Enter && (_pairs() || _blocks()))
        {
            var (ss, se) = GetSelection();
            if (LuaEditing.Enter(doc.Text, area.Caret.Offset, se - ss, _unit(), _pairs(), _blocks()) is { } r)
            { Apply(r); e.Handled = true; }
            return;
        }

        // Ctrl+/ (teclado US), Ctrl+; e Ctrl+/ (ABNT2) e o "/" do teclado numérico
        if (e.KeyModifiers == KeyModifiers.Control && e.Key is Key.Oem2 or Key.AbntC1 or Key.Divide)
        {
            ToggleComment();
            e.Handled = true;
        }
    }

    /// <summary>Comenta/descomenta as linhas da seleção (ou a linha do cursor).</summary>
    public void ToggleComment()
    {
        var (ss, se) = GetSelection();
        var r = LuaEditing.ToggleComment(_ed.Document.Text, ss, se);
        Apply(r);
    }
}
