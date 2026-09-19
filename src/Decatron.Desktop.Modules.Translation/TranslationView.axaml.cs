using Avalonia.Controls;
using Avalonia.Data.Converters;

namespace Decatron.Desktop.Modules.Translation;

public partial class TranslationView : UserControl
{
    public TranslationView() => InitializeComponent();
}

public static class BoolText
{
    public static readonly IValueConverter RunningIdle =
        new FuncValueConverter<bool, string>(b => b ? "Traduciendo en vivo" : "Detenido");
    public static readonly IValueConverter VoiceSilence =
        new FuncValueConverter<bool, string>(b => b ? "voz" : "silencio");
}
