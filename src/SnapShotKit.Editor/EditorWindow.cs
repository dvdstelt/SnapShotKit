using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using SnapShotKit.Contracts;
using SnapShotKit.Ui;

namespace SnapShotKit.Editor;

/// <summary>
/// The editor: a menu bar, one band carrying the drawing tools and their settings, the capture on a
/// mat in the middle, and the recent captures along the bottom.
///
/// Everything is a full-width horizontal band separated by hairlines, which is what keeps the
/// window reading as one instrument rather than a set of floating palettes. Commands live in the
/// menus and settings live in the band; nothing floats over the picture.
/// </summary>
public sealed class EditorWindow : Window
{
    /// <summary>The strip holds a generous number; the library is one click away for the rest.</summary>
    const int RecentCount = 30;

    /// <summary>
    /// The zoom steps the buttons, the keys and the wheel move between.
    ///
    /// A ladder rather than a percentage a notch: the sizes worth having are few, and landing on
    /// 100% exactly matters far more than being able to reach 87%. It stops at 400%, which is where
    /// the canvas stops.
    /// </summary>
    static readonly double[] ZoomStops = [0.1, 0.15, 0.25, 0.33, 0.5, 0.67, 1, 1.5, 2, 3, 4];

    readonly Stack<SnapshotDocument> undo = new();
    readonly Stack<SnapshotDocument> redo = new();
    readonly ThumbnailCache thumbnails = new();

    /// <summary>What the editor remembers between sessions, read once as the window opens.</summary>
    readonly EditorState state = EditorState.Load();

    readonly MenuBar menu;
    readonly ToolBand band;
    readonly RecentStrip recent;
    readonly Border mat;
    readonly ScrollViewer scroller;
    readonly Panel canvasHost = new();
    readonly TextBlock status;

    /// <summary>The mat's interior: the picture on its scroller, and the layer that floats over it.</summary>
    readonly Panel matLayer = new();

    /// <summary>
    /// What the mat shows when no capture is open.
    ///
    /// The window stays rather than closing itself. Deleting the capture on the canvas is an edit
    /// to a library, not a reason to take away the window somebody is working in, and the strip
    /// along the bottom is still full of captures to open.
    /// </summary>
    readonly Control nothing = NothingOnTheMat();

    /// <summary>
    /// What floats over the mat, which today is only the bar that confirms a canvas resize.
    ///
    /// A canvas rather than an ordinary panel, because a canvas asks for no size of its own however
    /// large or far out its children are placed. An ordinary panel hands its children's extent up
    /// the tree, where it becomes a size the window has to satisfy: the bar would then push the
    /// window wider, which would move the picture, which would move the bar.
    /// </summary>
    readonly Canvas floating = new();

    readonly Border confirmBar;

    /// <summary>The framed working surface, kept because a canvas drag has to place it by hand for as long as it lasts.</summary>
    Blueprint? framedCanvas;

    CancellationTokenSource thumbnailWork = new();

    /// <summary>
    /// The open capture, or nothing.
    ///
    /// Nothing is a real state rather than an impossible one: deleting the capture on the canvas
    /// leaves the editor with no document, and the window stays open around it. These three are
    /// assigned and cleared together, so anything that acts on a capture only has to ask once.
    /// </summary>
    Snapshot? snapshot;

    BlurCache? blurs;
    CanvasView? canvas;
    EditorTool tool = EditorTool.Arrow;

    bool dirty;
    bool closeApproved;

    /// <summary>
    /// The last panel edit, as "annotation id|property". Repeating the same edit on the same
    /// annotation coalesces into the undo step already recorded, so working a setting is one
    /// undoable action rather than a step per click.
    /// </summary>
    string? lastBandEdit;

    /// <summary>Where the framed canvas sat when a canvas drag began, in the mat's own coordinates.</summary>
    Point pinned;

    /// <param name="opened">
    /// Named for what it is rather than after the field it fills. The field can now hold nothing,
    /// and a parameter of the same name would shadow it here: anything deferred from this
    /// constructor would read the capture the window opened with for as long as the window lived,
    /// long after it had been closed and disposed.
    /// </param>
    public EditorWindow(Snapshot opened)
    {
        snapshot = opened;
        blurs = new BlurCache(opened.OriginalPng);
        canvas = new CanvasView(opened, blurs);

        Title = $"SnapShotKit - {Path.GetFileName(opened.Path)}";

        // Wide enough for the band's busiest tool. A window that opens too narrow for its own
        // chrome starts by hiding a control the user has not been shown yet.
        Width = 1320;
        Height = 760;
        Background = Tokens.BgBrush;

        WireCanvas();

        menu = new MenuBar("SnapShotKit");
        BuildMenus();

        band = new ToolBand(state);
        WireBand();

        recent = new RecentStrip();
        recent.Chosen += OpenSnapshot;
        recent.CopyRequested += CopySnapshot;
        recent.DeleteRequested += (path, answered) => _ = DeleteSnapshotAsync(path, answered);

        status = Labels.Body(string.Empty, 12, Tokens.Neutral600Brush);

        scroller = new ScrollViewer { Content = canvasHost };

        // The bar that applies or abandons a canvas resize. It lives on the mat rather than in the
        // canvas, because the mat is the part of the window where nothing happens: a question about
        // the picture must not be asked on top of the picture.
        confirmBar = new Border
        {
            IsVisible = false,
            Background = Tokens.BgBrush,
            BorderBrush = Tokens.DividerBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = Tokens.Radius,
            BoxShadow = Tokens.ShadowMd,
            Padding = new Thickness(Tokens.Space.S2),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = Tokens.Space.S2,
                Children =
                {
                    // Quiet answer first and the decisive one last, which is the order every other
                    // question in this application is asked in.
                    Buttons.Secondary("Cancel", null, () => canvas.CancelCanvasResize()),
                    Buttons.Primary("Apply", null, () => canvas.ApplyCanvasResize())
                }
            }
        };

        floating.Children.Add(confirmBar);

        matLayer.Children.Add(scroller);
        matLayer.Children.Add(floating);

        // The capture sits on a mat as a framed object, the way the design treats every figure:
        // hairline border, registration marks, a shallow shadow to lift it off the ground.
        mat = new Border
        {
            Background = Tokens.Neutral200Brush,
            Padding = new Thickness(Tokens.Space.S8),
            Child = matLayer
        };

        framedCanvas = ShowCanvas();
        SetZoom(null);

        var layout = new DockPanel();

        DockPanel.SetDock(menu, Dock.Top);
        layout.Children.Add(menu);

        DockPanel.SetDock(band, Dock.Top);
        layout.Children.Add(band);

        var footer = new Border
        {
            Background = Tokens.BgBrush,
            BorderBrush = Tokens.DividerBrush,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(Tokens.Space.S6, Tokens.Space.S1),
            Child = status
        };

        DockPanel.SetDock(footer, Dock.Bottom);
        layout.Children.Add(footer);

        DockPanel.SetDock(recent, Dock.Bottom);
        layout.Children.Add(recent);

        layout.Children.Add(mat);
        Content = layout;

        KeyDown += OnKeyDown;
        KeyUp += OnKeyUp;

        // A held key whose window goes away never reports being let go, and a hand left holding the
        // picture would then take the next click.
        Deactivated += (_, _) => { if (canvas is not null) canvas.Panning = false; };

        // On the mat rather than on the picture, so the wheel zooms anywhere over the working area
        // rather than only over whatever the picture happens to cover.
        mat.AddHandler(InputElement.PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);

        SetTool(EditorTool.Arrow);
        RefreshRecent();
        UpdateChrome();
    }

    static Control NothingOnTheMat()
    {
        var heading = Labels.Heading("NOTHING OPEN", 13, 0.18, Tokens.Neutral600Brush);
        heading.HorizontalAlignment = HorizontalAlignment.Center;

        var hint = Labels.Body("Choose a capture from the strip below, or press Print to take one.",
            13, Tokens.Neutral500Brush);
        hint.HorizontalAlignment = HorizontalAlignment.Center;

        return new StackPanel
        {
            Spacing = Tokens.Space.S2,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { heading, hint }
        };
    }

    /// <summary>
    /// Puts the editor back to holding nothing.
    ///
    /// What happens when the capture on the canvas is deleted. The document goes with its file
    /// rather than staying behind as unsaved work: it has nowhere left to be saved to, and keeping
    /// it would mean every capture opened afterwards had to step over a question about work the
    /// user has already thrown away.
    /// </summary>
    void CloseDocument()
    {
        canvasHost.Children.Clear();
        canvasHost.Children.Add(nothing);

        // Both hold full-resolution bitmaps in native memory the collector cannot see, so they are
        // released deliberately rather than left to finalisers.
        blurs?.Dispose();
        snapshot?.Dispose();

        snapshot = null;
        blurs = null;
        canvas = null;
        framedCanvas = null;

        undo.Clear();
        redo.Clear();
        lastBandEdit = null;
        dirty = false;

        // The drawing tools are about a capture, and there is none. The menus thin out on their
        // own, since their entries are built afresh each time they are opened.
        band.IsVisible = false;
        confirmBar.IsVisible = false;

        // Back to the state fitting leaves the mat in. A scroller that can scroll offers whatever
        // it holds infinite room, and the next capture opened into an empty editor is fitted: it
        // would have nothing finite to fit to.
        scroller.HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled;
        scroller.VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled;

        Title = "SnapShotKit";
    }

    Blueprint ShowCanvas()
    {
        canvasHost.Children.Clear();

        var shell = new Border
        {
            Background = Tokens.BgBrush,
            BoxShadow = Tokens.ShadowSm,
            CornerRadius = Tokens.Radius,
            ClipToBounds = true,
            Child = canvas
        };

        var framed = new Blueprint
        {
            Child = shell,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        canvasHost.Children.Add(framed);
        return framed;
    }

    /// <summary>
    /// Holds the picture still for the length of a canvas drag.
    ///
    /// The working surface is centred on its mat, so one growing to the right would move half as
    /// far to the left, sliding the picture out from under the pointer that is sizing the canvas.
    /// Anchoring it where it already sits, and then following its own corner, keeps every pixel of
    /// the capture exactly where it is and leaves only the boundary moving. It goes back to being
    /// centred when the drag ends, which is also when it settles into whatever size it now needs.
    ///
    /// Usually there is nothing to follow: the surface only changes when the canvas is dragged
    /// clean out of it, and until then this holds the picture exactly where it already was.
    /// </summary>
    void PinCanvas()
    {
        if (framedCanvas is null)
        {
            return;
        }

        pinned = framedCanvas.Bounds.Position;

        framedCanvas.HorizontalAlignment = HorizontalAlignment.Left;
        framedCanvas.VerticalAlignment = VerticalAlignment.Top;
        framedCanvas.Margin = new Thickness(pinned.X, pinned.Y, 0, 0);
    }

    void MoveCanvas(Vector shift)
    {
        if (framedCanvas is not null)
        {
            framedCanvas.Margin = new Thickness(pinned.X + shift.X, pinned.Y + shift.Y, 0, 0);
        }
    }

    void UnpinCanvas()
    {
        if (framedCanvas is null)
        {
            return;
        }

        framedCanvas.Margin = default;
        framedCanvas.HorizontalAlignment = HorizontalAlignment.Center;
        framedCanvas.VerticalAlignment = VerticalAlignment.Center;
    }

    /// <summary>The gap between the working surface and the bar that confirms it.</summary>
    const double BarGap = 12;

    /// <summary>
    /// Puts the confirm bar on the mat, clear of the picture.
    ///
    /// Under the working surface where there is room for it, and beside or above it where there is
    /// not: a tall capture leaves no room below and plenty either side. It never sits on the
    /// picture, which is the whole reason it is here rather than inside the canvas.
    ///
    /// Called after every layout pass, so it follows the picture through a zoom, a resize of the
    /// window and every step of a drag. It assigns only when the answer has changed, since a margin
    /// set during layout starts another pass and would otherwise never settle.
    /// </summary>
    void PlaceConfirmBar()
    {
        if (canvas is null)
        {
            confirmBar.IsVisible = false;
            return;
        }

        confirmBar.IsVisible = canvas.IsResizingCanvas;

        if (!confirmBar.IsVisible || canvas.TranslatePoint(default, floating) is not { } corner)
        {
            return;
        }

        var surface = new Rect(corner, canvas.Bounds.Size);
        var bar = confirmBar.DesiredSize;
        var room = new Rect(floating.Bounds.Size);

        var middle = surface.Center.X - bar.Width / 2;
        var centre = surface.Center.Y - bar.Height / 2;

        var wanted = new[]
        {
            new Point(middle, surface.Bottom + BarGap),
            new Point(middle, surface.Y - BarGap - bar.Height),
            new Point(surface.Right + BarGap, centre),
            new Point(surface.X - BarGap - bar.Width, centre)
        };

        var at = wanted.FirstOrDefault(candidate => room.Contains(new Rect(candidate, bar)));

        // Nowhere on the mat is clear of the picture, which happens only when the picture fills it
        // in both directions. Under the foot of the surface, held inside the mat.
        if (at == default)
        {
            at = new Point(
                Math.Clamp(middle, 0, Math.Max(room.Width - bar.Width, 0)),
                Math.Clamp(surface.Bottom - bar.Height - BarGap, 0, Math.Max(room.Height - bar.Height, 0)));
        }

        // Only when it has actually moved. This runs after every layout pass, and a placement that
        // asks for another pass every time it runs is a layout that never settles.
        if (Moved(Canvas.GetLeft(confirmBar), at.X) || Moved(Canvas.GetTop(confirmBar), at.Y))
        {
            Canvas.SetLeft(confirmBar, at.X);
            Canvas.SetTop(confirmBar, at.Y);
        }
    }

    /// <summary>
    /// Whether a coordinate wants setting.
    ///
    /// A canvas coordinate that has never been set reads as not-a-number, and every comparison with
    /// that is false, including the one that would have noticed it needed a value.
    /// </summary>
    static bool Moved(double current, double wanted) => double.IsNaN(current) || Math.Abs(current - wanted) > 0.5;

    /// <summary>What a menu offers when there is no capture to offer it about.</summary>
    static readonly MenuEntry[] NoCapture = [MenuEntry.Note("No capture open")];

    /// <summary>
    /// The menus.
    ///
    /// Each menu's entries are built afresh every time it is opened, so a menu with nothing to act
    /// on says so rather than listing commands that would do nothing. The commands still guard
    /// themselves: the keys reach them too, and the keys are not rebuilt.
    /// </summary>
    void BuildMenus()
    {
        menu.Add("File", FileMenu);

        menu.Add("Edit", () => snapshot is null ? NoCapture :
        [
            MenuEntry.Item("Undo", "Ctrl+Z", Undo),
            MenuEntry.Item("Redo", "Ctrl+Shift+Z", Redo),
            MenuEntry.Separator,
            MenuEntry.Item("Delete", "Del", () => canvas?.DeleteSelected()),
            MenuEntry.Item("Deselect", "Esc", () => canvas?.Select(null)),
            MenuEntry.Separator,
            MenuEntry.Item("Resize canvas", "C", () => SetTool(EditorTool.Canvas)),
            MenuEntry.Item("Fit canvas to capture", null, FitCanvasToCapture),
            MenuEntry.Separator,
            MenuEntry.Item("Cut out a band", "X", () => SetTool(EditorTool.Cut)),
            MenuEntry.Item("Put every cut back", null, () => canvas?.UncutAll()),
            MenuEntry.Separator,
            MenuEntry.Item("Bring to front", "Ctrl+Shift+]", () => Arrange(Order.Front)),
            MenuEntry.Item("Bring forward", "Ctrl+]", () => Arrange(Order.Forward)),
            MenuEntry.Item("Send backward", "Ctrl+[", () => Arrange(Order.Backward)),
            MenuEntry.Item("Send to back", "Ctrl+Shift+[", () => Arrange(Order.Back))
        ]);

        menu.Add("Draw", () => snapshot is null ? NoCapture :
        [
            MenuEntry.Item("Select", "V", () => SetTool(EditorTool.Select)),
            MenuEntry.Item("Arrow", "A", () => SetTool(EditorTool.Arrow)),
            MenuEntry.Item("Box", "B", () => SetTool(EditorTool.Box)),
            MenuEntry.Item("Blur", "L", () => SetTool(EditorTool.Blur)),
            MenuEntry.Item("Text", "T", () => SetTool(EditorTool.Text)),
            MenuEntry.Item("Numbered marker", "N", () => SetTool(EditorTool.Step)),
            MenuEntry.Separator,
            MenuEntry.Item("New line in text", "Shift+Enter", () => { }),
            MenuEntry.Item("Finish text", "Enter", () => canvas?.CommitEdit())
        ]);

        menu.Add("View", () => snapshot is null ? NoCapture :
        [
            MenuEntry.Item("Zoom in", "Ctrl++", () => StepZoom(1)),
            MenuEntry.Item("Zoom out", "Ctrl+-", () => StepZoom(-1)),
            MenuEntry.Separator,
            MenuEntry.Item("Fit to window", "Ctrl+0", () => SetZoom(null)),
            MenuEntry.Separator,
            MenuEntry.Item("Move the picture", "Space and drag", () => { }),
            MenuEntry.Separator,
            MenuEntry.Item("50%", null, () => SetZoom(0.5)),
            MenuEntry.Item("100%", null, () => SetZoom(1)),
            MenuEntry.Item("200%", null, () => SetZoom(2)),
            MenuEntry.Item("400%", null, () => SetZoom(4))
        ]);

        menu.Add("Library", () =>
        [
            MenuEntry.Item("Open library", "Ctrl+L", OpenLibrary)
        ]);

        menu.Add("Help", () =>
        [
            MenuEntry.Item("Snapshots folder", null, () => Report(SnapShotKitPaths.Snapshots)),
            MenuEntry.Item("Exports folder", null, () => Report(SnapShotKitPaths.Exports))
        ]);
    }

    /// <summary>
    /// The File menu.
    ///
    /// Taking a capture, opening one and closing the window make sense with an empty editor.
    /// Saving, exporting and copying do not, and are left out rather than listed and refused.
    /// </summary>
    IReadOnlyList<MenuEntry> FileMenu()
    {
        List<MenuEntry> entries =
        [
            MenuEntry.Item("New capture", "Print", NewCapture),
            MenuEntry.Item("Open…", "Ctrl+O", OpenLibrary),
            MenuEntry.Separator
        ];

        if (snapshot is not null)
        {
            entries.AddRange(
            [
                MenuEntry.Item("Save", "Ctrl+S", Save),
                MenuEntry.Item("Save as…", "Ctrl+Shift+S", () => _ = SaveAsAsync()),
                MenuEntry.Separator,
                MenuEntry.Item("Export PNG", "Ctrl+E", () => _ = ExportAsync("png")),
                MenuEntry.Item("Export JPEG…", "Ctrl+Shift+E", () => _ = ExportAsync("jpg")),
                MenuEntry.Item("Copy to clipboard", "Ctrl+C", CopyToClipboard),
                MenuEntry.Separator
            ]);
        }

        entries.Add(MenuEntry.Item("Close", "Ctrl+W", Close));
        return entries;
    }

    void WireBand()
    {
        band.ToolChosen += SetTool;
        band.UndoRequested += Undo;
        band.RedoRequested += Redo;

        band.StyleChosen += ApplyStyle;

        // Every setting below writes to the capture on the canvas, and the band is not shown when
        // there is none. The guards are the brace to that belt: an event that arrives anyway is
        // dropped rather than reaching for a document that is not there.
        band.CutDirectionChosen += axis =>
        {
            if (canvas is null) return;

            canvas.Defaults.CutDirection = axis;
            UpdateChrome();
        };

        band.ColourChosen += colour =>
        {
            if (canvas is null) return;

            switch (BandTarget())
            {
                case EditorTool.Box:
                    canvas.Defaults.BoxBorderColor = colour;
                    Apply<BoxAnnotation>("colour", box => box.BorderColor = colour);
                    break;

                case EditorTool.Text:
                    canvas.Defaults.TextColor = colour;
                    Apply<TextAnnotation>("colour", text => text.Color = colour);
                    break;

                case EditorTool.Step:
                    canvas.Defaults.StepColor = colour;
                    Apply<StepAnnotation>("colour", step => step.Color = colour);
                    break;

                default:
                    canvas.Defaults.ArrowColor = colour;
                    Apply<ArrowAnnotation>("colour", arrow => arrow.Color = colour);
                    break;
            }
        };

        band.WeightChosen += weight =>
        {
            if (canvas is null) return;

            if (BandTarget() == EditorTool.Box)
            {
                canvas.Defaults.BoxBorderThickness = weight;
                Apply<BoxAnnotation>("weight", box => box.BorderThickness = weight);
            }
            else
            {
                canvas.Defaults.ArrowThickness = weight;
                Apply<ArrowAnnotation>("weight", arrow => arrow.Thickness = weight);
            }
        };

        band.DoubleHeadChosen += doubled =>
        {
            if (canvas is null) return;

            canvas.Defaults.ArrowDoubleHeaded = doubled;
            Apply<ArrowAnnotation>("head", arrow => arrow.DoubleHeaded = doubled);
        };

        band.FillChosen += filled =>
        {
            if (canvas is null) return;

            canvas.Defaults.BoxFilled = filled;

            // Turning a fill off keeps the colour it had, so turning it back on restores it rather
            // than starting over at the default.
            Apply<BoxAnnotation>("fill", box => box.FillColor = filled
                ? (box.HasFill ? box.FillColor : canvas.Defaults.BoxFillColor)
                : string.Empty);

            UpdateChrome();
        };

        band.FillColourChosen += fill =>
        {
            if (canvas is null) return;

            canvas.Defaults.BoxFillColor = fill;
            Apply<BoxAnnotation>("fill-colour", box => box.FillColor = fill);
        };

        band.BlurChosen += strength =>
        {
            if (canvas is null) return;

            canvas.Defaults.BlurStrength = strength;
            Apply<BlurAnnotation>("strength", blur => blur.Strength = strength);
        };

        band.TextSizeChosen += size =>
        {
            if (canvas is null) return;

            canvas.Defaults.TextSize = size;
            Apply<TextAnnotation>("size", text => text.FontSize = size);
        };

        band.TextBackChosen += backed =>
        {
            if (canvas is null) return;

            canvas.Defaults.TextBackgrounded = backed;

            Apply<TextAnnotation>("background", text => text.Background = backed
                ? (text.HasBackground ? text.Background : canvas.Defaults.TextBackgroundColor)
                : string.Empty);

            UpdateChrome();
        };

        band.TextBackColourChosen += background =>
        {
            if (canvas is null) return;

            canvas.Defaults.TextBackgroundColor = background;
            Apply<TextAnnotation>("background-colour", text => text.Background = background);
        };

        band.StepNumberChosen += number => Apply<StepAnnotation>("number", step => step.Number = number);

        band.StepSizeChosen += diameter =>
        {
            if (canvas is null) return;

            canvas.Defaults.StepDiameter = diameter;
            Apply<StepAnnotation>("size", step => step.Diameter = diameter);
        };

        // The canvas keeps its top-left corner when it is given a size outright. A typed width says
        // how wide, not which way to grow, and growing from the corner already on screen is the
        // answer that needs no explaining. Nothing is applied yet: the fields propose, exactly as
        // dragging an edge does, and the resize is confirmed as a whole.
        band.CanvasWidthChosen += width => canvas?.ProposeCanvasSize(width, null);
        band.CanvasHeightChosen += height => canvas?.ProposeCanvasSize(null, height);
        band.CanvasFitRequested += FitCanvasToCapture;

        band.ZoomStepped += direction => StepZoom(direction);
        band.ZoomFitRequested += () => SetZoom(null);
    }

    /// <summary>
    /// Puts the canvas back around the capture exactly, undoing whatever crop or padding it had.
    ///
    /// A proposal while the canvas is being resized, and an edit in its own right otherwise, since
    /// the menu offers it whatever tool happens to be in hand.
    /// </summary>
    void FitCanvasToCapture()
    {
        if (snapshot is null || canvas is null)
        {
            return;
        }

        if (canvas.IsResizingCanvas)
        {
            canvas.ProposeCaptureBounds();
            return;
        }

        var capture = snapshot.Bitmap.PixelSize;
        var area = snapshot.Document.Canvas;

        if (area is { X: 0, Y: 0 } && area.Width == capture.Width && area.Height == capture.Height)
        {
            return;
        }

        Record();

        area.X = 0;
        area.Y = 0;
        area.Width = capture.Width;
        area.Height = capture.Height;

        dirty = true;
        canvas.CanvasResized();
        UpdateChrome();
    }

    /// <summary>
    /// Takes a ready-made look, for the next annotation drawn and for the selected one.
    ///
    /// The same two places every other setting on the band lands in, and one undoable step for the
    /// whole look rather than one per property it happens to cover.
    /// </summary>
    void ApplyStyle(AnnotationStyle style)
    {
        if (snapshot is null || canvas is null)
        {
            return;
        }

        canvas.Defaults.Adopt(style.Look);

        if (canvas.Selected is { } target && target.GetType() == style.Look.GetType())
        {
            var edit = $"{target.Id}|style {style.Name}";
            if (edit != lastBandEdit)
            {
                Record();
                lastBandEdit = edit;
            }

            target.AdoptStyle(style.Look);

            // A look changed while text is being typed has to reach the editor too, or the words
            // keep the old colour until the edit ends.
            canvas.RefreshEditing();

            dirty = true;
            canvas.InvalidateVisual();
        }

        UpdateChrome();
    }

    /// <summary>Which tool's settings the band is currently showing, which is the selection's kind when there is one.</summary>
    EditorTool BandTarget() => canvas?.Selected switch
    {
        ArrowAnnotation => EditorTool.Arrow,
        BoxAnnotation => EditorTool.Box,
        BlurAnnotation => EditorTool.Blur,
        TextAnnotation => EditorTool.Text,
        StepAnnotation => EditorTool.Step,
        _ => tool
    };

    /// <summary>Edits the selection when it is of the given kind, coalescing repeats of the same setting into one undo step.</summary>
    void Apply<T>(string property, Action<T> change) where T : Annotation
    {
        if (snapshot is null || canvas is null)
        {
            return;
        }

        if (canvas.Selected is not T target)
        {
            return;
        }

        var edit = $"{target.Id}|{property}";
        if (edit != lastBandEdit)
        {
            Record();
            lastBandEdit = edit;
        }

        change(target);

        // A style change made while text is being typed has to reach the editor too, or the words
        // keep the old size and colour until the edit ends.
        canvas.RefreshEditing();

        dirty = true;
        canvas.InvalidateVisual();
        UpdateChrome();
    }

    void WireCanvas()
    {
        if (canvas is null)
        {
            return;
        }

        canvas.BeforeChange += Record;
        canvas.Abandoned += () => { if (undo.Count > 0) undo.Pop(); };
        canvas.Changed += () => { dirty = true; UpdateChrome(); };
        canvas.SelectionChanged += () => { lastBandEdit = null; UpdateChrome(); };
        canvas.ZoomChanged += () => band.ShowZoom(canvas.EffectiveScale);
        canvas.CanvasResizeStarted += PinCanvas;
        canvas.CanvasResizeMoved += MoveCanvas;
        canvas.CanvasResizeEnded += UnpinCanvas;
        canvas.CanvasProposalChanged += UpdateChrome;
        canvas.LayoutUpdated += (_, _) => PlaceConfirmBar();

        // Dragging the picture to the right looks further left, which is what taking the movement
        // off the offset does.
        canvas.Panned += delta => scroller.Offset -= delta;

        // Applied or abandoned, the mode is done with. Select is where it hands back to, since the
        // thing just resized is the picture rather than anything on it.
        canvas.CanvasResizeFinished += () => SetTool(EditorTool.Select);
    }

    /// <summary>Takes an undo snapshot. Anything newly done invalidates whatever had been undone.</summary>
    void Record()
    {
        if (snapshot is null || canvas is null)
        {
            return;
        }

        lastBandEdit = null;
        undo.Push(snapshot.Document.Copy());
        redo.Clear();
    }

    /// <summary>
    /// Sets the zoom, or null to fit.
    ///
    /// The scroll bars are turned off while fitting. That is what makes fitting work at all: a
    /// scroll viewer offers infinite room in any direction it can scroll, and a canvas measured
    /// against infinity has nothing to fit to.
    /// </summary>
    void SetZoom(double? zoom, Point? anchor = null)
    {
        if (snapshot is null || canvas is null)
        {
            return;
        }

        var bars = zoom is null
            ? Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
            : Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;

        scroller.HorizontalScrollBarVisibility = bars;
        scroller.VerticalScrollBarVisibility = bars;

        // Which bit of the picture is under the anchor now, so that the same bit can be put back
        // under it afterwards.
        var held = anchor is { } point && scroller.TranslatePoint(point, canvas) is { } onCanvas
            ? canvas.ToImagePoint(onCanvas)
            : (Point?)null;

        canvas.Zoom = zoom;
        band.ShowZoom(zoom ?? canvas.EffectiveScale);

        if (held is { } image && anchor is { } stay)
        {
            KeepUnderPointer(image, stay);
        }
    }

    /// <summary>
    /// Scrolls so that a point of the picture lands back under the pointer.
    ///
    /// Zooming about the middle of the viewport is the wrong answer for a screenshot: the thing
    /// being looked at is under the pointer, which is exactly where it should still be afterwards.
    /// </summary>
    void KeepUnderPointer(Point image, Point anchor)
    {
        if (snapshot is null || canvas is null)
        {
            return;
        }

        // The new size and the new scroll extent have to exist before anything can be measured
        // against them, and the layout pass would otherwise not run until after this returns.
        scroller.UpdateLayout();

        if (canvas.TranslatePoint(canvas.ToViewPoint(image), scroller) is not { } landed)
        {
            return;
        }

        scroller.Offset += new Vector(landed.X - anchor.X, landed.Y - anchor.Y);
    }

    /// <summary>
    /// Moves one rung up or down the ladder from wherever the picture is now.
    ///
    /// From the scale in use rather than from the last one asked for, so the first step out of
    /// fitting goes to the nearest sensible size rather than jumping to whatever was set last.
    /// </summary>
    void StepZoom(int direction, Point? anchor = null)
    {
        if (snapshot is null || canvas is null)
        {
            return;
        }

        var current = canvas.EffectiveScale;

        var next = direction > 0
            ? ZoomStops.FirstOrDefault(stop => stop > current + 0.001, ZoomStops[^1])
            : ZoomStops.LastOrDefault(stop => stop < current - 0.001, ZoomStops[0]);

        SetZoom(next, anchor);
    }

    /// <summary>
    /// The wheel zooms about the pointer.
    ///
    /// Taken before the scroll viewer sees it, which is why the handler is tunnelled: the viewer
    /// treats a wheel as a scroll and marks it handled, and a bubbling handler would never run.
    /// Shift is left alone, so the viewer still has a wheel gesture of its own for panning across a
    /// picture too big to fit.
    /// </summary>
    void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) || e.Delta.Y == 0)
        {
            return;
        }

        StepZoom(e.Delta.Y > 0 ? 1 : -1, e.GetPosition(scroller));
        e.Handled = true;
    }

    void SetTool(EditorTool selected)
    {
        if (snapshot is null || canvas is null)
        {
            return;
        }

        tool = selected;

        // Picking the canvas tool opens a resize, and leaving it abandons one that was never
        // applied. The view owns that, because it owns what is being negotiated.
        canvas.Tool = selected;

        canvas.Focus();
        UpdateChrome();
    }

    /// <summary>
    /// Moves the selection through the stacking order.
    ///
    /// The layer list is the stacking order, so this is a move within it. Drawing something puts it
    /// on top, which is right almost always and wrong often enough to need a way out.
    /// </summary>
    void Arrange(Order order)
    {
        if (snapshot is null || canvas is null)
        {
            return;
        }

        if (canvas.Selected is not { } target)
        {
            return;
        }

        var layers = snapshot.Document.Layers;
        var at = layers.IndexOf(target);

        var to = order switch
        {
            Order.Front => layers.Count - 1,
            Order.Back => 0,
            Order.Forward => Math.Min(at + 1, layers.Count - 1),
            _ => Math.Max(at - 1, 0)
        };

        if (at < 0 || at == to)
        {
            return;
        }

        Record();

        layers.RemoveAt(at);
        layers.Insert(to, target);

        dirty = true;
        canvas.InvalidateVisual();
        UpdateChrome();
    }

    void Undo() => Step(undo, redo);

    void Redo() => Step(redo, undo);

    /// <summary>Moves one step between the two histories, which is the same operation in both directions.</summary>
    void Step(Stack<SnapshotDocument> from, Stack<SnapshotDocument> to)
    {
        if (snapshot is null || canvas is null)
        {
            return;
        }

        if (from.Count == 0)
        {
            return;
        }

        to.Push(snapshot.Document.Copy());

        var previous = from.Pop();
        snapshot.Document.Layers.Clear();
        snapshot.Document.Layers.AddRange(previous.Layers);

        // The canvas and the cuts are part of the document too. Restoring only the layers would
        // undo a crop by leaving the crop in place, and a cut by leaving the band cut out.
        snapshot.Document.Canvas = previous.Canvas;
        snapshot.Document.Cuts = previous.Cuts;
        snapshot.Recut();

        lastBandEdit = null;
        dirty = true;

        canvas.Select(null);

        // A resize being negotiated is pointed at the restored canvas rather than left describing
        // the one that has just been stepped away from. Undo is reachable from the keys, the band
        // and the menu while the mode is open, and a proposal is not in the history at all.
        canvas.SeedResize();
        canvas.CanvasResized();
        UpdateChrome();
    }

    void Save()
    {
        if (snapshot is null || canvas is null)
        {
            return;
        }

        canvas.CommitEdit();

        try
        {
            snapshot.Save();
            dirty = false;
            RefreshRecent();
            Report($"Saved {Path.GetFileName(snapshot.Path)}");
        }
        catch (Exception exception)
        {
            Report($"Could not save: {exception.Message}");
        }
    }

    async Task SaveAsAsync()
    {
        if (snapshot is null || canvas is null)
        {
            return;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save snapshot as",
            SuggestedFileName = Path.GetFileName(snapshot.Path),
            DefaultExtension = "ssk",
            SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(SnapShotKitPaths.Snapshots),
            FileTypeChoices = [new FilePickerFileType("SnapShotKit snapshot") { Patterns = ["*.ssk"] }]
        });

        if (file?.TryGetLocalPath() is not { } path)
        {
            return;
        }

        try
        {
            snapshot.SaveAs(path);
            dirty = false;
            RefreshRecent();
            UpdateChrome();
            Report($"Saved {Path.GetFileName(path)}");
        }
        catch (Exception exception)
        {
            Report($"Could not save: {exception.Message}");
        }
    }

    async Task ExportAsync(string extension)
    {
        if (snapshot is null || canvas is null || blurs is null)
        {
            return;
        }

        canvas.CommitEdit();

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = $"Export as {extension.ToUpperInvariant()}",
            SuggestedFileName = Path.GetFileNameWithoutExtension(snapshot.Path) + "." + extension,
            DefaultExtension = extension,
            // Exports belong with the user's pictures, not beside the working document.
            SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(SnapShotKitPaths.Exports),
            FileTypeChoices = [new FilePickerFileType(extension.ToUpperInvariant()) { Patterns = [$"*.{extension}"] }]
        });

        if (file?.TryGetLocalPath() is not { } path)
        {
            return;
        }

        try
        {
            Export.ToFile(snapshot, blurs, path);
            Report($"Exported {Path.GetFileName(path)}");
        }
        catch (Exception exception)
        {
            Report($"Export failed: {exception.Message}");
        }
    }

    /// <summary>Copies the annotated capture, which is what is on screen rather than the untouched original.</summary>
    void CopyToClipboard()
    {
        if (snapshot is null || canvas is null || blurs is null)
        {
            return;
        }

        canvas.CommitEdit();

        try
        {
            var png = Export.ToPng(snapshot, blurs);

            Report(WaylandClipboard.TryCopyPng(png, out var error)
                ? "Copied to the clipboard"
                : $"Could not copy: {error}");
        }
        catch (Exception exception)
        {
            Report($"Could not copy: {exception.Message}");
        }
    }

    /// <summary>
    /// Copies a capture the strip was asked about.
    ///
    /// The one on the canvas is copied as it stands, unsaved changes and all, because that is what
    /// the user is looking at. Any other is rendered from its file, since what is on disk is the
    /// whole of it.
    /// </summary>
    void CopySnapshot(string path)
    {
        if (snapshot is not null && string.Equals(path, snapshot.Path, StringComparison.Ordinal))
        {
            CopyToClipboard();
            return;
        }

        try
        {
            using var other = Snapshot.Open(path);

            // Both hold full-resolution bitmaps in native memory the collector cannot see, so a copy
            // made from the strip releases them rather than leaving them to finalisers.
            using var blurs = new BlurCache(other.OriginalPng);

            var png = Export.ToPng(other, blurs);

            Report(WaylandClipboard.TryCopyPng(png, out var error)
                ? $"Copied {Path.GetFileName(path)} to the clipboard"
                : $"Could not copy: {error}");
        }
        catch (Exception exception)
        {
            Report($"Could not copy {Path.GetFileName(path)}: {exception.Message}");
        }
    }

    /// <summary>
    /// Deletes a capture the strip was asked about, off disk.
    ///
    /// Deleting the one on the canvas is allowed, and closes it: the editor is left empty rather
    /// than holding a document with no file behind it. Keeping it would mean the next capture
    /// opened had to answer a question about saving work the user has just thrown away, and there
    /// is nowhere left to save it to. Refusing instead would make the capture being looked at the
    /// one capture that cannot be tidied away.
    /// </summary>
    /// <param name="answered">
    /// The user held shift, which is them answering the question in advance. Somebody clearing out a
    /// run of junk captures should not have to say so once per capture. It does not carry as far as
    /// unsaved work on the canvas, which is asked about however the delete was asked for.
    /// </param>
    async Task DeleteSnapshotAsync(string path, bool answered)
    {
        var name = Path.GetFileName(path);
        var onCanvas = snapshot is not null && string.Equals(path, snapshot.Path, StringComparison.Ordinal);

        // Deleting a capture is not undoable, so it gets a question. The wording says what will be
        // gone rather than asking whether the user is sure.
        //
        // Shift says not to ask about the file. It does not say to throw away annotations that were
        // never written to it, so the question survives a held shift when the capture on the canvas
        // has unsaved changes: that is the one thing here that no library still holds a copy of.
        var unsaved = onCanvas && dirty;

        var question = unsaved
            ? $"{name} will be deleted permanently, and the changes on the canvas that have not been saved go with it."
            : onCanvas
                ? $"{name} will be deleted permanently.\nIt is the capture on the canvas, so the editor will be left empty."
                : $"{name} will be deleted permanently.\nAny images you already exported are unaffected.";

        if ((!answered || unsaved) && !await Confirm.DeleteAsync(this, "Delete capture", question))
        {
            return;
        }

        try
        {
            SnapshotLibrary.Delete(path);
        }
        catch (Exception exception)
        {
            Report($"Could not delete {name}: {exception.Message}");
            return;
        }

        if (onCanvas)
        {
            CloseDocument();
        }

        RefreshRecent();

        // The chrome first and the message second: the readouts overwrite the status line, so
        // reporting before them would say something and then immediately take it back.
        UpdateChrome();
        Report(onCanvas ? $"Deleted {name}, and closed it" : $"Deleted {name}");
    }

    /// <summary>Asks the daemon for a capture, the same way pressing Print does.</summary>
    void NewCapture()
    {
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo("snapshotkit") { UseShellExecute = false };
            startInfo.ArgumentList.Add("capture");
            System.Diagnostics.Process.Start(startInfo);
        }
        catch (Exception exception)
        {
            Report($"Could not start a capture: {exception.Message}");
        }
    }

    void OpenLibrary()
    {
        var window = new LibraryWindow();
        window.Chosen += OpenSnapshot;
        window.Show(this);
    }

    /// <summary>
    /// Opens another snapshot in this window.
    ///
    /// In place rather than by opening a second window and closing this one. Closing the window that
    /// happens to be the application's main window is a good way to take the whole process down with
    /// it, and swapping the document keeps the window where the user put it.
    /// </summary>
    async void OpenSnapshot(string path)
    {
        if (snapshot is not null && string.Equals(path, snapshot.Path, StringComparison.Ordinal))
        {
            return;
        }

        // The strip opens on a single click, which makes a misclick cheap. Losing every annotation
        // since the last save must not be.
        if (dirty && !await ConfirmDiscardAsync())
        {
            RefreshRecent();
            return;
        }

        Snapshot next;

        try
        {
            next = Snapshot.Open(path);
        }
        catch (Exception exception)
        {
            Report($"Could not open {Path.GetFileName(path)}: {exception.Message}");
            return;
        }

        var previousSnapshot = snapshot;
        var previousBlurs = blurs;

        // The zoom carries over from the capture being put down, so opening one after another
        // keeps the working magnification. From an empty editor there is none to carry, and fitting
        // is what a capture arriving in an empty window should do.
        var zoom = canvas?.Zoom;

        snapshot = next;
        blurs = new BlurCache(next.OriginalPng);
        canvas = new CanvasView(next, blurs) { Zoom = zoom };
        WireCanvas();
        framedCanvas = ShowCanvas();
        band.IsVisible = true;

        // Both hold full-resolution bitmaps in native memory the collector cannot see, so leaving
        // them to finalisers would let every click in the strip stack another capture in memory.
        previousBlurs?.Dispose();
        previousSnapshot?.Dispose();

        undo.Clear();
        redo.Clear();
        lastBandEdit = null;
        dirty = false;

        Title = $"SnapShotKit - {Path.GetFileName(next.Path)}";

        SetTool(tool);
        RefreshRecent();
        Report($"Opened {Path.GetFileName(next.Path)}");
    }

    void RefreshRecent()
    {
        // Cancel thumbnails still loading for the previous list, so switching snapshots does not
        // leave work running for tiles that no longer exist.
        thumbnailWork.Cancel();
        thumbnailWork.Dispose();
        thumbnailWork = new CancellationTokenSource();

        var entries = SnapshotLibrary.List().Take(RecentCount).ToList();
        recent.Show(SnapshotItem.Build(entries, thumbnails, thumbnailWork.Token), snapshot?.Path ?? string.Empty);
    }

    /// <summary>Points the band, the menu bar and the status line at whatever is true now.</summary>
    void UpdateChrome()
    {
        // Nothing open: the band is not on screen and the status line has nothing to measure. The
        // mat already says so in the middle of the window, which is a better place to say it than
        // a line of small print along the bottom.
        if (snapshot is null || canvas is null)
        {
            menu.ShowNothing();
            status.Text = string.Empty;
            return;
        }

        band.Sync(tool, canvas.Defaults, canvas.Selected);
        band.ShowZoom(canvas.EffectiveScale);
        menu.Show(Path.GetFileName(snapshot.Path), dirty);

        // The canvas being proposed while one is being resized, and the document's own otherwise:
        // the readouts follow what is on screen, which is what the user is working on.
        var size = canvas.ShownCanvas;
        band.ShowCanvasSize((int)canvas.ShownCanvasLaid.Width, (int)canvas.ShownCanvasLaid.Height);

        var selection = canvas.Selected switch
        {
            ArrowAnnotation => "arrow selected",
            BoxAnnotation => "box selected",
            BlurAnnotation => "blur selected",
            TextAnnotation => "text selected",
            StepAnnotation => "marker selected",
            _ => "nothing selected"
        };

        // What the file would come out as, which is the canvas with its cuts closed up. The
        // capture's own size is worth saying only once that has stopped matching it, which is
        // exactly when "1920 × 1080" on its own would be ambiguous.
        var capture = snapshot.Bitmap.PixelSize;
        var laid = snapshot.Layout.ToLaid(size);

        var dimensions = laid == new Rect(0, 0, capture.Width, capture.Height)
            ? $"{laid.Width} × {laid.Height}"
            : $"{laid.Width} × {laid.Height} canvas on a {capture.Width} × {capture.Height} capture";

        var cuts = snapshot.Document.Cuts.Count switch
        {
            0 => string.Empty,
            1 => "   ·   1 cut",
            var many => $"   ·   {many} cuts"
        };

        status.Text = $"{dimensions}{cuts}   ·   {snapshot.Document.Layers.Count} object(s)   ·   {selection}";
    }

    /// <summary>Where in the stacking order to move something.</summary>
    enum Order
    {
        Forward,
        Backward,
        Front,
        Back
    }

    void Report(string message) => status.Text = message;

    /// <summary>
    /// Asks what to do about unsaved changes, and does it. True means carry on.
    ///
    /// Saving is offered alongside discarding, because it is usually the answer: a dialog that only
    /// offers losing the work or going back leaves the user to dismiss it and save by hand.
    /// </summary>
    async Task<bool> ConfirmDiscardAsync()
    {
        // Nothing open is nothing to lose. The question is only ever reached through the dirty
        // flag, which an empty editor does not carry, but an unanswerable question is worse than
        // a redundant guard.
        if (snapshot is null)
        {
            return true;
        }

        var chosen = await Confirm.AskAsync(this, "Unsaved changes",
            $"{Path.GetFileName(snapshot.Path)} has changes that have not been saved.",
            new Choice("Keep editing"),
            new Choice("Discard changes"),
            new Choice("Save", Tone.Primary));

        // Closing the dialog outright means the question was not answered, which is the same as
        // deciding to carry on editing.
        var answer = chosen switch
        {
            1 => Unsaved.Discard,
            2 => Unsaved.Save,
            _ => Unsaved.KeepEditing
        };

        if (answer == Unsaved.Save)
        {
            Save();

            // Only carry on if it actually got written; a failed save must not lose the work.
            return !dirty;
        }

        return answer == Unsaved.Discard;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (dirty && !closeApproved)
        {
            e.Cancel = true;
            CloseAfterConfirmationAsync();
        }

        base.OnClosing(e);
    }

    async void CloseAfterConfirmationAsync()
    {
        if (await ConfirmDiscardAsync())
        {
            closeApproved = true;
            Close();
        }
    }

    void OnKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && canvas is not null)
        {
            canvas.Panning = false;
        }
    }

    void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var control = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        // With nothing open, only the keys that are about the window rather than about a capture
        // still mean anything. The rest do nothing, and the empty mat is what says why.
        if (snapshot is null || canvas is null)
        {
            switch (e.Key)
            {
                case Key.O or Key.L when control:
                    OpenLibrary();
                    break;

                case Key.W when control:
                    Close();
                    break;

                case Key.Escape:
                    menu.CloseAll();
                    break;
            }

            return;
        }

        if (control)
        {
            switch (e.Key)
            {
                case Key.Z when shift:
                    Redo();
                    return;

                case Key.Z:
                    Undo();
                    return;

                case Key.S when shift:
                    _ = SaveAsAsync();
                    return;

                case Key.S:
                    Save();
                    return;

                case Key.E:
                    _ = ExportAsync(shift ? "jpg" : "png");
                    return;

                case Key.C:
                    CopyToClipboard();
                    return;

                case Key.O or Key.L:
                    OpenLibrary();
                    return;

                case Key.W:
                    Close();
                    return;

                // Both the key beside the digits and the one on the number pad, and both with and
                // without shift: everyone reaches for a different one of these.
                case Key.OemPlus or Key.Add:
                    StepZoom(1);
                    return;

                case Key.OemMinus or Key.Subtract:
                    StepZoom(-1);
                    return;

                case Key.D0 or Key.NumPad0:
                    SetZoom(null);
                    return;

                case Key.OemCloseBrackets:
                    Arrange(shift ? Order.Front : Order.Forward);
                    return;

                case Key.OemOpenBrackets:
                    Arrange(shift ? Order.Back : Order.Backward);
                    return;
            }

            return;
        }

        // Single letters are hostile while typing, so with a caret in a text field the editor's
        // shortcuts stand down apart from Escape as a way back out.
        if (FocusManager?.GetFocusedElement() is TextBox)
        {
            if (e.Key == Key.Escape)
            {
                canvas.Focus();
                e.Handled = true;
            }

            return;
        }

        // A band still being dragged out is abandoned rather than taken, since letting go is what
        // takes it and there has to be a way back from a drag begun by accident.
        if (canvas.IsCutting && e.Key == Key.Escape)
        {
            canvas.CancelCut();
            e.Handled = true;
            return;
        }

        // A resize is a question with two answers, and these are the two keys that answer it
        // anywhere else in the system.
        if (canvas.IsResizingCanvas && e.Key is Key.Enter or Key.Escape)
        {
            if (e.Key == Key.Enter)
            {
                canvas.ApplyCanvasResize();
            }
            else
            {
                canvas.CancelCanvasResize();
            }

            e.Handled = true;
            return;
        }

        // Held, not pressed: space is a mode for as long as it is down, and the key repeats while
        // it is, so entering the mode twice has to cost nothing.
        if (e.Key == Key.Space)
        {
            canvas.Panning = true;
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.Delete or Key.Back:
                canvas.DeleteSelected();
                break;

            case Key.Escape:
                menu.CloseAll();
                canvas.Select(null);
                break;

            case Key.V:
                SetTool(EditorTool.Select);
                break;

            case Key.A:
                SetTool(EditorTool.Arrow);
                break;

            case Key.B:
                SetTool(EditorTool.Box);
                break;

            case Key.L:
                SetTool(EditorTool.Blur);
                break;

            case Key.T:
                SetTool(EditorTool.Text);
                break;

            case Key.N:
                SetTool(EditorTool.Step);
                break;

            case Key.C:
                SetTool(EditorTool.Canvas);
                break;

            case Key.X:
                SetTool(EditorTool.Cut);
                break;
        }
    }
}
