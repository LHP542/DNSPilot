using Avalonia.Controls;
using Avalonia.Controls.Templates;
using DnsPilot.ViewModels;

namespace DnsPilot;

/// <summary>
/// Bildet ViewModels auf Views ab (<c>…ViewModels.FooViewModel</c> →
/// <c>…Views.FooView</c>). DnsPilot hat nur wenige Fenster, aber der Locator
/// gehört zum Kroste-Standard und kostet nichts.
/// </summary>
public class ViewLocator : IDataTemplate
{
    public Control? Build(object? param)
    {
        if (param is null) return null;

        string name = param.GetType().FullName!
            .Replace("ViewModels", "Views", StringComparison.Ordinal)
            .Replace("ViewModel", "View", StringComparison.Ordinal);

        var type = Type.GetType(name);
        return type is not null
            ? (Control)Activator.CreateInstance(type)!
            : new TextBlock { Text = $"View nicht gefunden: {name}" };
    }

    public bool Match(object? data) => data is ViewModelBase;
}
