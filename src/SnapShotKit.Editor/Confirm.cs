using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using SnapShotKit.Ui;

namespace SnapShotKit.Editor;

/// <summary>How a choice in a dialog is drawn, which is also how important it is.</summary>
public enum Tone
{
    /// <summary>The one solid accent button. At most one per dialog.</summary>
    Primary,

    /// <summary>The one solid red button, for a choice that destroys something.</summary>
    Danger,

    /// <summary>Everything else.</summary>
    Quiet
}

/// <summary>One button offered by a dialog.</summary>
public readonly record struct Choice(string Label, Tone Tone = Tone.Quiet);

/// <summary>
/// A question with a handful of answers. Avalonia ships no message box, so this is it.
///
/// The answers are buttons rather than a yes and a no, because the honest answer to "you have
/// unsaved changes" is usually a third thing: save them. A dialog that offers only losing the work
/// or going back is one the user has to dismiss and redo by hand.
/// </summary>
public static class Confirm
{
    /// <summary>Asks, and returns the index of the answer chosen, or -1 if the dialog was simply closed.</summary>
    public static async Task<int> AskAsync(Window owner, string title, string message, params Choice[] choices)
    {
        var answer = -1;

        var dialog = new Window
        {
            Title = title,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Tokens.BgBrush
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Tokens.Space.S2,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, Tokens.Space.S6, 0, 0)
        };

        // Quiet answers first and the decisive one last, so the button nearest the corner the eye
        // ends on is the one the dialog is actually asking about.
        for (var index = 0; index < choices.Length; index++)
        {
            var chosen = index;
            var choice = choices[index];

            void Answer()
            {
                answer = chosen;
                dialog.Close();
            }

            buttons.Children.Add(choice.Tone switch
            {
                Tone.Primary => Buttons.Primary(choice.Label, null, Answer),
                Tone.Danger => Buttons.Danger(choice.Label, null, Answer),
                _ => Buttons.Secondary(choice.Label, null, Answer)
            });
        }

        var heading = Labels.Heading(title, 13, 0.18, Tokens.Neutral800Brush);

        var body = Labels.Body(message, 13.5, Tokens.Neutral900Brush);
        body.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        body.Margin = new Thickness(0, Tokens.Space.S2, 0, 0);

        dialog.Content = new Border
        {
            Padding = new Thickness(Tokens.Space.S6),
            Child = new StackPanel { Children = { heading, body, buttons } }
        };

        await dialog.ShowDialog(owner);
        return answer;
    }

    /// <summary>The delete question. Says what will be gone rather than asking whether the user is sure.</summary>
    public static async Task<bool> DeleteAsync(Window owner, string title, string message) =>
        await AskAsync(owner, title, message,
            new Choice("Keep it"),
            new Choice("Delete", Tone.Danger)) == 1;
}

/// <summary>What to do about a document with unsaved changes.</summary>
public enum Unsaved
{
    /// <summary>Go back to editing. Also what closing the dialog means.</summary>
    KeepEditing,

    /// <summary>Lose the changes and carry on.</summary>
    Discard,

    /// <summary>Write them out first.</summary>
    Save
}
