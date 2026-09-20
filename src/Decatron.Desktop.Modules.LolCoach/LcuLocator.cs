using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Decatron.Desktop.Modules.LolCoach;

/// <summary>Datos del lockfile del cliente de LoL: puerto y contraseña de la API local (LCU).</summary>
public sealed record LcuEndpoint(int Port, string Password, string LockfilePath, string Protocol = "https");

/// <summary>
/// Encuentra el lockfile del cliente de LoL sin escanear procesos (eso pide WMI en
/// Windows y permisos raros en macOS). Riot deja la ruta de instalación en un yaml de
/// ProgramData; macOS instala siempre en /Applications; y si nada aplica, el usuario
/// puede fijar la ruta a mano desde la app.
/// </summary>
public static class LcuLocator
{
    private static readonly Regex InstallPath = new(@"product_install_full_path:\s*""?([^""\r\n]+)""?", RegexOptions.Compiled);

    public static IEnumerable<string> CandidateLockfiles(string? userOverride)
    {
        if (!string.IsNullOrWhiteSpace(userOverride))
        {
            var p = userOverride.Trim();
            yield return Path.GetFileName(p).Equals("lockfile", StringComparison.OrdinalIgnoreCase) ? p : Path.Combine(p, "lockfile");
        }

        if (RuntimeInfo.IsWindows)
        {
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            var yaml = Path.Combine(programData, "Riot Games", "Metadata", "league_of_legends.live", "league_of_legends.live.product_settings.yaml");
            string? fromYaml = null;
            try
            {
                if (File.Exists(yaml))
                {
                    var m = InstallPath.Match(File.ReadAllText(yaml));
                    if (m.Success) fromYaml = Path.Combine(m.Groups[1].Value.Trim().Replace('/', Path.DirectorySeparatorChar), "lockfile");
                }
            }
            catch { }
            if (fromYaml != null) yield return fromYaml;

            foreach (var drive in new[] { "C:", "D:", "E:" })
                yield return Path.Combine(drive + Path.DirectorySeparatorChar, "Riot Games", "League of Legends", "lockfile");
        }
        else if (RuntimeInfo.IsMacOS)
        {
            yield return "/Applications/League of Legends.app/Contents/LoL/lockfile";
        }
    }

    /// <summary>Lockfile: <c>name:pid:port:password:protocol</c>. Null si no está o el cliente está cerrado (el archivo se borra al cerrar).</summary>
    public static LcuEndpoint? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            // El cliente mantiene el archivo abierto con escritura: hay que abrirlo compartido.
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            var parts = sr.ReadToEnd().Trim().Split(':');
            if (parts.Length < 5 || !int.TryParse(parts[2], out var port)) return null;
            return new LcuEndpoint(port, parts[3], path, parts[4].Trim().Length > 0 ? parts[4].Trim() : "https");
        }
        catch { return null; }
    }

    public static LcuEndpoint? Find(string? userOverride)
    {
        foreach (var candidate in CandidateLockfiles(userOverride))
        {
            var ep = TryRead(candidate);
            if (ep != null) return ep;
        }
        return null;
    }
}

internal static class RuntimeInfo
{
    public static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    public static bool IsMacOS => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
}
