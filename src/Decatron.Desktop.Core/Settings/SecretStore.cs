using System.Security.Cryptography;
using System.Text;

namespace Decatron.Desktop.Core.Settings;

/// <summary>
/// Guarda el token de vinculación. En Windows va cifrado con DPAPI (solo el mismo
/// usuario de Windows lo puede leer); en macOS/Linux, archivo con permisos 600. Es un
/// token de bajo privilegio (solo abre el WebSocket de escritorio), pero igual no se
/// deja en texto plano donde cualquier proceso lo lea.
/// </summary>
public sealed class SecretStore
{
    private readonly string _path;

    public SecretStore(string? path = null)
    {
        _path = path ?? Path.Combine(AppSettingsStore.DataDirectory, "device.token");
    }

    public string? Read()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            var bytes = File.ReadAllBytes(_path);
            if (OperatingSystem.IsWindows())
                bytes = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch { return null; }
    }

    public void Write(string token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var bytes = Encoding.UTF8.GetBytes(token);
        if (OperatingSystem.IsWindows())
            bytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(_path, bytes);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public void Clear()
    {
        try { if (File.Exists(_path)) File.Delete(_path); } catch { }
    }
}
