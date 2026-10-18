using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MediaOrganizer.App.Localization;
using MediaOrganizer.App.ViewModels;

namespace MediaOrganizer.App;

public partial class MainWindow : Window
{
    private const string DragFormat = "MediaOrganizerItems";

    private Point _dragStart;
    private ResultItemViewModel? _mouseDownItem;
    private ResultItemViewModel? _selectionAnchor;
    private bool _pendingSingleSelect;

    // Rahmenauswahl (Marquee)
    private bool _isMarqueeActive;
    private Point _marqueeOrigin;

    /// <summary>Die Kacheln, die beim Aufziehen des Rahmens auf dem Schirm
    /// waren. Beim Ziehen selbst wird nur noch diese Liste geprüft.</summary>
    private IReadOnlyList<Border> _marqueeTiles = [];

    public MainWindow()
    {
        InitializeComponent();
    }

    private MainViewModel ViewModel => (MainViewModel)DataContext;

    // ------------------------------------------------------------------
    // Mehrfachauswahl & Drag/Drop
    // ------------------------------------------------------------------

    private void ItemTile_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ResultItemViewModel item })
        {
            return;
        }

        _dragStart = e.GetPosition(this);
        _mouseDownItem = item;
        _pendingSingleSelect = false;

        // Doppelklick öffnet den Betrachter.
        if (e.ClickCount >= 2)
        {
            OpenViewer(item);
            e.Handled = true;
            return;
        }

        // Der Ist-Zustand ist reine Anzeige — keine Auswahl, kein Ziehen.
        if (item.IsReadOnly)
        {
            _mouseDownItem = null;
            return;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            ViewModel.SelectRange(_selectionAnchor, item);
        }
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            item.IsSelected = !item.IsSelected;
            _selectionAnchor = item;
        }
        else if (!item.IsSelected)
        {
            ViewModel.ClearSelection();
            item.IsSelected = true;
            _selectionAnchor = item;
        }
        else
        {
            // Klick auf bereits markiertes Item: Reduktion auf Einzelauswahl
            // erst beim Loslassen (damit ein Drag die Mehrfachauswahl behält).
            _pendingSingleSelect = true;
            _selectionAnchor = item;
        }
    }

    private void AssignFaces(ResultItemViewModel item)
    {
        var path = item.CurrentPath ?? item.Item.SourcePath;
        if (!System.IO.File.Exists(path))
        {
            return;
        }

        var faceService = ViewModel.TryCreateFaceService();
        if (faceService is null)
        {
            return;
        }

        using (faceService)
        {
            var store = MediaOrganizer.Core.Faces.PersonStore.Load();
            var dialog = new FaceAssignWindow(path, faceService, store) { Owner = this };
            if (dialog.ShowDialog() == true)
            {
                ViewModel.RefreshFaceStatusCommand.Execute(null);
            }
        }
    }

    private void OpenViewer(ResultItemViewModel item)
    {
        var items = item.ParentFolder?.Items.ToList() ?? [item];
        new MediaViewerWindow(items, item) { Owner = this }.Show();
    }

    // ------------------------------------------------------------------
    // Rahmenauswahl (Marquee) auf der freien Fläche der Ergebnisansicht
    // ------------------------------------------------------------------

    private void ResultsSurface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Dieser Handler ist ein Preview-Handler und läuft VOR den Handlern
        // der Kacheln. Deshalb hier direkt am Ursprungselement prüfen, ob der
        // Klick auf einer Kachel begann — sonst würde die Rahmenauswahl die
        // Maus abfangen und Auswahl, Doppelklick und Drag der Kacheln
        // blockieren.
        // Ebenso für Bedienelemente auf den Ordnerkarten: Ohne diese Prüfung
        // schluckt die Rahmenauswahl den Klick, und Umbenennen-Feld, "⋯"-Menü
        // und Knöpfe reagieren nicht.
        if (sender is not IInputElement surface
            || IsInsideTile(e.OriginalSource as DependencyObject)
            || IsInteractive(e.OriginalSource as DependencyObject))
        {
            return;
        }

        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)
            && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            ViewModel.ClearSelection();
        }

        _isMarqueeActive = true;

        // Einmal einsammeln statt bei jeder Mausbewegung: Der Baum wurde bisher
        // pro Bewegung komplett durchlaufen — bei einer Karte mit tausenden
        // Kacheln zehntausende Elemente je Ereignis, und der Rahmen ruckelte.
        // Während des Ziehens entstehen keine neuen Kacheln.
        _marqueeTiles = [.. FindVisualChildren<Border>(FoldersHost)
            .Where(b => b.DataContext is ResultItemViewModel { IsReadOnly: false })];

        _marqueeOrigin = e.GetPosition(MarqueeLayer);
        Canvas.SetLeft(MarqueeRect, _marqueeOrigin.X);
        Canvas.SetTop(MarqueeRect, _marqueeOrigin.Y);
        MarqueeRect.Width = 0;
        MarqueeRect.Height = 0;
        MarqueeRect.Visibility = Visibility.Visible;
        Mouse.Capture(surface);
    }

    private void ResultsSurface_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isMarqueeActive)
        {
            return;
        }

        var current = e.GetPosition(MarqueeLayer);
        var rect = new Rect(_marqueeOrigin, current);
        Canvas.SetLeft(MarqueeRect, rect.X);
        Canvas.SetTop(MarqueeRect, rect.Y);
        MarqueeRect.Width = rect.Width;
        MarqueeRect.Height = rect.Height;

        SelectItemsInRect(rect);
    }

    private void ResultsSurface_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isMarqueeActive)
        {
            return;
        }

        _isMarqueeActive = false;
        _marqueeTiles = [];
        MarqueeRect.Visibility = Visibility.Collapsed;
        Mouse.Capture(null);
    }

    /// <summary>Markiert alle Kacheln, die den Auswahlrahmen schneiden.</summary>
    private void SelectItemsInRect(Rect rect)
    {
        foreach (var tile in _marqueeTiles)
        {
            if (tile.DataContext is not ResultItemViewModel item)
            {
                continue;
            }

            try
            {
                var topLeft = tile.TransformToVisual(MarqueeLayer).Transform(new Point(0, 0));
                var bounds = new Rect(topLeft, new Size(tile.ActualWidth, tile.ActualHeight));
                item.IsSelected = rect.IntersectsWith(bounds);
            }
            catch
            {
                // Element nicht (mehr) im Visual Tree verbunden.
            }
        }
    }

    /// <summary>Liegt das angeklickte Element innerhalb einer Datei-Kachel?</summary>
    private static bool IsInsideTile(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is FrameworkElement { DataContext: ResultItemViewModel })
            {
                return true;
            }

            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }

        return false;
    }

    /// <summary>Liegt der Klick auf einem Bedienelement (Knopf, Textfeld,
    /// Umschalter, Menü, Bildlaufleiste)? Dann gehört er diesem Element, nicht
    /// der Rahmenauswahl.
    /// <para><see cref="System.Windows.Controls.Primitives.ScrollBar"/> und
    /// <see cref="System.Windows.Controls.Primitives.Thumb"/> müssen mit
    /// geprüft werden: Der Griff einer Bildlaufleiste ist ein Thumb und damit
    /// KEIN <c>ButtonBase</c>. Ohne diese Prüfung fängt die Rahmenauswahl den
    /// Klick per <c>Mouse.Capture</c> ab, und der Griff lässt sich nicht
    /// ziehen.</para></summary>
    private static bool IsInteractive(DependencyObject? source)
    {
        for (var node = source; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is System.Windows.Controls.Primitives.ButtonBase
                or System.Windows.Controls.Primitives.TextBoxBase
                or System.Windows.Controls.Primitives.ScrollBar
                or System.Windows.Controls.Primitives.Thumb
                or ComboBox
                or MenuItem
                or System.Windows.Controls.ContextMenu)
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed)
            {
                yield return typed;
            }

            foreach (var descendant in FindVisualChildren<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private void ItemTile_MouseMove(object sender, MouseEventArgs e)
    {
        if (_mouseDownItem is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var position = e.GetPosition(this);
        if (Math.Abs(position.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(position.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var items = ViewModel.SelectedItems.Where(i => i.CanRelocate).ToList();
        if (items.Count == 0)
        {
            return;
        }

        _pendingSingleSelect = false;
        var payload = new DataObject(DragFormat, items);
        DragDrop.DoDragDrop((DependencyObject)sender, payload, DragDropEffects.Move);
        _mouseDownItem = null;
    }

    private void ItemTile_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_pendingSingleSelect && _mouseDownItem is { } item)
        {
            ViewModel.ClearSelection();
            item.IsSelected = true;
        }

        _pendingSingleSelect = false;
        _mouseDownItem = null;
    }

    /// <summary>Abstand zwischen zwei Kacheln — derselbe Wert wie in
    /// <see cref="Controls.UniformTilePanel.Spacing"/>.</summary>
    private const double TileGap = 8;

    /// <summary>Zeilen je Rastung, die Windows standardmäßig vorgibt. Dient
    /// hier als Bezugsgröße: Bei dieser Einstellung rückt eine Rastung um
    /// genau eine Kachelreihe weiter.</summary>
    private const double DefaultWheelScrollLines = 3;

    /// <summary>Strg + Mausrad zoomt die Kacheln; ohne Strg wird gerollt — mit
    /// der in Windows eingestellten Zeilenzahl statt WPFs starrem Standard.</summary>
    private void Results_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            ViewModel.ZoomThumbnails(e.Delta);
            e.Handled = true;
            return;
        }

        // Der ScrollViewer steckt jetzt in der Vorlage der Liste, das Ereignis
        // kommt also von der Liste selbst. Beide Ansichten — Ergebnis und
        // Ist-Zustand — hängen an diesem Handler.
        if (FindVisualChildren<ScrollViewer>(sender as DependencyObject ?? this)
                .FirstOrDefault() is not { } viewer)
        {
            return;
        }

        // Eine Rastung rückt um eine Kachelreihe weiter. Die Reihenhöhe ergibt
        // sich jetzt aus der Breite: Kacheln je Reihe teilen die Fläche, also
        // ist eine Reihe umso höher, je weniger hineinpassen. WPF ignoriert das
        // und rechnet stur mit 16 px je Zeile.
        var columns = Math.Max(1, (int)ViewModel.ThumbnailsPerRow);
        var rowHeight = ((viewer.ViewportWidth - ((columns - 1) * TileGap)) / columns) + TileGap;

        // "Jeweils ein Bildschirm" ist in der Systemsteuerung als -1 kodiert.
        var lines = SystemParameters.WheelScrollLines;
        var distance = lines < 0
            ? viewer.ViewportHeight
            : lines * rowHeight / DefaultWheelScrollLines;

        viewer.ScrollToVerticalOffset(
            viewer.VerticalOffset - (distance * e.Delta / Mouse.MouseWheelDeltaForOneLine));
        e.Handled = true;
    }

    private void FolderCard_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        if (sender is FrameworkElement { DataContext: ResultFolderViewModel { CanEdit: true } folder }
            && e.Data.GetData(DragFormat) is List<ResultItemViewModel> items
            && items.Any(i => i.CanRelocate && i.ParentFolder != folder))
        {
            e.Effects = DragDropEffects.Move;
        }

        e.Handled = true;
    }

    private async void FolderCard_Drop(object sender, DragEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ResultFolderViewModel folder }
            || e.Data.GetData(DragFormat) is not List<ResultItemViewModel> items)
        {
            return;
        }

        e.Handled = true;
        await ViewModel.MoveItemsToFolderAsync(items, folder);
    }

    /// <summary>Baut das Korrektur-Kontextmenü einer Datei-Kachel dynamisch
    /// auf (Zielordner-Liste ändert sich zur Laufzeit).</summary>
    private void ItemTile_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ResultItemViewModel item } element
            || element.ContextMenu is not { } menu)
        {
            return;
        }

        menu.Items.Clear();

        var open = new MenuItem { Header = Loc.Get("S.Menu.OpenPreview") };
        open.Click += (_, _) => OpenViewer(item);
        menu.Items.Insert(0, open);

        if (!item.IsVideo)
        {
            var assign = new MenuItem { Header = Loc.Get("S.Menu.AssignPeople") };
            assign.Click += (_, _) => AssignFaces(item);
            menu.Items.Insert(1, assign);
        }

        var note = new MenuItem
        {
            Header = Loc.Get(item.HasNote ? "S.Menu.EditNote" : "S.Menu.AddNote"),
        };
        note.Click += (_, _) =>
        {
            var dialog = new TextPromptDialog(
                Loc.Get("S.Dlg.NoteTitle"),
                Loc.Format("S.Dlg.NoteHeader", item.FileName),
                Loc.Get("S.Dlg.NoteBody"),
                item.UserNote ?? "",
                okLabel: Loc.Get("S.Btn.Save"),
                allowEmpty: true)
            { Owner = this };
            if (dialog.ShowDialog() == true)
            {
                ViewModel.SetItemNote(item, dialog.Text);
            }
        };
        menu.Items.Add(note);

        // Ist das Item Teil einer Mehrfachauswahl, wirken Beschreibung und
        // Verschieben auf die gesamte Auswahl.
        var selection = ViewModel.SelectedItems;
        IReadOnlyList<ResultItemViewModel> targetItems = item.IsSelected && selection.Count > 1
            ? selection
            : [item];

        AddDescriptionItems(menu, item, targetItems);

        if (!item.CanRelocate)
        {
            return;
        }

        menu.Items.Add(new Separator());

        var move = new MenuItem
        {
            Header = targetItems.Count > 1
                ? Loc.Format("S.Menu.MoveSelection", targetItems.Count)
                : Loc.Get("S.Menu.MoveTo"),
        };
        foreach (var folder in ViewModel.Folders)
        {
            // Same condition as the drop target: a card that has been applied
            // while the plan is still open takes nothing any more.
            if (!folder.CanEdit || targetItems.All(i => i.ParentFolder == folder))
            {
                continue;
            }

            var target = folder;
            var entry = new MenuItem { Header = target.DisplayName };
            entry.Click += async (_, _) => await ViewModel.MoveItemsToFolderAsync(targetItems, target);
            move.Items.Add(entry);
        }

        if (move.Items.Count > 0)
        {
            move.Items.Add(new Separator());
        }

        var newFolder = new MenuItem { Header = Loc.Get("S.Menu.NewTargetFolder") };
        newFolder.Click += async (_, _) =>
        {
            var dialog = new TextPromptDialog(
                Loc.Get("S.Dlg.NewFolderTitle"),
                Loc.Get("S.Dlg.NewFolderHeader"),
                Loc.Get("S.Dlg.NewFolderBody"),
                okLabel: Loc.Get("S.Btn.Create"))
            { Owner = this };
            if (dialog.ShowDialog() == true)
            {
                await ViewModel.MoveItemsToNewFolderAsync(targetItems, dialog.Text);
            }
        };
        move.Items.Add(newFolder);
        menu.Items.Add(move);
    }

    /// <summary>Menüeinträge zum Ändern und Entfernen der erzeugten
    /// Bildbeschreibung. Das Entfernen wirkt auf die gesamte Auswahl —
    /// Beschreibungen entfernt man selten einzeln.</summary>
    private void AddDescriptionItems(
        ContextMenu menu,
        ResultItemViewModel item,
        IReadOnlyList<ResultItemViewModel> targetItems)
    {
        var edit = new MenuItem
        {
            Header = Loc.Get(item.HasDescription ? "S.Menu.EditDescription" : "S.Menu.AddDescription"),
        };
        edit.Click += (_, _) =>
        {
            var dialog = new TextPromptDialog(
                Loc.Get("S.Dlg.DescriptionTitle"),
                Loc.Format("S.Dlg.DescriptionHeader", item.FileName),
                Loc.Get("S.Dlg.DescriptionBody"),
                item.Description ?? "",
                okLabel: Loc.Get("S.Btn.Save"),
                allowEmpty: true,
                multiline: true)
            { Owner = this };
            if (dialog.ShowDialog() == true)
            {
                ViewModel.SetItemDescription([item], dialog.Text);
            }
        };
        menu.Items.Add(edit);

        var withDescription = targetItems.Where(i => i.HasDescription).ToList();
        if (withDescription.Count == 0)
        {
            return;
        }

        var clear = new MenuItem
        {
            Header = withDescription.Count > 1
                ? Loc.Format("S.Menu.DeleteDescriptions", withDescription.Count)
                : Loc.Get("S.Menu.DeleteDescription"),
        };
        clear.Click += (_, _) => ViewModel.SetItemDescription(withDescription, null);
        menu.Items.Add(clear);
    }

    private void SaveHomeSuggestion_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new TextPromptDialog(
            Loc.Get("S.Dlg.HomeZoneTitle"),
            Loc.Get("S.Dlg.HomeZoneHeader"),
            Loc.Get("S.Dlg.HomeZoneBody"),
            Loc.Get("S.Dlg.HomeZoneDefault"),
            okLabel: Loc.Get("S.Btn.Save"))
        { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            ViewModel.AcceptHomeSuggestion(dialog.Text);
        }
    }

    // ------------------------------------------------------------------
    // Ordnername direkt in der Karte bearbeiten
    // ------------------------------------------------------------------

    private void FolderTitle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ResultFolderViewModel folder })
        {
            StartRename(folder);
        }
    }

    private static void StartRename(ResultFolderViewModel folder)
    {
        if (!folder.CanEdit)
        {
            return;
        }

        folder.EditTheme = folder.ThemePart;
        folder.IsRenaming = true;
    }

    /// <summary>Beim Einblenden den Fokus ins Feld setzen und den Text
    /// markieren — sonst müsste der Nutzer nach dem Klick noch einmal klicken.</summary>
    private void RenameBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is TextBox box && box.IsVisible)
        {
            box.Dispatcher.BeginInvoke(() =>
            {
                box.Focus();
                box.SelectAll();
            }, System.Windows.Threading.DispatcherPriority.Input);
        }
    }

    private void RenameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ResultFolderViewModel folder })
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                CommitRename(folder);
                break;
            case Key.Escape:
                e.Handled = true;
                folder.IsRenaming = false;
                break;
        }
    }

    private void RenameBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ResultFolderViewModel folder })
        {
            CommitRename(folder);
        }
    }

    private void CommitRename(ResultFolderViewModel folder)
    {
        if (!folder.IsRenaming)
        {
            return;
        }

        folder.IsRenaming = false;
        var theme = folder.EditTheme?.Trim() ?? "";
        if (theme.Length > 0 && !theme.Equals(folder.ThemePart, StringComparison.Ordinal))
        {
            ViewModel.RenameFolder(folder, theme);
        }
    }

    /// <summary>Rechtsklick auf die Ordnerkarte: dieselben Aktionen wie hinter
    /// dem "⋯"-Knopf.</summary>
    private void CreateFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new TextPromptDialog(
            Loc.Get("S.Dlg.CreateFolderTitle"),
            Loc.Get("S.Dlg.CreateFolderHeader"),
            Loc.Get("S.Dlg.CreateFolderBody"),
            okLabel: Loc.Get("S.Btn.Add"))
        { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            ViewModel.CreateEmptyFolder(dialog.Text, DateTime.Today);
        }
    }

    /// <summary>Rückgängigmachen bewegt echte Dateien — deshalb immer erst
    /// fragen und genau sagen, was passiert.</summary>
    private async void UndoLastRun_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanUndoLastRun)
        {
            return;
        }

        var answer = MessageBox.Show(
            this,
            ViewModel.UndoQuestionText,
            Loc.Get("S.Undo"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        await ViewModel.UndoLastRunAsync();
    }

}
