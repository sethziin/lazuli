using Executor.Contracts;

namespace Executor.UI.Services;

/// <summary>
/// Biblioteca de scripts em uma pasta de verdade: %APPDATA%\Executor\Scripts.
/// Arquivos .lua na raiz e em subpastas (a subpasta vira a "tag"). Na primeira execução cria alguns exemplos.
/// </summary>
public sealed class FolderScriptLibrary : IScriptLibrary
{
    public string Folder { get; }

    public FolderScriptLibrary(string? folder = null)
    {
        Folder = folder ?? Storage.ScriptsDir;
        SeedIfMissing();
    }

    private void SeedIfMissing()
    {
        try
        {
            if (Directory.Exists(Folder)) return;
            Directory.CreateDirectory(Folder);
            Directory.CreateDirectory(System.IO.Path.Combine(Folder, "utils"));
            File.WriteAllText(System.IO.Path.Combine(Folder, "hello.lua"), "print(\"hello world\")\n");
            File.WriteAllText(System.IO.Path.Combine(Folder, "loops.lua"), "for i = 1, 5 do\n    print(i)\nend\n");
            File.WriteAllText(System.IO.Path.Combine(Folder, "utils", "table_utils.lua"),
                "local t = { 3, 1, 2 }\ntable.sort(t)\nprint(table.concat(t, \", \"))\n");
            File.WriteAllText(System.IO.Path.Combine(Folder, "utils", "string_fmt.lua"),
                "print(string.format(\"%05.2f\", 3.14159))\n");
        }
        catch { /* sem permissão: a biblioteca fica vazia */ }
    }

    public IReadOnlyList<ScriptEntry> GetAll()
    {
        var list = new List<ScriptEntry>();
        try
        {
            if (!Directory.Exists(Folder)) return list;
            foreach (var file in Directory.EnumerateFiles(Folder, "*.lua", SearchOption.AllDirectories))
            {
                var rel = System.IO.Path.GetRelativePath(Folder, file);
                var dir = System.IO.Path.GetDirectoryName(rel) ?? "";
                // só um nível de subpasta como tag; mais fundo vira "a/b"
                var tag = dir.Replace(System.IO.Path.DirectorySeparatorChar, '/');
                // o conteúdo NÃO é lido aqui (a lista abre rápido mesmo com milhares de arquivos);
                // quem abre o script lê pelo Path.
                list.Add(new ScriptEntry(System.IO.Path.GetFileName(file), "", tag, file));
            }
        }
        catch { /* pasta inacessível: devolve o que deu */ }
        return list.OrderBy(e => e.Tag, StringComparer.OrdinalIgnoreCase)
                   .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
