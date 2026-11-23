namespace Lazuli.UI.Editing;

public enum LuaState { Code, String, Comment }

/// <summary>Palavra (identificador/palavra-chave) ou colchete encontrado fora de strings e comentários.</summary>
public readonly record struct LuaTok(string Text, int Start, bool IsWord);

/// <summary>
/// Leitor mínimo de Lua: sabe pular strings ("..", '..', [[..]], [==[..]==]) e comentários (-- e --[[ ]]),
/// dizer se uma posição está dentro de string/comentário e listar palavras e colchetes do código de verdade.
/// </summary>
public static class LuaScan
{
    public static LuaState StateAt(string s, int offset) => Scan(s, offset, null);

    /// <summary>Tokens (palavras e colchetes) que começam antes de <paramref name="limit"/>.</summary>
    public static List<LuaTok> Tokens(string s, int limit = int.MaxValue)
    {
        var list = new List<LuaTok>();
        Scan(s, limit, list);
        return list;
    }

    private static bool TryLongOpen(string s, int i, out int level, out int contentStart)
    {
        level = 0; contentStart = 0;
        if (i >= s.Length || s[i] != '[') return false;
        var j = i + 1;
        while (j < s.Length && s[j] == '=') { level++; j++; }
        if (j < s.Length && s[j] == '[') { contentStart = j + 1; return true; }
        return false;
    }

    /// <summary>Índice logo depois do fecho ]==] correspondente, ou -1 se não fecha.</summary>
    private static int FindLongClose(string s, int from, int level)
    {
        var close = "]" + new string('=', level) + "]";
        var idx = s.IndexOf(close, from, StringComparison.Ordinal);
        return idx < 0 ? -1 : idx + close.Length;
    }

    private static LuaState Scan(string s, int limit, List<LuaTok>? toks)
    {
        var n = s.Length;
        var i = 0;
        while (i < n && i < limit)
        {
            var c = s[i];

            // comentário
            if (c == '-' && i + 1 < n && s[i + 1] == '-')
            {
                var j = i + 2;
                if (j < n && TryLongOpen(s, j, out var lvl, out var cs))
                {
                    var end = FindLongClose(s, cs, lvl);
                    var open = end < 0;
                    if (open) end = n;
                    if (limit < end || (open && limit <= n)) return LuaState.Comment;
                    i = end; continue;
                }
                var e = s.IndexOf('\n', j);
                if (e < 0) e = n;
                if (limit <= e) return LuaState.Comment;
                i = e; continue;
            }

            // string curta
            if (c == '"' || c == '\'')
            {
                var j = i + 1;
                while (j < n && s[j] != c && s[j] != '\n') { if (s[j] == '\\' && j + 1 < n) j++; j++; }
                var closed = j < n && s[j] == c;
                if (limit <= j) return LuaState.String;
                i = closed ? j + 1 : j; continue;
            }

            // string longa
            if (c == '[' && TryLongOpen(s, i, out var lv, out var cstart))
            {
                var end = FindLongClose(s, cstart, lv);
                var open = end < 0;
                if (open) end = n;
                if (limit < end || (open && limit <= n)) return LuaState.String;
                i = end; continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                var j = i + 1;
                while (j < n && (char.IsLetterOrDigit(s[j]) || s[j] == '_')) j++;
                toks?.Add(new LuaTok(s.Substring(i, j - i), i, true));
                i = j; continue;
            }

            if (c is '(' or '[' or '{' or ')' or ']' or '}') toks?.Add(new LuaTok(c.ToString(), i, false));
            i++;
        }
        return LuaState.Code;
    }
}
