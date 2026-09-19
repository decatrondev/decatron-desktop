using Avalonia.Data.Converters;
using Decatron.Desktop.Sdk;

namespace Decatron.Desktop.ViewModels;

public static class StateConverters
{
    public static readonly IValueConverter IsConnected =
        new FuncValueConverter<ConnectionState, bool>(s => s == ConnectionState.Connected);
}
