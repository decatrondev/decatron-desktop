using System.Text.Json;
using System.Text.Json.Nodes;
using Decatron.Desktop.Sdk;

namespace Decatron.Desktop.Core.Settings;

/// <summary>
/// Un JSON en la carpeta de datos del usuario (%APPDATA%\Decatron Desktop, ~/.config/…).
/// Los módulos tienen su propio objeto bajo <c>modules[id]</c>. El token de vinculación
/// no va aquí: lo guarda <see cref="SecretStore"/>.
/// </summary>
public sealed class AppSettingsStore
{
    private readonly string _path;
    private JsonObject _root = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    public static string DataDirectory
    {
        get
        {
            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(baseDir, "Decatron Desktop");
        }
    }

    public AppSettingsStore(string? path = null)
    {
        _path = path ?? Path.Combine(DataDirectory, "settings.json");
    }

    public void Load()
    {
        try
        {
            if (File.Exists(_path))
                _root = JsonNode.Parse(File.ReadAllText(_path)) as JsonObject ?? new JsonObject();
        }
        catch { _root = new JsonObject(); }
    }

    public T? Get<T>(string key) => TryGet<T>(_root, key);
    public void Set<T>(string key, T value) => _root[key] = JsonSerializer.SerializeToNode(value);

    public IModuleSettings ForModule(string moduleId) => new ModuleSettings(this, moduleId);

    public async Task SaveAsync()
    {
        await _lock.WaitAsync();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            await File.WriteAllTextAsync(tmp, _root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, _path, overwrite: true);
        }
        finally { _lock.Release(); }
    }

    private static T? TryGet<T>(JsonObject obj, string key)
    {
        try { return obj[key] is { } n ? n.Deserialize<T>() : default; }
        catch { return default; }
    }

    private sealed class ModuleSettings(AppSettingsStore store, string id) : IModuleSettings
    {
        private JsonObject Bag
        {
            get
            {
                if (store._root["modules"] is not JsonObject mods) { mods = new JsonObject(); store._root["modules"] = mods; }
                if (mods[id] is not JsonObject bag) { bag = new JsonObject(); mods[id] = bag; }
                return bag;
            }
        }
        public T? Get<T>(string key) => TryGet<T>(Bag, key);
        public void Set<T>(string key, T value) => Bag[key] = JsonSerializer.SerializeToNode(value);
        public Task SaveAsync() => store.SaveAsync();
    }
}
