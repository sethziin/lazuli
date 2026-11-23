using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace Executor.UI.Services;

/// <summary>
/// Confere, antes de abrir a interface, o que o app precisa e o Windows pode não ter:
///  • Windows 10 (1607) ou mais novo;
///  • Microsoft Visual C++ Runtime (usado pelo desenho de tela, Skia): se faltar, oferece baixar e instalar o oficial da Microsoft.
/// O .NET não entra aqui: o exe já leva o runtime dentro (self-contained).
/// Roda antes do Avalonia porque, sem o VC++ Runtime, a janela nem consegue abrir. As caixas são do próprio Windows (MessageBox).
/// </summary>
internal static class Prerequisites
{
    private const string VcUrl = "https://aka.ms/vs/17/release/vc_redist.x64.exe";
    private const uint MB_YESNO = 0x4, MB_OK = 0x0, MB_ICONINFO = 0x40, MB_ICONERROR = 0x10, MB_ICONQUESTION = 0x20, IDYES = 6;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(nint hwnd, string text, string caption, uint type);

    private static bool Test => Environment.GetEnvironmentVariable("LAZULI_PREREQ_TEST") == "1";

    /// <summary>true = pode abrir o app; false = o chamador deve sair.</summary>
    public static bool EnsureReady()
    {
        try
        {
            if (!OperatingSystem.IsWindows()) return true;
            if (Environment.OSVersion.Version.Build < 14393)
            {
                Box("Lazuli needs Windows 10 (version 1607) or newer.", MB_OK | MB_ICONERROR);
                return false;
            }
            if (VcInstalled() && !Test) return true;
            return InstallVc();
        }
        catch { return true; } // a checagem nunca impede de abrir
    }

    private static bool VcInstalled()
    {
        var sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
        return new[] { "vcruntime140.dll", "vcruntime140_1.dll", "msvcp140.dll" }.All(f => File.Exists(Path.Combine(sys, f)));
    }

    private static bool InstallVc()
    {
        if (!Test && Box("Lazuli needs the Microsoft Visual C++ Runtime, a free component from Microsoft.\n\nDownload and install it now?\n(Windows will ask for permission.)",
                MB_YESNO | MB_ICONQUESTION) != IDYES) return false;

        var file = Path.Combine(Path.GetTempPath(), "Lazuli-vc_redist.x64.exe");
        try
        {
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("Lazuli");
                using var resp = http.GetAsync(VcUrl, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
                resp.EnsureSuccessStatusCode();
                using var fs = File.Create(file);
                resp.Content.CopyToAsync(fs).GetAwaiter().GetResult();
            }

            // só roda se for mesmo da Microsoft: assinatura Authenticode válida E assinante "Microsoft Corporation"
            if (!IsSignedByMicrosoft(file, out var why))
            {
                TryDelete(file);
                Done($"The download didn't pass the safety check ({why}), so it was not run.\n\nInstall it manually from:\n{VcUrl}", false);
                return false;
            }
            if (Test) { TryDelete(file); Done("TEST OK: downloaded and verified (not installed).", true); return false; }

            var psi = new ProcessStartInfo(file) { UseShellExecute = true, Verb = "runas" };
            psi.ArgumentList.Add("/install"); psi.ArgumentList.Add("/quiet"); psi.ArgumentList.Add("/norestart");
            using var p = Process.Start(psi);
            p?.WaitForExit();
            TryDelete(file);
            // 0 = ok · 3010 = ok, pede reiniciar · 1638 = já há uma versão igual ou mais nova
            if (p is not null && p.ExitCode is 0 or 3010 or 1638 && VcInstalled()) return true;
            Done($"The Visual C++ Runtime wasn't installed (code {p?.ExitCode}).\n\nInstall it manually from:\n{VcUrl}\n\nThen open Lazuli again.", false);
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // recusou o pedido de permissão do Windows
            TryDelete(file);
            Done($"The installation was cancelled.\n\nLazuli can't open without the Visual C++ Runtime. You can install it from:\n{VcUrl}", false);
            return false;
        }
        catch
        {
            TryDelete(file);
            Done($"Couldn't download the Visual C++ Runtime. Check your internet connection.\n\nYou can also install it manually from:\n{VcUrl}", false);
            return false;
        }
    }

    // ---------- assinatura ----------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo { public uint cbStruct; public string pcwszFilePath; public nint hFile; public nint pgKnownSubject; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint cbStruct; public nint pPolicyCallbackData, pSIPClientData;
        public uint dwUIChoice, fdwRevocationChecks, dwUnionChoice;
        public nint pFile; public uint dwStateAction; public nint hWVTStateData; public nint pwszURLReference;
        public uint dwProvFlags, dwUIContext;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(nint hwnd, ref Guid action, ref WinTrustData data);

    private static bool IsSignedByMicrosoft(string path, out string why)
    {
        why = "";
        var fi = new WinTrustFileInfo { cbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(), pcwszFilePath = path };
        var pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(fi, pFile, false);
            var data = new WinTrustData
            {
                cbStruct = (uint)Marshal.SizeOf<WinTrustData>(),
                dwUIChoice = 2,            // WTD_UI_NONE
                fdwRevocationChecks = 0,   // sem checagem online de revogação (não trava sem rede)
                dwUnionChoice = 1,         // WTD_CHOICE_FILE
                pFile = pFile,
                dwStateAction = 0,
                dwProvFlags = 0x00000010,  // WTD_CACHE_ONLY_URL_RETRIEVAL
            };
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
            var rc = WinVerifyTrust(nint.Zero, ref action, ref data);
            if (rc != 0) { why = $"invalid signature 0x{rc:X}"; return false; }
        }
        finally { Marshal.FreeHGlobal(pFile); }

        using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
        var subject = cert.Subject;
        if (!subject.Contains("O=Microsoft Corporation", StringComparison.OrdinalIgnoreCase)) { why = "not signed by Microsoft"; return false; }
        return true;
    }

    // ---------- util ----------

    private static int Box(string text, uint type) => MessageBoxW(0, text, "Lazuli", type);

    /// <summary>Em modo de teste grava o resultado num arquivo (sem caixa); no uso normal mostra a mensagem.</summary>
    private static void Done(string text, bool ok)
    {
        if (Test) { try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "lazuli-prereq-test.txt"), text); } catch { } return; }
        Box(text, MB_OK | (ok ? MB_ICONINFO : MB_ICONERROR));
    }

    private static void TryDelete(string f) { try { File.Delete(f); } catch { } }
}
