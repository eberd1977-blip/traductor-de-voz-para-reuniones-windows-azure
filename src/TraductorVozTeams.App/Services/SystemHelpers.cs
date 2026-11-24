using System.Diagnostics;
using Microsoft.Win32;

namespace TraductorVozTeams.App.Services;

public static class SystemHelpers
{
    public const string VcRedistUrl = "https://aka.ms/vs/17/release/vc_redist.x64.exe";

    /// <summary>El Speech SDK requiere Microsoft Visual C++ Redistributable x64 (2015-2022).</summary>
    public static bool IsVcRedistInstalled()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64");
        return key?.GetValue("Installed") is int installed && installed == 1;
    }

    public static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    public static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    /// <summary>Crea accesos directos al ejecutable actual en el Escritorio y el menú Inicio.</summary>
    public static IReadOnlyList<string> CreateShortcuts()
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("No se pudo determinar la ruta del ejecutable.");
        var targets = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
        };
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell no disponible.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        var created = new List<string>();
        foreach (var folder in targets)
        {
            var path = Path.Combine(folder, "Traductor de voz para reuniones en Windows más Azure.lnk");
            dynamic shortcut = shell.CreateShortcut(path);
            shortcut.TargetPath = exe;
            shortcut.WorkingDirectory = Path.GetDirectoryName(exe);
            shortcut.IconLocation = exe + ",0";
            shortcut.Description = "Traductor de voz para reuniones en Windows más Azure";
            shortcut.Save();
            created.Add(path);
        }

        return created;
    }
}
