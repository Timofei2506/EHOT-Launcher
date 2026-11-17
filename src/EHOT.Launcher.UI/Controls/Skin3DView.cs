using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace EHOT.Launcher.UI.Controls;

/// <summary>
/// Объёмное превью скина Minecraft: персонаж собирается из тех же областей текстуры,
/// что использует игра (голова 8×8×8, тело 8×12×4, руки и ноги 4×12×4), и вращается мышью.
/// <para>
/// Каждая грань закрашивается по настоящей текстуре — попиксельно, с учётом поворота,
/// поэтому видно лицо, одежду и слой шляпы/куртки. Поддерживаются форматы 64×64 и 64×32.
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

    /// <summary>Сколько пикселей текстуры в одном пункте модели (масштаб фигуры).</summary>
    private const double PixelsPerPoint = 34.0;

    private uint[]? _pixels;
    private int _texWidth;
    private int _texHeight;
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
        _pixels = null;
        _texWidth = 0;
        _texHeight = 0;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            InvalidateVisual();
            return;
        }

        Bitmap? bitmap = null;
        try
        {
            bitmap = new Bitmap(path);
            var size = bitmap.PixelSize;
            var buffer = new uint[size.Width * size.Height];
            var handle = System.Runtime.InteropServices.GCHandle.Alloc(
                buffer, System.Runtime.InteropServices.GCHandleType.Pinned);
            try
            {
                bitmap.CopyPixels(new PixelRect(0, 0, size.Width, size.Height),
                    handle.AddrOfPinnedObject(), buffer.Length * 4, size.Width * 4);
            }
            finally
            {
                handle.Free();
            }

            // CopyPixels отдаёт пиксели в порядке RGBA (uint = A<<24|B<<16|G<<8|R),
            // переставлять каналы не нужно — любая «нормализация» ломает цвета.
            _pixels = buffer;
            _texWidth = size.Width;
            _texHeight = size.Height;
        }
        catch (IOException)
        {
            _pixels = null;
        }
        catch (UnauthorizedAccessException)
        {
            _pixels = null;
        }
        finally
        {
            bitmap?.Dispose();
            InvalidateVisual();
        }
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
        Cursor = Cursor.Default;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(new SolidColorBrush(Color.Parse("#111828")), bounds, 14);

        if (_pixels is null || _texWidth == 0)
        {
            DrawPlaceholder(context, bounds);
            return;
        }

        // Фигура вытянутая (16×32), поэтому масштаб ограничивают и ширина, и высота.
        var scale = Math.Min(bounds.Width / 22.0, bounds.Height / PixelsPerPoint);
        var yaw = Yaw * Math.PI / 180.0;
        var pitch = Pitch * Math.PI / 180.0;

        var faces = new List<Face>();
        foreach (var box in BuildModel(_texHeight <= 32))
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

    private static void DrawPlaceholder(DrawingContext context, Rect bounds)
    {
        var size = Math.Min(bounds.Width, bounds.Height) * 0.45;
        context.DrawRectangle(new SolidColorBrush(Color.Parse("#252E45")), null,
            new Rect(bounds.Center.X - (size / 2), bounds.Center.Y - (size / 2), size, size), 10, 10);
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
                // Модель 32 пикселя в высоту; её середина (Y=16) совпадает с центром контрола.
                corners[i] = new Point(
                    center.X + (projected.X * scale),
                    center.Y - ((projected.Y - 16) * scale));
            }

            faces.Add(new Face(corners, face, depth / 4));
        }
    }

    /// <summary>
    /// Грань закрашивается по текстуре: каждый тексель превращается в четырёхугольник
    /// (проекция ортогональная, поэтому билинейная интерполяция точна) и группируется по цвету.
    /// </summary>
    private void DrawFace(DrawingContext context, Face face)
    {
        var (u0, v0) = face.Source.Uv;
        var w = face.Source.W;
        var h = face.Source.H;
        var brushes = new Dictionary<uint, SolidColorBrush>();

        // Подложка средним цветом области закрывает тёмные швы от сглаживания:
        // края соседних квадов полупрозрачны, и без подложки сквозь них виден фон.
        var underlay = FaceAverage(u0, v0, w, h, face.Source.Light);
        if (underlay is null)
        {
            return;
        }

        context.DrawGeometry(underlay, null, Polygon(face.Corners));

        for (var j = 0; j < h; j++)
        {
            for (var i = 0; i < w; i++)
            {
                var argb = Texel(u0 + i, v0 + j);
                var alpha = (argb >> 24) & 0xFF;
                if (alpha < 8)
                {
                    continue;
                }

                var shaded = ApplyLight(argb, face.Source.Light);
                if (!brushes.TryGetValue(shaded, out var brush))
                {
                    brush = new SolidColorBrush(Color.FromArgb(
                        (byte)(shaded >> 24), (byte)(shaded >> 16), (byte)(shaded >> 8), (byte)shaded));
                    brushes[shaded] = brush;
                }

                var s0 = (double)i / w;
                var s1 = (double)(i + 1) / w;
                var t0 = (double)j / h;
                var t1 = (double)(j + 1) / h;

                context.DrawGeometry(brush, null, Polygon(
                [
                    Bilinear(face.Corners, s0, t0),
                    Bilinear(face.Corners, s1, t0),
                    Bilinear(face.Corners, s1, t1),
                    Bilinear(face.Corners, s0, t1)
                ]));
            }
        }
    }

    /// <summary>Средний непрозрачный цвет области текстуры с учётом освещения грани.</summary>
    private SolidColorBrush? FaceAverage(int u0, int v0, int w, int h, double light)
    {
        long r = 0, g = 0, b = 0, count = 0;
        for (var j = 0; j < h; j++)
        {
            for (var i = 0; i < w; i++)
            {
                var argb = Texel(u0 + i, v0 + j);
                if (((argb >> 24) & 0xFF) < 8)
                {
                    continue;
                }

                r += (argb >> 16) & 0xFF;
                g += (argb >> 8) & 0xFF;
                b += argb & 0xFF;
                count++;
            }
        }

        if (count == 0)
        {
            return null;
        }

        var shaded = ApplyLight(0xFF000000 | ((uint)(r / count) << 16) | ((uint)(g / count) << 8) | (uint)(b / count), light);
        return new SolidColorBrush(Color.FromArgb(
            (byte)(shaded >> 24), (byte)(shaded >> 16), (byte)(shaded >> 8), (byte)shaded));
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

    /// <summary>Точка внутри четырёхугольника: A — левый верх, B — правый верх, D — левый низ.</summary>
    private static Point Bilinear(Point[] corners, double s, double t)
    {
        var top = corners[0] + ((corners[1] - corners[0]) * s);
        var bottom = corners[3] + ((corners[2] - corners[3]) * s);
        return top + ((bottom - top) * t);
    }

    private uint Texel(int x, int y)
    {
        if (_pixels is null || x < 0 || y < 0 || x >= _texWidth || y >= _texHeight)
        {
            return 0;
        }

        return _pixels[(y * _texWidth) + x];
    }

    /// <summary>Освещение по граням, как в игре: верх светлее, низ темнее.</summary>
    private static uint ApplyLight(uint argb, double light)
    {
        byte Scale(byte channel) => (byte)Math.Clamp(channel * light, 0, 255);
        var a = (byte)((argb >> 24) & 0xFF);
        return ((uint)a << 24)
               | ((uint)Scale((byte)((argb >> 16) & 0xFF)) << 16)
               | ((uint)Scale((byte)((argb >> 8) & 0xFF)) << 8)
               | Scale((byte)(argb & 0xFF));
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

        private Box(double x0, double y0, double z0, double x1, double y1, double z1, BoxFace[] faces)
        {
            _x0 = x0; _y0 = y0; _z0 = z0;
            _x1 = x1; _y1 = y1; _z1 = z1;
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
        /// </summary>
        private static BoxFace[] Faces(int u, int v, int w, int h, int d)
        {
            var top = (u + d, v);
            var bottom = (u + d + w, v);
            var right = (u, v + d);
            var front = (u + d, v + d);
            var left = (u + d + w, v + d);
            var back = (u + d + w + d, v + d);

            return
            [
                new(front, w, h, 1.00, [4, 5, 1, 0]),
                // Задняя грань видна с другой стороны, поэтому её текстурная ось U
                // должна идти в противоположную сторону — иначе рисунок зеркалится.
                new(back, w, h, 0.78, [6, 7, 3, 2]),
                new(right, d, h, 0.90, [5, 6, 2, 1]),
                new(left, d, h, 0.90, [7, 4, 0, 3]),
                new(top, w, d, 1.14, [4, 5, 6, 7]),
                new(bottom, w, d, 0.62, [3, 2, 1, 0])
            ];
        }

        public static Box Head(bool hat) => hat
            // слой шляпы — тот же куб, развёртка на 32 пикселя правее и на 8 ниже
            ? new Box(-4, 24, -4, 4, 32, 4, Faces(32, 0, 8, 8, 8))
            : new Box(-4, 24, -4, 4, 32, 4, Faces(0, 0, 8, 8, 8));

        public static Box Body(bool jacket) => jacket
            ? new Box(-4, 12, -2, 4, 24, 2, Faces(16, 32, 8, 12, 4))
            : new Box(-4, 12, -2, 4, 24, 2, Faces(16, 16, 8, 12, 4));

        /// <summary>Руки и ноги в формате 64×64: у каждой конечности своя область.</summary>
        public static Box Limb(LimbKind kind) => kind switch
        {
            // Правая рука игрока при взгляде спереди — слева на экране (X &lt; 0).
            LimbKind.RightArm => new Box(-8, 12, -2, -4, 24, 2, Faces(40, 16, 4, 12, 4)),
            LimbKind.LeftArm => new Box(4, 12, -2, 8, 24, 2, Faces(32, 48, 4, 12, 4)),
            LimbKind.RightLeg => new Box(-4, 0, -2, 0, 12, 2, Faces(0, 16, 4, 12, 4)),
            _ => new Box(0, 0, -2, 4, 12, 2, Faces(16, 48, 4, 12, 4))
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
    /// В старом формате 64×32 нет слоёв шляпы/куртки и отдельных левых конечностей.
    /// </summary>
    private static Box[] BuildModel(bool legacy) => legacy
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
            // Сначала непрозрачные кубы, потом слои шляпы/куртки — они рисуются поверх.
            Box.Limb(LimbKind.RightArm),
            Box.Limb(LimbKind.LeftArm),
            Box.Limb(LimbKind.RightLeg),
            Box.Limb(LimbKind.LeftLeg),
            Box.Body(false),
            Box.Head(false),
            Box.Body(true),
            Box.Head(true)
        ];
}
