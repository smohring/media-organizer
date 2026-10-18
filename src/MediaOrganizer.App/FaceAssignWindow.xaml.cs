using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using MediaOrganizer.App.Localization;
using MediaOrganizer.App.Services;
using MediaOrganizer.Core.Faces;

namespace MediaOrganizer.App;

/// <summary>Zeigt alle auf einem Foto gefundenen Gesichter und lässt ihnen
/// Namen zuweisen. Die zugeordneten Gesichter werden als Referenzvektoren
/// gespeichert und künftig automatisch wiedererkannt.</summary>
public partial class FaceAssignWindow : Window
{
    private readonly PersonStore _store;
    private readonly List<FaceRow> _rows = [];

    public FaceAssignWindow(string imagePath, FaceService faces, PersonStore store)
    {
        InitializeComponent();
        _store = store;

        SubtitleText.Text = System.IO.Path.GetFileName(imagePath);
        var knownNames = store.Persons.Select(p => p.Name).OrderBy(n => n).ToList();

        List<DetectedFace> detected;
        try
        {
            detected = faces.DetectFaces(imagePath, includeCrops: true);
        }
        catch (Exception ex)
        {
            StatusText.Text = Loc.Format("S.Assign.Failed", ex.Message);
            SaveButton.IsEnabled = false;
            return;
        }

        if (detected.Count == 0)
        {
            StatusText.Text = Loc.Get("S.Assign.NoFace");
            SaveButton.IsEnabled = false;
            return;
        }

        StatusText.Text = detected.Count == 1
            ? Loc.Get("S.Assign.FoundOne")
            : Loc.Format("S.Assign.FoundMany", detected.Count);

        foreach (var face in detected)
        {
            var match = FaceService.Match(face.Embedding, store.Persons);
            _rows.Add(new FaceRow(face, knownNames)
            {
                Image = face.CropJpeg is null ? null : ImageLoading.FromBytes(face.CropJpeg),
                AssignedName = match?.Person.Name ?? "",
                SuggestionText = match is null
                    ? Loc.Get("S.Assign.Unknown")
                    : Loc.Format("S.Assign.Suggestion", match.Person.Name),
                ScoreText = match is null
                    ? ""
                    : Loc.Format("S.Assign.Similarity", match.Similarity.ToString("F2")),
            });
        }

        FacesHost.ItemsSource = _rows;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var assigned = 0;
        foreach (var row in _rows)
        {
            var name = row.AssignedName?.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var person = _store.GetOrCreate(name);
            _store.AddReferenceFace(person, row.Face);
            assigned++;
        }

        if (assigned > 0)
        {
            _store.Save();
        }

        DialogResult = true;
    }

    /// <summary>Zeile im Zuordnungsdialog.</summary>
    private sealed class FaceRow(DetectedFace face, List<string> knownNames) : INotifyPropertyChanged
    {
        private string _assignedName = "";

        public DetectedFace Face { get; } = face;

        public List<string> KnownNames { get; } = knownNames;

        public ImageSource? Image { get; init; }

        public string SuggestionText { get; init; } = "";

        public string ScoreText { get; init; } = "";

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
