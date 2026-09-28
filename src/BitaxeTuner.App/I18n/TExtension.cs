using System.Windows.Markup;
using BitaxeTuner.Core.I18n;

namespace BitaxeTuner.App.I18n;

/// <summary>
/// Übersetzter Text in XAML: <c>Text="{l:T 'Speichern'}"</c>. Der deutsche Text ist der Schlüssel (Strings.en.json).
/// Die Sprache steht beim Start fest; ein Wechsel wirkt nach dem Neustart der App.
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension(string text) : MarkupExtension
{
    public string Text { get; } = text;

    public override object ProvideValue(IServiceProvider serviceProvider) => L.T(Text);
}
