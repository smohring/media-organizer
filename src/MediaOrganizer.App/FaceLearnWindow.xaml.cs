using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using MediaOrganizer.App.Localization;
using MediaOrganizer.App.Services;
using MediaOrganizer.Core.Faces;

namespace MediaOrganizer.App;

/// <summary>Lernt Personen aus einem ganzen Ordner an: Alle Gesichter werden
/// gefunden, nach Person gruppiert und einmal pro Gruppe benannt.</summary>
public partial class FaceLearnWindow : Window
{
    /// <summary>Mehr Bilder bringen kaum neue Personen, kosten aber Zeit.</summary>
    private const int MaxFilesToScan = 300;

    private readonly FaceService _faces;
    private readonly PersonStore _store;
    private readonly List<ClusterRow> _rows = [];

    public FaceLearnWindow(FaceService faces, PersonStore store, string? initialFolder)
    {
        InitializeComponent();
        _faces = faces;
        _store = store;
        FolderBox.Text = initialFolder ?? "";
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = Loc.Get("S.Learn.PickFolder") };
        if (dialog.ShowDialog() == true)
        {
            FolderBox.Text = dialog.FolderName;
        }
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        var folder = FolderBox.Text.Trim();
        if (!Directory.Exists(folder))
        {
            ResultText.Text = Loc.Get("S.Learn.NeedFolder");
            return;
        }

        ScanButton.IsEnabled = false;
        SaveButton.IsEnabled = false;
        BusyPanel.Visibility = Visibility.Visible;
        ClustersHost.ItemsSource = null;
        _rows.Clear();
        ResultText.Text = "";

        var progress = new Progress<(int Done, int Total, string File)>(p =>
        {
            ScanProgress.Value = p.Total == 0 ? 0 : p.Done / (double)p.Total;
            ScanStatus.Text = Loc.Format("S.Learn.Scanning", p.Done, p.Total, p.File);
        });

        try
        {
            var known = _store.Persons.ToList();
            var clusters = await Task.Run(() => FaceClusterer.ClusterFolder(
                folder, _faces, known, MaxFilesToScan, progress, CancellationToken.None));

            var knownNames = known.Select(p => p.Name).OrderBy(n => n).ToList();
            foreach (var cluster in clusters.Where(c => c.Faces.Count >= 2).Take(20))
            {
                _rows.Add(new ClusterRow(cluster, knownNames)
                {
                    Thumbs = cluster.SelectReferences()
                        .Select(f => f.CropJpeg is null ? null : ImageLoading.FromBytes(f.CropJpeg))
                        .Where(i => i is not null)
                        .Cast<ImageSource>()
                        .ToList(),
                    CountText = Loc.Format("S.Learn.ShotCount", cluster.Faces.Count),
                    AssignedName = cluster.ExistingMatch?.Person.Name ?? "",
                    HintText = cluster.ExistingMatch is null
                        ? Loc.Get("S.Learn.EnterName")
                        : Loc.Format("S.Learn.AlreadyKnown", cluster.ExistingMatch.Person.Name),
                });
            }

            ClustersHost.ItemsSource = _rows;
            ResultText.Text = _rows.Count == 0
                ? Loc.Get("S.Learn.NothingFound")
                : Loc.Format("S.Learn.Found", _rows.Count);
            SaveButton.IsEnabled = _rows.Count > 0;
        }
        catch (Exception ex)
        {
            ResultText.Text = Loc.Format("S.Learn.ScanFailed", ex.Message);
        }
        finally
        {
            BusyPanel.Visibility = Visibility.Collapsed;
            ScanButton.IsEnabled = true;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var saved = 0;
        foreach (var row in _rows)
        {
            var name = row.AssignedName?.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var person = _store.GetOrCreate(name);
            foreach (var face in row.Cluster.SelectReferences())
            {
                _store.AddReferenceFace(person, face);
            }

            saved++;
        }

        if (saved > 0)
        {
            _store.Save();
        }

        DialogResult = true;
    }

    private sealed class ClusterRow(FaceCluster cluster, List<string> knownNames) : INotifyPropertyChanged
    {
        private string _assignedName = "";

        public FaceCluster Cluster { get; } = cluster;

        public List<string> KnownNames { get; } = knownNames;

        public List<ImageSource> Thumbs { get; init; } = [];

        public string CountText { get; init; } = "";

        public string HintText { get; init; } = "";

        public string AssignedName
        {
            get => _assignedName;
            set
            {
                _assignedName = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AssignedName)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
