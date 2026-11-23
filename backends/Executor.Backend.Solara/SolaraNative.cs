using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Executor.SolaraBackend;

/// <summary>
/// Os dois exports da SolaraV3.dll, exatamente como o launcher oficial os usa (PHASE-02/05, 06H §1–2):
/// <c>SetEventSink(cb, ctx)</c> com <c>cb(ctx, int kind, const char* json)</c> e <c>int Start(const char* guid)</c>.
/// A DLL nunca é descarregada e <c>Start</c> só pode ser chamado uma vez por processo (a 2ª chamada devolve -1).
/// </summary>
internal static unsafe class SolaraNative
{
    private static nint _lib;
    private static delegate* unmanaged<byte*, int> _start;

    /// <summary>Eventos da DLL: kind 1 = linhas de console, kind 2 = cliente mudou. Vem de threads da DLL.</summary>
    public static event Action<int, string>? Event;

    public static bool Loaded => _lib != 0;

    /// <summary>Arquivo de rastreamento do host (diagnóstico). Cada linha é gravada e fechada na hora, para sobreviver a um encerramento abrupto.</summary>
    public static string? TracePath { get; set; }

    public static void Trace(string text)
    {
        var path = TracePath;
        if (path is null) return;
        try { File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} [t{Environment.CurrentManagedThreadId}] {text}{Environment.NewLine}"); }
        catch { }
    }

    /// <summary>Carrega a DLL, registra o sink de eventos e resolve o Start (ordem do launcher oficial).</summary>
    public static void Load(string path)
    {
        if (_lib != 0) return;
        var lib = NativeLibrary.Load(path);
        var setSink = (delegate* unmanaged<delegate* unmanaged<nint, int, byte*, void>, nint, void>)
            NativeLibrary.GetExport(lib, "SetEventSink");
        var start = (delegate* unmanaged<byte*, int>)NativeLibrary.GetExport(lib, "Start");
        setSink(&OnEvent, 0);
        _start = start;
        _lib = lib;
    }

    /// <summary>Chama Start(guid). Retorno: porta da ponte (1–65535, exceto 2), 2 = banido, -1 = falha.</summary>
    public static int Start(string guid)
    {
        if (_start == null) throw new InvalidOperationException("SolaraV3.dll not loaded.");
        var bytes = Encoding.UTF8.GetBytes(guid + "\0");
        fixed (byte* p = bytes) return _start(p);
    }

    [UnmanagedCallersOnly] // x64: há uma só convenção de chamada
    private static void OnEvent(nint ctx, int kind, byte* text)
    {
        // nunca deixar exceção voltar para dentro da DLL
        try
        {
            var s = text == null ? "" : Marshal.PtrToStringUTF8((nint)text) ?? "";
            Trace($"sink kind={kind} len={s.Length} {(s.Length > 300 ? s[..300] + "…" : s)}");
            Event?.Invoke(kind, s);
        }
        catch { }
    }
}
