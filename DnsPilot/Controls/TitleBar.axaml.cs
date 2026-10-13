using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace DnsPilot.Controls;

/// <summary>
/// Kroste-Standard-Titelleiste: Drag zum Verschieben, Doppelklick zum
/// Maximieren, eigene Min/Max/Close-Buttons und ein Platz für Fensteraktionen.
/// </summary>
public partial class TitleBar : UserControl
{
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<TitleBar, string?>(nameof(Title));

    /// <summary>
    /// Inhalt, der links neben den Fensterbuttons erscheint (typisch ein paar
    /// Chrome-Buttons). Als <see cref="StyledProperty"/> statt per Binding, weil
    /// ein wiederverwendbares Control mit kompilierten Bindings kein
    /// <c>x:DataType</c> haben kann.
    /// </summary>
    public static readonly StyledProperty<object?> ActionsProperty =
        AvaloniaProperty.Register<TitleBar, object?>(nameof(Actions));

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public object? Actions
    {
        get => GetValue(ActionsProperty);
        set => SetValue(ActionsProperty, value);
    }

    public TitleBar()
    {
        InitializeComponent();
        MinButton.Click += (_, _) => { if (Host is { } w) w.WindowState = WindowState.Minimized; };
        MaxButton.Click += (_, _) => ToggleMaximize();
        CloseButton.Click += (_, _) => Host?.Close();
        Bar.PointerPressed += OnBarPointerPressed;
        Bar.DoubleTapped += OnBarDoubleTapped;
    }

    // ACHTUNG (Avalonia 12): VisualRoot ist NICHT mehr das Window — die
    // Visual-Wurzel ist der interne TopLevelHost, das Window nur noch dessen
    // Kind. "VisualRoot as Window" liefert null und macht alle Handler hier zu
    // stillen No-Ops.
    private Window? Host => TopLevel.GetTopLevel(this) as Window;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TitleProperty) TitleText.Text = Title;
        else if (change.Property == ActionsProperty) ActionsHost.Content = Actions;
    }

    private void OnBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (LandedOnInteractiveChild(e.Source)) return;

        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) Host?.BeginMoveDrag(e);
    }

    private void OnBarDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (LandedOnInteractiveChild(e.Source)) return;

        ToggleMaximize();
    }

    /// <summary>
    /// Läuft vom Ereignis-Ursprung den Visual-Tree hoch bis zur Titelleisten-
    /// Border und meldet true, wenn unterwegs ein interaktives Control liegt.
    ///
    /// <b>Pflicht-Guard.</b> <c>PointerPressed</c> bubbelt. Ein Button fängt den
    /// Press selbst ab und captured den Pointer — eine ComboBox tut das NICHT.
    /// Ohne diesen Guard startet <c>BeginMoveDrag</c> einen Fenster-Drag, der
    /// Pointer wandert ans OS, und das Control sieht nie ein
    /// <c>PointerReleased</c>: das Dropdown lässt sich gar nicht mehr öffnen,
    /// nur der ToolTip erscheint noch.
    ///
    /// Die <c>ElementRole</c>-Rollen helfen hier NICHT — sie regeln den
    /// OS-Hit-Test-Pfad, dieser Handler ist der managed Fallback und läuft
    /// davon unabhängig. Also auch nicht als „dank ElementRole überflüssig"
    /// wegrefactoren.
    /// </summary>
    private bool LandedOnInteractiveChild(object? source)
    {
        for (var v = source as Visual; v is not null; v = v.GetVisualParent())
        {
            // Die Titelleiste selbst (und alles darüber) ist Drag-Fläche.
            if (ReferenceEquals(v, Bar)) return false;

            // Button deckt ToggleButton/CheckBox/RadioButton/RepeatButton mit ab.
            if (v is Button or ComboBox or TextBox or Slider or ListBox or MenuItem) return true;

            // Auffangnetz: alles Fokussierbare will den Klick selbst verarbeiten.
            if (v is InputElement { Focusable: true }) return true;
        }

        // Ursprung liegt außerhalb der Titelleiste (z.B. in einem Popup-Root).
        return true;
    }

    private void ToggleMaximize()
    {
        if (Host is { } w)
        {
            w.WindowState = w.WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
        }
    }
}
