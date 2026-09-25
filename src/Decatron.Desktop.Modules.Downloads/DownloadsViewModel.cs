using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Decatron.Desktop.Sdk;
using Microsoft.Extensions.Logging;

namespace Decatron.Desktop.Modules.Downloads;

/// <summary>
/// Pantalla de Descargas. Las descargas se piden desde el dashboard (Song Request → Descargas);
/// acá se ve el estado de yt-dlp/ffmpeg, la carpeta de destino y el progreso de cada una.
/// </summary>
public sealed partial class DownloadsViewModel : ObservableObject
{
    private ModuleContext? _ctx;
    private ToolManager? _tools;
    private DownloadsClient? _client;

    [ObservableProperty] private string _folder = DefaultFolder;
    [ObservableProperty] private bool _toolsReady;
    [ObservableProperty] private bool _preparing;
    [ObservableProperty] private string _toolsText = "Se preparan la primera vez que descargues algo.";
    [ObservableProperty] private string? _toolsError;
    [ObservableProperty] private double _prepPercent;
    [ObservableProperty] private bool _prepIndeterminate = true;

    public ObservableCollection<JobRow> Jobs { get; } = new();

    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Decatron");

    public void Attach(ModuleContext ctx)
    {
        _ctx = ctx;
        var log = ctx.LoggerFactory.CreateLogger("Downloads");
        Folder = ctx.Settings.Get<string>("folder") is { Length: > 0 } saved ? saved : DefaultFolder;
        _tools = new ToolManager(log);
        _tools.StatusChanged += s => Dispatcher.UIThread.Post(() => ApplyStatus(s));
        _client = new DownloadsClient(ctx.Connection, _tools, () => Folder, log);
        _client.JobChanged += j => Dispatcher.UIThread.Post(() => ApplyJob(j));
        _client.Cleared += () => Dispatcher.UIThread.Post(() => { foreach (var row in Jobs.Where(r => r.IsFinished).ToList()) Jobs.Remove(row); });

        // Si ya estaban instaladas, se revisa la actualización diaria de yt-dlp; si no, se espera al primer uso
        if (File.Exists(_tools.YtDlpPath)) _ = _tools.EnsureAsync();
    }

    public void Shutdown() => _client?.Dispose();

    partial void OnFolderChanged(string value)
    {
        if (_ctx == null || string.IsNullOrWhiteSpace(value)) return;
        _ctx.Settings.Set("folder", value.Trim());
        _ = _ctx.Settings.SaveAsync();
        _ = _client?.SendStatusAsync();
    }

    [RelayCommand]
    private Task PrepareTools() => _tools?.EnsureAsync() ?? Task.CompletedTask;

    [RelayCommand]
    private void OpenFolder() => _client?.OpenFolder(null);

    [RelayCommand]
    private void ResetFolder() => Folder = DefaultFolder;

    [RelayCommand]
    private void ClearFinished()
    {
        _client?.ClearFinished();
    }

    private void ApplyStatus(ToolStatus s)
    {
        ToolsReady = s.Ready;
        Preparing = s.Preparing;
        ToolsError = s.Error;
        PrepIndeterminate = s.Percent == null;
        PrepPercent = s.Percent ?? 0;
        ToolsText = s.Preparing
            ? s.Stage switch
            {
                "ytdlp" => $"Descargando yt-dlp… {s.Percent}%",
                "ffmpeg" => $"Descargando ffmpeg… {s.Percent}% (solo la primera vez)",
                "unpack" => "Descomprimiendo ffmpeg…",
                "update" => "Buscando actualizaciones de yt-dlp…",
                _ => "Preparando…",
            }
            : s.Ready ? $"Listo · yt-dlp {s.YtDlpVersion} · ffmpeg {(s.FfmpegReady ? "listo" : "falta")}"
            : "Se preparan la primera vez que descargues algo.";
    }

    private void ApplyJob(DownloadJob job)
    {
        var row = Jobs.FirstOrDefault(r => r.Id == job.Id);
        if (row == null) { row = new JobRow(job.Id, _client!); Jobs.Insert(0, row); }
        row.Apply(job);
        while (Jobs.Count > 30) Jobs.RemoveAt(Jobs.Count - 1);
    }
}

public sealed partial class JobRow : ObservableObject
{
    private readonly DownloadsClient _client;
    public string Id { get; }

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private string _stateText = "";
    [ObservableProperty] private double _percent;
    [ObservableProperty] private bool _indeterminate;
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _isDone;
    [ObservableProperty] private bool _isError;

    public bool IsFinished => !IsActive;

    public JobRow(string id, DownloadsClient client) { Id = id; _client = client; }

    public void Apply(DownloadJob job)
    {
        var p = job.Progress;
        Title = job.Title.Length > 0 ? job.Title : "Descarga";
        Detail = $"{job.Kind} · {job.Format}" + (p.FilePath != null ? $" · {Path.GetFileName(p.FilePath)}" : "");
        IsActive = p.State is "queued" or "preparing" or "downloading" or "processing";
        IsDone = p.State == "done";
        IsError = p.State == "error";
        Indeterminate = p.State is "queued" or "preparing" or "processing" || p.Percent == null;
        Percent = p.Percent ?? 0;
        StateText = p.State switch
        {
            "queued" => "En cola",
            "preparing" => "Preparando…",
            "downloading" => $"{p.Percent:0.#}%" + (p.Speed != null ? $" · {p.Speed}" : "") + (p.Eta != null ? $" · faltan {p.Eta}" : ""),
            "processing" => "Procesando (uniendo / convirtiendo)…",
            "done" => "Listo",
            "canceled" => "Cancelada",
            _ => ErrorText(p.Error),
        };
    }

    private static string ErrorText(string? code) => code switch
    {
        "ffmpeg_missing" => "Falta ffmpeg",
        "tools_unavailable" => "No se pudieron preparar yt-dlp/ffmpeg",
        "unsupported_site" => "Sitio no soportado",
        "private" => "Video privado o con restricción de edad",
        "not_found" => "No disponible",
        "blocked" => "El sitio bloqueó la descarga; intenta más tarde",
        "disk_full" => "No hay espacio en el disco",
        "no_permission" => "Sin permiso para escribir en la carpeta",
        "format_unavailable" => "Esa calidad/formato no está disponible",
        _ => "Error al descargar",
    };

    [RelayCommand] private void Cancel() => _client.Cancel(Id);
    [RelayCommand] private void Open() => _client.OpenFolder(Id);
}
