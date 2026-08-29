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
    Blueprint framedCanvas;

    CancellationTokenSource thumbnailWork = new();

    Snapshot snapshot;
    BlurCache blurs;
    CanvasView canvas;
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

    public EditorWindow(Snapshot snapshot)
    {
        this.snapshot = snapshot;
        blurs = new BlurCache(snapshot.OriginalPng);
        canvas = new CanvasView(snapshot, blurs);

        Title = $"SnapShotKit - {Path.GetFileName(snapshot.Path)}";

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
        Deactivated += (_, _) => canvas.Panning = false;

        // On the mat rather than on the picture, so the wheel zooms anywhere over the working area
        // rather than only over whatever the picture happens to cover.
        mat.AddHandler(InputElement.PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);

        SetTool(EditorTool.Arrow);
        RefreshRecent();
        UpdateChrome();
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
        pinned = framedCanvas.Bounds.Position;

        framedCanvas.HorizontalAlignment = HorizontalAlignment.Left;
        framedCanvas.VerticalAlignment = VerticalAlignment.Top;
        framedCanvas.Margin = new Thickness(pinned.X, pinned.Y, 0, 0);
    }

    void MoveCanvas(Vector shift) =>
        framedCanvas.Margin = new Thickness(pinned.X + shift.X, pinned.Y + shift.Y, 0, 0);

    void UnpinCanvas()
    {
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

    void BuildMenus()
    {
        menu.Add("File", () =>
        [
            MenuEntry.Item("New capture", "Print", NewCapture),
            MenuEntry.Item("Open…", "Ctrl+O", OpenLibrary),
            MenuEntry.Separator,
            MenuEntry.Item("Save", "Ctrl+S", Save),
            MenuEntry.Item("Save as…", "Ctrl+Shift+S", () => _ = SaveAsAsync()),
            MenuEntry.Separator,
            MenuEntry.Item("Export PNG", "Ctrl+E", () => _ = ExportAsync("png")),
            MenuEntry.Item("Export JPEG…", "Ctrl+Shift+E", () => _ = ExportAsync("jpg")),
            MenuEntry.Item("Copy to clipboard", "Ctrl+C", CopyToClipboard),
            MenuEntry.Separator,
            MenuEntry.Item("Close", "Ctrl+W", Close)
        ]);

        menu.Add("Edit", () =>
        [
            MenuEntry.Item("Undo", "Ctrl+Z", Undo),
            MenuEntry.Item("Redo", "Ctrl+Shift+Z", Redo),
            MenuEntry.Separator,
            MenuEntry.Item("Delete", "Del", () => canvas.DeleteSelected()),
            MenuEntry.Item("Deselect", "Esc", () => canvas.Select(null)),
            MenuEntry.Separator,
            MenuEntry.Item("Resize canvas", "C", () => SetTool(EditorTool.Canvas)),
            MenuEntry.Item("Fit canvas to capture", null, FitCanvasToCapture),
            MenuEntry.Separator,
            MenuEntry.Item("Bring to front", "Ctrl+Shift+]", () => Arrange(Order.Front)),
            MenuEntry.Item("Bring forward", "Ctrl+]", () => Arrange(Order.Forward)),
            MenuEntry.Item("Send backward", "Ctrl+[", () => Arrange(Order.Backward)),
            MenuEntry.Item("Send to back", "Ctrl+Shift+[", () => Arrange(Order.Back))
        ]);

        menu.Add("Draw", () =>
        [
            MenuEntry.Item("Select", "V", () => SetTool(EditorTool.Select)),
            MenuEntry.Item("Arrow", "A", () => SetTool(EditorTool.Arrow)),
            MenuEntry.Item("Box", "B", () => SetTool(EditorTool.Box)),
            MenuEntry.Item("Blur", "L", () => SetTool(EditorTool.Blur)),
            MenuEntry.Item("Text", "T", () => SetTool(EditorTool.Text)),
            MenuEntry.Item("Numbered marker", "N", () => SetTool(EditorTool.Step)),
            MenuEntry.Separator,
            MenuEntry.Item("New line in text", "Shift+Enter", () => { }),
            MenuEntry.Item("Finish text", "Enter", () => canvas.CommitEdit())
        ]);

        menu.Add("View", () =>
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

    void WireBand()
    {
        band.ToolChosen += SetTool;
        band.UndoRequested += Undo;
        band.RedoRequested += Redo;

        band.StyleChosen += ApplyStyle;

        band.ColourChosen += colour =>
        {
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
            canvas.Defaults.ArrowDoubleHeaded = doubled;
            Apply<ArrowAnnotation>("head", arrow => arrow.DoubleHeaded = doubled);
        };

        band.FillChosen += filled =>
        {
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
            canvas.Defaults.BoxFillColor = fill;
            Apply<BoxAnnotation>("fill-colour", box => box.FillColor = fill);
        };

        band.BlurChosen += strength =>
        {
            canvas.Defaults.BlurStrength = strength;
            Apply<BlurAnnotation>("strength", blur => blur.Strength = strength);
        };

        band.TextSizeChosen += size =>
        {
            canvas.Defaults.TextSize = size;
            Apply<TextAnnotation>("size", text => text.FontSize = size);
        };

        band.TextBackChosen += backed =>
        {
            canvas.Defaults.TextBackgrounded = backed;

            Apply<TextAnnotation>("background", text => text.Background = backed
                ? (text.HasBackground ? text.Background : canvas.Defaults.TextBackgroundColor)
                : string.Empty);

            UpdateChrome();
        };

        band.TextBackColourChosen += background =>
        {
            canvas.Defaults.TextBackgroundColor = background;
            Apply<TextAnnotation>("background-colour", text => text.Background = background);
        };

        band.StepNumberChosen += number => Apply<StepAnnotation>("number", step => step.Number = number);

        band.StepSizeChosen += diameter =>
        {
            canvas.Defaults.StepDiameter = diameter;
            Apply<StepAnnotation>("size", step => step.Diameter = diameter);
        };

        // The canvas keeps its top-left corner when it is given a size outright. A typed width says
        // how wide, not which way to grow, and growing from the corner already on screen is the
        // answer that needs no explaining. Nothing is applied yet: the fields propose, exactly as
        // dragging an edge does, and the resize is confirmed as a whole.
        band.CanvasWidthChosen += width => canvas.ProposeCanvasSize(width, null);
        band.CanvasHeightChosen += height => canvas.ProposeCanvasSize(null, height);
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
    EditorTool BandTarget() => canvas.Selected switch
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
        if (from.Count == 0)
        {
            return;
        }

        to.Push(snapshot.Document.Copy());

        var previous = from.Pop();
        snapshot.Document.Layers.Clear();
        snapshot.Document.Layers.AddRange(previous.Layers);

        // The canvas is part of the document too. Restoring only the layers would undo a crop by
        // leaving the crop in place.
        snapshot.Document.Canvas = previous.Canvas;

        lastBandEdit = null;
        dirty = true;

        canvas.Select(null);
        canvas.CanvasResized();
        UpdateChrome();
    }

    void Save()
    {
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
        if (string.Equals(path, snapshot.Path, StringComparison.Ordinal))
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

        snapshot = next;
        blurs = new BlurCache(next.OriginalPng);
        canvas = new CanvasView(next, blurs) { Zoom = canvas.Zoom };
        WireCanvas();
        framedCanvas = ShowCanvas();

        // Both hold full-resolution bitmaps in native memory the collector cannot see, so leaving
        // them to finalisers would let every click in the strip stack another capture in memory.
        previousBlurs.Dispose();
        previousSnapshot.Dispose();

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
        recent.Show(SnapshotItem.Build(entries, thumbnails, thumbnailWork.Token), snapshot.Path);
    }

    /// <summary>Points the band, the menu bar and the status line at whatever is true now.</summary>
    void UpdateChrome()
    {
        band.Sync(tool, canvas.Defaults, canvas.Selected);
        band.ShowZoom(canvas.EffectiveScale);
        menu.Show(Path.GetFileName(snapshot.Path), dirty);

        // The canvas being proposed while one is being resized, and the document's own otherwise:
        // the readouts follow what is on screen, which is what the user is working on.
        var size = canvas.ShownCanvas;
        band.ShowCanvasSize((int)size.Width, (int)size.Height);

        var selection = canvas.Selected switch
        {
            ArrowAnnotation => "arrow selected",
            BoxAnnotation => "box selected",
            BlurAnnotation => "blur selected",
            TextAnnotation => "text selected",
            StepAnnotation => "marker selected",
            _ => "nothing selected"
        };

        // The capture's own size is worth saying only once the canvas has stopped matching it,
        // which is exactly when "1920 × 1080" on its own would be ambiguous.
        var capture = snapshot.Bitmap.PixelSize;
        var dimensions = size == new Rect(0, 0, capture.Width, capture.Height)
            ? $"{size.Width} × {size.Height}"
            : $"{size.Width} × {size.Height} canvas on a {capture.Width} × {capture.Height} capture";

        status.Text = $"{dimensions}   ·   {snapshot.Document.Layers.Count} object(s)   ·   {selection}";
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
        if (e.Key == Key.Space)
        {
            canvas.Panning = false;
        }
    }

    void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var control = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

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
        }
    }
}
