using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using MediaOrganizer.App.Localization;
using MediaOrganizer.Core.Geo;
using MediaOrganizer.Core.Models;

namespace MediaOrganizer.App;

/// <summary>Anlegen und Bearbeiten einer benannten Zone.
///
/// <para>Statt einer Karte stehen die eigenen Aufnahmeorte zur Wahl: Ein
/// Vorschlag trifft den Schwerpunkt der Fotos exakt, während ein von Hand
/// gesetzter Punkt leicht ein, zwei Kilometer daneben liegt — und dann greift
/// die Zuhause-Erkennung nicht mehr. Wer die Koordinaten kennt, trägt sie
/// direkt ein.</para></summary>
public partial class ZoneEditWindow : Window
{
    /// <summary>Zeile der Vorschlagsliste.</summary>
    private sealed record SuggestionRow(string Title, string Detail, PlaceSuggestion Suggestion);

    private sealed record KindOption(ZoneKind Value, string Label);

    public ZoneEditWindow(HomeZone? existing)
    {
        InitializeComponent();

        Title = Loc.Get(existing is null ? "S.Zone.TitleNew" : "S.Zone.TitleEdit");
        HeaderText.Text = Title;

        KindBox.ItemsSource = new[]
        {
            new KindOption(ZoneKind.Home, Loc.Get("S.Zone.KindHome")),
            new KindOption(ZoneKind.Vacation, Loc.Get("S.Zone.KindVacation")),
            new KindOption(ZoneKind.Place, Loc.Get("S.Zone.KindPlace")),
        };

        NameBox.Text = existing?.Name ?? "";
        RadiusBox.Text = (existing?.RadiusKm ?? 0.7).ToString("0.##", CultureInfo.CurrentCulture);
        KindBox.SelectedIndex = (int)(existing?.Kind ?? ZoneKind.Home);

        if (existing is not null)
        {
            LatBox.Text = existing.Latitude.ToString("0.######", CultureInfo.CurrentCulture);
            LonBox.Text = existing.Longitude.ToString("0.######", CultureInfo.CurrentCulture);
        }

        Loaded += (_, _) => NameBox.Focus();
    }

    /// <summary>Hands in the place suggestions once the search over all shots
    /// has finished. The dialog is usable before that — everything here can be
    /// typed by hand.</summary>
    public void ShowSuggestions(IReadOnlyList<PlaceSuggestion> suggestions)
    {
        SearchingText.Visibility = Visibility.Collapsed;

        if (suggestions.Count == 0)
        {
            NoSuggestionsText.Visibility = Visibility.Visible;
            return;
        }

        SuggestionList.ItemsSource = suggestions.Select(s => new SuggestionRow(
            s.DisplayName,
            Loc.Format(
                "S.Zone.SuggestionSummary",
                s.PhotoCount,
                s.Visits,
                s.Center.Latitude.ToString("F4"),
                s.Center.Longitude.ToString("F4")),
            s)).ToList();
        SuggestionList.Visibility = Visibility.Visible;
    }

    /// <summary>Die bearbeitete Zone — erst nach <c>DialogResult == true</c> gültig.</summary>
    public HomeZone Result { get; private set; } = new();

    private void Suggestion_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SuggestionList.SelectedItem is not SuggestionRow row)
        {
            return;
        }

        LatBox.Text = row.Suggestion.Center.Latitude.ToString("0.######", CultureInfo.CurrentCulture);
        LonBox.Text = row.Suggestion.Center.Longitude.ToString("0.######", CultureInfo.CurrentCulture);

        // Name und Kategorie nur vorschlagen, solange nichts eingetragen ist —
        // eine bewusste Eingabe darf ein Klick nicht überschreiben.
        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            NameBox.Text = row.Suggestion.LikelyKind == ZoneKind.Home
                ? Loc.Get("S.Dlg.HomeZoneDefault")
                : row.Suggestion.DisplayName;
            KindBox.SelectedIndex = (int)row.Suggestion.LikelyKind;
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            ShowError(Loc.Get("S.Zone.NeedName"));
            return;
        }

        if (!TryParse(LatBox.Text, out var latitude) || latitude is < -90 or > 90)
        {
            ShowError(Loc.Get("S.Zone.BadLatitude"));
            return;
        }

        if (!TryParse(LonBox.Text, out var longitude) || longitude is < -180 or > 180)
        {
            ShowError(Loc.Get("S.Zone.BadLongitude"));
            return;
        }

        if (!TryParse(RadiusBox.Text, out var radius) || radius <= 0 || radius > 200)
        {
            ShowError(Loc.Get("S.Zone.BadRadius"));
            return;
        }

        Result = new HomeZone
        {
            Name = name,
            Kind = (KindBox.SelectedItem as KindOption)?.Value ?? ZoneKind.Home,
            Latitude = latitude,
            Longitude = longitude,
            RadiusKm = radius,
        };

        DialogResult = true;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    /// <summary>Nimmt Komma und Punkt als Dezimaltrenner — Koordinaten werden
    /// aus allen möglichen Quellen kopiert.
    /// <para>Bewusst <see cref="NumberStyles.Float"/> und nicht
    /// <c>NumberStyles.Any</c>: Letzteres erlaubt Tausendertrenner, und dann
    /// liest die deutsche Kultur "52.520008" als 48687063 — die Zone läge
    /// wortlos im Nirgendwo. Ohne Tausendertrenner scheitert der Versuch
    /// stattdessen und die zweite Zeile liest den Punkt korrekt als
    /// Dezimaltrenner.</para></summary>
    private static bool TryParse(string text, out double value) =>
        double.TryParse(text?.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out value)
        || double.TryParse(text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
