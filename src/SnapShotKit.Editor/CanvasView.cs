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
    Canvas,

    /// <summary>Takes a band out of the picture and closes the gap.</summary>
    Cut
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
    /// The colour a filled box takes. Black by default: the usual reason to fill a box on a
    /// screenshot is to cover something up, and the border stays whatever colour it was.
    /// </summary>
    public string BoxFillColor { get; set; } = "#000000";

    public int BlurStrength { get; set; } = 35;

    public double StepDiameter { get; set; } = 36;
    public string StepColor { get; set; } = SnapShotKit.Ui.Tokens.AnnotationDefault;

    /// <summary>Whether new text sits on a plate, kept apart from the colour so turning it off and on again remembers it.</summary>
    public bool TextBackgrounded { get; set; }

    public string TextBackgroundColor { get; set; } = "#000000";

    public string TextColor { get; set; } = SnapShotKit.Ui.Tokens.AnnotationDefault;
    public string TextFont { get; set; } = "Barlow, sans-serif";
    public double TextSize { get; set; } = 22;

    /// <summary>
    /// Which way a cut runs, or null to take it from the drag.
    ///
    /// Working it out from the drag is right almost always and useless for a band a few pixels
    /// across, where the answer changes with every twitch. Saying which is meant settles it.
    /// </summary>
    public CutAxis? CutDirection { get; set; }

    /// <summary>
    /// Takes on a ready-made look, so that the next annotation of that kind is drawn wearing it.
    ///
    /// A style is a complete look, so it sets everything it covers, including turning a fill or a
    /// plate off. The colour of one that has been turned off is kept, which is what makes turning
    /// it back on remember what it was.
    /// </summary>
    public void Adopt(Annotation style)
    {
        switch (style)
        {
            case ArrowAnnotation arrow:
                ArrowColor = arrow.Color;
                ArrowThickness = arrow.Thickness;
                ArrowDoubleHeaded = arrow.DoubleHeaded;
                break;

            case BoxAnnotation box:
                BoxBorderColor = box.BorderColor;
                BoxBorderThickness = box.BorderThickness;
                BoxFilled = box.HasFill;

                if (box.HasFill)
                {
                    BoxFillColor = box.FillColor;
                }

                break;

            case TextAnnotation text:
                TextColor = text.Color;
                TextSize = text.FontSize;
                TextBackgrounded = text.HasBackground;

                if (text.HasBackground)
                {
                    TextBackgroundColor = text.Background;
                }

                break;

            case StepAnnotation step:
                StepColor = step.Color;
                StepDiameter = step.Diameter;
                break;

            case BlurAnnotation blur:
                BlurStrength = blur.Strength;
                break;
        }
    }

    /// <summary>Whether the next annotation drawn would come out looking exactly like this style.</summary>
    public bool Wears(Annotation style) => style switch
    {
        ArrowAnnotation arrow => ArrowColor == arrow.Color
            && ArrowThickness == arrow.Thickness
            && ArrowDoubleHeaded == arrow.DoubleHeaded,

        BoxAnnotation box => BoxBorderColor == box.BorderColor
            && BoxBorderThickness == box.BorderThickness
            && (BoxFilled ? BoxFillColor : string.Empty) == box.FillColor,

        TextAnnotation text => TextColor == text.Color
            && TextSize == text.FontSize
            && (TextBackgrounded ? TextBackgroundColor : string.Empty) == text.Background,

        StepAnnotation step => StepColor == step.Color && StepDiameter == step.Diameter,

        BlurAnnotation blur => BlurStrength == blur.Strength,

        _ => false
    };
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

    /// <summary>What everything outside the canvas is covered with while the canvas is being resized.</summary>
    static readonly IBrush Scrim = new SolidColorBrush(Color.FromArgb(0x9E, 0x2B, 0x2B, 0x2D));

    // The canvas boundary while it is being resized, and the thirds inside it. Hairlines, unlike
    // the selection's heavier outline: what matters here is seeing the picture past the boundary,
    // and the dimmed surround already says which side of it is which.
    static readonly IPen BoundaryShadow = new Pen(new SolidColorBrush(Color.FromArgb(110, 0, 0, 0)), 1);

    static readonly IPen BoundaryPen = new Pen(new SolidColorBrush(Color.FromArgb(240, 255, 255, 255)), 1);

    static readonly IPen GuidePen = new Pen(new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)), 1);

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

    /// <summary>
    /// Explicit zoom never goes past this.
    ///
    /// Four hundred percent, because placing an arrow's tip or a blur's edge on a particular pixel
    /// of a screenshot is a real thing to want, and at anything less the pixel is smaller than the
    /// hand can aim at. Past it the screen is showing magnified pixels rather than the picture.
    /// </summary>
    const double MaxZoom = 4;

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

    /// <summary>The resize being negotiated, or null when the canvas tool is not in hand.</summary>
    CanvasResize? resizing;

    /// <summary>The band being dragged out, in capture pixels, or null when nothing is being cut.</summary>
    CutBand? cutting;

    /// <summary>Where the cut began, in capture pixels.</summary>
    Point cutFrom;

    bool cuttingDrag;

    /// <summary>Whether the picture is being dragged about, as opposed to merely being ready to be.</summary>
    bool grabbed;

    /// <summary>Where the pointer was, in the window's own coordinates, at the last step of a pan.</summary>
    Point panOrigin;

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

    /// <summary>The working surface as it was when the drag began, for reporting how far it has since moved.</summary>
    Rect frameBaseline;

    /// <summary>
    /// The scale held still for the length of a resize, or zero when none is under way.
    ///
    /// The working surface grows if the canvas is dragged past it, and a surface that refits as it
    /// grows shrinks the picture under the pointer that is sizing it.
    /// </summary>
    double sessionScale;

    /// <summary>
    /// Whether a picture is being dragged, and the canvas may be growing around it.
    ///
    /// Held apart from <see cref="dragging"/>, which says what the drag does to the picture, because
    /// this is about what it does to everything else: for as long as it lasts the scale is held and
    /// the window keeps the picture where it is on screen, the same way it does while an edge of
    /// the canvas is dragged.
    /// </summary>
    bool growing;

    /// <summary>The canvas as it was when the picture was picked up, which is what it grows from.</summary>
    Rect growFrom;

    /// <summary>
    /// The laid-out stretch the control was last arranged to show.
    ///
    /// A pointer position is measured against the control as it was last laid out, and while a
    /// picture drags the canvas out the document is always one layout pass ahead of that. Mapped
    /// through the canvas the document has now, a single movement lands a whole growth step away
    /// from where the pointer is, and the picture jumps.
    /// </summary>
    Rect arranged;

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
    /// The canvas tool is a mode rather than a way of drawing: picking it opens a resize, and
    /// leaving it abandons one that was never applied.
    ///
    /// Neither it nor the cut tool works on anything standing on the picture, so both drop the
    /// selection as they are picked up.
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

            var previous = field;
            field = value;

            if (previous == EditorTool.Canvas)
            {
                // Nothing has been applied to the document yet, and a crop left half negotiated
                // must not be applied behind the user's back.
                CloseResize();
            }

            if (value == EditorTool.Canvas)
            {
                OpenResize();
            }

            if (value == EditorTool.Cut)
            {
                // For the same reason the canvas tool does it, and with the same consequences if it
                // does not. A selection left standing keeps the band pointed at that object's own
                // settings, so the one thing this tool has to set is nowhere to be found; and since
                // nothing on the picture is outlined while a band is being marked, what is left is
                // an invisible selection that Delete still deletes.
                CommitEdit();
                Select(null);
            }

            InvalidateMeasure();
            InvalidateVisual();
        }
    } = EditorTool.Select;

    /// <summary>
    /// Whether the space bar is held.
    ///
    /// It turns every tool into a hand for as long as it is down: the picture is moved about rather
    /// than drawn on. This is the gesture every editor with a canvas larger than its window has, and
    /// it is worth having for the same reason they all do, which is that reaching for a scroll bar
    /// to nudge a picture along is a poor way to look at one.
    /// </summary>
    public bool Panning
    {
        get;
        set
        {
            if (field == value)
            {
                return;
            }

            field = value;

            if (!value)
            {
                grabbed = false;
            }

            ShowCursor();
        }
    }

    /// <summary>How far the pointer has moved since the last step of a pan, in the window's coordinates.</summary>
    public event Action<Vector>? Panned;

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

    /// <summary>How far the working surface's top-left corner has moved since the drag began, in view pixels.</summary>
    public event Action<Vector>? CanvasResizeMoved;

    public event Action? CanvasResizeEnded;

    /// <summary>Raised when the canvas being proposed changes, so the band and the status line can follow it.</summary>
    public event Action? CanvasProposalChanged;

    /// <summary>Raised when a resize is applied or abandoned, so the window can leave the mode.</summary>
    public event Action? CanvasResizeFinished;

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

            // A resize under way has its scale held still, and an explicit zoom is a deliberate
            // change of mind about it. Dropping the held value lets fitting be worked out afresh.
            // Not mid-drag, though: the whole point of holding it is that an edge being dragged
            // must not have the picture rescale under it.
            if (canvasGrip == DragKind.None && !growing)
            {
                sessionScale = 0;
            }

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
        // The canvas ordinarily, and the whole working surface while one is being resized: in that
        // mode the control is deliberately larger than the canvas, because what is about to be
        // cropped away has to stay in sight.
        var area = Area();

        if (area.Width <= 0 || area.Height <= 0)
        {
            return default;
        }

        double scale;

        if (Zoom is { } requested)
        {
            scale = Math.Clamp(requested, 0.05, MaxZoom);
        }
        else if (sessionScale > 0)
        {
            // Held still for the length of a resize. See the field.
            scale = sessionScale;
        }
        else
        {
            // Fitting relies on the scroll viewer having its bars turned off while in this mode, so
            // the space offered here is the viewport rather than the infinity a scrollable
            // direction would report.
            var room = Math.Min(availableSize.Width / area.Width, availableSize.Height / area.Height);

            // Fitting never enlarges. A small capture blown up to fill the window is a wall of fat
            // pixels, and the honest thing is to show it at its own size with the mat around it.
            scale = double.IsFinite(room) ? Math.Min(room, 1) : 1;

            if (resizing is not null)
            {
                sessionScale = scale;
            }
        }

        if (Math.Abs(scale - EffectiveScale) > 0.0001)
        {
            EffectiveScale = scale;
            ZoomChanged?.Invoke();
        }

        var size = new Size(area.Width * scale, area.Height * scale);

        // The canvas measures to the picture, but the editing layer still has to be measured or
        // the editor is never given a size and stays invisible.
        Child?.Measure(size);

        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        // The editing layer covers the whole picture; the editor inside it is placed by coordinate.
        Child?.Arrange(new Rect(finalSize));

        arranged = Area();

        return finalSize;
    }

    // The control is exactly the area on show, so that area is all of it.
    Rect Target() => new(0, 0, Bounds.Width, Bounds.Height);

    /// <summary>
    /// The stretch the control is showing, in laid-out pixels: the working surface while the canvas
    /// is being resized, and the canvas itself otherwise.
    ///
    /// Laid, because that is what is on screen. The document is written in capture pixels and knows
    /// nothing about how tall the picture ends up once its cuts are closed.
    /// </summary>
    Rect Area() => snapshot.Layout.ToLaid(resizing?.Frame ?? CanvasRect());

    double Scale => Shown().Width <= 0 ? 1 : Bounds.Width / Shown().Width;

    /// <summary>
    /// The stretch the control's current bounds actually correspond to: the one it was last laid
    /// out for while a picture is growing the canvas, and the one the document describes otherwise.
    /// See <see cref="arranged"/>.
    /// </summary>
    Rect Shown() => growing && arranged.Width > 0 ? arranged : Area();

    /// <summary>The canvas in image pixels, as the document has it.</summary>
    Rect CanvasRect()
    {
        var canvas = snapshot.Document.Canvas;
        return new Rect(canvas.X, canvas.Y, canvas.Width, canvas.Height);
    }

    /// <summary>
    /// Everything the pictures cover, in image pixels, or the capture where it was taken when there
    /// are no pictures left at all.
    ///
    /// What "fit" means for the canvas. It used to be the capture, and once a picture can be pasted
    /// beside it or the capture moved, the capture alone would crop whatever was put next to it.
    /// </summary>
    public Rect PicturesRect() => snapshot.Document.Layers
        .OfType<ImageAnnotation>()
        .Select(picture => new Rect(picture.X, picture.Y, picture.Width, picture.Height))
        .Aggregate((Rect?)null, (union, next) => union?.Union(next) ?? next)
        ?? new Rect(0, 0, snapshot.Bitmap.PixelSize.Width, snapshot.Bitmap.PixelSize.Height);

    /// <summary>
    /// Where the capture's top-left corner falls on the control.
    ///
    /// Image coordinates are measured from the capture rather than from the canvas, so this is the
    /// zero of everything drawn on the picture. Taken from the renderer so the two cannot disagree:
    /// if they did, every click would land somewhere other than where it looks.
    /// </summary>
    Point Origin() => SnapshotRenderer.Origin(Shown(), Target(), Scale);

    /// <summary>Where a point on this control falls on the picture, in image pixels.</summary>
    public Point ToImagePoint(Point view) => ToImage(view);

    /// <summary>Where a point on the picture falls on this control. The other direction of the same map.</summary>
    public Point ToViewPoint(Point image) => ToView(image.X, image.Y);

    /// <summary>An image-space rectangle where it lands on the control.</summary>
    Rect ViewRect(Rect image) => new(ToView(image.X, image.Y), ToView(image.Right, image.Bottom));

    Point ToImage(Point view)
    {
        var origin = Origin();
        var scale = Scale;

        return snapshot.Layout.ToCapture(new Point((view.X - origin.X) / scale, (view.Y - origin.Y) / scale));
    }

    Point ToView(double x, double y) => FromLaid(snapshot.Layout.ToLaid(new Point(x, y)));

    /// <summary>Where a laid-out point lands on the control, for the few things that are laid already.</summary>
    Point FromLaid(Point laid)
    {
        var origin = Origin();
        var scale = Scale;

        return new Point(origin.X + laid.X * scale, origin.Y + laid.Y * scale);
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

        // With space held the press takes hold of the picture rather than of anything on it.
        if (Panning)
        {
            grabbed = true;
            panOrigin = e.GetPosition(null);

            e.Pointer.Capture(this);
            ShowCursor();
            return;
        }

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

        // The cut tool marks a band of the picture rather than putting anything on it.
        if (Tool == EditorTool.Cut)
        {
            Select(null);

            cutFrom = image;
            cutting = null;
            cuttingDrag = true;

            e.Pointer.Capture(this);
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
            BeginGrowing();
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
            BeginGrowing();
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
        if (grabbed)
        {
            // Measured against the window rather than against this control, which is itself being
            // scrolled by the very movement being measured. Step by step rather than from where the
            // drag began, so that a pan run into the end of the picture and back does not have to
            // work off a distance the picture never travelled.
            var now = e.GetPosition(null);
            Panned?.Invoke(now - panOrigin);
            panOrigin = now;
            return;
        }

        if (Panning)
        {
            return;
        }

        if (cuttingDrag)
        {
            Cut(ToImage(e.GetPosition(this)));
            return;
        }

        if (canvasGrip != DragKind.None)
        {
            ResizeCanvas(ToImage(e.GetPosition(this)) - grabOffset);
            return;
        }

        if (dragging == DragKind.None)
        {
            ShowCursor(e.GetPosition(this));
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

            // Shift lets a corner stretch a picture out of shape, which is the rarer thing to want.
            case ImageAnnotation picture when dragBaseline is ImageAnnotation baseline:
                Apply(picture, baseline, delta, image, proportional: !e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                GrowCanvasAround(picture, BoundsOf(baseline));
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

    /// <summary>Nothing narrower or shorter than this. A picture of no width is one that can never be picked up again.</summary>
    const double MinimumPicture = 4;

    /// <summary>
    /// Moves or resizes a picture, on whole pixels.
    ///
    /// Whole pixels because a picture placed between them is resampled, and a screenshot resampled
    /// by half a pixel is a screenshot with soft text, on the canvas and in the export alike.
    ///
    /// A corner keeps the picture's proportions unless told otherwise, and grows it from the corner
    /// opposite. A side stretches it, since a side can only ever say one dimension, and a picture
    /// dragged through itself turns inside out the way a box does rather than stopping.
    /// </summary>
    void Apply(ImageAnnotation picture, ImageAnnotation baseline, Vector delta, Point image, bool proportional)
    {
        if (dragging == DragKind.Move)
        {
            picture.X = baseline.X + Math.Round(delta.X);
            picture.Y = baseline.Y + Math.Round(delta.Y);
            return;
        }

        var x = Math.Round(image.X);
        var y = Math.Round(image.Y);

        var left = MovesLeft(dragging) ? x : baseline.X;
        var top = MovesTop(dragging) ? y : baseline.Y;
        var right = MovesRight(dragging) ? x : baseline.X + baseline.Width;
        var bottom = MovesBottom(dragging) ? y : baseline.Y + baseline.Height;

        var corner = (MovesLeft(dragging) || MovesRight(dragging)) && (MovesTop(dragging) || MovesBottom(dragging));

        if (proportional && corner && baseline.Width > 0 && baseline.Height > 0)
        {
            // The corner that stays put, and however far the pointer has gone from it in whichever
            // direction has gone further. Measuring by the larger keeps the picture under the
            // pointer rather than letting one axis lag behind it.
            var anchorX = MovesLeft(dragging) ? baseline.X + baseline.Width : baseline.X;
            var anchorY = MovesTop(dragging) ? baseline.Y + baseline.Height : baseline.Y;

            var factor = Math.Max(Math.Abs(x - anchorX) / baseline.Width, Math.Abs(y - anchorY) / baseline.Height);

            var width = Math.Max(Math.Round(baseline.Width * factor), MinimumPicture);
            var height = Math.Max(Math.Round(baseline.Height * factor), MinimumPicture);

            left = x < anchorX ? anchorX - width : anchorX;
            right = left + width;
            top = y < anchorY ? anchorY - height : anchorY;
            bottom = top + height;
        }

        picture.X = Math.Min(left, right);
        picture.Y = Math.Min(top, bottom);
        picture.Width = Math.Max(Math.Abs(right - left), MinimumPicture);
        picture.Height = Math.Max(Math.Abs(bottom - top), MinimumPicture);
    }

    static Rect BoundsOf(RectAnnotation rect) => new(rect.X, rect.Y, rect.Width, rect.Height);

    // ---- Growing the canvas around a picture ----------------------------------------------------
    //
    // A picture moved or resized past the edge of the canvas takes the canvas with it, so nothing
    // pasted or dragged is ever quietly cropped. The canvas only ever grows by what the picture has
    // been pushed out past both the canvas and where the picture already was. A picture that was
    // already cropped by the canvas stays cropped by the same amount when it is nudged: the crop was
    // somebody's decision, and moving the picture a few pixels is not a change of mind about it.
    //
    // It happens as the picture moves rather than when it is let go, so the part being dragged out
    // is on screen the whole way. The scale is held and the window keeps the picture still while it
    // does, exactly as it does while an edge of the canvas is dragged, and for the same reason: a
    // surface that refits as it grows would take the picture out from under the pointer moving it.

    /// <summary>Starts a picture drag, if what is being dragged is a picture.</summary>
    void BeginGrowing()
    {
        if (Selected is not ImageAnnotation)
        {
            return;
        }

        growing = true;
        growFrom = CanvasRect();
        sessionScale = EffectiveScale;

        CanvasResizeStarted?.Invoke();
    }

    /// <summary>
    /// Grows the canvas to take in a picture that has gone past it.
    ///
    /// Worked out afresh from the canvas as it was when the picture was picked up, rather than from
    /// the last step, so bringing the picture back in shrinks the canvas back to where it started
    /// and no further. Outside a drag, as after a paste, it grows from the canvas as it is.
    /// </summary>
    /// <param name="before">Where the picture stood before, so a crop it already had is kept.</param>
    void GrowCanvasAround(ImageAnnotation picture, Rect before)
    {
        var from = growing ? growFrom : CanvasRect();
        var now = BoundsOf(picture);

        var grown = new Rect(
            new Point(
                Math.Floor(from.X - Math.Max(0, Math.Min(from.X, before.X) - now.X)),
                Math.Floor(from.Y - Math.Max(0, Math.Min(from.Y, before.Y) - now.Y))),
            new Point(
                Math.Ceiling(from.Right + Math.Max(0, now.Right - Math.Max(from.Right, before.Right))),
                Math.Ceiling(from.Bottom + Math.Max(0, now.Bottom - Math.Max(from.Bottom, before.Bottom)))));

        if (grown == CanvasRect())
        {
            return;
        }

        var canvas = snapshot.Document.Canvas;
        canvas.X = (int)grown.X;
        canvas.Y = (int)grown.Y;
        canvas.Width = (int)grown.Width;
        canvas.Height = (int)grown.Height;

        if (growing)
        {
            // How far the canvas's corner has moved on screen, which is how far the window has to
            // move the control to leave the picture where it was.
            var shift = snapshot.Layout.ToLaid(grown).TopLeft - snapshot.Layout.ToLaid(growFrom).TopLeft;
            CanvasResizeMoved?.Invoke(shift * EffectiveScale);
        }

        InvalidateMeasure();
        InvalidateVisual();
    }

    /// <summary>
    /// Puts a picture on the canvas, on top of everything, and selects it.
    ///
    /// At its own size, centred on <paramref name="centre"/>, which is the middle of whatever part
    /// of the canvas is on screen: a picture pasted somewhere out of sight looks like a paste that
    /// did nothing. Kept inside the canvas where it fits, and where it does not it starts at the
    /// canvas's top-left corner, so the canvas grows right and down to take it in rather than
    /// spreading out on every side around what was already there.
    /// </summary>
    /// <param name="source">The entry the snapshot keeps the picture under.</param>
    /// <param name="centre">In image pixels.</param>
    public void Paste(string source, Avalonia.PixelSize size, Point centre)
    {
        CommitEdit();

        var bounds = CanvasRect();

        var picture = new ImageAnnotation
        {
            Source = source,
            X = Place(centre.X, size.Width, bounds.X, bounds.Width),
            Y = Place(centre.Y, size.Height, bounds.Y, bounds.Height),
            Width = size.Width,
            Height = size.Height
        };

        BeforeChange?.Invoke();

        snapshot.Document.Layers.Add(picture);

        // Measured against the canvas as it stands, so the canvas simply grows to take in whatever
        // of the picture it does not already cover.
        GrowCanvasAround(picture, bounds);

        Select(picture);
        Changed?.Invoke();

        InvalidateMeasure();
        InvalidateVisual();
    }

    /// <summary>Where along one side a pasted picture starts. See <see cref="Paste"/>.</summary>
    static double Place(double centre, double extent, double from, double room) => extent <= room
        ? Math.Clamp(Math.Round(centre - extent / 2), from, from + room - extent)
        : from;

    /// <summary>Mirrors the selected picture, as one undoable step. Anything else selected is left alone.</summary>
    public void Flip(bool horizontally)
    {
        if (Selected is not ImageAnnotation picture)
        {
            return;
        }

        Edit(() =>
        {
            if (horizontally)
            {
                picture.FlipHorizontal = !picture.FlipHorizontal;
            }
            else
            {
                picture.FlipVertical = !picture.FlipVertical;
            }
        });
    }

    /// <summary>
    /// Puts the selected picture back to its own size, one image pixel to a picture pixel.
    ///
    /// From its top-left corner, which is the one that stays put, and the canvas grows if the
    /// picture now reaches past it, the same as when it is stretched by hand.
    /// </summary>
    public void RestorePictureSize()
    {
        if (Selected is not ImageAnnotation picture || snapshot.BitmapOf(picture.Source) is not { } bitmap)
        {
            return;
        }

        if (picture.Width == bitmap.PixelSize.Width && picture.Height == bitmap.PixelSize.Height)
        {
            return;
        }

        var before = BoundsOf(picture);

        BeforeChange?.Invoke();

        picture.Width = bitmap.PixelSize.Width;
        picture.Height = bitmap.PixelSize.Height;
        GrowCanvasAround(picture, before);

        Changed?.Invoke();

        InvalidateMeasure();
        InvalidateVisual();
    }

    /// <summary>
    /// Ends a picture drag and lets the layout have the picture back.
    ///
    /// From the release and from the loss of capture both, and harmless the second time, for the
    /// reason every drag here ends down both paths: letting go of the capture reports it lost.
    /// </summary>
    void EndGrowing()
    {
        if (!growing)
        {
            return;
        }

        growing = false;
        sessionScale = 0;

        CanvasResizeEnded?.Invoke();

        InvalidateMeasure();
        InvalidateVisual();
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

    // ---- Resizing the canvas -----------------------------------------------------------------
    //
    // The canvas is the rectangle that gets exported, and it is not obliged to match the capture.
    // Pulling an edge in crops the picture; pushing one out adds space, and what it adds is
    // transparent. Neither touches a pixel of the capture or moves a single annotation: cropping is
    // geometry, so an edge pulled in can always be pulled back out again.
    //
    // It is a mode, and while it lasts the control shows more than the canvas. That is the point of
    // it: an edge dragged inward has to leave what is being cut away in sight, dimmed rather than
    // gone, and an edge dragged outward has to have somewhere visible to go. Nothing reaches the
    // document until the resize is applied, so the whole negotiation is one undo step or none.

    /// <summary>A resize being negotiated.</summary>
    sealed class CanvasResize
    {
        /// <summary>The canvas as proposed, in image pixels.</summary>
        public Rect Proposed;

        /// <summary>The working surface on show: the proposal and the capture, with room around both.</summary>
        public Rect Frame;
    }

    /// <summary>Nothing smaller than this, in image pixels. A canvas of nothing is not a canvas.</summary>
    const int MinimumCanvas = 16;

    /// <summary>How far either side of the boundary counts as grabbing it.</summary>
    const double EdgeReach = 10;

    /// <summary>How far along the boundary from a corner still counts as the corner rather than the side.</summary>
    const double CornerReach = 24;

    public bool IsResizingCanvas => resizing is not null;

    /// <summary>The canvas as it is being shown: the proposal while one is on the table, the document's own otherwise.</summary>
    public Rect ShownCanvas => resizing?.Proposed ?? CanvasRect();

    void OpenResize()
    {
        CommitEdit();

        // Nothing on the picture is worked on in this mode, and a selection left over from the last
        // tool would only put handles on the picture that this one does not use.
        Select(null);

        resizing = new CanvasResize();
        SeedResize();
    }

    /// <summary>
    /// Points an open resize back at the canvas the document now has.
    ///
    /// A proposal is not in the undo history, because nothing reaches the document until it is
    /// applied. A step through that history therefore leaves the proposal describing a canvas that
    /// no longer exists, and the boundary on screen and the size fields go on offering it: pressing
    /// Enter would then write back the very crop that was just undone. Seeded afresh, they show
    /// what was restored, and the mode carries on rather than being thrown away underneath someone.
    /// </summary>
    public void SeedResize()
    {
        if (resizing is not { } session)
        {
            return;
        }

        session.Proposed = CanvasRect();
        session.Frame = FrameAround(session.Proposed, PicturesRect());

        // Left for the next measure to work out, since it depends on the room available.
        sessionScale = 0;

        CanvasProposalChanged?.Invoke();

        InvalidateMeasure();
        InvalidateVisual();
    }

    /// <summary>
    /// The working surface for a proposal: everything it and the pictures cover, and nothing more.
    ///
    /// No room is kept back around the pair. Opening the mode would otherwise shrink the picture to
    /// make space that is not needed yet, which reads as the editor having done something when all
    /// that happened was a tool being picked. The room appears when it is called for, which is when
    /// an edge is actually dragged outward, and the canvas grows into it.
    /// </summary>
    static Rect FrameAround(Rect proposed, Rect capture) => proposed.Union(capture);

    /// <summary>Leaves the mode. Whatever was proposed is dropped; only <see cref="ApplyCanvasResize"/> writes to the document.</summary>
    void CloseResize()
    {
        if (resizing is null)
        {
            return;
        }

        resizing = null;
        canvasGrip = DragKind.None;
        sessionScale = 0;

        CanvasResizeEnded?.Invoke();
        CanvasProposalChanged?.Invoke();
    }

    /// <summary>Applies the proposal to the document as one undoable step, and leaves the mode.</summary>
    public void ApplyCanvasResize()
    {
        if (resizing is not { } session)
        {
            return;
        }

        var canvas = snapshot.Document.Canvas;
        var proposed = session.Proposed;

        var changed = canvas.X != (int)proposed.X || canvas.Y != (int)proposed.Y
            || canvas.Width != (int)proposed.Width || canvas.Height != (int)proposed.Height;

        if (changed)
        {
            BeforeChange?.Invoke();

            canvas.X = (int)proposed.X;
            canvas.Y = (int)proposed.Y;
            canvas.Width = (int)proposed.Width;
            canvas.Height = (int)proposed.Height;
        }

        CloseResize();

        // After the mode has been left, so that the window redraws against the canvas the document
        // now has rather than the one it was still negotiating.
        if (changed)
        {
            Changed?.Invoke();
        }

        InvalidateMeasure();
        InvalidateVisual();
        CanvasResizeFinished?.Invoke();
    }

    /// <summary>Abandons the proposal and leaves the mode. The document was never touched.</summary>
    public void CancelCanvasResize()
    {
        if (resizing is null)
        {
            return;
        }

        CloseResize();

        InvalidateMeasure();
        InvalidateVisual();
        CanvasResizeFinished?.Invoke();
    }

    /// <summary>
    /// Proposes a width, a height, or both, keeping the canvas's top-left corner where it is.
    ///
    /// The numbers are what the file will come out as, which with a band cut out of the picture is
    /// not the same as how much capture the canvas covers. The field says the honest thing and the
    /// canvas is widened to produce it.
    /// </summary>
    public void ProposeCanvasSize(int? width, int? height)
    {
        if (resizing is not { } session)
        {
            return;
        }

        var laid = snapshot.Layout.ToLaid(session.Proposed);

        var wanted = new Size(
            Math.Max(width ?? laid.Width, MinimumCanvas),
            Math.Max(height ?? laid.Height, MinimumCanvas));

        Propose(new Rect(
            session.Proposed.X,
            session.Proposed.Y,
            snapshot.Layout.Widen(session.Proposed.X, wanted.Width, CutAxis.Columns),
            snapshot.Layout.Widen(session.Proposed.Y, wanted.Height, CutAxis.Rows)));
    }

    /// <summary>The canvas being shown, as the file would come out: the cuts closed up.</summary>
    public Rect ShownCanvasLaid => snapshot.Layout.ToLaid(ShownCanvas);

    /// <summary>Proposes exactly what the pictures cover, which is the way back from any crop or padding.</summary>
    public void ProposePictureBounds()
    {
        if (resizing is not null)
        {
            Propose(PicturesRect());
        }
    }

    void Propose(Rect proposed)
    {
        if (resizing is not { } session)
        {
            return;
        }

        session.Proposed = proposed;

        // The surface follows the canvas exactly, in both directions. Anything else leaves grey
        // where the canvas has been but no longer is, which says "something was cropped here" about
        // a place where nothing was. The picture still does not move: the window holds it where it
        // is for the length of the drag, whichever way the surface is going.
        session.Frame = FrameAround(proposed, PicturesRect());

        CanvasProposalChanged?.Invoke();

        InvalidateMeasure();
        InvalidateVisual();
    }

    void BeginCanvasResize(Point view, Point image, PointerPressedEventArgs e)
    {
        if (resizing is not { } session)
        {
            return;
        }

        var grip = HitCanvasEdge(view, session);

        // Pressing inside the canvas moves the whole of it, which is how a crop is aimed at the
        // part of the picture worth keeping. Pressing on the dimmed surround does nothing.
        if (grip == DragKind.None)
        {
            if (!ViewRect(session.Proposed).Contains(view))
            {
                return;
            }

            grip = DragKind.Move;
        }

        canvasGrip = grip;
        canvasBaseline = session.Proposed;
        frameBaseline = session.Frame;

        grabOffset = grip == DragKind.Move
            ? image - session.Proposed.TopLeft
            : image - AnchorOf(grip, canvasBaseline);

        e.Pointer.Capture(this);
        CanvasResizeStarted?.Invoke();
    }

    void ResizeCanvas(Point to)
    {
        if (resizing is not { } session)
        {
            return;
        }

        // Whole pixels, because that is what the canvas is measured in and what gets exported.
        var x = Math.Round(to.X);
        var y = Math.Round(to.Y);

        if (canvasGrip == DragKind.Move)
        {
            Propose(new Rect(x, y, canvasBaseline.Width, canvasBaseline.Height));
        }
        else
        {
            // Stopped at the minimum rather than turned inside out. A rectangle drawn backwards is
            // still a rectangle; a canvas dragged past its far edge would swing the picture across
            // the screen.
            var left = MovesLeft(canvasGrip) ? Math.Min(x, canvasBaseline.Right - MinimumCanvas) : canvasBaseline.X;
            var top = MovesTop(canvasGrip) ? Math.Min(y, canvasBaseline.Bottom - MinimumCanvas) : canvasBaseline.Y;
            var right = MovesRight(canvasGrip) ? Math.Max(x, canvasBaseline.X + MinimumCanvas) : canvasBaseline.Right;
            var bottom = MovesBottom(canvasGrip) ? Math.Max(y, canvasBaseline.Y + MinimumCanvas) : canvasBaseline.Bottom;

            Propose(new Rect(left, top, right - left, bottom - top));
        }

        // How far the control's own top-left corner has to move to leave the picture exactly where
        // it is on screen. Usually nothing at all, since the working surface only changes when the
        // canvas is dragged clean out of it; when it does change, the layout would otherwise
        // recentre it and take the picture out from under the pointer.
        CanvasResizeMoved?.Invoke(new Vector(
            (session.Frame.X - frameBaseline.X) * EffectiveScale,
            (session.Frame.Y - frameBaseline.Y) * EffectiveScale));
    }

    /// <summary>
    /// Ends a canvas drag and hands the layout back.
    ///
    /// Called from both the release and the loss of capture, and safe either way round: capture is
    /// released on the way out of a drag, and losing it to a window switch or a cancelled touch has
    /// to end the drag too. A drag left running would hold the picture pinned where it was and
    /// resize the canvas on the next movement of the pointer, with no button held at all.
    /// </summary>
    void EndCanvasDrag()
    {
        if (canvasGrip == DragKind.None)
        {
            return;
        }

        canvasGrip = DragKind.None;

        // Back to the scale that shows all of it. A canvas dragged out past the window is worth
        // seeing whole the moment it is let go, and a drag that has ended is the one point where
        // moving the picture costs nothing.
        sessionScale = 0;

        // Laid out normally again, which settles the working surface back into the middle of its
        // mat if a drag had pushed it off centre.
        CanvasResizeEnded?.Invoke();

        InvalidateMeasure();
        InvalidateVisual();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        EndCut();

        if (grabbed)
        {
            grabbed = false;
            ShowCursor();
        }

        EndCanvasDrag();
        EndGrowing();
        base.OnPointerCaptureLost(e);
    }

    // ---- Cutting a band out --------------------------------------------------------------------
    //
    // A band is marked by dragging across the picture, and the way the pointer goes decides what it
    // takes: down or up marks rows, left or right marks columns. Nothing is taken from the capture
    // itself, which is never touched; the band goes into the document and the picture is drawn from
    // then on with that band skipped and everything after it closed up.

    /// <summary>Anything thinner than this in capture pixels is a click that wandered, not a band.</summary>
    const double MinimumCut = 3;

    /// <summary>
    /// How much further the pointer has to go the other way before a band decided by the drag
    /// changes its mind.
    ///
    /// Without it a band a few pixels across flickers between the two as the hand wavers, which is
    /// no way to choose anything. With it the first direction wins until the other one is clearly
    /// meant, and a drag begun the wrong way can still be corrected without letting go.
    /// </summary>
    const double AxisHysteresis = 10;

    void Cut(Point to)
    {
        // Whole pixels, the same way the canvas is, and for the same reason: a band is a stretch of
        // rows or columns, and there is no such thing as three fifths of a row. Both ends are
        // rounded rather than the width, so a band always starts and finishes on a real pixel.
        //
        // A fractional band would be paid for everywhere afterwards. The picture past the join is
        // drawn shifted by what the band took, and shifted by a fraction it lands between pixels
        // and is resampled, which on a screenshot means soft text below every cut. The export would
        // round the height it allocates while drawing an area that was never rounded, and the size
        // fields would keep handing back a number one short of the one typed into them.
        var from = new Point(Math.Round(cutFrom.X), Math.Round(cutFrom.Y));
        var at = new Point(Math.Round(to.X), Math.Round(to.Y));

        var across = Math.Abs(at.X - from.X);
        var down = Math.Abs(at.Y - from.Y);

        var axis = Defaults.CutDirection ?? cutting?.Axis switch
        {
            CutAxis.Rows when across > down + AxisHysteresis => CutAxis.Columns,
            CutAxis.Columns when down > across + AxisHysteresis => CutAxis.Rows,
            { } decided => decided,
            _ => down >= across ? CutAxis.Rows : CutAxis.Columns
        };

        cutting = axis == CutAxis.Rows
            ? new CutBand { Axis = CutAxis.Rows, At = Math.Min(from.Y, at.Y), Extent = down }
            : new CutBand { Axis = CutAxis.Columns, At = Math.Min(from.X, at.X), Extent = across };

        InvalidateVisual();
    }

    /// <summary>
    /// Ends the drag and takes the band out.
    ///
    /// Called from the release and from the loss of capture, which here are the same event twice:
    /// letting the capture go is itself reported as capture lost, and there is no saying which of
    /// the two arrives first. So both do the same thing and the order cannot matter. An earlier
    /// version had one of them keep the band and the other throw it away, and whichever ran second
    /// found nothing left to do, which is why nothing was ever cut. Escape is what abandons a band.
    /// </summary>
    void EndCut()
    {
        if (!cuttingDrag)
        {
            return;
        }

        cuttingDrag = false;

        if (cutting is { Extent: >= MinimumCut } band)
        {
            BeforeChange?.Invoke();

            snapshot.Document.Cuts.Add(band);
            snapshot.Recut();

            Changed?.Invoke();
        }

        cutting = null;

        // The picture is a different size now, so the control is too.
        InvalidateMeasure();
        InvalidateVisual();
    }

    /// <summary>True while a band is being dragged out, so the window knows what Escape is for.</summary>
    public bool IsCutting => cuttingDrag;

    /// <summary>Abandons the band being dragged, which is the one way out of a cut that does not make it.</summary>
    public void CancelCut()
    {
        if (!cuttingDrag)
        {
            return;
        }

        cuttingDrag = false;
        cutting = null;

        InvalidateVisual();
    }

    /// <summary>Puts every cut back, as one undoable step. The capture was never touched, so there is nothing to restore.</summary>
    public void UncutAll()
    {
        if (snapshot.Document.Cuts.Count == 0)
        {
            return;
        }

        BeforeChange?.Invoke();

        snapshot.Document.Cuts.Clear();
        snapshot.Recut();

        Changed?.Invoke();

        InvalidateMeasure();
        InvalidateVisual();
    }

    /// <summary>
    /// The band being marked, while it is being marked and never after.
    ///
    /// A cut that has been made leaves no mark at all. It is simply a shorter picture, and a line
    /// drawn where the join is would be the editor pointing at its own work: there is nothing wrong
    /// with the picture at that spot, and nothing there to do anything about. How many cuts a
    /// snapshot has is on the status line for the times that matters.
    /// </summary>
    void DrawCutting(DrawingContext context, Rect target)
    {
        if (cutting is not { Extent: > 0 } band)
        {
            return;
        }

        var start = ToView(band.At, band.At);
        var end = ToView(band.At + band.Extent, band.At + band.Extent);

        var marked = band.Axis == CutAxis.Rows
            ? new Rect(target.X, start.Y, target.Width, Math.Max(end.Y - start.Y, 0))
            : new Rect(start.X, target.Y, Math.Max(end.X - start.X, 0), target.Height);

        // Dimmed, the same way the surround is while the canvas is being resized: this is the part
        // that will not be there, shown while there is still time to change it.
        context.FillRectangle(Scrim, marked);
        context.DrawRectangle(null, BoundaryShadow, marked.Inflate(1));
        context.DrawRectangle(null, BoundaryPen, marked);
    }

    /// <summary>
    /// Which part of the canvas boundary the pointer is on, if any.
    ///
    /// The whole of a side is grabbable rather than only a handle in the middle of it: an edge is
    /// what the eye sees and what the hand goes for. The corners take a longer stretch of both
    /// their sides, so the one place two grips meet is not a pixel hunt. Either side of the line
    /// counts, since the surround is on show in this mode and is as good a place to aim at.
    ///
    /// How far a corner reaches is capped at half the side it reaches along, so that the two ends
    /// of a side can never both claim the same stretch of it. Without the cap, a canvas narrower
    /// than two corners answered every question with its left edge, and aiming at the right one to
    /// pull the canvas back out dragged the left one the other way instead. It takes a small canvas
    /// to manage that, or an ordinary one seen at ten percent.
    /// </summary>
    DragKind HitCanvasEdge(Point view, CanvasResize session)
    {
        var rect = ViewRect(session.Proposed);

        if (!rect.Inflate(EdgeReach).Contains(view))
        {
            return DragKind.None;
        }

        var insideX = view.X > rect.X + EdgeReach && view.X < rect.Right - EdgeReach;
        var insideY = view.Y > rect.Y + EdgeReach && view.Y < rect.Bottom - EdgeReach;

        if (insideX && insideY)
        {
            return DragKind.None;
        }

        var alongX = Math.Min(CornerReach, rect.Width / 2);
        var alongY = Math.Min(CornerReach, rect.Height / 2);

        var horizontal = view.X <= rect.X + alongX ? -1 : view.X >= rect.Right - alongX ? 1 : 0;
        var vertical = view.Y <= rect.Y + alongY ? -1 : view.Y >= rect.Bottom - alongY ? 1 : 0;

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

    /// <summary>
    /// The cursor for what the pointer is over, or for the mode the editor is in.
    ///
    /// Called on movement, where the position is known, and on a change of mode, where it is not:
    /// a hand has to appear the moment space goes down rather than on the next twitch of the mouse.
    /// </summary>
    void ShowCursor(Point? over = null)
    {
        if (Panning)
        {
            // An open hand while it is only ready, and the move cursor while it actually has hold
            // of the picture. The toolkit offers no closed hand of its own.
            Cursor = new Cursor(grabbed ? StandardCursorType.SizeAll : StandardCursorType.Hand);
            return;
        }

        if (over is not { } point)
        {
            Cursor = new Cursor(Tool == EditorTool.Select ? StandardCursorType.Arrow : StandardCursorType.Cross);
            return;
        }

        if (resizing is { } session)
        {
            var grip = HitCanvasEdge(point, session);

            Cursor = new Cursor(grip != DragKind.None ? CursorFor(grip)
                : ViewRect(session.Proposed).Contains(point) ? StandardCursorType.SizeAll
                : StandardCursorType.Arrow);

            return;
        }

        Cursor = new Cursor(
            HitHandle(point) != DragKind.None ? StandardCursorType.SizeAll
            : HitTest(ToImage(point)) is not null ? StandardCursorType.Hand
            : Tool == EditorTool.Select ? StandardCursorType.Arrow
            : StandardCursorType.Cross);
    }

    static StandardCursorType CursorFor(DragKind grip) => grip switch
    {
        DragKind.RectLeft or DragKind.RectRight => StandardCursorType.SizeWestEast,
        DragKind.RectTop or DragKind.RectBottom => StandardCursorType.SizeNorthSouth,
        DragKind.RectTopLeft => StandardCursorType.TopLeftCorner,
        DragKind.RectTopRight => StandardCursorType.TopRightCorner,
        DragKind.RectBottomLeft => StandardCursorType.BottomLeftCorner,
        DragKind.RectBottomRight => StandardCursorType.BottomRightCorner,
        DragKind.Move => StandardCursorType.SizeAll,
        _ => StandardCursorType.Arrow
    };

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

    /// <summary>
    /// The canvas boundary, what falls outside it, and the grips that move it.
    ///
    /// Thin lines, and only lines: a heavy outline over the boundary hides the very pixels being
    /// decided about. The dimmed surround is what says which side is which, so the line itself only
    /// has to be findable, and the thirds are the guide every camera and every crop tool draws.
    /// </summary>
    void DrawCanvasChrome(DrawingContext context, Rect target, CanvasResize session)
    {
        var rect = ViewRect(session.Proposed);

        // Outside the canvas is dimmed rather than hidden. Seeing what is about to be cropped away
        // is the whole reason this mode shows more than the canvas.
        context.FillRectangle(Scrim, new Rect(target.X, target.Y, target.Width, Math.Max(rect.Y - target.Y, 0)));
        context.FillRectangle(Scrim, new Rect(target.X, rect.Bottom, target.Width, Math.Max(target.Bottom - rect.Bottom, 0)));
        context.FillRectangle(Scrim, new Rect(target.X, rect.Y, Math.Max(rect.X - target.X, 0), Math.Max(rect.Height, 0)));
        context.FillRectangle(Scrim, new Rect(rect.Right, rect.Y, Math.Max(target.Right - rect.Right, 0), Math.Max(rect.Height, 0)));

        for (var third = 1; third <= 2; third++)
        {
            var x = Math.Round(rect.X + rect.Width * third / 3) + 0.5;
            var y = Math.Round(rect.Y + rect.Height * third / 3) + 0.5;

            context.DrawLine(GuidePen, new Point(x, rect.Y), new Point(x, rect.Bottom));
            context.DrawLine(GuidePen, new Point(rect.X, y), new Point(rect.Right, y));
        }

        // Two hairlines rather than one. The surround is dark and the inside is a screenshot, which
        // can be any colour at all, so a single line is invisible against one side or the other.
        context.DrawRectangle(null, BoundaryShadow, rect.Inflate(1));
        context.DrawRectangle(null, BoundaryPen, rect);

        // Centred on the boundary where there is room, and tucked inside it where there is not. The
        // surface is exactly the canvas until the canvas is grown, so at first the boundary is the
        // control's own edge, and a grip straddling it would be sliced down the middle.
        var grips = Inside(rect, target, HandleSize / 2);

        foreach (var grip in new[]
                 {
                     DragKind.RectTopLeft, DragKind.RectTop, DragKind.RectTopRight, DragKind.RectRight,
                     DragKind.RectBottomRight, DragKind.RectBottom, DragKind.RectBottomLeft, DragKind.RectLeft
                 })
        {
            DrawHandle(context, AnchorOf(grip, grips));
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (cuttingDrag)
        {
            EndCut();
            e.Pointer.Capture(null);
            return;
        }

        if (grabbed)
        {
            grabbed = false;
            e.Pointer.Capture(null);
            ShowCursor();
            return;
        }

        if (canvasGrip != DragKind.None)
        {
            // Letting the capture go is itself reported as capture lost, which is where the drag
            // ends; the call after it is then the no-op that makes the order not matter.
            e.Pointer.Capture(null);
            EndCanvasDrag();
            return;
        }

        e.Pointer.Capture(null);
        EndGrowing();

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
                // Pictures are picked up only with the select tool. They are the ground everything
                // else is drawn on, and the whole of the capture answering to a press would leave
                // every drawing tool moving the screenshot instead of drawing on it.
                ImageAnnotation picture => Tool == EditorTool.Select && BoundsOf(picture).Contains(image),

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

        var shown = ShownCanvas;

        // A chequerboard under the whole canvas, which is the only backdrop this control has:
        // wherever a picture stands it is covered, and the mat around it belongs to the window.
        // Laid under all of it rather than worked out around the pictures, which can be moved,
        // mirrored and have transparent corners of their own. Editing chrome, like the outlines
        // below it: an export paints nothing there, which is what makes the transparency real
        // rather than drawn.
        context.FillRectangle(Chequerboard, ViewRect(shown));

        SnapshotRenderer.Draw(context, snapshot, blurs, target, Area(), editing);

        if (resizing is { } session)
        {
            // The mode is modal. Nothing on the picture can be selected or typed while the canvas
            // itself is the thing being worked on, so none of the chrome below applies.
            DrawCanvasChrome(context, target, session);
            return;
        }

        if (Tool == EditorTool.Cut)
        {
            DrawCutting(context, target);
            return;
        }

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
    }

    /// <summary>A rectangle pushed in far enough from the edges of another to be drawn on whole.</summary>
    static Rect Inside(Rect rect, Rect within, double reach)
    {
        var left = Math.Max(rect.X, within.X + reach);
        var top = Math.Max(rect.Y, within.Y + reach);
        var right = Math.Min(rect.Right, within.Right - reach);
        var bottom = Math.Min(rect.Bottom, within.Bottom - reach);

        return new Rect(left, top, Math.Max(right - left, 0), Math.Max(bottom - top, 0));
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
