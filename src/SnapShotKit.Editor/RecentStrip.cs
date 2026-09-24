using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using SnapShotKit.Ui;

namespace SnapShotKit.Editor;

/// <summary>
/// The band of recent captures along the bottom of the editor.
///
/// It answers one question, which is "which of the last few captures did I mean", and the picture
/// is the only thing that answers it. Names and times are there to separate two similar shots, not
/// to be read.
///
/// Unpinned, it waits below the bottom edge of the window and slides up over the picture when the
/// pointer reaches that edge, then slides away again when the pointer leaves it. The picture is
/// what the window is for, and a band that is only wanted between captures should not take a
/// sixth of the height the rest of the time. Pinned, it stays up and takes its share of the window
/// the way it always used to.
///
/// Captures pinned to it sit at the front, ahead of anything newer, and everything else follows
/// newest first, because that is where the eye starts and the newest capture is almost always the
/// one being looked for.
///
/// The tiles are built by hand rather than through a ListBox. A list control brings a selection
/// visual of its own, a filled rounded block, which is precisely what this design does not want,
/// and suppressing it is more work than laying out a row of tiles.
///
/// The strip asks rather than acts: it raises what the user chose and leaves opening, copying,
/// pinning and deleting to the window, which is the only thing that knows whether the capture in
/// question is the one on the canvas, and the only thing that remembers anything.
/// </summary>
public sealed class RecentStrip : Border
{
    const double TileWidth = 160;
    const double TileHeight = 90;

    /// <summary>The controls in a tile's corners.</summary>
    const double BadgeSize = 22;

    /// <summary>
    /// How far below its own edge the strip goes to hide, beyond its height: enough to take the
    /// shadow it casts while it is up out of sight with it.
    /// </summary>
    const double ShadowReach = 48;

    readonly StackPanel tiles;
    readonly TextBlock caption;
    readonly TextBlock current;
    readonly Border pinHost = new();
    readonly TranslateTransform slide = new();

    /// <summary>
    /// How long the strip waits after the pointer leaves it before going.
    ///
    /// Long enough that a pointer cutting a corner on its way along the strip does not send it
    /// down and straight back up, short enough that it is out of the way by the time the pointer
    /// has reached whatever it was going for.
    /// </summary>
    readonly DispatcherTimer retractDelay = new() { Interval = TimeSpan.FromMilliseconds(450) };

    /// <summary>
    /// The context menu, built once and re-anchored to whichever tile it is asked for.
    ///
    /// One popup for the whole strip rather than one per tile, and never moved in the tree: a popup
    /// put under a new parent stops opening and says nothing about it, and the tiles are thrown away
    /// and rebuilt every time the strip is filled. Only its placement target changes.
    /// </summary>
    readonly Popup menu;

    /// <summary>Controls beside the strip that count as still being on it. See <see cref="HoldWhileOver"/>.</summary>
    readonly List<Control> holders = [];

    bool pinned;
    bool raised;

    public RecentStrip()
    {
        Background = Tokens.BgBrush;
        BorderBrush = Tokens.DividerBrush;
        BorderThickness = new Thickness(0, 1, 0, 0);
        Padding = new Thickness(Tokens.Space.S6, Tokens.Space.S3);

        // Moved rather than laid out again. A render transform leaves the layout exactly as it was,
        // so the strip coming and going never makes the window measure anything, and the picture
        // under it stays where it is.
        slide.Transitions =
        [
            new DoubleTransition
            {
                Property = TranslateTransform.YProperty,
                Duration = TimeSpan.FromMilliseconds(220),
                Easing = new CubicEaseOut()
            }
        ];

        RenderTransform = slide;

        // Far enough down to be out of sight whatever height the strip turns out to be. The real
        // distance is set once it has been measured.
        slide.Y = 10000;
        IsHitTestVisible = false;
        BoxShadow = Tokens.ShadowLg;

        caption = Labels.Heading("TODAY", 11.5, 0.18);
        current = Labels.Body(string.Empty, 12.5, Tokens.Neutral700Brush);
        current.HorizontalAlignment = HorizontalAlignment.Right;
        current.VerticalAlignment = VerticalAlignment.Center;

        pinHost.Margin = new Thickness(Tokens.Space.S3, 0, 0, 0);
        pinHost.Child = PinButton();

        menu = new Popup
        {
            // Above the tile it was asked for. The strip is the bottom band of the window, so there
            // is never room below it, and a menu anchored to the tile lands in the same place every
            // time rather than wherever the pointer happened to be.
            Placement = PlacementMode.TopEdgeAlignedLeft,
            IsLightDismissEnabled = true,
            OverlayDismissEventPassThrough = true
        };

        // The pointer is over the menu rather than the strip while it is open, and the strip has
        // been waiting to go since it left. Once the menu has been answered it can.
        menu.Closed += (_, _) => RetractSoon();

        var captionRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        Grid.SetColumn(caption, 0);
        Grid.SetColumn(current, 1);
        Grid.SetColumn(pinHost, 2);
        caption.VerticalAlignment = VerticalAlignment.Center;
        captionRow.Children.Add(caption);
        captionRow.Children.Add(current);
        captionRow.Children.Add(pinHost);

        // A popup has to live in the visual tree to be hosted at all. A zero-sized cell sharing the
        // caption row asks for no space of its own, which a row in the stack above the tiles would.
        var host = new Panel { Width = 0, Height = 0, Children = { menu } };
        Grid.SetColumn(host, 0);
        captionRow.Children.Add(host);

        tiles = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Tokens.Space.S4 };

        var scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = tiles
        };

        Child = new StackPanel
        {
            Spacing = Tokens.Space.S2,
            Children = { captionRow, scroller }
        };

        Handle = BuildHandle();

        PointerEntered += (_, _) => retractDelay.Stop();
        PointerExited += (_, _) => RetractSoon();

        retractDelay.Tick += (_, _) =>
        {
            // Still being answered. The menu closing asks again.
            if (menu.IsOpen)
            {
                return;
            }

            retractDelay.Stop();

            if (!IsPointerOver && !Handle.IsPointerOver && !holders.Any(holder => holder.IsPointerOver))
            {
                Retract();
            }
        };

        // The height is only known once the strip has been measured, and changes with the window's
        // fonts and whether the tiles need a scroll bar. A hidden strip follows it down.
        SizeChanged += (_, e) =>
        {
            if (!pinned && !raised)
            {
                slide.Y = e.NewSize.Height + ShadowReach;
            }
        };
    }

    /// <summary>
    /// The edge the strip comes up from, for the window to lay along the bottom of the picture.
    ///
    /// A thin band with a grip drawn in the middle of it, so there is something to find: a strip
    /// that only appears when the pointer happens across the right few pixels is one most people
    /// would never discover. Hidden while the strip is pinned, since there is then nothing below
    /// the edge to fetch.
    /// </summary>
    public Control Handle { get; }

    /// <summary>The capture to put on the canvas.</summary>
    public event Action<string>? Chosen;

    /// <summary>The capture to put on the clipboard, annotations and all.</summary>
    public event Action<string>? CopyRequested;

    /// <summary>The capture whose file location should go on the clipboard as text.</summary>
    public event Action<string>? CopyLocationRequested;

    /// <summary>
    /// The capture to delete from disk, and whether the user has already answered for it.
    ///
    /// Shift on the badge is that answer. It skips the question rather than doing anything
    /// different, so somebody who never discovers it loses nothing by not knowing.
    /// </summary>
    public event Action<string, bool>? DeleteRequested;

    /// <summary>The capture to take off the strip, leaving it in the library.</summary>
    public event Action<string>? RemoveRequested;

    /// <summary>The capture to pin to the front of the strip, or to let go of.</summary>
    public event Action<string, bool>? PinRequested;

    /// <summary>The strip being pinned up, or let go to hide below the edge.</summary>
    public event Action<bool>? PinnedChanged;

    /// <summary>Whether the strip stays up.</summary>
    public bool Pinned
    {
        get => pinned;
        set
        {
            var wasPinned = pinned;
            pinned = value;
            pinHost.Child = PinButton();
            Handle.IsVisible = !value;

            // Floating over the picture, it casts a shadow onto it. Docked below it, it is part of
            // the window and sits flat like the rest of the chrome.
            BoxShadow = value ? default : Tokens.ShadowLg;

            if (value)
            {
                retractDelay.Stop();
                raised = true;
                IsHitTestVisible = true;
                slide.Y = 0;
            }
            // Only when it is being let go. Told it is unpinned when it already was, it stays as it
            // is, which may be up: an empty editor offers the strip before the window has said
            // whether it is pinned.
            else if (wasPinned && !IsPointerOver)
            {
                Retract();
            }
        }
    }

    /// <summary>Fills the strip, leaving the open snapshot in place and marked, and the pinned ones first.</summary>
    public void Show(IReadOnlyList<SnapshotItem> items, string openPath, IReadOnlySet<string> pinnedPaths)
    {
        caption.Text = Caption(items);

        var open = items.FirstOrDefault(item => string.Equals(item.Entry.Path, openPath, StringComparison.Ordinal));
        current.Text = open is null ? string.Empty : $"{open.When} — {open.ShortName}";

        // Whatever it was offered on is about to be thrown away, and a menu left standing over a
        // rebuilt strip would act on a tile that is no longer there.
        menu.IsOpen = false;

        tiles.Children.Clear();

        Control? openTile = null;
        var pinnedBefore = false;

        foreach (var item in items)
        {
            var isPinned = pinnedPaths.Contains(item.Entry.Path);

            // A hairline between the pinned captures and the rest, so it is plain that the ones on
            // the left are there because they were put there rather than because they are new.
            if (pinnedBefore && !isPinned)
            {
                tiles.Children.Add(new Border
                {
                    Width = 1,
                    Height = TileHeight,
                    Margin = new Thickness(0, Blueprint.Reach, 0, 0),
                    VerticalAlignment = VerticalAlignment.Top,
                    Background = Tokens.DividerBrush
                });
            }

            pinnedBefore = isPinned;

            var tile = Tile(item, ReferenceEquals(item, open), isPinned);
            tiles.Children.Add(tile);

            if (ReferenceEquals(item, open))
            {
                openTile = tile;
            }
        }

        // The open capture is not necessarily among the newest, so it can sit off the end of the
        // strip. Bringing it into view means the accent ring is actually visible saying so.
        openTile?.BringIntoView();
    }

    /// <summary>
    /// Asks for its height and no width at all, and takes whatever width it is given.
    ///
    /// Thirty tiles laid end to end come to several thousand pixels. The scroller is meant to keep
    /// that to itself, but a strip that reports it as a width it would like hands it up the tree,
    /// where it becomes a width the window has to satisfy. The strip is always as wide as the
    /// window it sits in; it has no business proposing another.
    /// </summary>
    protected override Size MeasureOverride(Size availableSize) =>
        base.MeasureOverride(availableSize).WithWidth(0);

    /// <summary>
    /// Keeps the strip up while the pointer is over <paramref name="holder"/>, once it is up.
    ///
    /// For the status line directly below it. The pointer crossing from the strip to the zoom
    /// controls has not left for the picture, and a strip that dropped away under it would have
    /// to be fetched again from an edge the pointer has just gone past. It does not bring the strip
    /// up: somebody reaching for the zoom has not asked for the captures.
    /// </summary>
    public void HoldWhileOver(Control holder)
    {
        holders.Add(holder);
        holder.PointerEntered += (_, _) => retractDelay.Stop();
        holder.PointerExited += (_, _) => RetractSoon();
    }

    /// <summary>
    /// Brings the strip up without waiting for the pointer to fetch it, and leaves it there until
    /// the pointer has been over it and left.
    ///
    /// For an editor with nothing on the canvas, where the strip is the thing to choose from and
    /// making somebody find the edge first would hide the one useful thing on screen. It is not
    /// pinned by this: once it has been used and left, it goes like it always does.
    /// </summary>
    public void Offer() => Raise();

    /// <summary>Brings the strip up over the picture, unless it is already up.</summary>
    void Raise()
    {
        retractDelay.Stop();

        if (pinned || raised)
        {
            return;
        }

        raised = true;
        IsHitTestVisible = true;
        slide.Y = 0;
    }

    /// <summary>Sends the strip back below the edge, unless it is pinned up.</summary>
    void Retract()
    {
        if (pinned)
        {
            return;
        }

        raised = false;
        IsHitTestVisible = false;
        menu.IsOpen = false;
        slide.Y = Bounds.Height + ShadowReach;
    }

    void RetractSoon()
    {
        if (!pinned && raised)
        {
            retractDelay.Start();
        }
    }

    Control BuildHandle()
    {
        var grip = new Border
        {
            Width = 56,
            Height = 4,
            Margin = new Thickness(0, 0, 0, Tokens.Space.S1),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = Tokens.Neutral400Brush
        };

        var handle = new Border
        {
            Height = Tokens.Space.S4,
            VerticalAlignment = VerticalAlignment.Bottom,

            // Transparent rather than left empty, which would let the pointer straight through it
            // to the mat and the band would never know it had been reached.
            Background = Brushes.Transparent,
            Child = grip
        };

        // Not while a button is held. Dragging an arrow down to the bottom of the picture is not a
        // request for the strip, and one that rose under the pointer would take the release.
        void Reached(PointerEventArgs e)
        {
            var point = e.GetCurrentPoint(handle).Properties;

            if (!point.IsLeftButtonPressed && !point.IsRightButtonPressed && !point.IsMiddleButtonPressed)
            {
                Raise();
            }
        }

        handle.PointerEntered += (_, e) =>
        {
            grip.Background = Tokens.Neutral600Brush;
            Reached(e);
        };

        handle.PointerMoved += (_, e) => Reached(e);

        handle.PointerExited += (_, _) =>
        {
            grip.Background = Tokens.Neutral400Brush;
            RetractSoon();
        };

        return handle;
    }

    Control PinButton()
    {
        var button = Badge(
            stroke => Lucide.Icon(Lucide.Pin, 13, stroke),
            pinned ? "Let the strip hide below the edge" : "Keep the strip up",
            pinned ? Tokens.Accent700Brush : Tokens.AccentBrush,
            lit: pinned,
            _ => PinnedChanged?.Invoke(!pinned));

        button.VerticalAlignment = VerticalAlignment.Center;
        return button;
    }

    static string Caption(IReadOnlyList<SnapshotItem> items)
    {
        var today = items.Count(item => item.Entry.Modified.Date == DateTime.Today);

        return today > 0
            ? $"TODAY · {DateTime.Today:d MMM} · {today} CAPTURE{(today == 1 ? string.Empty : "S")}".ToUpperInvariant()
            : $"{items.Count} CAPTURE{(items.Count == 1 ? string.Empty : "S")}";
    }

    /// <summary>
    /// One tile: the picture, framed and marked, with its time underneath.
    ///
    /// The open capture is ringed in the accent and lifted off the ground, so the strip always says
    /// which one is on the canvas without having to hide it from the list.
    ///
    /// Under the pointer the picture dims and a control comes up in each corner. The middle is left
    /// clear, since a click there opens the capture. They stay out of sight otherwise, because the strip is a row of pictures and a
    /// permanent row of buttons over them would compete with the only thing the strip is for. A
    /// pinned capture keeps its pin showing, which is the one thing about it the picture cannot say.
    /// </summary>
    Control Tile(SnapshotItem item, bool open, bool isPinned)
    {
        var path = item.Entry.Path;

        var picture = new Image { Stretch = Stretch.UniformToFill };
        picture.Bind(Image.SourceProperty, new Avalonia.Data.Binding(nameof(SnapshotItem.Thumbnail)) { Source = item });

        // Dimmed rather than blurred. It only has to push the picture back far enough for the
        // controls over it to read; the picture is still what says which capture this is.
        var scrim = new Border
        {
            IsVisible = false,
            IsHitTestVisible = false,
            Background = new SolidColorBrush(Tokens.Bg, 0.55)
        };

        var remove = Corner(Badge(stroke => Lucide.Icon(Lucide.Cancel, 13, stroke),
                $"Take {item.Name} off the strip\nIt stays in the library",
                Tokens.Neutral800Brush, lit: false, _ => RemoveRequested?.Invoke(path)),
            HorizontalAlignment.Left, VerticalAlignment.Top);

        var pin = Corner(Badge(stroke => Lucide.Icon(Lucide.Pin, 13, stroke),
                isPinned ? $"Unpin {item.Name}" : $"Pin {item.Name} to the front of the strip",
                isPinned ? Tokens.Accent700Brush : Tokens.AccentBrush, lit: isPinned,
                _ => PinRequested?.Invoke(path, !isPinned)),
            HorizontalAlignment.Right, VerticalAlignment.Top);

        var delete = Corner(Badge(stroke => Lucide.Icon(Lucide.Delete, 13, stroke),
                $"Delete {item.Name}\nShift-click to delete without being asked",
                Tokens.DangerBrush, lit: false,
                modifiers => DeleteRequested?.Invoke(path, modifiers.HasFlag(KeyModifiers.Shift))),
            HorizontalAlignment.Left, VerticalAlignment.Bottom);

        var location = Corner(Badge(stroke => Lucide.Icon(Lucide.Location, 13, stroke),
                $"Copy where {item.Name} is kept",
                Tokens.AccentBrush, lit: false, _ => CopyLocationRequested?.Invoke(path)),
            HorizontalAlignment.Right, VerticalAlignment.Bottom);

        Control[] onHover = [scrim, remove, delete, location];

        foreach (var control in onHover)
        {
            control.IsVisible = false;
        }

        pin.IsVisible = isPinned;

        var frame = new Border
        {
            Width = TileWidth,
            Height = TileHeight,
            Background = Tokens.Neutral200Brush,
            CornerRadius = Tokens.Radius,
            ClipToBounds = true,
            Child = new Panel { Children = { picture, scrim, remove, pin, delete, location } },
            BorderBrush = open ? Tokens.AccentBrush : Brushes.Transparent,
            BorderThickness = new Thickness(open ? 2 : 0),
            BoxShadow = open ? Tokens.ShadowMd : default
        };

        var time = Labels.Body(item.When, 11.5, open ? Tokens.Accent800Brush : Tokens.Neutral600Brush);
        time.TextAlignment = TextAlignment.Center;
        time.HorizontalAlignment = HorizontalAlignment.Stretch;

        var tile = new StackPanel
        {
            Spacing = Tokens.Space.S1,
            Cursor = new Cursor(StandardCursorType.Hand),
            Children = { Blueprint.Wrap(frame, drawFrame: !open), time }
        };

        ToolTip.SetTip(tile, item.Name);

        // The controls are children of the tile, so moving onto one is not leaving the tile: the
        // pointer can travel from the picture to a button without the button disappearing under it.
        tile.PointerEntered += (_, _) =>
        {
            foreach (var control in onHover)
            {
                control.IsVisible = true;
            }

            pin.IsVisible = true;
        };

        tile.PointerExited += (_, _) =>
        {
            foreach (var control in onHover)
            {
                control.IsVisible = false;
            }

            pin.IsVisible = isPinned;
        };

        tile.PointerPressed += (_, e) =>
        {
            var properties = e.GetCurrentPoint(tile).Properties;

            if (properties.IsRightButtonPressed)
            {
                ShowMenu(item, tile, isPinned);
                return;
            }

            // One click opens. These are cheap to switch between and the whole point of the strip is
            // to move quickly, so asking for a double click would be friction for its own sake.
            if (properties.IsLeftButtonPressed)
            {
                Chosen?.Invoke(path);
            }
        };

        return tile;
    }

    static Border Corner(Border badge, HorizontalAlignment horizontal, VerticalAlignment vertical)
    {
        badge.HorizontalAlignment = horizontal;
        badge.VerticalAlignment = vertical;
        badge.Margin = new Thickness(Tokens.Space.S1);
        return badge;
    }

    /// <summary>
    /// A small square control, drawn quiet and filled under the pointer.
    ///
    /// Each takes the fill that says what it does: the system's one red for the control that
    /// destroys a capture, so it never looks like the tile that opens it, and the accent for the
    /// rest. A lit one, a pin that is in, wears its fill all the time.
    ///
    /// It swallows its own click. A press that both did what the control says and opened the
    /// capture under it would be the worst of both.
    /// </summary>
    static Border Badge(Func<IBrush, Control> content, string tip, IBrush hover, bool lit, Action<KeyModifiers> pressed)
    {
        var quiet = content(lit ? Tokens.BgBrush : Tokens.Neutral800Brush);
        var bright = content(Tokens.BgBrush);
        var rest = lit ? Tokens.AccentBrush : Tokens.BgBrush;
        var edge = lit ? Tokens.AccentBrush : Tokens.DividerBrush;

        var badge = new Border
        {
            MinWidth = BadgeSize,
            Height = BadgeSize,
            Background = rest,
            BorderBrush = edge,
            BorderThickness = new Thickness(1),
            CornerRadius = Tokens.Radius,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = quiet
        };

        ToolTip.SetTip(badge, tip);

        badge.PointerEntered += (_, _) =>
        {
            badge.Background = hover;
            badge.BorderBrush = hover;
            badge.Child = bright;
        };

        badge.PointerExited += (_, _) =>
        {
            badge.Background = rest;
            badge.BorderBrush = edge;
            badge.Child = quiet;
        };

        badge.PointerPressed += (_, e) =>
        {
            e.Handled = true;

            if (e.GetCurrentPoint(badge).Properties.IsLeftButtonPressed)
            {
                pressed(e.KeyModifiers);
            }
        };

        return badge;
    }

    void ShowMenu(SnapshotItem item, Control tile, bool isPinned)
    {
        var path = item.Entry.Path;

        menu.IsOpen = false;
        menu.PlacementTarget = tile;

        menu.Child = PopupMenu.Build(
        [
            MenuEntry.Item("Open", null, () => Chosen?.Invoke(path)),
            MenuEntry.Separator,
            MenuEntry.Item("Copy", null, () => CopyRequested?.Invoke(path)),
            MenuEntry.Item("Copy location", null, () => CopyLocationRequested?.Invoke(path)),
            MenuEntry.Separator,
            MenuEntry.Item(isPinned ? "Unpin" : "Pin to the front", null, () => PinRequested?.Invoke(path, !isPinned)),
            MenuEntry.Item("Take off the strip", null, () => RemoveRequested?.Invoke(path)),
            MenuEntry.Separator,
            // The menu always asks. A menu item is chosen from a list rather than aimed at, and
            // whatever was held to open the menu is long released by the time one is picked.
            MenuEntry.Item("Delete", null, () => DeleteRequested?.Invoke(path, false))
        ], menu);

        menu.IsOpen = true;
    }
}
