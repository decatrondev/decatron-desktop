using CommunityToolkit.Mvvm.ComponentModel;

namespace Decatron.Desktop.ViewModels;

public sealed partial class SplashViewModel : ObservableObject
{
    [ObservableProperty] private string _statusText = "Iniciando…";
}
