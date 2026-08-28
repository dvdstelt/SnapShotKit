using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace SnapShotKit.Ui;

/// <summary>
/// Text boxes that draw nothing but the words.
///
/// For typing directly onto the picture, where a field with a background and a focus ring would sit
/// over the very thing being annotated. The stock theme paints both, on the template rather than on
/// the control, so they win over anything set on the box itself: the only way to be rid of them is
/// not to use that template.
/// </summary>
public static class TextFields
{
    /// <summary>A text box reduced to a caret, a selection and the text.</summary>
    public static readonly ControlTheme Bare = new(typeof(TextBox))
    {
        Setters =
        {
            new Setter(TemplatedControl.TemplateProperty, new FuncControlTemplate<TextBox>((box, scope) =>
                new TextPresenter
                {
                    Name = "PART_TextPresenter",
                    [!TextPresenter.TextProperty] = box[!TextBox.TextProperty],
                    [!TextPresenter.CaretIndexProperty] = box[!TextBox.CaretIndexProperty],
                    [!TextPresenter.SelectionStartProperty] = box[!TextBox.SelectionStartProperty],
                    [!TextPresenter.SelectionEndProperty] = box[!TextBox.SelectionEndProperty],
                    [!TextPresenter.CaretBrushProperty] = box[!TextBox.CaretBrushProperty],
                    [!TextPresenter.SelectionBrushProperty] = box[!TextBox.SelectionBrushProperty],
                    [!TextPresenter.SelectionForegroundBrushProperty] = box[!TextBox.SelectionForegroundBrushProperty],
                    [!TextPresenter.TextAlignmentProperty] = box[!TextBox.TextAlignmentProperty],
                    [!TextPresenter.TextWrappingProperty] = box[!TextBox.TextWrappingProperty],
                    [!TextPresenter.PasswordCharProperty] = box[!TextBox.PasswordCharProperty],
                    [!TextPresenter.RevealPasswordProperty] = box[!TextBox.RevealPasswordProperty]
                }.RegisterInNameScope(scope))),

            new Setter(TemplatedControl.BackgroundProperty, Brushes.Transparent),
            new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(0)),
            new Setter(TemplatedControl.PaddingProperty, new Thickness(0)),
            new Setter(Layoutable.MinHeightProperty, 0.0)
        }
    };
}
