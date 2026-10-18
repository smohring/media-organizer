using System.Windows;

namespace MediaOrganizer.App;

/// <summary>Generischer Ein-Feld-Eingabedialog (Ordnerthema, Notiz, Zonenname, Fakt).</summary>
public partial class TextPromptDialog : Window
{
    private readonly bool _allowEmpty;

    public TextPromptDialog(
        string title,
        string header,
        string caption,
        string initialText = "",
        string okLabel = "OK",
        bool allowEmpty = false,
        bool multiline = false)
    {
        InitializeComponent();
        Title = title;
        HeaderText.Text = header;
        CaptionText.Text = caption;
        CaptionText.Visibility = string.IsNullOrEmpty(caption) ? Visibility.Collapsed : Visibility.Visible;
        InputBox.Text = initialText;
        OkButton.Content = okLabel;
        _allowEmpty = allowEmpty;

        if (multiline)
        {
            // Ganze Sätze statt Stichwort: Zeilenumbruch muss ins Feld gehen,
            // darf also nicht den OK-Knopf auslösen.
            InputBox.AcceptsReturn = true;
            InputBox.MinHeight = 110;
            OkButton.IsDefault = false;
        }

        Loaded += (_, _) =>
        {
            InputBox.Focus();
            InputBox.SelectAll();
        };
    }

    public string Text => InputBox.Text.Trim();

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (!_allowEmpty && Text.Length == 0)
        {
            return;
        }

        DialogResult = true;
    }
}
