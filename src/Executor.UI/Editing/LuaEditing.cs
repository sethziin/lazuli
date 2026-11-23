namespace Executor.UI.Editing;

/// <summary>Uma troca contínua no texto: remove <c>RemoveLength</c> a partir de <c>Offset</c> e insere <c>Insert</c>.</summary>
public sealed record EditResult(int Offset, int RemoveLength, string Insert, int CaretAfter,
                                int SelStartAfter = 0, int SelLengthAfter = 0);

/// <summary>Estrutura de blocos Lua (function/if/do/repeat … end/until) e colchetes.</summary>
public static class LuaBlocks
{
    private static bool IsOpenerKw(string w) => w is "function" or "if" or "do" or "repeat";
    private static bool IsCloserKw(string w) => w is "end" or "until";
    private static bool IsKw(string k) => IsOpenerKw(k);

    /// <summary>Abertos menos fechados em todo o texto (&gt; 0 = falta fechar algum bloco).</summary>
    public static int Depth(string text)
    {
        var d = 0;
        foreach (var t in LuaScan.Tokens(text))
        {
            if (!t.IsWord) continue;
            if (IsOpenerKw(t.Text)) d++;
            else if (IsCloserKw(t.Text)) d--;
        }
        return d;
    }

    internal static string IndentAt(string text, int offset)
    {
        var ls = offset == 0 ? 0 : text.LastIndexOf('\n', offset - 1) + 1;
        var j = ls;
        while (j < text.Length && (text[j] == ' ' || text[j] == '\t')) j++;
        return text.Substring(ls, j - ls);
    }

    /// <summary>
    /// Indentação do bloco/colchete que <paramref name="closer"/> (end, else, elseif, until, ) ] }) vai fechar,
    /// olhando só o que vem antes da linha que começa em <paramref name="lineStart"/>. Null se não há casamento.
    /// </summary>
    public static string? MatchingIndent(string text, int lineStart, string closer)
    {
        var stack = new List<(string Kind, string Indent)>();
        foreach (var t in LuaScan.Tokens(text, lineStart))
        {
            if (t.IsWord)
            {
                if (IsOpenerKw(t.Text)) stack.Add((t.Text, IndentAt(text, t.Start)));
                else if (IsCloserKw(t.Text))
                {
                    var idx = stack.FindLastIndex(e => IsKw(e.Kind));
                    if (idx >= 0) stack.RemoveRange(idx, stack.Count - idx);
                }
            }
            else if (t.Text is "(" or "[" or "{") stack.Add((t.Text, IndentAt(text, t.Start)));
            else
            {
                var want = t.Text == ")" ? "(" : t.Text == "]" ? "[" : "{";
                var idx = stack.FindLastIndex(e => e.Kind == want);
                if (idx >= 0) stack.RemoveRange(idx, stack.Count - idx);
            }
        }
        if (stack.Count == 0) return null;
        var top = stack[^1];
        var ok = closer switch
        {
            "end" => top.Kind is "function" or "if" or "do",
            "else" or "elseif" => top.Kind == "if",
            "until" => top.Kind == "repeat",
            ")" => top.Kind == "(",
            "]" => top.Kind == "[",
            "}" => top.Kind == "{",
            _ => false,
        };
        return ok ? top.Indent : null;
    }
}

/// <summary>
/// Regras de edição para Lua, como funções puras (texto + posição → troca). Quem aplica no editor é o
/// <see cref="LuaEditorBehavior"/>. Assim tudo é testável sem janela.
/// </summary>
public static class LuaEditing
{
    private const string Openers = "([{";
    private const string Closers = ")]}";

    public static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';
    private static char CloserFor(char o) => o switch { '(' => ')', '[' => ']', '{' => '}', _ => '\0' };
    private static bool IsBlockKw(string s) => s is "end" or "else" or "elseif" or "until";

    private static int LineStart(string t, int o) => o == 0 ? 0 : t.LastIndexOf('\n', o - 1) + 1;

    private static int LineEnd(string t, int o)
    {
        var e = t.IndexOf('\n', o);
        if (e < 0) return t.Length;
        return e > 0 && t[e - 1] == '\r' ? e - 1 : e;
    }

    private static string LeadingWs(string s)
    {
        var j = 0;
        while (j < s.Length && (s[j] == ' ' || s[j] == '\t')) j++;
        return s[..j];
    }

    /// <summary>O que pode vir logo depois do cursor para valer a pena fechar o par (fim, espaço ou pontuação).</summary>
    private static bool AllowedNext(char next) => next == '\0' || char.IsWhiteSpace(next) || ")]},;.".IndexOf(next) >= 0;

    // ---------------------------------------------------------------- digitação

    /// <summary>
    /// Chamado ANTES de inserir o caractere digitado. Devolve a troca a fazer no lugar da inserção normal,
    /// ou null para deixar o editor inserir o caractere sozinho.
    /// <paramref name="pairs"/>: fecha/pula/envolve. <paramref name="blocks"/>: realinha end/else/elseif/until e fechos.
    /// </summary>
    public static EditResult? Typed(string text, int caret, int selStart, int selEnd, char ch,
                                    string unit, bool pairs = true, bool blocks = true)
    {
        var hasSel = selEnd > selStart;
        var isOpen = Openers.IndexOf(ch) >= 0;
        var isClose = Closers.IndexOf(ch) >= 0;
        var isQuote = ch is '"' or '\'';

        // seleção + abre-par/aspas = envolve a seleção
        if (hasSel)
        {
            if (!pairs || !(isOpen || isQuote)) return null;
            var close = isQuote ? ch : CloserFor(ch);
            var inner = text.Substring(selStart, selEnd - selStart);
            return new EditResult(selStart, inner.Length, ch + inner + close,
                                  selStart + 1 + inner.Length, selStart + 1, inner.Length);
        }

        var next = caret < text.Length ? text[caret] : '\0';
        var prev = caret > 0 ? text[caret - 1] : '\0';

        // digitou o fecha-par que já está logo à frente: só anda
        if (pairs && (isClose || isQuote) && next == ch)
            return new EditResult(caret, 0, "", caret + 1);

        var state = LuaScan.StateAt(text, caret);

        if (pairs && isOpen)
        {
            if (state != LuaState.Code || !AllowedNext(next)) return null;
            return new EditResult(caret, 0, ch.ToString() + CloserFor(ch), caret + 1);
        }

        if (pairs && isQuote)
        {
            // dentro de string/comentário, depois de letra (apóstrofo: don't) ou de barra invertida: não fecha
            if (state != LuaState.Code || IsWordChar(prev) || prev == '\\' || !AllowedNext(next)) return null;
            return new EditResult(caret, 0, new string(ch, 2), caret + 1);
        }

        if (!blocks || state != LuaState.Code) return null;
        var ls = LineStart(text, caret);
        var before = text.Substring(ls, caret - ls);

        // fecho no começo da linha: alinha com a linha que abriu
        if (isClose && before.Trim().Length == 0)
        {
            var want = LuaBlocks.MatchingIndent(text, ls, ch.ToString());
            if (want != null && want != before)
                return new EditResult(ls, before.Length, want + ch, ls + want.Length + 1);
            return null;
        }

        // end/else/elseif/until sozinho no começo da linha e o usuário digitou um separador: realinha
        if (!IsWordChar(ch))
        {
            var word = before.TrimStart();
            if (IsBlockKw(word))
            {
                var cur = before[..^word.Length];
                var want = LuaBlocks.MatchingIndent(text, ls, word);
                if (want != null && want != cur)
                    return new EditResult(ls, before.Length, want + word + ch, ls + want.Length + word.Length + 1);
            }
        }
        return null;
    }

    // ---------------------------------------------------------------- backspace

    /// <summary>Backspace dentro de um par vazio — () [] {} "" '' — apaga os dois lados.</summary>
    public static EditResult? Backspace(string text, int caret, int selLength)
    {
        if (selLength > 0 || caret <= 0 || caret >= text.Length) return null;
        var b = text[caret - 1];
        var a = text[caret];
        var pair = (Openers.IndexOf(b) >= 0 && CloserFor(b) == a) || (b == a && b is '"' or '\'');
        return pair ? new EditResult(caret - 1, 2, "", caret - 1) : null;
    }

    // ---------------------------------------------------------------- enter

    private static bool IsFunctionHeader(List<LuaTok> toks) =>
        toks.Count > 0 && toks[^1].Text == ")" && toks.Any(t => t.IsWord && t.Text == "function")
        && !toks.Any(t => t.IsWord && t.Text == "end");

    /// <summary>
    /// Enter com cuidado de Lua: indenta depois de then/do/else/repeat/function(...) e de colchete aberto,
    /// escreve o <c>end</c> que falta, abre o par <c>{|}</c> em três linhas e realinha uma linha que começa
    /// com end/else/elseif/until. Null = Enter normal do editor.
    /// </summary>
    public static EditResult? Enter(string text, int caret, int selLength, string unit,
                                    bool pairs = true, bool blocks = true)
    {
        if (selLength > 0) return null;
        if (LuaScan.StateAt(text, caret) != LuaState.Code) return null;

        var nl = text.Contains("\r\n") ? "\r\n" : "\n";
        var ls = LineStart(text, caret);
        var le = LineEnd(text, caret);
        var before = text.Substring(ls, caret - ls);
        var after = text.Substring(caret, le - caret);
        if (before.Trim().Length == 0) return null;

        var indent = LeadingWs(before);
        var toks = LuaScan.Tokens(text, caret).Where(t => t.Start >= ls).ToList();
        var first = toks.Count > 0 ? toks[0] : default;
        var last = toks.Count > 0 ? toks[^1] : default;
        var hasToks = toks.Count > 0;

        // linha que começa com end/else/elseif/until: realinha com o bloco que ela fecha
        var lineIndent = indent;
        var editStart = caret;
        var prefix = "";
        if (blocks && hasToks && first.IsWord && IsBlockKw(first.Text) && first.Start - ls == indent.Length)
        {
            var want = LuaBlocks.MatchingIndent(text, ls, first.Text);
            if (want != null && want != indent)
            {
                lineIndent = want;
                editStart = ls;
                prefix = want + before[indent.Length..];
            }
        }

        var opensKw = blocks && hasToks &&
                      ((last.IsWord && last.Text is "then" or "do" or "else" or "repeat") || IsFunctionHeader(toks));
        var opensBracket = blocks && hasToks && !last.IsWord && last.Text is "{" or "(" or "[";
        var newIndent = (opensKw || opensBracket) ? lineIndent + unit : lineIndent;

        // {|} → três linhas
        var cb = caret > 0 ? text[caret - 1] : '\0';
        var ca = caret < text.Length ? text[caret] : '\0';
        if (pairs && Openers.IndexOf(cb) >= 0 && CloserFor(cb) == ca)
        {
            var ind = blocks ? newIndent : indent + unit;
            var lind = blocks ? lineIndent : indent;
            return new EditResult(editStart, caret - editStart, prefix + nl + ind + nl + lind,
                                  editStart + prefix.Length + nl.Length + ind.Length);
        }

        // depois do cursor só sobram fechos (ex.: o ")" do pcall(function()|) ): o "end" entra antes deles
        var tail = after.Trim();
        var tailIsCloser = tail.All(c => ")]},;".IndexOf(c) >= 0);
        var needsEnd = opensKw && tailIsCloser && hasToks &&
                       ((last.Text == "then" && first.Text == "if") || last.Text == "do" || IsFunctionHeader(toks)) &&
                       LuaBlocks.Depth(text) > 0;
        if (needsEnd)
        {
            return new EditResult(editStart, le - editStart,
                                  prefix + nl + newIndent + nl + lineIndent + "end" + tail,
                                  editStart + prefix.Length + nl.Length + newIndent.Length);
        }

        if (prefix.Length > 0 || opensKw || opensBracket)
        {
            var removeEnd = caret + LeadingWs(after).Length;
            return new EditResult(editStart, removeEnd - editStart, prefix + nl + newIndent,
                                  editStart + prefix.Length + nl.Length + newIndent.Length);
        }
        return null;
    }

    // ---------------------------------------------------------------- comentar

    /// <summary>Ctrl+/ : comenta (-- ) ou descomenta as linhas da seleção (ou a linha do cursor).</summary>
    public static EditResult ToggleComment(string text, int selStart, int selEnd)
    {
        if (selEnd > selStart && text[selEnd - 1] == '\n') selEnd--; // seleção que termina no começo da linha de baixo
        var blockStart = LineStart(text, selStart);
        var blockEnd = LineEnd(text, Math.Max(selStart, selEnd));
        var block = text.Substring(blockStart, blockEnd - blockStart);
        var nl = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = block.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var code = lines.Where(l => l.Trim().Length > 0).ToList();

        string[] result;
        if (code.Count > 0 && code.All(l => l.TrimStart().StartsWith("--")))
        {
            result = lines.Select(l =>
            {
                if (l.Trim().Length == 0) return l;
                var ws = LeadingWs(l);
                var rest = l[(ws.Length + 2)..];
                return ws + (rest.StartsWith(' ') ? rest[1..] : rest);
            }).ToArray();
        }
        else
        {
            var col = code.Count == 0 ? 0 : code.Min(l => LeadingWs(l).Length);
            result = lines.Select(l => l.Trim().Length == 0 ? l : l[..col] + "-- " + l[col..]).ToArray();
        }

        var newBlock = string.Join(nl, result);
        // com seleção: mantém o bloco selecionado; sem seleção: o cursor acompanha a mudança da linha
        if (selEnd > selStart || lines.Count > 1)
            return new EditResult(blockStart, blockEnd - blockStart, newBlock, blockStart + newBlock.Length,
                                  blockStart, newBlock.Length);
        var caretCol = selStart - blockStart;
        var delta = newBlock.Length - block.Length;
        var newCaret = blockStart + Math.Clamp(caretCol + delta, 0, newBlock.Length);
        return new EditResult(blockStart, blockEnd - blockStart, newBlock, newCaret);
    }
}
