using System.Windows;
using MediaOrganizer.App.Localization;
using MediaOrganizer.Core.Faces;

namespace MediaOrganizer.App;

/// <summary>Bearbeiten einer bekannten Person: Name, Beziehung und
/// Zusammenführen mit einer anderen Person.</summary>
public partial class PersonEditWindow : Window
{
    private readonly PersonStore _store;
    private readonly KnownPerson _person;

    public PersonEditWindow(PersonStore store, KnownPerson person)
    {
        InitializeComponent();
        _store = store;
        _person = person;

        SubtitleText.Text = Loc.Format("S.Person.FaceCount", person.Embeddings.Count);
        NameBox.Text = person.Name;
        RelationBox.Text = person.Relation;

        var others = store.Persons.Where(p => p != person).OrderBy(p => p.Name).ToList();
        MergeBox.ItemsSource = others;
        MergeBox.IsEnabled = others.Count > 0;
        MergeButton.IsEnabled = others.Count > 0;

        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            return;
        }

        _store.Rename(_person, name, RelationBox.Text);
        _store.Save();
        DialogResult = true;
    }

    private void Merge_Click(object sender, RoutedEventArgs e)
    {
        if (MergeBox.SelectedItem is not KnownPerson target)
        {
            return;
        }

        var answer = MessageBox.Show(
            Loc.Format(
                "S.Person.MergeConfirm", _person.Embeddings.Count, _person.Name, target.Name),
            Loc.Get("S.Person.MergeTitle"),
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK)
        {
            return;
        }

        _store.Merge(_person, target);
        _store.Save();
        DialogResult = true;
    }
}
