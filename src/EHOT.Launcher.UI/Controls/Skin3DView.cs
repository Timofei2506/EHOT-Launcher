using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace EHOT.Launcher.UI.Controls;

/// <summary>
/// Объёмное превью скина Minecraft: персонаж собирается из областей текстуры
/// (голова 8×8×8, тело 8×12×4, руки и ноги 4×12×4, второй слой — шляпа/куртка/рукава/брюки)
/// и вращается мышью.
/// <para>
/// Грань рисуется одним аффинным преобразованием всей области текстуры (ортографическая
/// проекция сохраняет прямые линии), поэтому кадр стоит десятки операций вместо тысяч
/// полигонов — превью больше не тормозит и рисуется без «рваных» текселей.
/// Поддерживаются форматы 64×64 (классика), 64×32 (legacy) и slim-рукава (Alex).
/// </para>
/// </summary>
public sealed class Skin3DView : Control
{
    public static readonly StyledProperty<string?> SkinPathProperty =
        AvaloniaProperty.Register<Skin3DView, string?>(nameof(SkinPath));

    public static readonly StyledProperty<double> YawProperty =
        AvaloniaProperty.Register<Skin3DView, double>(nameof(Yaw), -28d);

    public static readonly StyledProperty<double> PitchProperty =
        AvaloniaProperty.Register<Skin3DView, double>(nameof(Pitch), 6d);

    private const double PixelsPerPoint = 44.0;

    private Bitmap? _bitmap;
    private int _texHeight;
    private bool _slim;
    private bool _dragging;
    private Point _lastPoint;

    static Skin3DView()
    {
        AffectsRender<Skin3DView>(SkinPathProperty, YawProperty, PitchProperty);
        SkinPathProperty.Changed.AddClassHandler<Skin3DView>((view, args) =>
            view.LoadTexture(args.NewValue as string));
    }

    public string? SkinPath
    {
        get => GetValue(SkinPathProperty);
        set => SetValue(SkinPathProperty, value);
    }

    /// <summary>Поворот вокруг вертикальной оси, градусы.</summary>
    public double Yaw
    {
        get => GetValue(YawProperty);
        set => SetValue(YawProperty, value);
    }

    /// <summary>Наклон, градусы.</summary>
    public double Pitch
    {
        get => GetValue(PitchProperty);
        set => SetValue(PitchProperty, value);
    }

    private void LoadTexture(string? path)
    {
        _bitmap?.Dispose();
        _bitmap = null;
        _texHeight = 0;
        _slim = false;

        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            try
            {
                var bitmap = new Bitmap(path);
                _texHeight = bitmap.PixelSize.Height;
                _slim = _texHeight >= 64 && DetectSlim(bitmap);
                _bitmap = bitmap;
            }
            catch (Exception)
            {
                _bitmap?.Dispose();
                _bitmap = null;
            }
        }

        InvalidateVisual();
    }

    /// <summary>
    /// Slim-модель: рукава шириной 3 пикселя. В slim-текстуре четвёртый столбец
    /// передней грани руки прозрачен (рука 4×12 классики всегда сплошная).
    /// </summary>
    private static bool DetectSlim(Bitmap bitmap)
    {
        const int w = 1, h = 12; // столбец 47, строки 20..31 — «лишняя» колонка slim-руки
        var buffer = new byte[w * h * 4];
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(buffer, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(47, 20, w, h), handle.AddrOfPinnedObject(), buffer.Length, w * 4);
        }
        catch (Exception)
        {
            return false; // нет доступа к пикселям — считаем модель классической
        }
        finally
        {
            handle.Free();
        }

        var transparent = 0;
        for (var i = 3; i < buffer.Length; i += 4)
        {
            if (buffer[i] < 8)
            {
                transparent++;
            }
        }

        return transparent >= h - 2; // почти вся колонка прозрачная → slim (Alex)
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        _dragging = true;
        _lastPoint = e.GetPosition(this);
        e.Pointer.Capture(this);
        Cursor = new Cursor(StandardCursorType.SizeAll);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging)
        {
            return;
        }

        var point = e.GetPosition(this);
        Yaw += (point.X - _lastPoint.X) * 0.6;
        Pitch = Math.Clamp(Pitch - ((point.Y - _lastPoint.Y) * 0.4), -30, 30);
        _lastPoint = point;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = false;
        e.Pointer.Capture(null);
        Cursor = null;
    }

    // Освобождение текстуры при выгрузке элемента из визуального дерева.
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _bitmap?.Dispose();
        _bitmap = null;
        base.OnDetachedFromVisualTree(e);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = new Rect(Bounds.Size);

        if (_bitmap is null)
        {
            DrawPlaceholder(context, bounds);
            return;
        }

        var scale = Math.Min(bounds.Width / 22.0, bounds.Height / PixelsPerPoint);
        var yaw = Yaw * Math.PI / 180.0;
        var pitch = Pitch * Math.PI / 180.0;

        var faces = new List<Face>();
        foreach (var box in BuildModel(_texHeight <= 32, _slim))
        {
            CollectFaces(faces, box, yaw, pitch, scale, bounds.Center);
        }

        // Сначала дальние грани — ближние перекрывают их корректно.
        faces.Sort((a, b) => a.Depth.CompareTo(b.Depth));
        foreach (var face in faces)
        {
            DrawFace(context, face);
        }
    }

    /// <summary>Силуэт персонажа, когда файла скина нет (или он битый).</summary>
    private static void DrawPlaceholder(DrawingContext context, Rect bounds)
    {
        var unit = Math.Min(bounds.Width, bounds.Height) / 16.0;
        var cx = bounds.Center.X;
        var top = bounds.Center.Y - (10.0 * unit / 2);
        var muted = new SolidColorBrush(Color.Parse("#2E313B"));
        var line = new SolidColorBrush(Color.Parse("#3A3E4A"));

        // голова
        context.DrawRectangle(muted, new Pen(line, 1), new RoundedRect(new Rect(cx - (2.1 * unit), top, 4.2 * unit, 4.2 * unit), 1.6 * unit));
        // тело
        context.DrawRectangle(muted, new Pen(line, 1), new RoundedRect(new Rect(cx - (2.5 * unit), top + (5.0 * unit), 5.0 * unit, 7.0 * unit), 1.6 * unit));
        // ноги
        context.DrawRectangle(muted, new Pen(line, 1), new RoundedRect(new Rect(cx - (2.5 * unit), top + (12.6 * unit), 2.0 * unit, 4.6 * unit), 0.9 * unit));
        context.DrawRectangle(muted, new Pen(line, 1), new RoundedRect(new Rect(cx + (0.5 * unit), top + (12.6 * unit), 2.0 * unit, 4.6 * unit), 0.9 * unit));
    }

    private static void CollectFaces(List<Face> faces, Box box, double yaw, double pitch, double scale, Point center)
    {
        foreach (var face in box.Faces())
        {
            var corners = new Point[4];
            var depth = 0.0;
            for (var i = 0; i < 4; i++)
            {
                var projected = Project(box.Corner(face.Corners[i]), yaw, pitch);
                depth += projected.Z;
                corners[i] = new Point(
                    center.X + (projected.X * scale),
                    center.Y - ((projected.Y - 16) * scale));
            }

            faces.Add(new Face(corners, face, depth / 4));
        }
    }

    /// <summary>
    /// Грань = одна аффинная матрица (ортографическая проекция сохраняет прямые линии),
    /// поверх — полупрозрачное затемнение для объёма. Вся область рисуется целиком,
    /// прозрачные пиксели текстуры обрабатывает сам DrawImage.
    /// </summary>
    private void DrawFace(DrawingContext context, Face face)
    {
        if (_bitmap is null)
        {
            return;
        }

        var (u0, v0) = face.Source.Uv;
        var w = face.Source.W;
        var h = face.Source.H;
        var c = face.Corners;

        var matrix = new Matrix(
            (c[1].X - c[0].X) / w,
            (c[1].Y - c[0].Y) / w,
            (c[3].X - c[0].X) / h,
            (c[3].Y - c[0].Y) / h,
            c[0].X,
            c[0].Y);

        using (context.PushTransform(matrix))
        {
            context.DrawImage(_bitmap, new Rect(u0, v0, w, h), new Rect(0, 0, w, h));
        }

        if (face.Source.Light < 1.0)
        {
            var shade = new SolidColorBrush(Color.FromArgb(
                (byte)Math.Clamp((1.0 - face.Source.Light) * 200, 0, 255), 10, 8, 6));
            context.DrawGeometry(shade, null, Polygon(face.Corners));
        }
    }

    private static StreamGeometry Polygon(Point[] points)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(points[0], true);
            ctx.LineTo(points[1]);
            ctx.LineTo(points[2]);
            ctx.LineTo(points[3]);
            ctx.EndFigure(true);
        }

        return geometry;
    }

    /// <summary>Поворот вокруг Y, затем наклон вокруг X; на экран идут X и Y.</summary>
    private static (double X, double Y, double Z) Project((double X, double Y, double Z) p, double yaw, double pitch)
    {
        var cosY = Math.Cos(yaw);
        var sinY = Math.Sin(yaw);
        var x = (p.X * cosY) - (p.Z * sinY);
        var z = (p.X * sinY) + (p.Z * cosY);

        var cosP = Math.Cos(pitch);
        var sinP = Math.Sin(pitch);
        var y = (p.Y * cosP) - (z * sinP);
        var z2 = (p.Y * sinP) + (z * cosP);
        return (x, y, z2);
    }

    private sealed record Face(Point[] Corners, BoxFace Source, double Depth);

    private enum LimbKind
    {
        RightArm,
        LeftArm,
        RightLeg,
        LeftLeg
    }

    /// <summary>
    /// Грань куба: Uv — левый верхний угол области текстуры, W×H — её размер,
    /// Light — множитель освещения, Corners — индексы вершин 0..7 в порядке
    /// «левый верх, правый верх, правый низ, левый низ».
    /// </summary>
    private sealed record BoxFace((int X, int Y) Uv, int W, int H, double Light, int[] Corners);

    /// <summary>Куб модели: границы в пикселях текстуры + список граней с UV из развёртки.</summary>
    private sealed class Box
    {
        private readonly double _x0, _y0, _z0, _x1, _y1, _z1;
        private readonly BoxFace[] _faces;

        private Box(double x0, double y0, double z0, double x1, double y1, double z1, BoxFace[] faces, double inflate = 0)
        {
            _x0 = x0 - inflate; _y0 = y0 - inflate; _z0 = z0 - inflate;
            _x1 = x1 + inflate; _y1 = y1 + inflate; _z1 = z1 + inflate;
            _faces = faces;
        }

        public BoxFace[] Faces() => _faces;

        /// <summary>Вершины куба: 0..3 — нижние, 4..7 — верхние.</summary>
        public (double X, double Y, double Z) Corner(int index) => index switch
        {
            0 => (_x0, _y0, _z1),
            1 => (_x1, _y0, _z1),
            2 => (_x1, _y0, _z0),
            3 => (_x0, _y0, _z0),
            4 => (_x0, _y1, _z1),
            5 => (_x1, _y1, _z1),
            6 => (_x1, _y1, _z0),
            _ => (_x0, _y1, _z0)
        };

        /// <summary>
        /// Раскладка как в модели Minecraft: от левого верхнего угла области идут
        /// верх (w×d), низ (w×d), право (d×h), перед (w×h), лево (d×h), зад (w×h).
        /// <paramref name="cellW"/> — ширина передней ячейки в текстуре: у slim-рук
        /// видимая грань 3 пикселя, но ячейка остаётся 4-пиксельной, как в классике.
        /// </summary>
        private static BoxFace[] Faces(int u, int v, int w, int h, int d, int? cellW = null)
        {
            var step = cellW ?? w;
            var top = (u + d, v);
            var bottom = (u + d + step, v);
            var right = (u, v + d);
            var front = (u + d, v + d);
            var left = (u + d + step, v + d);
            var back = (u + d + step + d, v + d);

            return
            [
                new(front, w, h, 1.00, [4, 5, 1, 0]),
                new(back, w, h, 0.80, [6, 7, 3, 2]),
                new(right, d, h, 0.90, [5, 6, 2, 1]),
                new(left, d, h, 0.86, [7, 4, 0, 3]),
                new(top, w, d, 1.00, [4, 5, 6, 7]),
                new(bottom, w, d, 0.65, [3, 2, 1, 0])
            ];
        }

        public static Box Head(bool hat) => hat
            ? new Box(-4, 24, -4, 4, 32, 4, Faces(32, 0, 8, 8, 8), 0.35)
            : new Box(-4, 24, -4, 4, 32, 4, Faces(0, 0, 8, 8, 8));

        public static Box Body(bool jacket) => jacket
            ? new Box(-4, 12, -2, 4, 24, 2, Faces(16, 32, 8, 12, 4), 0.35)
            : new Box(-4, 12, -2, 4, 24, 2, Faces(16, 16, 8, 12, 4));

        /// <summary>Руки и ноги 64×64; в slim-модели ширина рук — 3 пикселя.</summary>
        public static Box Limb(LimbKind kind, bool slim) => kind switch
        {
            // Правая рука игрока при взгляде спереди — слева на экране (X &lt; 0).
            LimbKind.RightArm => slim
                ? new Box(-8, 12, -2, -5, 24, 2, Faces(40, 16, 3, 12, 4, 4))
                : new Box(-8, 12, -2, -4, 24, 2, Faces(40, 16, 4, 12, 4)),
            LimbKind.LeftArm => slim
                ? new Box(5, 12, -2, 8, 24, 2, Faces(32, 48, 3, 12, 4, 4))
                : new Box(4, 12, -2, 8, 24, 2, Faces(32, 48, 4, 12, 4)),
            LimbKind.RightLeg => new Box(-4, 0, -2, 0, 12, 2, Faces(0, 16, 4, 12, 4)),
            _ => new Box(0, 0, -2, 4, 12, 2, Faces(16, 48, 4, 12, 4))
        };

        /// <summary>Второй слой конечностей (рукава/брюки): рукава повторяют slim или классику.</summary>
        public static Box LimbOverlay(LimbKind kind, bool slim) => kind switch
        {
            LimbKind.RightArm => slim
                ? new Box(-8, 12, -2, -5, 24, 2, Faces(40, 32, 3, 12, 4, 4), 0.35)
                : new Box(-8, 12, -2, -4, 24, 2, Faces(40, 32, 4, 12, 4), 0.35),
            LimbKind.LeftArm => slim
                ? new Box(5, 12, -2, 8, 24, 2, Faces(48, 48, 3, 12, 4, 4), 0.35)
                : new Box(4, 12, -2, 8, 24, 2, Faces(48, 48, 4, 12, 4), 0.35),
            LimbKind.RightLeg => new Box(-4, 0, -2, 0, 12, 2, Faces(0, 32, 4, 12, 4), 0.35),
            _ => new Box(0, 0, -2, 4, 12, 2, Faces(0, 48, 4, 12, 4), 0.35)
        };

        /// <summary>В старом формате 64×32 у левых конечностей нет своей области — берут правые.</summary>
        public static Box LimbLegacy(LimbKind kind) => kind switch
        {
            LimbKind.RightArm => new Box(-8, 12, -2, -4, 24, 2, Faces(40, 16, 4, 12, 4)),
            LimbKind.LeftArm => new Box(4, 12, -2, 8, 24, 2, Faces(40, 16, 4, 12, 4)),
            LimbKind.RightLeg => new Box(-4, 0, -2, 0, 12, 2, Faces(0, 16, 4, 12, 4)),
            _ => new Box(0, 0, -2, 4, 12, 2, Faces(0, 16, 4, 12, 4))
        };
    }

    /// <summary>
    /// Кубы модели в пикселях текстуры: центр по X=0, ступни на Y=0.
    /// Порядок важен: непрозрачные кубы, затем второй слой — он перекрывает базовый.
    /// </summary>
    private static Box[] BuildModel(bool legacy, bool slim) => legacy
        ?
        [
            Box.LimbLegacy(LimbKind.RightArm),
            Box.LimbLegacy(LimbKind.LeftArm),
            Box.LimbLegacy(LimbKind.RightLeg),
            Box.LimbLegacy(LimbKind.LeftLeg),
            Box.Body(false),
            Box.Head(false)
        ]
        :
        [
            Box.Limb(LimbKind.RightArm, slim),
            Box.Limb(LimbKind.LeftArm, slim),
            Box.Limb(LimbKind.RightLeg, slim: false),
            Box.Limb(LimbKind.LeftLeg, slim: false),
            Box.Body(false),
            Box.Head(false),
            Box.LimbOverlay(LimbKind.RightArm, slim),
            Box.LimbOverlay(LimbKind.LeftArm, slim),
            Box.LimbOverlay(LimbKind.RightLeg, slim: false),
            Box.LimbOverlay(LimbKind.LeftLeg, slim: false),
            Box.Body(true),
            Box.Head(true)
        ];
}
