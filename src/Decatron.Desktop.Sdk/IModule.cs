using Avalonia.Controls;

namespace Decatron.Desktop.Sdk;

/// <summary>
/// Un módulo de Decatron Desktop (traducción en vivo, asistente de LoL…). El shell lo
/// lista en la barra lateral, le da su contexto al arrancar y muestra su vista.
/// Un módulo nunca referencia a otro módulo: lo compartido vive en el SDK o en Core.
/// </summary>
public interface IModule
{
    /// <summary>Id estable; coincide con el nombre del canal en el WebSocket del servidor.</summary>
    string Id { get; }

    string Title { get; }

    /// <summary>Glifo o emoji corto para la barra lateral.</summary>
    string Icon { get; }

    /// <summary>ViewModel que la vista enlaza. El shell lo expone también para estado (badge, etc.).</summary>
    object ViewModel { get; }

    Control CreateView();

    /// <summary>El shell ya está vinculado y conectado (o intentándolo). El módulo se suscribe a su canal aquí.</summary>
    Task StartAsync(ModuleContext context, CancellationToken ct);

    /// <summary>La app se cierra o el usuario desvinculó. Liberar micrófono, procesos, etc.</summary>
    Task StopAsync();
}
