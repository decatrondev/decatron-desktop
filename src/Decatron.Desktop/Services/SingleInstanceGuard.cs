namespace Decatron.Desktop.Services;

/// <summary>Una sola instancia por usuario: dos apps capturando el mismo mic no tiene sentido.</summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex _mutex;
    public bool IsPrimary { get; }

    public SingleInstanceGuard()
    {
        _mutex = new Mutex(true, @"Local\DecatronDesktop.SingleInstance", out var created);
        IsPrimary = created;
    }

    public void Dispose()
    {
        if (IsPrimary) { try { _mutex.ReleaseMutex(); } catch { } }
        _mutex.Dispose();
    }
}
