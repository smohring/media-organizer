using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MediaOrganizer.App.Controls;

/// <summary>Zahleneingabe mit Schrittknöpfen.
///
/// <para>Der Wert wird als Text nach außen gegeben, nicht als Zahl: Die
/// Einstellungen binden mit <c>UpdateSourceTrigger=LostFocus</c>, und ein
/// Zwischenstand beim Tippen ("0," oder leer) darf nicht sofort auf eine Zahl
/// gezwungen werden — sonst springt das Feld unter den Fingern zurück.</para>
///
/// <para>Komma und Punkt werden beide als Dezimaltrenner akzeptiert; beim
/// Schreiben gilt die Kultur des Systems.</para></summary>
public partial class NumberInput : UserControl
{
    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(
            nameof(Value), typeof(string), typeof(NumberInput),
            new FrameworkPropertyMetadata(
                "", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnValueChanged));

    public static readonly DependencyProperty StepProperty =
        DependencyProperty.Register(nameof(Step), typeof(double), typeof(NumberInput),
            new PropertyMetadata(1.0));

    public static readonly DependencyProperty MinimumProperty =
        DependencyProperty.Register(nameof(Minimum), typeof(double), typeof(NumberInput),
            new PropertyMetadata(0.0));

    public static readonly DependencyProperty MaximumProperty =
        DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(NumberInput),
            new PropertyMetadata(999.0));

    /// <summary>Nachkommastellen bei der Ausgabe. 0 ergibt ganze Zahlen.</summary>
    public static readonly DependencyProperty DecimalsProperty =
        DependencyProperty.Register(nameof(Decimals), typeof(int), typeof(NumberInput),
            new PropertyMetadata(0));

    /// <summary>Einheit, links im Feld angezeigt (z.B. "km"). Leer lassen für
    /// reine Anzahlen.</summary>
    public static readonly DependencyProperty UnitProperty =
        DependencyProperty.Register(nameof(Unit), typeof(string), typeof(NumberInput),
            new PropertyMetadata(""));

    private bool _updatingFromValue;

    public NumberInput()
    {
        InitializeComponent();
        Entry.Text = Value;
    }

    public string Value
    {
        get => (string)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Step
    {
        get => (double)GetValue(StepProperty);
        set => SetValue(StepProperty, value);
    }

    public double Minimum
    {
        get => (double)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public int Decimals
    {
        get => (int)GetValue(DecimalsProperty);
        set => SetValue(DecimalsProperty, value);
    }

    public string Unit
    {
        get => (string)GetValue(UnitProperty);
        set => SetValue(UnitProperty, value);
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (NumberInput)d;
        if (control._updatingFromValue)
        {
            return;
        }

        control.Entry.Text = (string)e.NewValue;
    }

    private void Entry_LostFocus(object sender, RoutedEventArgs e) => Commit(Entry.Text);

    /// <summary>Pfeiltasten treten den Wert wie die Knöpfe; Eingabetaste
    /// übernimmt sofort, statt auf den Fokuswechsel zu warten.</summary>
    private void Entry_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Up:
                Nudge(Step);
                e.Handled = true;
                break;
            case Key.Down:
                Nudge(-Step);
                e.Handled = true;
                break;
            case Key.Enter:
                Commit(Entry.Text);
                e.Handled = true;
                break;
        }
    }

    private void Up_Click(object sender, RoutedEventArgs e) => Nudge(Step);

    private void Down_Click(object sender, RoutedEventArgs e) => Nudge(-Step);

    private void Nudge(double delta)
    {
        var current = TryParse(Entry.Text, out var parsed) ? parsed : Minimum;
        Commit(Format(current + delta));
    }

    /// <summary>Übernimmt den Text als Zahl: begrenzt auf den erlaubten
    /// Bereich, einheitlich formatiert und zurück in <see cref="Value"/>.
    /// Unlesbares fällt auf das Minimum, damit nie ein ungültiger Wert
    /// weitergereicht wird.</summary>
    private void Commit(string text)
    {
        var number = TryParse(text, out var parsed) ? parsed : Minimum;
        var formatted = Format(Math.Clamp(number, Minimum, Maximum));

        _updatingFromValue = true;
        Entry.Text = formatted;
        Value = formatted;
        _updatingFromValue = false;
    }

    private string Format(double value) =>
        Math.Clamp(value, Minimum, Maximum)
            .ToString("F" + Decimals, CultureInfo.CurrentCulture);

    /// <summary>Erst die Kultur des Systems, dann invariant — so werden Komma
    /// und Punkt beide angenommen. <see cref="NumberStyles.Float"/> statt
    /// <c>Any</c>, weil Letzteres Tausendertrenner erlaubt und "52.520" in
    /// deutscher Kultur zu 48687 machen würde.</summary>
    private static bool TryParse(string text, out double value) =>
        double.TryParse(text?.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out value)
        || double.TryParse(text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
