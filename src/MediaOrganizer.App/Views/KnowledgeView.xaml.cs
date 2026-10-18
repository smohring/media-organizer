using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MediaOrganizer.App.Localization;
using MediaOrganizer.App.ViewModels;

namespace MediaOrganizer.App.Views;

/// <summary>Wissensverwaltung als Ansicht im Hauptfenster (bewusst kein
/// eigenes Fenster mehr — die Navigation bleibt in der App).</summary>
public partial class KnowledgeView : UserControl
{
    public KnowledgeView()
    {
        InitializeComponent();
    }

    /// <summary>Quellordner als Vorgabe beim Anlernen aus einem Ordner.</summary>
    public string? DefaultLearnFolder { get; set; }

    private KnowledgeViewModel? ViewModel => DataContext as KnowledgeViewModel;

    private Window? Host => Window.GetWindow(this);

    // ------------------------------------------------------------------
    // Zonen
    // ------------------------------------------------------------------

    private void AddZone_Click(object sender, RoutedEventArgs e) => EditZone(null);

    private void EditZone_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: HomeZoneViewModel zone })
        {
            EditZone(zone);
        }
    }

    private void Zone_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: HomeZoneViewModel zone })
        {
            EditZone(zone);
        }
    }

    private void EditZone(HomeZoneViewModel? existing)
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        var dialog = new ZoneEditWindow(existing?.Zone) { Owner = Host };

        // The dialog opens at once and the suggestions arrive later. Searching
        // first meant walking the places of every shot read so far before the
        // window appeared — on a large collection the application looked
        // frozen. Everything in the dialog can be filled in by hand meanwhile.
        using var pending = new CancellationTokenSource();
        dialog.Loaded += async (_, _) =>
        {
            try
            {
                var suggestions = await vm.PlaceSuggestionsForZonesAsync(pending.Token);
                if (!pending.IsCancellationRequested)
                {
                    dialog.ShowSuggestions(suggestions);
                }
            }
            catch (OperationCanceledException)
            {
                // Dialog closed before the search came back.
            }
        };

        // Closing the dialog must not leave the search writing into a dead
        // window; the token also stops it being awaited for nothing.
        dialog.Closed += (_, _) => pending.Cancel();

        if (dialog.ShowDialog() == true)
        {
            vm.SaveZone(existing?.Zone, dialog.Result);
        }
    }

    private void LearnFromFolder_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        var models = new Core.Faces.FaceModelProvider();
        if (!models.IsInstalled)
        {
            vm.StatusMessage = Loc.Get("S.Know.ModelsMissing");
            return;
        }

        Core.Faces.FaceService faceService;
        try
        {
            faceService = new Core.Faces.FaceService(models);
        }
        catch (Exception ex)
        {
            vm.StatusMessage = Loc.Format("S.Msg.FaceUnavailable", ex.Message);
            return;
        }

        using (faceService)
        {
            var store = Core.Faces.PersonStore.Load();
            var dialog = new FaceLearnWindow(faceService, store, DefaultLearnFolder) { Owner = Host };
            if (dialog.ShowDialog() == true)
            {
                vm.Reload();
                vm.StatusMessage = Loc.Get("S.Know.PeopleSaved");
            }
        }
    }

    private void AddPerson_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        var nameDialog = new TextPromptDialog(
            Loc.Get("S.Dlg.PersonTitle"),
            Loc.Get("S.Dlg.PersonNameHeader"),
            Loc.Get("S.Dlg.PersonNameBody"),
            okLabel: Loc.Get("S.Btn.Next"))
        { Owner = Host };
        if (nameDialog.ShowDialog() != true)
        {
            return;
        }

        var relationDialog = new TextPromptDialog(
            Loc.Get("S.Dlg.PersonTitle"),
            Loc.Format("S.Dlg.PersonRelationHeader", nameDialog.Text),
            Loc.Get("S.Dlg.PersonRelationBody"),
            okLabel: Loc.Get("S.Btn.Save"),
            allowEmpty: true)
        { Owner = Host };
        if (relationDialog.ShowDialog() == true)
        {
            vm.AddOrUpdatePerson(nameDialog.Text, relationDialog.Text);
        }
    }

    private void AddFact_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        var dialog = new TextPromptDialog(
            Loc.Get("S.Dlg.FactAddTitle"),
            Loc.Get("S.Dlg.FactAddHeader"),
            Loc.Get("S.Dlg.FactAddBody"),
            okLabel: Loc.Get("S.Btn.AddFact"))
        { Owner = Host };
        if (dialog.ShowDialog() == true)
        {
            vm.AddFact(dialog.Text);
        }
    }

    // ------------------------------------------------------------------
    // Personen bearbeiten
    // ------------------------------------------------------------------

    private void Person_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount >= 2 && sender is FrameworkElement { DataContext: PersonViewModel person })
        {
            EditPerson(person);
        }
    }

    private void EditPerson_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PersonViewModel person })
        {
            EditPerson(person);
        }
    }

    private void EditPerson(PersonViewModel person)
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        var dialog = new PersonEditWindow(vm.Store, person.Person) { Owner = Host };
        if (dialog.ShowDialog() == true)
        {
            vm.AfterPersonEdit(Loc.Get("S.Know.PersonUpdated"));
        }
    }

    /// <summary>Kontextmenü eines Referenzgesichts: entfernen oder einer
    /// anderen Person zuordnen (Fehlzuordnung der Gruppierung korrigieren).</summary>
    private void FaceThumb_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (ViewModel is not { } vm
            || sender is not FrameworkElement { DataContext: FaceThumbViewModel face } element
            || element.ContextMenu is not { } menu)
        {
            return;
        }

        menu.Items.Clear();

        var remove = new MenuItem { Header = Loc.Get("S.Menu.RemoveReferenceFace") };
        remove.Click += (_, _) => vm.RemoveFaceCommand.Execute(face);
        menu.Items.Add(remove);

        var others = vm.OtherPersons(face.Person);
        if (others.Count == 0)
        {
            return;
        }

        menu.Items.Add(new Separator());
        var move = new MenuItem { Header = Loc.Get("S.Menu.AssignTo") };
        foreach (var target in others)
        {
            var person = target;
            var entry = new MenuItem { Header = person.Name };
            entry.Click += (_, _) => vm.MoveFace(face, person);
            move.Items.Add(entry);
        }

        menu.Items.Add(move);
    }

    // ------------------------------------------------------------------
    // Fakten und Notizen bearbeiten
    // ------------------------------------------------------------------

    private void Fact_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount >= 2 && sender is FrameworkElement { DataContext: string fact })
        {
            EditFact(fact);
        }
    }

    private void EditFact_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: string fact })
        {
            EditFact(fact);
        }
    }

    private void EditFact(string fact)
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        var dialog = new TextPromptDialog(
            Loc.Get("S.Dlg.FactEditTitle"),
            Loc.Get("S.Dlg.FactEditHeader"),
            Loc.Get("S.Dlg.FactEditBody"),
            fact,
            okLabel: Loc.Get("S.Btn.Save"),
            allowEmpty: true)
        { Owner = Host };
        if (dialog.ShowDialog() == true)
        {
            vm.ReplaceFact(fact, dialog.Text);
        }
    }

    private void Note_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel is not { } vm
            || e.ClickCount < 2
            || sender is not FrameworkElement { DataContext: NoteViewModel note })
        {
            return;
        }

        var dialog = new TextPromptDialog(
            Loc.Get("S.Dlg.NoteEditTitle"),
            Loc.Format("S.Dlg.NoteHeader", note.FileName),
            Loc.Get("S.Dlg.NoteEditBody"),
            note.Note,
            okLabel: Loc.Get("S.Btn.Save"),
            allowEmpty: true)
        { Owner = Host };
        if (dialog.ShowDialog() == true)
        {
            vm.UpdateNote(note, dialog.Text);
        }
    }
}
