using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using SnapShotKit.Ui;

namespace SnapShotKit.Overlay;

/// <summary>What the user chose to do.</summary>
public enum OverlayAction
{
    WholeScreen,
    WholeScreenToClipboard,
    Save,
    Edit,
    Copy,
    Cancel
}

/// <summary>
/// The row of actions the overlay offers.
///
/// There are two of them, one per phase. Before a region is drawn the offer is about the whole
/// screen and sits at the top, out of the way of the pointer; once a region exists the offer is
/// about that region and follows it, anchored under its bottom-left corner so the actions are
/// beside the thing they act on rather than somewhere fixed on screen.
/// </summary>
public sealed class ActionBar : StackPanel
{
    const double Gap = 14;

    public ActionBar(bool forRegion, Action<OverlayAction> chosen)
    {
        Orientation = Orientation.Horizontal;
        Spacing = Tokens.Space.S2;

        if (forRegion)
        {
            Children.Add(Buttons.Primary("Save to disk", Lucide.SaveToDisk, () => chosen(OverlayAction.Save)));
            Children.Add(Buttons.Secondary("Open in editor", Lucide.OpenInEditor, () => chosen(OverlayAction.Edit)));
            Children.Add(Buttons.Secondary("Copy to clipboard", Lucide.CopyToClipboard, () => chosen(OverlayAction.Copy)));
            Children.Add(Buttons.Secondary("Cancel", Lucide.Cancel, () => chosen(OverlayAction.Cancel)));
        }
        else
        {
            Children.Add(Buttons.Primary("Whole screen", Lucide.WholeScreen, () => chosen(OverlayAction.WholeScreen)));
            Children.Add(Buttons.Secondary("Whole screen to clipboard", Lucide.CopyToClipboard,
                () => chosen(OverlayAction.WholeScreenToClipboard)));
            Children.Add(Buttons.Secondary("Cancel", Lucide.Cancel, () => chosen(OverlayAction.Cancel)));
        }
    }

    /// <summary>Places the row under a region's bottom-left corner, kept fully on screen.</summary>
    public void PlaceUnder(Rect region, Size screen)
    {
        Measure(screen);
        var size = DesiredSize;

        // Below the region normally; above it when the region reaches the bottom of the screen, and
        // tucked inside it when the region covers the screen entirely and there is no outside left.
        var below = region.Bottom + Gap;
        var above = region.Top - Gap - size.Height;

        var top = below + size.Height <= screen.Height ? below
            : above >= 0 ? above
            : Math.Max(region.Bottom - Gap - size.Height, 0);

        var left = Math.Clamp(region.Left, Gap, Math.Max(screen.Width - size.Width - Gap, Gap));

        Canvas.SetLeft(this, left);
        Canvas.SetTop(this, top);
    }

    /// <summary>Places the row across the top of the screen, where the pointer is least likely to be.</summary>
    public void PlaceAtTop(Size screen)
    {
        Measure(screen);

        Canvas.SetLeft(this, Math.Max((screen.Width - DesiredSize.Width) / 2, 0));
        Canvas.SetTop(this, 30 - Blueprint.Reach);
    }
}
