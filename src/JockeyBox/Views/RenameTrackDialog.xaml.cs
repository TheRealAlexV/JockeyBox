using System.IO;
using System.Windows;
using System.Windows.Controls;
using JockeyBox.Library;

namespace JockeyBox.Views;

/// <summary>
/// Rename one library track in place: a text box for the base name, the file's
/// extension shown read-only next to it (it is preserved, never edited), inline
/// validation from <see cref="MediaFileOps.ValidateName(string)"/> and OK/Cancel.
///
/// Enter is OK and Esc is Cancel, as elsewhere in the app. The dialog only wires
/// the fields; the rename itself is done by <see cref="MediaFileOps.Rename"/>, so
/// the caller decides what to do with the returned <see cref="MediaOpOutcome"/>.
///
/// <see cref="NewBaseName"/> is set on OK only: after a <c>false</c> result it
/// still holds the original base name and the caller should ignore it.
/// </summary>
public partial class RenameTrackDialog : Window
{
    /// <summary>Create the dialog for one file; nothing is read from or written to disk.</summary>
    public RenameTrackDialog(string currentPath)
    {
        CurrentPath = (currentPath ?? "").Trim();
        InitializeComponent();

        var fileName = Path.GetFileName(CurrentPath);
        CurrentFileLine.Text = fileName.Length > 0 ? fileName : CurrentPath;

        var folder = Path.GetDirectoryName(CurrentPath) ?? "";
        CurrentFolderLine.Text = folder;
        CurrentFolderLine.Visibility = folder.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        var extension = Path.GetExtension(CurrentPath);
        ExtensionBox.Text = extension;
        ExtensionBox.Visibility = extension.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        NewBaseName = Path.GetFileNameWithoutExtension(CurrentPath);
        NameBox.Text = NewBaseName;

        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
            Validate();
        };
    }

    /// <summary>The file being renamed; the extension shown in the dialog comes from it.</summary>
    public string CurrentPath { get; }

    /// <summary>The accepted base name (no extension), or the original when cancelled.</summary>
    public string NewBaseName { get; private set; }

    private void OnNameChanged(object sender, TextChangedEventArgs e) => Validate();

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (Validate() is { } error)
        {
            ShowMessage(error, isError: true);
            NameBox.Focus();
            return;
        }

        NewBaseName = NameBox.Text;
        CloseWithResult(true);
    }

    /// <summary>
    /// Check the box and show the result inline. Returns the error, or null when
    /// the name is usable. An empty box stays silent while the user is clearing it;
    /// pressing OK surfaces the error.
    /// </summary>
    private string? Validate()
    {
        if (NameBox is null || ValidationLine is null) return null;

        var text = NameBox.Text;
        var error = MediaFileOps.ValidateName(text);

        if (error is not null && text.Length == 0)
        {
            ShowMessage("", isError: false);
            return error;
        }

        if (error is not null)
        {
            ShowMessage(error, isError: true);
            return error;
        }

        var extension = Path.GetExtension(CurrentPath);
        ShowMessage($"New file name: {text}{extension}", isError: false);
        return null;
    }

    private void ShowMessage(string message, bool isError)
    {
        ValidationLine.Text = message;
        ValidationLine.Foreground = (System.Windows.Media.Brush)FindResource(
            isError ? "AccentBBrush" : "TextDimBrush");
    }

    private void CloseWithResult(bool result)
    {
        try { DialogResult = result; }
        catch (InvalidOperationException) { Close(); }
    }
}
