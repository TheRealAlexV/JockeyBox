using System;
using System.Windows;

namespace JockeyBox.Views;

/// <summary>
/// Text of one lyric line, with no word-level targets: the operator edits the line
/// as written, and the word spans are re-estimated across the line's unchanged
/// window afterwards (see <see cref="JockeyBox.Lyrics.LyricEdits"/>).
///
/// <para>
/// Enter is Save and Esc is Cancel, as elsewhere in the app. Nothing is written to
/// disk here: the caller passes <see cref="LineText"/> to the view model, which
/// records the edit against the current document's source id.
/// </para>
/// </summary>
public partial class EditLyricLineDialog : Window
{
    /// <param name="currentText">The line as it reads now; the box is prefilled with it.</param>
    /// <param name="lineCount">How many lines the edit will be applied to.</param>
    public EditLyricLineDialog(string? currentText, int lineCount = 1)
    {
        InitializeComponent();

        LineText = currentText ?? "";
        TextLineBox.Text = LineText;

        if (lineCount > 1)
        {
            HeadingLine.Text = $"Edit {lineCount} line texts";
            HintLine.Text = "The same text replaces every selected line. "
                            + "Each line keeps its own start time and its own word timing window.";
        }
        else
        {
            HintLine.Text = "Line start time is unchanged; the words are re-spaced inside it.";
        }

        Loaded += (_, _) =>
        {
            TextLineBox.Focus();
            TextLineBox.CaretIndex = TextLineBox.Text.Length;
        };
    }

    /// <summary>The accepted text; the original when the dialog was cancelled.</summary>
    public string LineText { get; private set; }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        LineText = TextLineBox.Text ?? "";
        try { DialogResult = true; }
        catch (InvalidOperationException) { Close(); }
    }
}
