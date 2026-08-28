using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace SnapShotKit.Editor;

public enum EditorTool
{
    Select,
    Arrow,
    Box,
    Blur,
    Text,
    Step,

    /// <summary>Resizes the canvas rather than anything drawn on it.</summary>
    Canvas
}

enum DragKind
{
    None,
    Create,
    Move,
    ArrowFrom,
    ArrowTo,
    RectTopLeft,
    RectTopRight,
    RectBottomLeft,
    RectBottomRight,
    RectTop,
    RectBottom,
    RectLeft,
    RectRight
}

/// <summary>Style for annotations not yet drawn. Whatever is selected overrides these while it is selected.</summary>
public sealed class ToolDefaults
{
    public string ArrowColor { get; set; } = SnapShotKit.Ui.Tokens.AnnotationDefault;
    public double ArrowThickness { get; set; } = 4;
    public bool ArrowDoubleHeaded { get; set; }

    public string BoxBorderColor { get; set; } = SnapShotKit.Ui.Tokens.AnnotationDefault;
    public double BoxBorderThickness { get; set; } = 4;

    /// <summary>Whether a new box is filled at all, kept apart from the colour so switching the fill off and on again remembers it.</summary>
    public bool BoxFilled { get; set; }

    /// <summary>
    /// The colour a filled box takes. Near-black by default: the usual reason to fill a box on a
    /// screenshot is to cover something up, and the border stays whatever colour it was.
    /// </summary>
    public string BoxFillColor { get; set; } = "#2B2B2D";

    public int BlurStrength { get; set; } = 35;

    public double StepDiameter { get; set; } = 36;
    public string StepColor { get; set; } = SnapShotKit.Ui.Tokens.AnnotationDefault;

    /// <summary>Whether new text sits on a plate, kept apart from the colour so turning it off and on again remembers it.</summary>
    public bool TextBackgrounded { get; set; }

    public string TextBackgroundColor { get; set; } = "#1D2D3D";

    public string TextColor { get; set; } = SnapShotKit.Ui.Tokens.AnnotationDefault;
    public string TextFont { get; set; } = "Barlow, sans-serif";
    public double TextSize { get; set; } = 22;
}

/// <summary>
/// The editing surface. Draws the snapshot through <see cref="SnapshotRenderer"/> and adds only the
/// chrome that belongs to editing: selection outlines and handles.
///
/// All annotation geometry is kept in image pixels. Handles are sized in view pixels so they stay
/// grabbable at any zoom.
/// </summary>
public sealed class CanvasView : Decorator
{
    // Two-toned: a dark line under a light one. A single hairline is invisible against whatever
    // the screenshot happens to contain, and a screenshot can contain anything.
    static readonly IPen SelectionShadow = new Pen(new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), 3);

    static readonly IPen SelectionPen = new Pen(new SolidColorBrush(Color.FromArgb(235, 255, 255, 255)), 1)
    {
        DashStyle = new DashStyle([4, 3], 0)
    };

    /// <summary>The plate behind text being typed, at the light end. Chosen against the text's own colour.</summary>
    static readonly IBrush LightPlate = new SolidColorBrush(SnapShotKit.Ui.Tokens.Bg, 0.93);

    static readonly IBrush DarkPlate = new SolidColorBrush(SnapShotKit.Ui.Tokens.Accent900, 0.93);

    static readonly IBrush HandleFill = SnapShotKit.Ui.Tokens.BgBrush;
    static readonly IPen HandleBorder = new Pen(SnapShotKit.Ui.Tokens.Accent700Brush, 1);

    /// <summary>One square of the chequerboard, in view pixels. Chrome, so it does not scale with the picture.</summary>
    const double ChequerCell = 8;

    /// <summary>
    /// The chequerboard that shows through wherever the canvas covers no capture.
    ///
    /// A tiled brush rather than a loop of squares: the control draws all of itself whatever is on
    /// screen, so at 200% on a wide capture a loop would be hundreds of thousands of rectangles on
    /// every repaint, for a pattern that means "nothing here".
    /// </summary>
    static readonly IBrush Chequerboard = BuildChequerboard();

    const double HandleSize = 8;
    const double HandleReach = 8;

    /// <summary>How far outside the object the dashed outline sits, so it never traces over the object's own stroke.</summary>
    const double SelectionOffset = 5;

    /// <summary>Explicit zoom never goes past this. Beyond it you are looking at magnified pixels rather than the picture.</summary>
    const double MaxZoom = 2;

    readonly Snapshot snapshot;
    readonly BlurCache blurs;

    /// <summary>Holds the in-place text editor, positioned over the annotation being typed.</summary>
    readonly Canvas editingLayer = new();

    TextBox? editor;
    TextAnnotation? editing;
    string? textBeforeEdit;
    bool editingIsNew;

    DragKind dragging;
    Point dragOrigin;
    Annotation? dragBaseline;

    /// <summary>
    /// Pointer minus the handle's anchor at the moment of the press, in image pixels. Without it,
    /// grabbing a handle a few pixels off centre yanks the endpoint to the pointer, and the shape
    /// jumps before it moves. The overlay corrects for this; the editor gets the same treatment.
    /// </summary>
    Vector grabOffset;

    /// <summary>
    /// Set when a drag has begun but nothing has moved yet. The undo step is recorded on the first
    /// actual movement rather than on the press, so clicking an annotation to select it does not
    /// leave a do-nothing entry on the undo stack.
    /// </summary>
    bool undoPending;

    /// <summary>
    /// Which edge of the canvas is being dragged, or None.
    ///
    /// Kept apart from <see cref="dragging"/> rather than folded into it. The canvas and an
    /// annotation are never resized at the same time, and they behave differently at the limit: a
    /// rectangle dragged through itself flips, while a canvas dragged through itself stops.
    /// </summary>
    DragKind canvasGrip;

    /// <summary>The canvas as it was when the drag began, in image pixels.</summary>
    Rect canvasBaseline;

    /// <summary>
    /// The scale held still for the duration of a canvas drag, or zero when there is none.
    ///
    /// A fitted canvas that refits as it grows shrinks the picture under the pointer that is
    /// sizing it, and the drag then chases its own tail.
    /// </summary>
    double pinnedScale;

    public CanvasView(Snapshot snapshot, BlurCache blurs)
    {
        this.snapshot = snapshot;
        this.blurs = blurs;
        Focusable = true;
        ClipToBounds = true;

        Child = editingLayer;
    }

    /// <summary>
    /// The active tool.
    ///
    /// Changing it remeasures, because the canvas tool asks for room around the picture to drag an
    /// edge out into, which fitting has to allow for.
    /// </summary>
    public EditorTool Tool
    {
        get;
        set
        {
            if (field == value)
            {
                return;
            }

            field = value;
            InvalidateMeasure();
            InvalidateVisual();
        }
    } = EditorTool.Select;

    public Annotation? Selected { get; private set; }

    public ToolDefaults Defaults { get; } = new();

    public event Action? SelectionChanged;

    /// <summary>Raised before a mutation begins, so the caller can record an undo step.</summary>
    public event Action? BeforeChange;

    /// <summary>Raised when a mutation announced by <see cref="BeforeChange"/> came to nothing, so the recorded undo step can be taken back.</summary>
    public event Action? Abandoned;

    public event Action? Changed;

    /// <summary>Raised when a canvas drag begins, so the window can hold the picture still while it lasts.</summary>
    public event Action? CanvasResizeStarted;

    /// <summary>How far the canvas's top-left corner has moved since the drag began, in view pixels.</summary>
    public event Action<Vector>? CanvasResizeMoved;

    public event Action? CanvasResizeEnded;

    /// <summary>
    /// The canvas rectangle changed from outside, through the band or a menu.
    ///
    /// It decides the control's own size as well as what is drawn in it, so both have to be worked
    /// out again; a repaint on its own would draw the new canvas at the old size.
    /// </summary>
    public void CanvasResized()
    {
        InvalidateMeasure();
        InvalidateVisual();
    }

    public void Select(Annotation? annotation)
    {
        Selected = annotation;
        SelectionChanged?.Invoke();
        InvalidateVisual();
    }

    public void DeleteSelected()
    {
        if (Selected is null)
        {
            return;
        }

        BeforeChange?.Invoke();
        snapshot.Document.Layers.Remove(Selected);
        Select(null);
        Changed?.Invoke();
    }

    /// <summary>Applies a change to the selection as one undoable step.</summary>
    public void Edit(Action change)
    {
        if (Selected is null)
        {
            return;
        }

        BeforeChange?.Invoke();
        change();
        Changed?.Invoke();
        InvalidateVisual();
    }

    /// <summary>
    /// The zoom level, or null to fit the space available.
    ///
    /// The canvas is a framed object sitting on a mat rather than a viewport that letterboxes, so
    /// it measures to the size of the picture it is showing and lets its parent centre it.
    /// </summary>
    public double? Zoom
    {
        get;
        set
        {
            field = value;
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    /// <summary>The scale actually in use, whether that was asked for or worked out to fit.</summary>
    public double EffectiveScale { get; private set; } = 1;

    /// <summary>Raised when the scale changes, so the band's readout can follow it.</summary>
    public event Action? ZoomChanged;

    protected override Size MeasureOverride(Size availableSize)
    {
        var canvas = snapshot.Document.Canvas;
        if (canvas.Width == 0 || canvas.Height == 0)
        {
            return default;
        }

        double scale;

        if (pinnedScale > 0)
        {
            // Held still for the length of a canvas drag. See the field.
            scale = pinnedScale;
        }
        else if (Zoom is { } requested)
        {
            scale = Math.Clamp(requested, 0.05, MaxZoom);
        }
        else
        {
            // Fitting relies on the scroll viewer having its bars turned off while in this mode, so
            // the space offered here is the viewport rather than the infinity a scrollable
            // direction would report.
            var room = Math.Min(availableSize.Width / canvas.Width, availableSize.Height / canvas.Height);

            // Fitting never enlarges. A small capture blown up to fill the window is a wall of fat
            // pixels, and the honest thing is to show it at its own size with the mat around it.
            scale = double.IsFinite(room) ? Math.Min(room, 1) : 1;

            // The canvas tool needs somewhere to drag an edge out to. A canvas fitted exactly sits
            // flush against the room available, where growing it is a drag into nothing, so this
            // mode keeps a margin back, though only when the picture is filling the space, since
            // otherwise there is already room to spare.
            if (Tool == EditorTool.Canvas && double.IsFinite(room))
            {
                scale = Math.Min(scale, room * CanvasToolSlack);
            }
        }

        if (Math.Abs(scale - EffectiveScale) > 0.0001)
        {
            EffectiveScale = scale;
            ZoomChanged?.Invoke();
        }

        var size = new Size(canvas.Width * scale, canvas.Height * scale);

        // The canvas measures to the picture, but the editing layer still has to be measured or
        // the editor is never given a size and stays invisible.
        Child?.Measure(size);

        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        // The editing layer covers the whole picture; the editor inside it is placed by coordinate.
        Child?.Arrange(new Rect(finalSize));
        return finalSize;
    }

    // The control is exactly the canvas, so the canvas is all of it. The capture may sit anywhere
    // within that, or hang off any side of it.
    Rect Target() => new(0, 0, Bounds.Width, Bounds.Height);

    double Scale => snapshot.Document.Canvas.Width == 0 ? 1 : Bounds.Width / snapshot.Document.Canvas.Width;

    /// <summary>The canvas in image pixels, which is what a canvas drag works on.</summary>
    Rect CanvasRect()
    {
        var canvas = snapshot.Document.Canvas;
        return new Rect(canvas.X, canvas.Y, canvas.Width, canvas.Height);
    }

    /// <summary>
    /// Where the capture's top-left corner falls on the control.
    ///
    /// Image coordinates are measured from the capture rather than from the canvas, so this is the
    /// zero of everything drawn on the picture. Taken from the renderer so the two cannot disagree:
    /// if they did, every click would land somewhere other than where it looks.
    /// </summary>
    Point Origin() => SnapshotRenderer.Origin(snapshot.Document.Canvas, Target(), Scale);

    Point ToImage(Point view)
    {
        var origin = Origin();
        var scale = Scale;
        return new Point((view.X - origin.X) / scale, (view.Y - origin.Y) / scale);
    }

    Point ToView(double x, double y)
    {
        var origin = Origin();
        var scale = Scale;
        return new Point(origin.X + x * scale, origin.Y + y * scale);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        // Clicks inside the editor belong to the editor: it is a child of this control, so its
        // presses bubble up here and would otherwise be read as drawing on the canvas.
        if (editor is not null && e.Source is Visual source && editor.IsVisualAncestorOf(source))
        {
            return;
        }

        // Anywhere else ends the edit before it does anything, so a click both commits and lands.
        CommitEdit();

        Focus();

        var view = e.GetPosition(this);
        var image = ToImage(view);
        dragOrigin = image;

        // The canvas tool works on the canvas and on nothing else. Its grips sit on the boundary,
        // where an annotation's own would be indistinguishable from them, and the objects on the
        // picture are not what is being resized.
        if (Tool == EditorTool.Canvas)
        {
            BeginCanvasResize(view, image, e);
            return;
        }

        // Text is opened for editing by double clicking it, whichever tool happens to be active.
        if (e.ClickCount >= 2 && HitTest(image) is TextAnnotation existing)
        {
            Select(existing);
            BeginEdit(existing, isNew: false);
            return;
        }

        // Handles of the current selection win over everything, so a handle sitting over another
        // annotation stays usable.
        var grip = HitHandle(view);
        if (grip != DragKind.None)
        {
            undoPending = true;
            dragging = grip;
            dragBaseline = Selected!.Copy();
            grabOffset = image - AnchorOf(grip);
            e.Pointer.Capture(this);
            return;
        }

        // Anything already drawn can be picked up whatever tool is active. Having to switch to
        // Select before touching an existing arrow is the kind of friction that makes an editor feel
        // stiff, and the drawing tools lose nothing by it: a new annotation starts from empty
        // canvas, which is where you would start one anyway.
        if (HitTest(image) is { } hit)
        {
            Select(hit);

            undoPending = true;
            dragging = DragKind.Move;
            dragBaseline = hit.Copy();
            grabOffset = default;
            e.Pointer.Capture(this);
            return;
        }

        if (Tool == EditorTool.Select)
        {
            Select(null);
            return;
        }

        BeforeChange?.Invoke();

        var created = Create(image);
        snapshot.Document.Layers.Add(created);
        Selected = created;

        // Text has no size to drag out: it is placed, then typed. Everything else is dragged into
        // being, so the press begins a resize from its own origin.
        dragging = created is TextAnnotation or StepAnnotation ? DragKind.None : DragKind.Create;
        dragBaseline = created.Copy();
        grabOffset = default;

        SelectionChanged?.Invoke();

        if (created is StepAnnotation)
        {
            // A marker is complete the moment it is placed; there is nothing to type or drag out.
            Changed?.Invoke();
        }

        if (created is TextAnnotation placed)
        {
            // Placing text is only half of it: the words are what make it an annotation, so the
            // editor opens straight away rather than leaving an empty mark on the picture.
            BeginEdit(placed, isNew: true);
        }
        else if (dragging != DragKind.None)
        {
            e.Pointer.Capture(this);
        }

        InvalidateVisual();
    }

    Annotation Create(Point image) => Tool switch
    {
        EditorTool.Arrow => new ArrowAnnotation
        {
            X1 = image.X, Y1 = image.Y, X2 = image.X, Y2 = image.Y,
            Color = Defaults.ArrowColor, Thickness = Defaults.ArrowThickness,
            DoubleHeaded = Defaults.ArrowDoubleHeaded
        },

        EditorTool.Box => new BoxAnnotation
        {
            X = image.X, Y = image.Y,
            BorderColor = Defaults.BoxBorderColor,
            BorderThickness = Defaults.BoxBorderThickness,
            FillColor = Defaults.BoxFilled ? Defaults.BoxFillColor : string.Empty
        },

        EditorTool.Text => new TextAnnotation
        {
            X = image.X, Y = image.Y,
            // Empty rather than the class default, which is a placeholder for documents that omit
            // the field. Starting empty means backing out of a fresh text leaves nothing behind,
            // and the first keystroke is the first letter rather than a replacement.
            Text = string.Empty,
            Color = Defaults.TextColor, FontFamily = Defaults.TextFont, FontSize = Defaults.TextSize,
            Background = Defaults.TextBackgrounded ? Defaults.TextBackgroundColor : string.Empty
        },

        EditorTool.Step => new StepAnnotation
        {
            X = image.X, Y = image.Y,
            Number = NextStepNumber(),
            Diameter = Defaults.StepDiameter,
            Color = Defaults.StepColor
        },

        _ => new BlurAnnotation { X = image.X, Y = image.Y, Strength = Defaults.BlurStrength }
    };

    /// <summary>One above the highest marker on the picture, so a walkthrough numbers itself.</summary>
    int NextStepNumber() => snapshot.Document.Layers
        .OfType<StepAnnotation>()
        .Select(step => step.Number)
        .DefaultIfEmpty(0)
        .Max() + 1;

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (canvasGrip != DragKind.None)
        {
            ResizeCanvas(ToImage(e.GetPosition(this)) - grabOffset);
            return;
        }

        if (dragging == DragKind.None)
        {
            var over = e.GetPosition(this);

            if (Tool == EditorTool.Canvas)
            {
                Cursor = new Cursor(CursorFor(HitCanvasEdge(over)));
                return;
            }

            Cursor = new Cursor(
                HitHandle(over) != DragKind.None ? StandardCursorType.SizeAll
                : HitTest(ToImage(over)) is not null ? StandardCursorType.Hand
                : Tool == EditorTool.Select ? StandardCursorType.Arrow
                : StandardCursorType.Cross);

            return;
        }

        // The offset keeps the grabbed handle under the same spot of the pointer it was pressed
        // at; it is zero for moves and creates, where the raw position is the right one.
        var image = ToImage(e.GetPosition(this)) - grabOffset;
        var delta = image - dragOrigin;

        if (undoPending)
        {
            BeforeChange?.Invoke();
            undoPending = false;
        }

        switch (Selected)
        {
            case ArrowAnnotation arrow when dragBaseline is ArrowAnnotation baseline:
                Apply(arrow, baseline, delta, image);
                break;

            case RectAnnotation rect when dragBaseline is RectAnnotation baseline:
                Apply(rect, baseline, delta, image);
                break;

            case TextAnnotation text when dragBaseline is TextAnnotation baseline:
                text.X = baseline.X + delta.X;
                text.Y = baseline.Y + delta.Y;
                break;

            case StepAnnotation step when dragBaseline is StepAnnotation baseline:
                step.X = baseline.X + delta.X;
                step.Y = baseline.Y + delta.Y;
                break;
        }

        Changed?.Invoke();
        InvalidateVisual();
    }

    void Apply(ArrowAnnotation arrow, ArrowAnnotation baseline, Vector delta, Point image)
    {
        switch (dragging)
        {
            case DragKind.Create:
            case DragKind.ArrowTo:
                arrow.X2 = image.X;
                arrow.Y2 = image.Y;
                break;

            case DragKind.ArrowFrom:
                arrow.X1 = image.X;
                arrow.Y1 = image.Y;
                break;

            case DragKind.Move:
                arrow.X1 = baseline.X1 + delta.X;
                arrow.Y1 = baseline.Y1 + delta.Y;
                arrow.X2 = baseline.X2 + delta.X;
                arrow.Y2 = baseline.Y2 + delta.Y;
                break;
        }
    }

    void Apply(RectAnnotation rect, RectAnnotation baseline, Vector delta, Point image)
    {
        if (dragging == DragKind.Move)
        {
            rect.X = baseline.X + delta.X;
            rect.Y = baseline.Y + delta.Y;
            return;
        }

        // Each grip moves the edges it names and leaves the others where they were. A mid handle
        // names one, a corner names two, and creating names the bottom right of a rectangle grown
        // from where the press landed.
        var left = MovesLeft(dragging) ? image.X : baseline.X;
        var top = MovesTop(dragging) ? image.Y : baseline.Y;
        var right = MovesRight(dragging) ? image.X : baseline.X + baseline.Width;
        var bottom = MovesBottom(dragging) ? image.Y : baseline.Y + baseline.Height;

        // Dragged through itself, a rectangle turns inside out rather than stopping, which is what
        // lets a box be drawn in any direction.
        rect.X = Math.Min(left, right);
        rect.Y = Math.Min(top, bottom);
        rect.Width = Math.Abs(right - left);
        rect.Height = Math.Abs(bottom - top);
    }

    // Which edges a grip moves. Shared with the canvas, which is dragged by the same eight grips
    // and differs only in what it does at the limit.

    static bool MovesLeft(DragKind grip) =>
        grip is DragKind.RectLeft or DragKind.RectTopLeft or DragKind.RectBottomLeft;

    static bool MovesRight(DragKind grip) =>
        grip is DragKind.Create or DragKind.RectRight or DragKind.RectTopRight or DragKind.RectBottomRight;

    static bool MovesTop(DragKind grip) =>
        grip is DragKind.RectTop or DragKind.RectTopLeft or DragKind.RectTopRight;

    static bool MovesBottom(DragKind grip) =>
        grip is DragKind.Create or DragKind.RectBottom or DragKind.RectBottomLeft or DragKind.RectBottomRight;

    // ---- Resizing the canvas ----------------------------------------------------------------
    //
    // The canvas is the rectangle that gets exported, and it is not obliged to match the capture.
    // Pulling an edge in crops the picture; pushing one out adds space, and what it adds is
    // transparent. Neither touches a pixel of the capture or moves a single annotation: cropping is
    // geometry, so an edge pulled in can always be pulled back out again.

    /// <summary>Nothing smaller than this, in image pixels. A canvas of nothing is not a canvas.</summary>
    public const int MinimumCanvas = 16;

    /// <summary>How much of the space available the canvas tool keeps back to drag an edge out into.</summary>
    const double CanvasToolSlack = 0.88;

    /// <summary>How far in from the boundary counts as grabbing it.</summary>
    const double EdgeReach = 10;

    /// <summary>How far along the boundary from a corner still counts as the corner rather than the side.</summary>
    const double CornerReach = 24;

    void BeginCanvasResize(Point view, Point image, PointerPressedEventArgs e)
    {
        // Nothing on the picture is being worked on in this mode, and a selection outline left
        // over from the last one only adds handles that do nothing.
        Select(null);

        var grip = HitCanvasEdge(view);
        if (grip == DragKind.None)
        {
            return;
        }

        canvasGrip = grip;
        canvasBaseline = CanvasRect();
        grabOffset = image - AnchorOf(grip, canvasBaseline);

        // Frozen before the first move, so the picture cannot resize under the pointer that is
        // sizing it.
        pinnedScale = Scale;
        undoPending = true;

        e.Pointer.Capture(this);
        CanvasResizeStarted?.Invoke();
    }

    void ResizeCanvas(Point to)
    {
        if (undoPending)
        {
            BeforeChange?.Invoke();
            undoPending = false;
        }

        // Whole pixels, because that is what the canvas is measured in and what gets exported.
        var x = Math.Round(to.X);
        var y = Math.Round(to.Y);

        // Stopped at the minimum rather than turned inside out. A rectangle drawn backwards is a
        // rectangle; a canvas dragged past its far edge would swing the picture across the screen.
        var left = MovesLeft(canvasGrip) ? Math.Min(x, canvasBaseline.Right - MinimumCanvas) : canvasBaseline.X;
        var top = MovesTop(canvasGrip) ? Math.Min(y, canvasBaseline.Bottom - MinimumCanvas) : canvasBaseline.Y;
        var right = MovesRight(canvasGrip) ? Math.Max(x, canvasBaseline.X + MinimumCanvas) : canvasBaseline.Right;
        var bottom = MovesBottom(canvasGrip) ? Math.Max(y, canvasBaseline.Y + MinimumCanvas) : canvasBaseline.Bottom;

        var canvas = snapshot.Document.Canvas;
        canvas.X = (int)left;
        canvas.Y = (int)top;
        canvas.Width = (int)(right - left);
        canvas.Height = (int)(bottom - top);

        // How far the control's own top-left corner has to move to leave the capture exactly where
        // it is on screen. Without it the layout recentres the growing canvas and drags the picture
        // out from under the pointer.
        CanvasResizeMoved?.Invoke(new Vector(
            (canvas.X - canvasBaseline.X) * pinnedScale,
            (canvas.Y - canvasBaseline.Y) * pinnedScale));

        Changed?.Invoke();
        InvalidateMeasure();
        InvalidateVisual();
    }

    /// <summary>
    /// Ends a canvas drag and hands the layout back.
    ///
    /// Called from both the release and the loss of capture, and safe either way round: capture is
    /// released on the way out of a drag, and losing it to a window switch or a cancelled touch has
    /// to end the drag too. A drag left running would hold the picture pinned where it was and
    /// resize the canvas on the next movement of the pointer, with no button held at all.
    /// </summary>
    void EndCanvasResize()
    {
        if (canvasGrip == DragKind.None)
        {
            return;
        }

        // Nothing moved means nothing happened: pressing on the boundary and letting go must not
        // mark the document as changed, exactly as pressing on an annotation does not.
        var resized = !undoPending;

        canvasGrip = DragKind.None;
        undoPending = false;

        // The picture goes back to being laid out normally, which settles it into the middle of its
        // mat and, when fitting, into the whole of the space now available.
        pinnedScale = 0;
        CanvasResizeEnded?.Invoke();

        InvalidateMeasure();
        InvalidateVisual();

        if (resized)
        {
            Changed?.Invoke();
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        EndCanvasResize();
        base.OnPointerCaptureLost(e);
    }

    /// <summary>
    /// Which part of the canvas boundary the pointer is on, if any.
    ///
    /// The whole of a side is grabbable rather than only a handle in the middle of it: an edge is
    /// what the eye sees and what the hand goes for. The corners take a longer stretch of both
    /// their sides, so the one place two grips meet is not a pixel hunt.
    /// </summary>
    DragKind HitCanvasEdge(Point view)
    {
        var onEdge = view.X <= EdgeReach || view.X >= Bounds.Width - EdgeReach
            || view.Y <= EdgeReach || view.Y >= Bounds.Height - EdgeReach;

        if (!onEdge)
        {
            return DragKind.None;
        }

        var horizontal = view.X <= CornerReach ? -1 : view.X >= Bounds.Width - CornerReach ? 1 : 0;
        var vertical = view.Y <= CornerReach ? -1 : view.Y >= Bounds.Height - CornerReach ? 1 : 0;

        return (horizontal, vertical) switch
        {
            (-1, -1) => DragKind.RectTopLeft,
            (1, -1) => DragKind.RectTopRight,
            (-1, 1) => DragKind.RectBottomLeft,
            (1, 1) => DragKind.RectBottomRight,
            (-1, 0) => DragKind.RectLeft,
            (1, 0) => DragKind.RectRight,
            (0, -1) => DragKind.RectTop,
            (0, 1) => DragKind.RectBottom,
            _ => DragKind.None
        };
    }

    static StandardCursorType CursorFor(DragKind grip) => grip switch
    {
        DragKind.RectLeft or DragKind.RectRight => StandardCursorType.SizeWestEast,
        DragKind.RectTop or DragKind.RectBottom => StandardCursorType.SizeNorthSouth,
        DragKind.RectTopLeft => StandardCursorType.TopLeftCorner,
        DragKind.RectTopRight => StandardCursorType.TopRightCorner,
        DragKind.RectBottomLeft => StandardCursorType.BottomLeftCorner,
        DragKind.RectBottomRight => StandardCursorType.BottomRightCorner,
        _ => StandardCursorType.Arrow
    };

    /// <summary>Whether the canvas reaches past the capture on any side, and so has transparency to show.</summary>
    bool HasTransparency()
    {
        var canvas = snapshot.Document.Canvas;
        var image = snapshot.Bitmap.PixelSize;

        return canvas.X < 0 || canvas.Y < 0
            || canvas.X + canvas.Width > image.Width
            || canvas.Y + canvas.Height > image.Height;
    }

    static IBrush BuildChequerboard()
    {
        const double tile = 2 * ChequerCell;

        var group = new DrawingGroup
        {
            Children =
            {
                new GeometryDrawing
                {
                    Brush = SnapShotKit.Ui.Tokens.Neutral100Brush,
                    Geometry = new RectangleGeometry(new Rect(0, 0, tile, tile))
                },
                new GeometryDrawing
                {
                    Brush = SnapShotKit.Ui.Tokens.Neutral300Brush,
                    Geometry = new RectangleGeometry(new Rect(0, 0, ChequerCell, ChequerCell))
                },
                new GeometryDrawing
                {
                    Brush = SnapShotKit.Ui.Tokens.Neutral300Brush,
                    Geometry = new RectangleGeometry(new Rect(ChequerCell, ChequerCell, ChequerCell, ChequerCell))
                }
            }
        };

        return new DrawingBrush
        {
            Drawing = group,
            TileMode = TileMode.Tile,
            Stretch = Stretch.Fill,
            SourceRect = new RelativeRect(0, 0, tile, tile, RelativeUnit.Absolute),
            DestinationRect = new RelativeRect(0, 0, tile, tile, RelativeUnit.Absolute)
        };
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (canvasGrip != DragKind.None)
        {
            // Letting the capture go is itself reported as capture lost, which is where the drag
            // ends; the call after it is then the no-op that makes the order not matter.
            e.Pointer.Capture(null);
            EndCanvasResize();
            return;
        }

        e.Pointer.Capture(null);

        // A click with a drawing tool leaves a zero-sized annotation behind, which would be
        // invisible and unselectable. Drop it rather than littering the document, and take back
        // the undo step the press recorded, since the document ends up exactly as it was.
        var abandoned = dragging == DragKind.Create && Selected is { } created && IsDegenerate(created);
        if (abandoned)
        {
            snapshot.Document.Layers.Remove(Selected!);
            Select(null);
            Abandoned?.Invoke();
        }

        // Only a completed drag changed anything. A bare click, or releasing right after selecting,
        // must not mark the document as having unsaved changes.
        var changed = dragging != DragKind.None && !undoPending && !abandoned;

        dragging = DragKind.None;
        dragBaseline = null;
        undoPending = false;

        if (changed)
        {
            Changed?.Invoke();
        }

        InvalidateVisual();
    }

    static bool IsDegenerate(Annotation annotation) => annotation switch
    {
        ArrowAnnotation arrow => Math.Abs(arrow.X2 - arrow.X1) < 4 && Math.Abs(arrow.Y2 - arrow.Y1) < 4,
        RectAnnotation rect => rect.Width < 4 || rect.Height < 4,
        _ => false
    };

    Rect BoundsOf(TextAnnotation text)
    {
        var measured = SnapshotRenderer.Format(text, 1);
        return new Rect(text.X, text.Y, measured.Width, measured.Height);
    }

    Annotation? HitTest(Point image)
    {
        // Topmost first, matching what the eye picks.
        for (var i = snapshot.Document.Layers.Count - 1; i >= 0; i--)
        {
            var annotation = snapshot.Document.Layers[i];

            var hit = annotation switch
            {
                // A box without a fill is a border around something the user still wants to work
                // on. Treating its whole interior as the box would make everything inside it
                // unreachable, so only the border itself is hit.
                BoxAnnotation box when !box.HasFill => OnBoxBorder(box, image),
                RectAnnotation rect => image.X >= rect.X && image.X <= rect.X + rect.Width
                    && image.Y >= rect.Y && image.Y <= rect.Y + rect.Height,
                TextAnnotation text => BoundsOf(text).Contains(image),
                StepAnnotation step => Distance(image, new Point(step.X, step.Y)) <= step.Radius,
                ArrowAnnotation arrow => DistanceToSegment(image,
                    new Point(arrow.X1, arrow.Y1), new Point(arrow.X2, arrow.Y2)) <= Math.Max(arrow.Thickness, 10),
                _ => false
            };

            if (hit)
            {
                return annotation;
            }
        }

        return null;
    }

    /// <summary>The image-space point a handle controls, which is what the grab offset is measured against.</summary>
    Point AnchorOf(DragKind grip) => (Selected, grip) switch
    {
        (ArrowAnnotation arrow, DragKind.ArrowFrom) => new Point(arrow.X1, arrow.Y1),
        (ArrowAnnotation arrow, DragKind.ArrowTo) => new Point(arrow.X2, arrow.Y2),
        (RectAnnotation rect, _) => AnchorOf(grip, new Rect(rect.X, rect.Y, rect.Width, rect.Height)),
        _ => default
    };

    /// <summary>The same, for any rectangle. The canvas is dragged by the same grips as a box is.</summary>
    static Point AnchorOf(DragKind grip, Rect rect) => grip switch
    {
        DragKind.RectTopLeft => rect.TopLeft,
        DragKind.RectTopRight => rect.TopRight,
        DragKind.RectBottomLeft => rect.BottomLeft,
        DragKind.RectBottomRight => rect.BottomRight,
        DragKind.RectTop => new Point(rect.Center.X, rect.Y),
        DragKind.RectBottom => new Point(rect.Center.X, rect.Bottom),
        DragKind.RectLeft => new Point(rect.X, rect.Center.Y),
        DragKind.RectRight => new Point(rect.Right, rect.Center.Y),
        _ => default
    };

    /// <summary>Whether the point sits on the border band of an unfilled box, with a little slack so a thin border stays grabbable.</summary>
    bool OnBoxBorder(BoxAnnotation box, Point image)
    {
        var reach = Math.Max(box.BorderThickness, 8 / Scale);

        var outer = image.X >= box.X - reach && image.X <= box.X + box.Width + reach
            && image.Y >= box.Y - reach && image.Y <= box.Y + box.Height + reach;

        var inner = image.X >= box.X + reach && image.X <= box.X + box.Width - reach
            && image.Y >= box.Y + reach && image.Y <= box.Y + box.Height - reach;

        return outer && !inner;
    }

    DragKind HitHandle(Point view)
    {
        foreach (var (kind, point) in Handles())
        {
            if (Math.Abs(view.X - point.X) <= HandleReach && Math.Abs(view.Y - point.Y) <= HandleReach)
            {
                return kind;
            }
        }

        return DragKind.None;
    }

    IEnumerable<(DragKind Kind, Point Point)> Handles()
    {
        switch (Selected)
        {
            case ArrowAnnotation arrow:
                yield return (DragKind.ArrowFrom, ToView(arrow.X1, arrow.Y1));
                yield return (DragKind.ArrowTo, ToView(arrow.X2, arrow.Y2));
                break;

            case RectAnnotation rect:
                var midX = rect.X + rect.Width / 2;
                var midY = rect.Y + rect.Height / 2;
                var right = rect.X + rect.Width;
                var bottom = rect.Y + rect.Height;

                // Eight rather than the design's six. The two extra handles resize width alone,
                // which was asked for by name and is genuinely useful; the design's six leave no
                // way to change one horizontal edge without also moving a corner.
                yield return (DragKind.RectTopLeft, ToView(rect.X, rect.Y));
                yield return (DragKind.RectTop, ToView(midX, rect.Y));
                yield return (DragKind.RectTopRight, ToView(right, rect.Y));
                yield return (DragKind.RectRight, ToView(right, midY));
                yield return (DragKind.RectBottomRight, ToView(right, bottom));
                yield return (DragKind.RectBottom, ToView(midX, bottom));
                yield return (DragKind.RectBottomLeft, ToView(rect.X, bottom));
                yield return (DragKind.RectLeft, ToView(rect.X, midY));
                break;

            // Text has no handles. Its size is its font size, which belongs in the panel rather than
            // on a corner grip that would distort it.
        }
    }

    static double Distance(Point from, Point to)
    {
        var span = from - to;
        return Math.Sqrt(span.X * span.X + span.Y * span.Y);
    }

    static double DistanceToSegment(Point point, Point a, Point b)
    {
        var span = b - a;
        var lengthSquared = span.X * span.X + span.Y * span.Y;

        if (lengthSquared < 0.0001)
        {
            return Math.Sqrt((point - a).X * (point - a).X + (point - a).Y * (point - a).Y);
        }

        var t = Math.Clamp(((point - a).X * span.X + (point - a).Y * span.Y) / lengthSquared, 0, 1);
        var closest = new Point(a.X + span.X * t, a.Y + span.Y * t);
        var offset = point - closest;

        return Math.Sqrt(offset.X * offset.X + offset.Y * offset.Y);
    }

    public override void Render(DrawingContext context)
    {
        var target = Target();
        if (target.Width <= 0)
        {
            return;
        }

        // A chequerboard wherever the canvas reaches past the capture, which is the only backdrop
        // this control has: everywhere else the picture covers it, and the mat around it belongs to
        // the window. Editing chrome, like the outlines below it: an export paints nothing there,
        // which is what makes the transparency real rather than drawn.
        if (HasTransparency())
        {
            context.FillRectangle(Chequerboard, target);
        }

        SnapshotRenderer.Draw(context, snapshot, blurs, target, editing);

        // The selection outline sits outside the object rather than on it, so an object keeps its
        // own stroke visible while selected: tracing over a 2px red box with a dashed steel line
        // hides the very colour the user is about to change.
        // Nothing is outlined while it is being typed: the editor draws its own frame, and a second
        // one around the same words is just clutter.
        switch (editing is null ? Selected : null)
        {
            case RectAnnotation rect:
                DrawSelectionBox(context, Outline(new Rect(
                    ToView(rect.X, rect.Y), ToView(rect.X + rect.Width, rect.Y + rect.Height))));
                break;

            case ArrowAnnotation arrow:
                context.DrawLine(SelectionShadow, ToView(arrow.X1, arrow.Y1), ToView(arrow.X2, arrow.Y2));
                context.DrawLine(SelectionPen, ToView(arrow.X1, arrow.Y1), ToView(arrow.X2, arrow.Y2));
                break;

            case TextAnnotation text:
                var bounds = BoundsOf(text);
                DrawSelectionBox(context, Outline(new Rect(
                    ToView(bounds.X, bounds.Y), ToView(bounds.Right, bounds.Bottom))));
                break;

            case StepAnnotation step:
                DrawSelectionBox(context, Outline(new Rect(
                    ToView(step.X - step.Radius, step.Y - step.Radius),
                    ToView(step.X + step.Radius, step.Y + step.Radius))));
                break;
        }

        DrawEditingPlate(context);

        foreach (var (_, point) in Handles())
        {
            DrawHandle(context, point);
        }

        if (Tool == EditorTool.Canvas)
        {
            DrawCanvasGrips(context, target);
        }
    }

    /// <summary>
    /// The canvas boundary and its eight grips.
    ///
    /// Set in by half a handle rather than centred on the boundary. The control is clipped to the
    /// canvas, so a grip straddling the edge would be sliced down the middle, and a handle showing
    /// half of itself reads as a rendering fault rather than as something to grab.
    /// </summary>
    void DrawCanvasGrips(DrawingContext context, Rect target)
    {
        var inset = target.Deflate(HandleSize / 2);

        if (inset.Width <= 0 || inset.Height <= 0)
        {
            return;
        }

        DrawSelectionBox(context, inset);

        foreach (var grip in new[]
                 {
                     DragKind.RectTopLeft, DragKind.RectTop, DragKind.RectTopRight, DragKind.RectRight,
                     DragKind.RectBottomRight, DragKind.RectBottom, DragKind.RectBottomLeft, DragKind.RectLeft
                 })
        {
            DrawHandle(context, AnchorOf(grip, inset));
        }
    }

    static void DrawHandle(DrawingContext context, Point at)
    {
        var handle = new Rect(at.X - HandleSize / 2, at.Y - HandleSize / 2, HandleSize, HandleSize);
        context.FillRectangle(HandleFill, handle);
        context.DrawRectangle(HandleBorder, handle);
    }

    static Rect Outline(Rect around) => around.Inflate(SelectionOffset);

    static void DrawSelectionBox(DrawingContext context, Rect box)
    {
        context.DrawRectangle(null, SelectionShadow, box);
        context.DrawRectangle(null, SelectionPen, box);
    }

    /// <summary>
    /// The frame behind text as it is typed.
    ///
    /// A plate rather than an outline alone. Typing over a screenshot means typing over anything at
    /// all, and against a busy photograph neither a thin box nor a one pixel caret can be found. The
    /// plate is picked to contrast with the text's own colour, so light text gets a dark backing and
    /// dark text a light one, and both the words and the caret stay legible while they are being
    /// worked on. It is chrome: it goes the moment the edit is finished.
    /// </summary>
    void DrawEditingPlate(DrawingContext context)
    {
        if (editor is null || editing is null)
        {
            return;
        }

        var plate = new Rect(
            Canvas.GetLeft(editor),
            Canvas.GetTop(editor),
            Math.Max(editor.Bounds.Width, editor.MinWidth),
            editor.Bounds.Height).Inflate(EditingPadding);

        context.FillRectangle(OnLightPlate ? LightPlate : DarkPlate, plate);
        context.DrawRectangle(null, SelectionShadow, plate);
        context.DrawRectangle(null, SelectionPen, plate);
    }

    const double EditingPadding = 5;

    /// <summary>Whether the plate under the text being typed is the light one.</summary>
    bool OnLightPlate { get; set; } = true;

    /// <summary>
    /// Perceived brightness of a colour, on the usual weighting: the eye is far more sensitive to
    /// green than to blue, so a straight average would call yellow and blue equally bright.
    /// </summary>
    static double Brightness(Color colour) =>
        (0.299 * colour.R + 0.587 * colour.G + 0.114 * colour.B) / 255;

    /// <summary>True while text is being typed on the canvas.</summary>
    public bool IsEditingText => editor is not null;

    /// <summary>
    /// Opens the in-place editor over a text annotation.
    ///
    /// A real text box rather than a caret painted by hand. Editing text means selection, arrow
    /// keys, home and end, backspace across a line break, the clipboard and input methods for
    /// languages that need them; reimplementing that on a drawing surface produces a worse version
    /// of something the toolkit already has. It is positioned and styled to match what the
    /// renderer would draw, and the annotation underneath is left undrawn while it is open, so the
    /// words never appear twice.
    /// </summary>
    void BeginEdit(TextAnnotation text, bool isNew)
    {
        CommitEdit();

        editing = text;
        editingIsNew = isNew;
        textBeforeEdit = text.Text;

        if (!isNew)
        {
            BeforeChange?.Invoke();
        }

        editor = new TextBox
        {
            Theme = SnapShotKit.Ui.TextFields.Bare,
            Text = text.Text,
            AcceptsReturn = true,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            MinWidth = 24,
            MinHeight = 0,
        };

        StyleEditor();

        // Tunnelled, so the key is seen before the text box acts on it. With AcceptsReturn set,
        // the box treats Enter as a line break and marks it handled, which would leave a plain
        // Enter with no way to finish the edit.
        editor.AddHandler(InputElement.KeyDownEvent, OnEditorKey, RoutingStrategies.Tunnel);
        editor.LostFocus += (_, _) => CommitEdit();

        editor.PropertyChanged += (_, e) =>
        {
            // The plate is drawn around the editor's own box, so it has to be repainted whenever
            // that box changes size, not only when the words change.
            if (e.Property == BoundsProperty)
            {
                InvalidateVisual();
                return;
            }

            if (e.Property == TextBox.TextProperty && editing is not null)
            {
                // Kept in step as it is typed, so the selection outline and the hit area grow with
                // the words rather than snapping to size when the edit ends.
                editing.Text = editor?.Text ?? string.Empty;
                InvalidateVisual();
            }
        };

        editingLayer.Children.Add(editor);
        Place();

        editor.Focus();
        editor.SelectAll();

        InvalidateVisual();
    }

    /// <summary>Matches the editor to what the renderer would draw, so committing never moves the text.</summary>
    void StyleEditor()
    {
        if (editor is null || editing is null)
        {
            return;
        }

        editor.FontFamily = FontFamily.Parse(editing.FontFamily);
        editor.FontSize = Math.Max(editing.FontSize * Scale, 1);
        editor.Foreground = SnapshotRenderer.BrushFor(editing.Color);

        // The plate backs whichever way the text does not, and the caret and selection follow it,
        // so all three stay legible whatever colour the text is.
        OnLightPlate = Brightness(SnapshotRenderer.ParseColor(editing.Color)) < 0.6;

        editor.CaretBrush = OnLightPlate ? SnapShotKit.Ui.Tokens.Accent700Brush : SnapShotKit.Ui.Tokens.Accent200Brush;
        editor.SelectionBrush = OnLightPlate ? SnapShotKit.Ui.Tokens.Accent300Brush : SnapShotKit.Ui.Tokens.Accent700Brush;
        editor.SelectionForegroundBrush = OnLightPlate ? SnapShotKit.Ui.Tokens.Neutral900Brush : SnapShotKit.Ui.Tokens.BgBrush;
    }

    void Place()
    {
        if (editor is null || editing is null)
        {
            return;
        }

        var at = ToView(editing.X, editing.Y);

        Canvas.SetLeft(editor, at.X);
        Canvas.SetTop(editor, at.Y);
    }

    /// <summary>Re-reads the annotation's style, for when the band changes it mid-edit.</summary>
    public void RefreshEditing()
    {
        StyleEditor();
        Place();
    }

    void OnEditorKey(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            // Enter finishes. A new line is Shift and Enter, which is the convention anywhere a
            // single line is the common case and the field still accepts more.
            case Key.Enter when !e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                e.Handled = true;
                CommitEdit();
                Focus();
                break;

            // Escape backs out: the words go back to what they were, and text that never had any
            // is dropped. Enter is how an edit is kept.
            case Key.Escape:
                e.Handled = true;
                CloseEdit(keep: false);
                Focus();
                break;
        }
    }

    /// <summary>Closes the editor, keeping what was typed. Text left empty is dropped rather than left invisible.</summary>
    public void CommitEdit() => CloseEdit(keep: true);

    void CloseEdit(bool keep)
    {
        if (editor is null || editing is null)
        {
            return;
        }

        var annotation = editing;
        var typed = keep ? editor.Text ?? string.Empty : textBeforeEdit ?? string.Empty;
        var wasNew = editingIsNew;
        var before = textBeforeEdit;

        var closing = editor;
        editor = null;
        editing = null;

        closing.RemoveHandler(InputElement.KeyDownEvent, OnEditorKey);
        editingLayer.Children.Remove(closing);

        annotation.Text = typed;

        if (string.IsNullOrWhiteSpace(typed))
        {
            // Text with nothing in it is invisible and unselectable, which is a worse outcome than
            // never having placed it.
            snapshot.Document.Layers.Remove(annotation);
            Select(null);
            Abandoned?.Invoke();
        }
        else if (wasNew || !string.Equals(typed, before, StringComparison.Ordinal))
        {
            Changed?.Invoke();
        }
        else
        {
            // Opened and closed without a change: take back the undo step recorded on opening.
            Abandoned?.Invoke();
        }

        InvalidateVisual();
    }
}
