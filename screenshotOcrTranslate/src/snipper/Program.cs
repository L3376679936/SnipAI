using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RubickSnipper
{
    static class Program
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [STAThread]
        static int Main(string[] args)
        {
            try { SetProcessDPIAware(); } catch { }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Bitmap fullScreenBmp = null;
            try
            {
                Rectangle bounds = SystemInformation.VirtualScreen;
                fullScreenBmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(fullScreenBmp))
                {
                    g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
                }

                using (SnipperForm form = new SnipperForm(fullScreenBmp, bounds))
                {
                    DialogResult res = form.ShowDialog();
                    if (res == DialogResult.OK && form.CroppedResult != null)
                    {
                        Clipboard.SetImage(form.CroppedResult);
                        form.CroppedResult.Dispose();

                        if (form.IsOcrRequested)
                        {
                            Console.WriteLine("ACTION:OCR");
                            return 10; // 退出码 10 代表触发 OCR 识别
                        }
                        else
                        {
                            Console.WriteLine("ACTION:COPY");
                            return 0; // 退出码 0 代表仅复制到剪贴板，不触发 OCR
                        }
                    }
                    else
                    {
                        if (form.IsColorCopied)
                        {
                            Console.WriteLine("ACTION:COPY_COLOR:" + form.CopiedColorString);
                            return 0; // 取色成功复制到剪贴板
                        }
                        Console.WriteLine("ACTION:CANCEL");
                        return 1; // 取消
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 2;
            }
            finally
            {
                if (fullScreenBmp != null)
                {
                    fullScreenBmp.Dispose();
                }
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }
    }

    public enum ToolType
    {
        None,
        Rect,
        Ellipse,
        Arrow,
        Brush,
        Mosaic,
        Text
    }

    public enum HitType
    {
        None,
        Move,
        TopLeft,
        Top,
        TopRight,
        Right,
        BottomRight,
        Bottom,
        BottomLeft,
        Left,
        ToolBar,
        SubBar,
        TextBody,
        TextHandleTopLeft,
        TextHandleTopRight,
        TextHandleBottomLeft,
        TextHandleBottomRight
    }

    public enum TextHandleType
    {
        None,
        Body,
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight
    }

    public abstract class AnnotationItem : IDisposable
    {
        public abstract void Draw(Graphics g);
        public abstract void Offset(int dx, int dy);
        public virtual void Dispose() { }
    }

    public class RectAnnotation : AnnotationItem
    {
        public Rectangle Rect;
        public Color Color;
        public float Width;

        public override void Draw(Graphics g)
        {
            if (Rect.Width <= 0 || Rect.Height <= 0) return;
            using (Pen p = new Pen(Color, Width))
            {
                g.DrawRectangle(p, Rect);
            }
        }

        public override void Offset(int dx, int dy)
        {
            Rect.Offset(dx, dy);
        }
    }

    public class EllipseAnnotation : AnnotationItem
    {
        public Rectangle Rect;
        public Color Color;
        public float Width;

        public override void Draw(Graphics g)
        {
            if (Rect.Width <= 0 || Rect.Height <= 0) return;
            using (Pen p = new Pen(Color, Width))
            {
                g.DrawEllipse(p, Rect);
            }
        }

        public override void Offset(int dx, int dy)
        {
            Rect.Offset(dx, dy);
        }
    }

    public class ArrowAnnotation : AnnotationItem
    {
        public Point Start;
        public Point End;
        public Color Color;
        public float Width;

        public override void Draw(Graphics g)
        {
            if (Start == End) return;
            using (Pen p = new Pen(Color, Width))
            {
                p.StartCap = LineCap.Round;
                p.EndCap = LineCap.Round;
                g.DrawLine(p, Start, End);

                float dx = End.X - Start.X;
                float dy = End.Y - Start.Y;
                float dist = (float)Math.Sqrt(dx * dx + dy * dy);
                if (dist > 6)
                {
                    float angle = (float)Math.Atan2(dy, dx);
                    float arrowLen = Math.Min(18f, Math.Max(10f, Width * 3.5f));
                    float arrowAngle = 0.45f;

                    PointF p1 = new PointF(
                        End.X - arrowLen * (float)Math.Cos(angle - arrowAngle),
                        End.Y - arrowLen * (float)Math.Sin(angle - arrowAngle));
                    PointF p2 = new PointF(
                        End.X - arrowLen * (float)Math.Cos(angle + arrowAngle),
                        End.Y - arrowLen * (float)Math.Sin(angle + arrowAngle));

                    using (SolidBrush b = new SolidBrush(Color))
                    {
                        GraphicsPath head = new GraphicsPath();
                        head.AddPolygon(new PointF[] { End, p1, p2 });
                        g.FillPath(b, head);
                    }
                }
            }
        }

        public override void Offset(int dx, int dy)
        {
            Start.Offset(dx, dy);
            End.Offset(dx, dy);
        }
    }

    public class BrushAnnotation : AnnotationItem
    {
        public List<Point> Points = new List<Point>();
        public Color Color;
        public float Width;

        public override void Draw(Graphics g)
        {
            if (Points.Count < 2) return;
            using (Pen p = new Pen(Color, Width))
            {
                p.StartCap = LineCap.Round;
                p.EndCap = LineCap.Round;
                p.LineJoin = LineJoin.Round;
                g.DrawLines(p, Points.ToArray());
            }
        }

        public override void Offset(int dx, int dy)
        {
            for (int i = 0; i < Points.Count; i++)
            {
                Point pt = Points[i];
                pt.Offset(dx, dy);
                Points[i] = pt;
            }
        }
    }

    public class MosaicAnnotation : AnnotationItem
    {
        public Rectangle Rect;
        public Bitmap MosaicSnippet;

        public override void Draw(Graphics g)
        {
            if (MosaicSnippet != null && Rect.Width > 0 && Rect.Height > 0)
            {
                g.DrawImage(MosaicSnippet, Rect);
            }
        }

        public override void Offset(int dx, int dy)
        {
            Rect.Offset(dx, dy);
        }

        public override void Dispose()
        {
            if (MosaicSnippet != null)
            {
                MosaicSnippet.Dispose();
                MosaicSnippet = null;
            }
        }
    }

    public class TextAnnotation : AnnotationItem
    {
        public Point Location;
        public string Text;
        public Font TextFont;
        public float FontSize;
        public Color Color;
        public bool IsSelected = false;

        public Rectangle GetBounds()
        {
            if (string.IsNullOrEmpty(Text) || TextFont == null) return Rectangle.Empty;
            Size sz = TextRenderer.MeasureText(Text, TextFont);
            return new Rectangle(Location.X - 4, Location.Y - 2, sz.Width + 8, sz.Height + 4);
        }

        public Rectangle GetHandleRect(TextHandleType handle)
        {
            Rectangle b = GetBounds();
            if (b.IsEmpty) return Rectangle.Empty;
            int hs = 8;
            switch (handle)
            {
                case TextHandleType.TopLeft:
                    return new Rectangle(b.Left - hs / 2, b.Top - hs / 2, hs, hs);
                case TextHandleType.TopRight:
                    return new Rectangle(b.Right - hs / 2, b.Top - hs / 2, hs, hs);
                case TextHandleType.BottomLeft:
                    return new Rectangle(b.Left - hs / 2, b.Bottom - hs / 2, hs, hs);
                case TextHandleType.BottomRight:
                    return new Rectangle(b.Right - hs / 2, b.Bottom - hs / 2, hs, hs);
                default:
                    return Rectangle.Empty;
            }
        }

        public TextHandleType HitTest(Point pt)
        {
            if (string.IsNullOrEmpty(Text) || TextFont == null) return TextHandleType.None;

            if (IsSelected)
            {
                if (GetHandleRect(TextHandleType.TopLeft).Contains(pt)) return TextHandleType.TopLeft;
                if (GetHandleRect(TextHandleType.TopRight).Contains(pt)) return TextHandleType.TopRight;
                if (GetHandleRect(TextHandleType.BottomLeft).Contains(pt)) return TextHandleType.BottomLeft;
                if (GetHandleRect(TextHandleType.BottomRight).Contains(pt)) return TextHandleType.BottomRight;
            }

            Rectangle b = GetBounds();
            if (b.Contains(pt)) return TextHandleType.Body;

            return TextHandleType.None;
        }

        public override void Draw(Graphics g)
        {
            if (string.IsNullOrEmpty(Text) || TextFont == null) return;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            // 1. Snipaste 核心质感：高对比度微阴影描边
            bool isDarkColor = (Color.R * 0.299 + Color.G * 0.587 + Color.B * 0.114) < 128;
            Color shadowColor = isDarkColor ? Color.FromArgb(160, 255, 255, 255) : Color.FromArgb(160, 15, 23, 42);

            using (SolidBrush shadowBrush = new SolidBrush(shadowColor))
            {
                g.DrawString(Text, TextFont, shadowBrush, Location.X + 1, Location.Y + 1);
            }

            // 2. 主体色文字
            using (SolidBrush mainBrush = new SolidBrush(Color))
            {
                g.DrawString(Text, TextFont, mainBrush, Location.X, Location.Y);
            }

            // 3. 选中状态：天蓝色虚线框与四个角手柄（支持同比例自由缩放）
            if (IsSelected)
            {
                Rectangle b = GetBounds();
                using (Pen dashPen = new Pen(Color.FromArgb(14, 165, 233), 1.5f))
                {
                    dashPen.DashStyle = DashStyle.Dash;
                    g.DrawRectangle(dashPen, b);
                }

                // 绘制四个角的手柄锚点 (8x8 纯白填充 + 蓝色描边)
                TextHandleType[] corners = new TextHandleType[]
                {
                    TextHandleType.TopLeft,
                    TextHandleType.TopRight,
                    TextHandleType.BottomLeft,
                    TextHandleType.BottomRight
                };

                using (SolidBrush handleFill = new SolidBrush(Color.White))
                using (Pen handleBorder = new Pen(Color.FromArgb(2, 132, 199), 1.5f))
                {
                    foreach (var h in corners)
                    {
                        Rectangle hr = GetHandleRect(h);
                        g.FillRectangle(handleFill, hr);
                        g.DrawRectangle(handleBorder, hr);
                    }
                }
            }
        }

        public override void Offset(int dx, int dy)
        {
            Location.Offset(dx, dy);
        }

        public override void Dispose()
        {
            if (TextFont != null)
            {
                TextFont.Dispose();
                TextFont = null;
            }
        }
    }

    public class SnipperForm : Form
    {
        private readonly Bitmap _fullBmp;
        private readonly Rectangle _screenBounds;

        private Point _startPoint;
        private Point _lastDragPoint;
        private bool _isSelecting = false;
        private bool _hasSelected = false;
        private Rectangle _selectedRect;

        // 8点拉伸与移动选区
        private HitType _currentHit = HitType.None;
        private bool _isResizingOrMoving = false;
        private Rectangle _initialRect;

        // 标注图元列表与重做栈
        private ToolType _currentTool = ToolType.None;
        private readonly List<AnnotationItem> _annotations = new List<AnnotationItem>();
        private readonly List<AnnotationItem> _redoStack = new List<AnnotationItem>();
        private AnnotationItem _tempAnnotation = null;

        // 文字标注高级交互（拖拽平移与四角同比例缩放）
        private TextBox _activeTextBox = null;
        private Point _textInputPos;
        private bool _isCommittingText = false;
        private TextAnnotation _selectedText = null;
        private TextHandleType _textManipulatingHandle = TextHandleType.None;
        private bool _isManipulatingText = false;
        private Point _textDragStartMouse;
        private Point _textInitialLocation;
        private float _textInitialFontSize;
        private Rectangle _textInitialBounds;

        // 样式配置：颜色、线宽与默认字号
        private Color _activeColor = Color.FromArgb(239, 68, 68); // 默认红
        private float _penWidth = 3f; // 细=2, 中=4, 粗=8
        private float _defaultFontSize = 18f;

        private static readonly Color[] PRESET_COLORS = new Color[]
        {
            Color.FromArgb(239, 68, 68),   // 红
            Color.FromArgb(249, 115, 22),  // 橙
            Color.FromArgb(234, 179, 8),   // 黄
            Color.FromArgb(34, 197, 94),   // 绿
            Color.FromArgb(59, 130, 246),  // 蓝
            Color.FromArgb(168, 85, 247),  // 紫
            Color.FromArgb(15, 23, 42),    // 黑
            Color.FromArgb(255, 255, 255)  // 白
        };

        private static readonly float[] PRESET_WIDTHS = new float[] { 2f, 4f, 8f };

        // 交互按钮定义
        private class ToolButton
        {
            public Rectangle Bounds;
            public string Id;
            public string Tooltip;
            public ToolType Tool;
            public bool IsDisabled;
            public Action ClickAction;
        }

        private class SubOptionItem
        {
            public Rectangle Bounds;
            public bool IsColor;
            public Color ColorValue;
            public float SizeValue;
            public string Label;
            public Action ClickAction;
        }

        private readonly List<ToolButton> _toolButtons = new List<ToolButton>();
        private readonly List<SubOptionItem> _subOptions = new List<SubOptionItem>();
        private Rectangle _toolBarBounds;
        private Rectangle _subBarBounds;
        private ToolButton _hoveredBtn = null;
        private SubOptionItem _hoveredSub = null;

        // 资源缓存与双缓冲
        private readonly SolidBrush _maskBrush = new SolidBrush(Color.FromArgb(130, 0, 0, 0));
        private readonly Pen _borderPen = new Pen(Color.FromArgb(59, 130, 246), 2);
        private readonly Font _uiFont = new Font("Microsoft YaHei", 9f);
        private readonly Font _tipFont = new Font("Segoe UI", 9f, FontStyle.Bold);

        public Bitmap CroppedResult { get; private set; }
        public bool IsOcrRequested { get; private set; }
        public bool IsColorCopied { get; private set; }
        public string CopiedColorString { get; private set; }

        // Snipaste 经典像素放大镜与取色 HUD
        private Point _currentMousePos;
        private bool _isColorHexMode = false;

        private Color GetPixelColorSafe(int x, int y)
        {
            if (_fullBmp == null) return Color.Black;
            if (x < 0 || x >= _fullBmp.Width || y < 0 || y >= _fullBmp.Height) return Color.Black;
            return _fullBmp.GetPixel(x, y);
        }

        public SnipperForm(Bitmap fullBmp, Rectangle bounds)
        {
            _fullBmp = fullBmp;
            _screenBounds = bounds;

            this.FormBorderStyle = FormBorderStyle.None;
            this.StartPosition = FormStartPosition.Manual;
            this.Location = bounds.Location;
            this.Size = bounds.Size;
            this.TopMost = true;
            this.ShowInTaskbar = false;
            this.DoubleBuffered = true;
            this.Cursor = Cursors.Cross;
            this.KeyPreview = true;
        }

        private HitType CheckHitTest(Point pt)
        {
            if (!_hasSelected) return HitType.None;

            if (_toolBarBounds.Contains(pt)) return HitType.ToolBar;
            if (HasSubBar() && _subBarBounds.Contains(pt)) return HitType.SubBar;

            // 优先检查当前选中的文字标注（四角手柄或主体）
            if (_selectedText != null)
            {
                TextHandleType th = _selectedText.HitTest(pt);
                switch (th)
                {
                    case TextHandleType.TopLeft: return HitType.TextHandleTopLeft;
                    case TextHandleType.TopRight: return HitType.TextHandleTopRight;
                    case TextHandleType.BottomLeft: return HitType.TextHandleBottomLeft;
                    case TextHandleType.BottomRight: return HitType.TextHandleBottomRight;
                    case TextHandleType.Body: return HitType.TextBody;
                }
            }

            // 检查其它未选中的文字标注主体
            for (int i = _annotations.Count - 1; i >= 0; i--)
            {
                TextAnnotation ta = _annotations[i] as TextAnnotation;
                if (ta != null && ta != _selectedText)
                {
                    if (ta.HitTest(pt) == TextHandleType.Body)
                    {
                        return HitType.TextBody;
                    }
                }
            }

            int handleSize = 10;
            Rectangle r = _selectedRect;

            Rectangle rTL = new Rectangle(r.Left - handleSize / 2, r.Top - handleSize / 2, handleSize, handleSize);
            Rectangle rTR = new Rectangle(r.Right - handleSize / 2, r.Top - handleSize / 2, handleSize, handleSize);
            Rectangle rBL = new Rectangle(r.Left - handleSize / 2, r.Bottom - handleSize / 2, handleSize, handleSize);
            Rectangle rBR = new Rectangle(r.Right - handleSize / 2, r.Bottom - handleSize / 2, handleSize, handleSize);

            Rectangle rT = new Rectangle(r.Left + r.Width / 2 - handleSize / 2, r.Top - handleSize / 2, handleSize, handleSize);
            Rectangle rB = new Rectangle(r.Left + r.Width / 2 - handleSize / 2, r.Bottom - handleSize / 2, handleSize, handleSize);
            Rectangle rL = new Rectangle(r.Left - handleSize / 2, r.Top + r.Height / 2 - handleSize / 2, handleSize, handleSize);
            Rectangle rR = new Rectangle(r.Right - handleSize / 2, r.Top + r.Height / 2 - handleSize / 2, handleSize, handleSize);

            if (rTL.Contains(pt)) return HitType.TopLeft;
            if (rTR.Contains(pt)) return HitType.TopRight;
            if (rBL.Contains(pt)) return HitType.BottomLeft;
            if (rBR.Contains(pt)) return HitType.BottomRight;
            if (rT.Contains(pt)) return HitType.Top;
            if (rB.Contains(pt)) return HitType.Bottom;
            if (rL.Contains(pt)) return HitType.Left;
            if (rR.Contains(pt)) return HitType.Right;

            int borderEdge = 5;
            if (Math.Abs(pt.Y - r.Top) <= borderEdge && pt.X >= r.Left && pt.X <= r.Right) return HitType.Top;
            if (Math.Abs(pt.Y - r.Bottom) <= borderEdge && pt.X >= r.Left && pt.X <= r.Right) return HitType.Bottom;
            if (Math.Abs(pt.X - r.Left) <= borderEdge && pt.Y >= r.Top && pt.Y <= r.Bottom) return HitType.Left;
            if (Math.Abs(pt.X - r.Right) <= borderEdge && pt.Y >= r.Top && pt.Y <= r.Bottom) return HitType.Right;

            if (r.Contains(pt))
            {
                if (_currentTool == ToolType.None)
                {
                    return HitType.Move;
                }
                return HitType.None;
            }

            return HitType.None;
        }

        private Cursor GetCursorForHit(HitType hit)
        {
            switch (hit)
            {
                case HitType.TopLeft:
                case HitType.BottomRight:
                case HitType.TextHandleTopLeft:
                case HitType.TextHandleBottomRight:
                    return Cursors.SizeNWSE;

                case HitType.TopRight:
                case HitType.BottomLeft:
                case HitType.TextHandleTopRight:
                case HitType.TextHandleBottomLeft:
                    return Cursors.SizeNESW;

                case HitType.Top:
                case HitType.Bottom:
                    return Cursors.SizeNS;

                case HitType.Left:
                case HitType.Right:
                    return Cursors.SizeWE;

                case HitType.Move:
                case HitType.TextBody:
                    return Cursors.SizeAll;

                case HitType.ToolBar:
                case HitType.SubBar:
                    return Cursors.Hand;

                default:
                    if (_currentTool == ToolType.Text) return Cursors.IBeam;
                    return _hasSelected && _currentTool != ToolType.None ? Cursors.Cross : Cursors.Cross;
            }
        }

        private bool HasSubBar()
        {
            return _hasSelected && (_currentTool == ToolType.Rect ||
                                    _currentTool == ToolType.Ellipse ||
                                    _currentTool == ToolType.Arrow ||
                                    _currentTool == ToolType.Brush ||
                                    _currentTool == ToolType.Text ||
                                    _selectedText != null);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                // 如果正在输入文字，点击其它区域先安全提交文字
                if (_activeTextBox != null)
                {
                    CommitActiveText();
                }

                // 1. 点击主工具栏
                if (_hasSelected && _toolBarBounds.Contains(e.Location))
                {
                    foreach (ToolButton btn in _toolButtons)
                    {
                        if (btn.Bounds.Contains(e.Location))
                        {
                            if (!btn.IsDisabled && btn.ClickAction != null)
                            {
                                btn.ClickAction();
                            }
                            UpdateBarsLayout();
                            Invalidate();
                            return;
                        }
                    }
                    return;
                }

                // 2. 点击二级属性栏（颜色/粗细）
                if (HasSubBar() && _subBarBounds.Contains(e.Location))
                {
                    foreach (SubOptionItem sub in _subOptions)
                    {
                        if (sub.Bounds.Contains(e.Location))
                        {
                            if (sub.ClickAction != null)
                            {
                                sub.ClickAction();
                            }
                            Invalidate();
                            return;
                        }
                    }
                    return;
                }

                // 3. 检查是否点击了文字标注（手柄缩放或主体平移）
                if (_hasSelected)
                {
                    // 3.1 优先检查已选中的文字标注
                    if (_selectedText != null)
                    {
                        TextHandleType th = _selectedText.HitTest(e.Location);
                        if (th != TextHandleType.None)
                        {
                            _isManipulatingText = true;
                            _textManipulatingHandle = th;
                            _textDragStartMouse = e.Location;
                            _textInitialLocation = _selectedText.Location;
                            _textInitialFontSize = _selectedText.FontSize;
                            _textInitialBounds = _selectedText.GetBounds();
                            Invalidate();
                            return;
                        }
                    }

                    // 3.2 检查是否点击了其它文字标注（将其设为选中）
                    TextAnnotation clickedTa = null;
                    for (int i = _annotations.Count - 1; i >= 0; i--)
                    {
                        TextAnnotation ta = _annotations[i] as TextAnnotation;
                        if (ta != null && ta.HitTest(e.Location) == TextHandleType.Body)
                        {
                            clickedTa = ta;
                            break;
                        }
                    }

                    if (clickedTa != null)
                    {
                        DeselectAllTexts();
                        clickedTa.IsSelected = true;
                        _selectedText = clickedTa;
                        _activeColor = clickedTa.Color;

                        _isManipulatingText = true;
                        _textManipulatingHandle = TextHandleType.Body;
                        _textDragStartMouse = e.Location;
                        _textInitialLocation = clickedTa.Location;
                        _textInitialFontSize = clickedTa.FontSize;
                        _textInitialBounds = clickedTa.GetBounds();

                        UpdateBarsLayout();
                        Invalidate();
                        return;
                    }
                }

                // 点击空白处取消文字选中
                if (_selectedText != null)
                {
                    DeselectAllTexts();
                    UpdateBarsLayout();
                    Invalidate();
                }

                // 4. 选区调整大小或平移
                HitType hit = CheckHitTest(e.Location);
                if (hit != HitType.None && hit != HitType.ToolBar && hit != HitType.SubBar)
                {
                    _currentHit = hit;
                    _isResizingOrMoving = true;
                    _startPoint = e.Location;
                    _lastDragPoint = e.Location;
                    _initialRect = _selectedRect;
                    return;
                }

                // 5. 文字标注工具：点击选区位置弹出输入框
                if (_hasSelected && _currentTool == ToolType.Text && _selectedRect.Contains(e.Location))
                {
                    SpawnTextInput(e.Location);
                    return;
                }

                // 6. 在选区内绘制其它图形标注
                if (_hasSelected && _currentTool != ToolType.None && _selectedRect.Contains(e.Location))
                {
                    _startPoint = e.Location;
                    _lastDragPoint = e.Location;
                    StartNewAnnotation(e.Location);
                    Invalidate();
                    return;
                }

                // 7. 点击外部重新框选
                _startPoint = e.Location;
                _lastDragPoint = e.Location;
                _isSelecting = true;
                _hasSelected = false;
                _currentTool = ToolType.None;
                ClearAnnotations();
                Invalidate();
            }
            else if (e.Button == MouseButtons.Right)
            {
                if (_activeTextBox != null)
                {
                    CancelActiveText();
                    return;
                }

                if (_selectedText != null)
                {
                    DeselectAllTexts();
                    UpdateBarsLayout();
                    Invalidate();
                    return;
                }

                if (_hasSelected)
                {
                    _hasSelected = false;
                    _selectedRect = Rectangle.Empty;
                    ClearAnnotations();
                    Invalidate();
                }
                else
                {
                    CancelCrop();
                }
            }
            base.OnMouseDown(e);
        }

        private void DeselectAllTexts()
        {
            foreach (var ann in _annotations)
            {
                TextAnnotation ta = ann as TextAnnotation;
                if (ta != null) ta.IsSelected = false;
            }
            _selectedText = null;
        }

        private void SpawnTextInput(Point pt)
        {
            if (_activeTextBox != null)
            {
                CommitActiveText();
            }

            _textInputPos = pt;
            int boxW = Math.Max(160, Math.Min(400, _selectedRect.Right - pt.X - 8));
            int boxH = (int)(_defaultFontSize * 2.2f) + 16;

            _activeTextBox = new TextBox();
            _activeTextBox.Multiline = true;
            _activeTextBox.WordWrap = true;
            _activeTextBox.Location = pt;
            _activeTextBox.Size = new Size(boxW, boxH);
            _activeTextBox.Font = new Font("Microsoft YaHei", _defaultFontSize, FontStyle.Bold);
            _activeTextBox.ForeColor = _activeColor;
            _activeTextBox.BackColor = Color.FromArgb(255, 255, 255);
            _activeTextBox.BorderStyle = BorderStyle.FixedSingle;

            // 动态测量随文字自适应撑开宽高
            _activeTextBox.TextChanged += (s, te) =>
            {
                if (_activeTextBox == null) return;
                try
                {
                    Size sz = TextRenderer.MeasureText(_activeTextBox.Text + " \n", _activeTextBox.Font);
                    int nw = Math.Max(boxW, Math.Min(_selectedRect.Right - pt.X - 4, sz.Width + 24));
                    int nh = Math.Max(boxH, Math.Min(_selectedRect.Bottom - pt.Y - 4, sz.Height + 16));
                    _activeTextBox.Size = new Size(nw, nh);
                }
                catch { }
            };

            _activeTextBox.KeyDown += (s, ke) =>
            {
                if (ke.Control && ke.KeyCode == Keys.Enter)
                {
                    ke.SuppressKeyPress = true;
                    CommitActiveText();
                }
                else if (ke.KeyCode == Keys.Escape)
                {
                    ke.SuppressKeyPress = true;
                    CancelActiveText();
                }
            };

            _activeTextBox.LostFocus += (s, le) =>
            {
                CommitActiveText();
            };

            this.Controls.Add(_activeTextBox);
            _activeTextBox.Focus();
        }

        private void CommitActiveText()
        {
            if (_isCommittingText) return;
            if (_activeTextBox == null) return;

            _isCommittingText = true;
            try
            {
                TextBox tb = _activeTextBox;
                _activeTextBox = null; // 立即断开引用，彻底根除重入 NullReferenceException

                string txt = tb.Text != null ? tb.Text.Trim() : "";
                if (!string.IsNullOrEmpty(txt))
                {
                    DeselectAllTexts();

                    Font f = new Font("Microsoft YaHei", _defaultFontSize, FontStyle.Bold);
                    TextAnnotation ta = new TextAnnotation
                    {
                        Location = _textInputPos,
                        Text = txt,
                        TextFont = f,
                        FontSize = _defaultFontSize,
                        Color = _activeColor,
                        IsSelected = true // 生成后自动选中，呈现拖拽与缩放锚点
                    };
                    _annotations.Add(ta);
                    _selectedText = ta;

                    ClearRedoStack();
                    UpdateBarsLayout();
                }

                this.Controls.Remove(tb);
                tb.Dispose();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("CommitActiveText exception: " + ex.Message);
            }
            finally
            {
                _isCommittingText = false;
                Invalidate();
            }
        }

        private void CancelActiveText()
        {
            if (_isCommittingText) return;
            if (_activeTextBox != null)
            {
                _isCommittingText = true;
                try
                {
                    TextBox tb = _activeTextBox;
                    _activeTextBox = null;
                    this.Controls.Remove(tb);
                    tb.Dispose();
                }
                catch { }
                finally
                {
                    _isCommittingText = false;
                    Invalidate();
                }
            }
        }

        private void StartNewAnnotation(Point pt)
        {
            switch (_currentTool)
            {
                case ToolType.Rect:
                    _tempAnnotation = new RectAnnotation { Rect = new Rectangle(pt, new Size(0, 0)), Color = _activeColor, Width = _penWidth };
                    break;
                case ToolType.Ellipse:
                    _tempAnnotation = new EllipseAnnotation { Rect = new Rectangle(pt, new Size(0, 0)), Color = _activeColor, Width = _penWidth };
                    break;
                case ToolType.Arrow:
                    _tempAnnotation = new ArrowAnnotation { Start = pt, End = pt, Color = _activeColor, Width = _penWidth };
                    break;
                case ToolType.Brush:
                    BrushAnnotation brush = new BrushAnnotation { Color = _activeColor, Width = _penWidth };
                    brush.Points.Add(pt);
                    _tempAnnotation = brush;
                    break;
                case ToolType.Mosaic:
                    _tempAnnotation = new MosaicAnnotation { Rect = new Rectangle(pt, new Size(0, 0)) };
                    break;
            }
        }

        private Bitmap CreateLocalMosaic(Bitmap sourceBmp, Rectangle area, int blockSize)
        {
            Rectangle safeArea = Rectangle.Intersect(new Rectangle(0, 0, sourceBmp.Width, sourceBmp.Height), area);
            if (safeArea.Width <= 0 || safeArea.Height <= 0) return null;

            Bitmap snippet = new Bitmap(safeArea.Width, safeArea.Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(snippet))
            {
                g.DrawImage(sourceBmp, new Rectangle(0, 0, safeArea.Width, safeArea.Height), safeArea, GraphicsUnit.Pixel);
            }

            BitmapData bmpData = snippet.LockBits(new Rectangle(0, 0, snippet.Width, snippet.Height),
                ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);

            int stride = bmpData.Stride;
            int width = bmpData.Width;
            int height = bmpData.Height;
            IntPtr scan0 = bmpData.Scan0;

            byte[] pixelBuffer = new byte[stride * height];
            Marshal.Copy(scan0, pixelBuffer, 0, pixelBuffer.Length);

            for (int y = 0; y < height; y += blockSize)
            {
                for (int x = 0; x < width; x += blockSize)
                {
                    int blockW = Math.Min(blockSize, width - x);
                    int blockH = Math.Min(blockSize, height - y);

                    long bSum = 0, gSum = 0, rSum = 0;
                    int count = blockW * blockH;

                    for (int by = 0; by < blockH; by++)
                    {
                        int rowIdx = (y + by) * stride;
                        for (int bx = 0; bx < blockW; bx++)
                        {
                            int idx = rowIdx + (x + bx) * 4;
                            bSum += pixelBuffer[idx];
                            gSum += pixelBuffer[idx + 1];
                            rSum += pixelBuffer[idx + 2];
                        }
                    }

                    byte avgB = (byte)(bSum / count);
                    byte avgG = (byte)(gSum / count);
                    byte avgR = (byte)(rSum / count);

                    for (int by = 0; by < blockH; by++)
                    {
                        int rowIdx = (y + by) * stride;
                        for (int bx = 0; bx < blockW; bx++)
                        {
                            int idx = rowIdx + (x + bx) * 4;
                            pixelBuffer[idx] = avgB;
                            pixelBuffer[idx + 1] = avgG;
                            pixelBuffer[idx + 2] = avgR;
                            pixelBuffer[idx + 3] = 255;
                        }
                    }
                }
            }

            Marshal.Copy(pixelBuffer, 0, scan0, pixelBuffer.Length);
            snippet.UnlockBits(bmpData);
            return snippet;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            _currentMousePos = e.Location;
            if (!_isSelecting && !_hasSelected)
            {
                Invalidate();
            }

            if (_hasSelected && _activeTextBox == null)
            {
                ToolButton foundBtn = null;
                if (_toolBarBounds.Contains(e.Location))
                {
                    foreach (ToolButton btn in _toolButtons)
                    {
                        if (btn.Bounds.Contains(e.Location))
                        {
                            foundBtn = btn;
                            break;
                        }
                    }
                }
                if (_hoveredBtn != foundBtn)
                {
                    _hoveredBtn = foundBtn;
                    Invalidate();
                }

                SubOptionItem foundSub = null;
                if (HasSubBar() && _subBarBounds.Contains(e.Location))
                {
                    foreach (SubOptionItem sub in _subOptions)
                    {
                        if (sub.Bounds.Contains(e.Location))
                        {
                            foundSub = sub;
                            break;
                        }
                    }
                }
                if (_hoveredSub != foundSub)
                {
                    _hoveredSub = foundSub;
                    Invalidate();
                }

                // 检查文字手柄并更新光标
                if (!_isSelecting && !_isResizingOrMoving && _tempAnnotation == null && !_isManipulatingText)
                {
                    HitType hit = CheckHitTest(e.Location);
                    this.Cursor = GetCursorForHit(hit);
                }
            }

            if (_isSelecting)
            {
                _lastDragPoint = e.Location;
                _selectedRect = GetNormalizedRect(_startPoint, _lastDragPoint);
                Invalidate();
            }
            else if (_isManipulatingText && _selectedText != null)
            {
                // 文字高级操作：平移移动或四角等比缩放
                if (_textManipulatingHandle == TextHandleType.Body)
                {
                    // 平移移动
                    int dx = e.Location.X - _textDragStartMouse.X;
                    int dy = e.Location.Y - _textDragStartMouse.Y;

                    int newX = _textInitialLocation.X + dx;
                    int newY = _textInitialLocation.Y + dy;

                    // 限制在截图选区内部
                    newX = Math.Max(_selectedRect.Left + 2, Math.Min(_selectedRect.Right - 20, newX));
                    newY = Math.Max(_selectedRect.Top + 2, Math.Min(_selectedRect.Bottom - 20, newY));

                    _selectedText.Location = new Point(newX, newY);
                    Invalidate();
                }
                else
                {
                    // 四个角同比例缩放文字大小
                    Point anchorPoint;
                    switch (_textManipulatingHandle)
                    {
                        case TextHandleType.TopLeft:
                            anchorPoint = new Point(_textInitialBounds.Right, _textInitialBounds.Bottom);
                            break;
                        case TextHandleType.TopRight:
                            anchorPoint = new Point(_textInitialBounds.Left, _textInitialBounds.Bottom);
                            break;
                        case TextHandleType.BottomLeft:
                            anchorPoint = new Point(_textInitialBounds.Right, _textInitialBounds.Top);
                            break;
                        case TextHandleType.BottomRight:
                        default:
                            anchorPoint = new Point(_textInitialBounds.Left, _textInitialBounds.Top);
                            break;
                    }

                    // 计算鼠标到不动锚点的距离比
                    float initDist = (float)Math.Sqrt(Math.Pow(_textDragStartMouse.X - anchorPoint.X, 2) + Math.Pow(_textDragStartMouse.Y - anchorPoint.Y, 2));
                    float currDist = (float)Math.Sqrt(Math.Pow(e.Location.X - anchorPoint.X, 2) + Math.Pow(e.Location.Y - anchorPoint.Y, 2));

                    if (initDist > 4f)
                    {
                        float scaleRatio = currDist / initDist;
                        float newFontSize = (float)Math.Round(_textInitialFontSize * scaleRatio, 1);
                        newFontSize = Math.Max(10f, Math.Min(96f, newFontSize));

                        if (Math.Abs(newFontSize - _selectedText.FontSize) > 0.4f)
                        {
                            _selectedText.FontSize = newFontSize;
                            if (_selectedText.TextFont != null) _selectedText.TextFont.Dispose();
                            _selectedText.TextFont = new Font("Microsoft YaHei", newFontSize, FontStyle.Bold);
                        }
                    }
                    Invalidate();
                }
            }
            else if (_isResizingOrMoving)
            {
                int dx = e.Location.X - _startPoint.X;
                int dy = e.Location.Y - _startPoint.Y;

                if (_currentHit == HitType.Move)
                {
                    int stepX = e.Location.X - _lastDragPoint.X;
                    int stepY = e.Location.Y - _lastDragPoint.Y;
                    _lastDragPoint = e.Location;

                    int newX = _selectedRect.X + stepX;
                    int newY = _selectedRect.Y + stepY;
                    newX = Math.Max(0, Math.Min(this.Width - _selectedRect.Width, newX));
                    newY = Math.Max(0, Math.Min(this.Height - _selectedRect.Height, newY));

                    int actualDx = newX - _selectedRect.X;
                    int actualDy = newY - _selectedRect.Y;

                    _selectedRect.X = newX;
                    _selectedRect.Y = newY;

                    if (actualDx != 0 || actualDy != 0)
                    {
                        foreach (var ann in _annotations)
                        {
                            ann.Offset(actualDx, actualDy);
                        }
                    }
                }
                else
                {
                    Rectangle r = _initialRect;
                    int left = r.Left;
                    int top = r.Top;
                    int right = r.Right;
                    int bottom = r.Bottom;

                    switch (_currentHit)
                    {
                        case HitType.TopLeft:
                            left += dx; top += dy; break;
                        case HitType.Top:
                            top += dy; break;
                        case HitType.TopRight:
                            right += dx; top += dy; break;
                        case HitType.Right:
                            right += dx; break;
                        case HitType.BottomRight:
                            right += dx; bottom += dy; break;
                        case HitType.Bottom:
                            bottom += dy; break;
                        case HitType.BottomLeft:
                            left += dx; bottom += dy; break;
                        case HitType.Left:
                            left += dx; break;
                    }

                    int finalX = Math.Min(left, right);
                    int finalY = Math.Min(top, bottom);
                    int finalW = Math.Abs(right - left);
                    int finalH = Math.Abs(bottom - top);

                    _selectedRect = new Rectangle(finalX, finalY, finalW, finalH);
                }

                UpdateBarsLayout();
                Invalidate();
            }
            else if (_tempAnnotation != null)
            {
                _lastDragPoint = e.Location;

                RectAnnotation ra = _tempAnnotation as RectAnnotation;
                if (ra != null) ra.Rect = GetNormalizedRect(_startPoint, _lastDragPoint);

                EllipseAnnotation ea = _tempAnnotation as EllipseAnnotation;
                if (ea != null) ea.Rect = GetNormalizedRect(_startPoint, _lastDragPoint);

                ArrowAnnotation aa = _tempAnnotation as ArrowAnnotation;
                if (aa != null) aa.End = _lastDragPoint;

                BrushAnnotation ba = _tempAnnotation as BrushAnnotation;
                if (ba != null) ba.Points.Add(_lastDragPoint);

                MosaicAnnotation ma = _tempAnnotation as MosaicAnnotation;
                if (ma != null) ma.Rect = GetNormalizedRect(_startPoint, _lastDragPoint);

                Invalidate();
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (_isSelecting)
            {
                _isSelecting = false;
                _selectedRect = GetNormalizedRect(_startPoint, _lastDragPoint);
                if (_selectedRect.Width > 15 && _selectedRect.Height > 15)
                {
                    _hasSelected = true;
                    UpdateBarsLayout();
                }
                else
                {
                    _hasSelected = false;
                    _selectedRect = Rectangle.Empty;
                }
                Invalidate();
            }
            else if (_isManipulatingText)
            {
                _isManipulatingText = false;
                _textManipulatingHandle = TextHandleType.None;
                UpdateBarsLayout();
                Invalidate();
            }
            else if (_isResizingOrMoving)
            {
                _isResizingOrMoving = false;
                _currentHit = HitType.None;
                UpdateBarsLayout();
                Invalidate();
            }
            else if (_tempAnnotation != null)
            {
                MosaicAnnotation ma = _tempAnnotation as MosaicAnnotation;
                if (ma != null && ma.Rect.Width > 4 && ma.Rect.Height > 4)
                {
                    ma.MosaicSnippet = CreateLocalMosaic(_fullBmp, ma.Rect, 10);
                }

                _annotations.Add(_tempAnnotation);
                _tempAnnotation = null;

                ClearRedoStack();
                UpdateBarsLayout();
                Invalidate();
            }
            base.OnMouseUp(e);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            if (_hasSelected && _selectedRect.Contains(e.Location) && !_toolBarBounds.Contains(e.Location))
            {
                // 双击选区直接完成并复制到剪贴板，平稳退出
                ConfirmCrop(false);
            }
            base.OnMouseDoubleClick(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            // 取色器快捷操作 (框选前有效)
            if (!_isSelecting && !_hasSelected)
            {
                if (e.KeyCode == Keys.ShiftKey || e.KeyCode == Keys.Shift)
                {
                    _isColorHexMode = !_isColorHexMode;
                    Invalidate();
                    return;
                }
                else if (e.KeyCode == Keys.C)
                {
                    Color c = GetPixelColorSafe(_currentMousePos.X, _currentMousePos.Y);
                    string colorStr = _isColorHexMode
                        ? string.Format("#{0:X2}{1:X2}{2:X2}", c.R, c.G, c.B)
                        : string.Format("{0}, {1}, {2}", c.R, c.G, c.B);
                    Clipboard.SetText(colorStr);
                    this.IsColorCopied = true;
                    this.CopiedColorString = colorStr;
                    CancelCrop();
                    return;
                }
            }

            if (e.KeyCode == Keys.Escape)
            {
                if (_activeTextBox != null)
                {
                    CancelActiveText();
                    return;
                }
                if (_selectedText != null)
                {
                    DeselectAllTexts();
                    UpdateBarsLayout();
                    Invalidate();
                    return;
                }
                CancelCrop();
            }
            else if (e.KeyCode == Keys.Enter)
            {
                if (_activeTextBox != null)
                {
                    CommitActiveText();
                    return;
                }
                if (_hasSelected)
                {
                    // 回车快捷键：复制到剪贴板
                    ConfirmCrop(false);
                }
            }
            else if (e.Control && e.KeyCode == Keys.Z)
            {
                if (_activeTextBox == null)
                {
                    PerformUndo();
                }
            }
            else if (e.Control && e.KeyCode == Keys.Y)
            {
                if (_activeTextBox == null)
                {
                    PerformRedo();
                }
            }
            else if (e.Control && e.KeyCode == Keys.S)
            {
                if (_hasSelected && _activeTextBox == null)
                {
                    SaveToFile();
                }
            }
            else if (e.Control && e.KeyCode == Keys.C)
            {
                if (_hasSelected && _activeTextBox == null)
                {
                    ConfirmCrop(false);
                }
            }
            else if (e.KeyCode == Keys.Delete || e.KeyCode == Keys.Back)
            {
                if (_selectedText != null && _activeTextBox == null)
                {
                    _annotations.Remove(_selectedText);
                    _selectedText.Dispose();
                    _selectedText = null;
                    UpdateBarsLayout();
                    Invalidate();
                }
            }
            base.OnKeyDown(e);
        }

        private void PerformUndo()
        {
            if (_annotations.Count > 0)
            {
                int lastIdx = _annotations.Count - 1;
                AnnotationItem item = _annotations[lastIdx];
                _annotations.RemoveAt(lastIdx);
                _redoStack.Add(item);

                if (item == _selectedText)
                {
                    _selectedText = null;
                }

                UpdateBarsLayout();
                Invalidate();
            }
        }

        private void PerformRedo()
        {
            if (_redoStack.Count > 0)
            {
                int lastIdx = _redoStack.Count - 1;
                AnnotationItem item = _redoStack[lastIdx];
                _redoStack.RemoveAt(lastIdx);
                _annotations.Add(item);

                UpdateBarsLayout();
                Invalidate();
            }
        }

        private void CancelCrop()
        {
            this.CroppedResult = null;
            this.DialogResult = DialogResult.Cancel;
            this.Close();
        }

        private void UpdateBarsLayout()
        {
            _toolButtons.Clear();
            _subOptions.Clear();

            if (!_hasSelected) return;

            int btnSize = 28;
            int margin = 3;

            // 工具栏定义：取消(cancel) 紧邻 确定(ok)，后跟独立的 识别文字(ocr) 按钮
            var defs = new[]
            {
                new { Id = "rect",    Tip = "矩形", Tool = ToolType.Rect },
                new { Id = "ellipse", Tip = "椭圆", Tool = ToolType.Ellipse },
                new { Id = "arrow",   Tip = "箭头", Tool = ToolType.Arrow },
                new { Id = "brush",   Tip = "画笔", Tool = ToolType.Brush },
                new { Id = "mosaic",  Tip = "马赛克", Tool = ToolType.Mosaic },
                new { Id = "text",    Tip = "文字标注 (可拖拽平移与四角自由缩放)", Tool = ToolType.Text },
                new { Id = "sep1",    Tip = "", Tool = ToolType.None },
                new { Id = "undo",    Tip = "撤销 (Ctrl+Z)", Tool = ToolType.None },
                new { Id = "redo",    Tip = "重做/下一步 (Ctrl+Y)", Tool = ToolType.None },
                new { Id = "sep2",    Tip = "", Tool = ToolType.None },
                new { Id = "save",    Tip = "保存图片 (Ctrl+S)", Tool = ToolType.None },
                new { Id = "cancel",  Tip = "放弃截图 (Esc)", Tool = ToolType.None },
                new { Id = "ok",      Tip = "确定截图并复制到剪贴板 (Enter/双击)", Tool = ToolType.None },
                new { Id = "ocr",     Tip = "识别文字并流式翻译", Tool = ToolType.None }
            };

            int totalWidth = 8;
            foreach (var d in defs)
            {
                totalWidth += (d.Id.StartsWith("sep") ? 6 : (d.Id == "ocr" ? 36 : btnSize)) + margin;
            }
            totalWidth += 6;

            int barH = btnSize + 10;
            int barX = _selectedRect.Right - totalWidth;
            if (barX < 8) barX = 8;
            if (barX + totalWidth > this.Width - 8) barX = this.Width - totalWidth - 8;

            int barY = _selectedRect.Bottom + 10;
            if (barY + barH + 40 > this.Height)
            {
                barY = _selectedRect.Top - barH - 10;
                if (barY < 8) barY = _selectedRect.Top + 8;
            }

            _toolBarBounds = new Rectangle(barX, barY, totalWidth, barH);

            int curX = barX + 6;
            foreach (var d in defs)
            {
                int w = d.Id.StartsWith("sep") ? 6 : (d.Id == "ocr" ? 36 : btnSize);
                Rectangle r = new Rectangle(curX, barY + 5, w, btnSize);
                Action act = null;
                bool disabled = false;

                if (d.Id == "rect")
                {
                    act = delegate { _currentTool = (_currentTool == ToolType.Rect) ? ToolType.None : ToolType.Rect; DeselectAllTexts(); };
                }
                else if (d.Id == "ellipse")
                {
                    act = delegate { _currentTool = (_currentTool == ToolType.Ellipse) ? ToolType.None : ToolType.Ellipse; DeselectAllTexts(); };
                }
                else if (d.Id == "arrow")
                {
                    act = delegate { _currentTool = (_currentTool == ToolType.Arrow) ? ToolType.None : ToolType.Arrow; DeselectAllTexts(); };
                }
                else if (d.Id == "brush")
                {
                    act = delegate { _currentTool = (_currentTool == ToolType.Brush) ? ToolType.None : ToolType.Brush; DeselectAllTexts(); };
                }
                else if (d.Id == "mosaic")
                {
                    act = delegate { _currentTool = (_currentTool == ToolType.Mosaic) ? ToolType.None : ToolType.Mosaic; DeselectAllTexts(); };
                }
                else if (d.Id == "text")
                {
                    act = delegate { _currentTool = (_currentTool == ToolType.Text) ? ToolType.None : ToolType.Text; };
                }
                else if (d.Id == "undo")
                {
                    disabled = (_annotations.Count == 0);
                    act = delegate { PerformUndo(); };
                }
                else if (d.Id == "redo")
                {
                    disabled = (_redoStack.Count == 0);
                    act = delegate { PerformRedo(); };
                }
                else if (d.Id == "save")
                {
                    act = delegate { SaveToFile(); };
                }
                else if (d.Id == "cancel")
                {
                    act = delegate { CancelCrop(); };
                }
                else if (d.Id == "ok")
                {
                    // 确定截图：仅复制到剪贴板，不触发 OCR
                    act = delegate { ConfirmCrop(false); };
                }
                else if (d.Id == "ocr")
                {
                    // 识别文字：复制到剪贴板并触发 OCR 识别
                    act = delegate { ConfirmCrop(true); };
                }

                _toolButtons.Add(new ToolButton
                {
                    Bounds = r,
                    Id = d.Id,
                    Tooltip = d.Tip,
                    Tool = d.Tool,
                    IsDisabled = disabled,
                    ClickAction = act
                });

                curX += w + margin;
            }

            // 布局二级属性栏：画笔显示粗细+颜色；文字标注无需死板字号，仅显示 8 种高品质颜色圆点
            if (HasSubBar())
            {
                bool isTextMode = (_currentTool == ToolType.Text || _selectedText != null);

                int subH = 34;
                int subW = isTextMode
                    ? (10 + (PRESET_COLORS.Length * 24 + 4) + 8)
                    : (8 + (3 * 26 + 6) + 10 + (PRESET_COLORS.Length * 24 + 4) + 8);

                int subX = _toolBarBounds.Left;
                if (subX + subW > this.Width - 8) subX = this.Width - subW - 8;

                int subY = _toolBarBounds.Bottom + 5;
                if (subY + subH > this.Height - 8)
                {
                    subY = _toolBarBounds.Top - subH - 5;
                }

                _subBarBounds = new Rectangle(subX, subY, subW, subH);

                int sx = subX + 8;
                int sy = subY + 5;

                // 若非文字模式，才展示画笔粗细三档（文字字号由用户直接按住四个角自由缩放，交互完全合理）
                if (!isTextMode)
                {
                    string[] labels = new string[] { "细", "中", "粗" };
                    for (int i = 0; i < PRESET_WIDTHS.Length; i++)
                    {
                        float szVal = PRESET_WIDTHS[i];
                        string lbl = labels[i];
                        Rectangle br = new Rectangle(sx, sy, 24, 24);
                        _subOptions.Add(new SubOptionItem
                        {
                            Bounds = br,
                            IsColor = false,
                            SizeValue = szVal,
                            Label = lbl,
                            ClickAction = delegate
                            {
                                _penWidth = szVal;
                            }
                        });
                        sx += 26;
                    }
                    sx += 8;
                }

                // 8种高品质现代预设颜色
                for (int i = 0; i < PRESET_COLORS.Length; i++)
                {
                    Color c = PRESET_COLORS[i];
                    Rectangle cr = new Rectangle(sx, sy + 2, 20, 20);
                    _subOptions.Add(new SubOptionItem
                    {
                        Bounds = cr,
                        IsColor = true,
                        ColorValue = c,
                        ClickAction = delegate
                        {
                            _activeColor = c;
                            if (_activeTextBox != null)
                            {
                                _activeTextBox.ForeColor = c;
                            }
                            if (_selectedText != null)
                            {
                                _selectedText.Color = c;
                            }
                        }
                    });
                    sx += 24;
                }
            }
        }

        private void SaveToFile()
        {
            using (Bitmap result = GenerateFinalBitmap())
            {
                using (SaveFileDialog sfd = new SaveFileDialog())
                {
                    sfd.Filter = "PNG 图像|*.png|JPEG 图像|*.jpg";
                    sfd.FileName = "Screenshot_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png";
                    if (sfd.ShowDialog() == DialogResult.OK)
                    {
                        result.Save(sfd.FileName);
                        ConfirmCrop(false);
                    }
                }
            }
        }

        private Bitmap GenerateFinalBitmap()
        {
            Bitmap cropped = new Bitmap(_selectedRect.Width, _selectedRect.Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(cropped))
            {
                g.DrawImage(_fullBmp,
                    new Rectangle(0, 0, _selectedRect.Width, _selectedRect.Height),
                    _selectedRect,
                    GraphicsUnit.Pixel);

                g.TranslateTransform(-_selectedRect.Left, -_selectedRect.Top);

                // 导出前先取消文字选中手柄的绘制
                bool prevSelected = false;
                if (_selectedText != null)
                {
                    prevSelected = _selectedText.IsSelected;
                    _selectedText.IsSelected = false;
                }

                foreach (AnnotationItem ann in _annotations)
                {
                    ann.Draw(g);
                }
                if (_tempAnnotation != null)
                {
                    _tempAnnotation.Draw(g);
                }

                if (_selectedText != null)
                {
                    _selectedText.IsSelected = prevSelected;
                }
            }
            return cropped;
        }

        private void ConfirmCrop(bool isOcr)
        {
            if (_selectedRect.Width <= 0 || _selectedRect.Height <= 0) return;
            this.CroppedResult = GenerateFinalBitmap();
            this.IsOcrRequested = isOcr;
            this.DialogResult = DialogResult.OK;
            this.Close();
        }

        private Rectangle GetNormalizedRect(Point p1, Point p2)
        {
            int x = Math.Min(p1.X, p2.X);
            int y = Math.Min(p1.Y, p2.Y);
            int w = Math.Abs(p1.X - p2.X);
            int h = Math.Abs(p1.Y - p2.Y);
            return new Rectangle(x, y, w, h);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;

            g.DrawImageUnscaled(_fullBmp, 0, 0);

            if (_isSelecting || _hasSelected)
            {
                Rectangle r = _selectedRect;
                if (r.Top > 0) g.FillRectangle(_maskBrush, 0, 0, this.Width, r.Top);
                if (this.Height > r.Bottom) g.FillRectangle(_maskBrush, 0, r.Bottom, this.Width, this.Height - r.Bottom);
                if (r.Left > 0 && r.Height > 0) g.FillRectangle(_maskBrush, 0, r.Top, r.Left, r.Height);
                if (this.Width > r.Right && r.Height > 0) g.FillRectangle(_maskBrush, r.Right, r.Top, this.Width - r.Right, r.Height);

                // 全程绘制高对比度鲜明边框线 (无论正在拖拽拉框还是已框选完毕)
                if (r.Width > 0 && r.Height > 0)
                {
                    using (Pen outerPen = new Pen(Color.FromArgb(2, 132, 199), 2f)) // 鲜艳天空蓝
                    {
                        g.DrawRectangle(outerPen, r);
                    }
                    using (Pen innerPen = new Pen(Color.FromArgb(160, 255, 255, 255), 1f)) // 内侧微光白描边
                    {
                        Rectangle inner = new Rectangle(r.X + 1, r.Y + 1, Math.Max(0, r.Width - 2), Math.Max(0, r.Height - 2));
                        g.DrawRectangle(innerPen, inner);
                    }

                    // 实时浮动尺寸胶囊提示 (宽 × 高 像素)
                    if (r.Width > 20 && r.Height > 15)
                    {
                        string sizeTip = string.Format("{0} × {1}", r.Width, r.Height);
                        using (Font tipFont = new Font("Segoe UI", 9f, FontStyle.Bold))
                        {
                            SizeF tipSize = g.MeasureString(sizeTip, tipFont);
                            int tipH = (int)tipSize.Height + 6;
                            int tipW = (int)tipSize.Width + 14;

                            int tipX = r.Left;
                            if (tipX + tipW > this.Width - 4) tipX = this.Width - tipW - 4;
                            if (tipX < 4) tipX = 4;

                            int tipY = r.Top - tipH - 4;
                            if (tipY < 4) tipY = r.Top + 6; // 靠顶时向内贴合

                            Rectangle tipRect = new Rectangle(tipX, tipY, tipW, tipH);
                            using (GraphicsPath tipPath = CreateRoundedRectanglePath(tipRect, 3))
                            using (SolidBrush tipBg = new SolidBrush(Color.FromArgb(230, 15, 23, 42)))
                            using (SolidBrush tipText = new SolidBrush(Color.White))
                            {
                                g.FillPath(tipBg, tipPath);
                                g.DrawString(sizeTip, tipFont, tipText, tipX + 7, tipY + 3);
                            }
                        }
                    }
                }
            }
            else
            {
                g.FillRectangle(_maskBrush, 0, 0, this.Width, this.Height);
            }

            foreach (AnnotationItem ann in _annotations)
            {
                ann.Draw(g);
            }
            if (_tempAnnotation != null)
            {
                _tempAnnotation.Draw(g);
            }

            if (_hasSelected)
            {
                DrawHandlePoints(g, _selectedRect);

                DrawToolBar(g);

                if (HasSubBar())
                {
                    DrawSubBar(g);
                }
            }
            else if (!_isSelecting && !_hasSelected)
            {
                DrawColorPickerMagnifier(g, _currentMousePos);
            }
        }

        private void DrawColorPickerMagnifier(Graphics g, Point pt)
        {
            int gridCols = 17;
            int gridRows = 11;
            int cellSize = 8;
            int magW = gridCols * cellSize; // 136
            int magH = gridRows * cellSize; // 88

            int hudW = magW + 8; // 144
            int hudH = magH + 88; // 176

            // 智能避让屏幕边缘（默认在鼠标右下方，靠近边缘时自动翻转到左侧或上方）
            int hx = pt.X + 18;
            int hy = pt.Y + 18;

            if (hx + hudW > this.Width - 6) hx = pt.X - hudW - 18;
            if (hy + hudH > this.Height - 6) hy = pt.Y - hudH - 18;

            if (hx < 4) hx = 4;
            if (hy < 4) hy = 4;

            // 1. 整体微阴影与纯黑卡片底
            Rectangle cardRect = new Rectangle(hx, hy, hudW, hudH);
            DrawCardShadow(g, cardRect, 3);
            using (SolidBrush bg = new SolidBrush(Color.FromArgb(248, 10, 15, 26)))
            using (Pen borderPen = new Pen(Color.FromArgb(100, 148, 163, 184), 1f))
            {
                g.FillRectangle(bg, cardRect);
                g.DrawRectangle(borderPen, cardRect);
            }

            // 2. 放大镜采样像素格绘制
            int magX = hx + 4;
            int magY = hy + 4;
            Rectangle magRect = new Rectangle(magX, magY, magW, magH);

            int centerCol = gridCols / 2; // 8
            int centerRow = gridRows / 2; // 5

            for (int r = 0; r < gridRows; r++)
            {
                int sy = pt.Y + (r - centerRow);
                for (int c = 0; c < gridCols; c++)
                {
                    int sx = pt.X + (c - centerCol);
                    Color pxColor = GetPixelColorSafe(sx, sy);
                    using (SolidBrush pb = new SolidBrush(pxColor))
                    {
                        g.FillRectangle(pb, magX + c * cellSize, magY + r * cellSize, cellSize, cellSize);
                    }
                }
            }

            // 3. 像素网格浅细线 (微弱半透明)
            using (Pen gridPen = new Pen(Color.FromArgb(24, 255, 255, 255), 1f))
            {
                for (int c = 1; c < gridCols; c++)
                {
                    g.DrawLine(gridPen, magX + c * cellSize, magY, magX + c * cellSize, magY + magH);
                }
                for (int r = 1; r < gridRows; r++)
                {
                    g.DrawLine(gridPen, magX, magY + r * cellSize, magX + magW, magY + r * cellSize);
                }
            }

            // 4. 十字瞄准微光导轨 (高亮整行整列)
            using (SolidBrush crossBrush = new SolidBrush(Color.FromArgb(50, 59, 130, 246)))
            {
                g.FillRectangle(crossBrush, magX, magY + centerRow * cellSize, magW, cellSize);
                g.FillRectangle(crossBrush, magX + centerCol * cellSize, magY, cellSize, magH);
            }

            // 5. 正中心准心框 (8x8 小方框，白内黑外高对比度)
            Rectangle centerCell = new Rectangle(magX + centerCol * cellSize, magY + centerRow * cellSize, cellSize, cellSize);
            using (Pen wp = new Pen(Color.White, 1.5f))
            using (Pen bp = new Pen(Color.Black, 1f))
            {
                g.DrawRectangle(wp, centerCell);
                g.DrawRectangle(bp, centerCell.X - 1, centerCell.Y - 1, centerCell.Width + 2, centerCell.Height + 2);
            }

            // 放大镜外围 1.2px 纯白边框
            using (Pen magBorder = new Pen(Color.White, 1.2f))
            {
                g.DrawRectangle(magBorder, magRect);
            }

            // 6. 坐标与颜色信息区
            Color centerColor = GetPixelColorSafe(pt.X, pt.Y);
            int infoY = magY + magH + 8;

            // 6.1 居中显示坐标 (X , Y)
            string coordStr = string.Format("({0} , {1})", pt.X, pt.Y);
            using (Font cf = new Font("Segoe UI", 9.5f, FontStyle.Bold))
            using (SolidBrush cw = new SolidBrush(Color.White))
            {
                SizeF csz = g.MeasureString(coordStr, cf);
                g.DrawString(coordStr, cf, cw, hx + (hudW - csz.Width) / 2, infoY);
            }

            infoY += 19;

            // 6.2 颜色方块 + 颜色数值 (例如 "16,  16,  16" 或 "#101010")
            string colorValStr = _isColorHexMode
                ? string.Format("#{0:X2}{1:X2}{2:X2}", centerColor.R, centerColor.G, centerColor.B)
                : string.Format("{0},  {1},  {2}", centerColor.R, centerColor.G, centerColor.B);

            int sampleBoxSize = 11;
            using (Font vf = new Font("Segoe UI", 9.5f, FontStyle.Bold))
            using (SolidBrush vw = new SolidBrush(Color.White))
            using (SolidBrush cb = new SolidBrush(centerColor))
            using (Pen cbp = new Pen(Color.White, 1f))
            {
                SizeF vsz = g.MeasureString(colorValStr, vf);
                int lineW = sampleBoxSize + 7 + (int)vsz.Width;
                int lineX = hx + (hudW - lineW) / 2;

                g.FillRectangle(cb, lineX, infoY + 2, sampleBoxSize, sampleBoxSize);
                g.DrawRectangle(cbp, lineX, infoY + 2, sampleBoxSize, sampleBoxSize);
                g.DrawString(colorValStr, vf, vw, lineX + sampleBoxSize + 7, infoY);
            }

            infoY += 21;

            // 6.3 快捷键操作提示行 (按 C 复制颜色值 / 按 Shift 切换 RGB/HEX)
            using (Font tf = new Font("Microsoft YaHei", 8f))
            using (SolidBrush tw = new SolidBrush(Color.FromArgb(220, 255, 255, 255)))
            {
                string tip1 = "按 C 复制颜色值";
                string tip2 = "按 Shift 切换 RGB/HEX";
                SizeF t1sz = g.MeasureString(tip1, tf);
                SizeF t2sz = g.MeasureString(tip2, tf);
                g.DrawString(tip1, tf, tw, hx + (hudW - t1sz.Width) / 2, infoY);
                g.DrawString(tip2, tf, tw, hx + (hudW - t2sz.Width) / 2, infoY + 14);
            }
        }

        private void DrawHandlePoints(Graphics g, Rectangle r)
        {
            int s = 8;
            Point[] pts = new Point[] {
                new Point(r.Left - s/2, r.Top - s/2),
                new Point(r.Left + r.Width/2 - s/2, r.Top - s/2),
                new Point(r.Right - s/2, r.Top - s/2),
                new Point(r.Left - s/2, r.Top + r.Height/2 - s/2),
                new Point(r.Right - s/2, r.Top + r.Height/2 - s/2),
                new Point(r.Left - s/2, r.Bottom - s/2),
                new Point(r.Left + r.Width/2 - s/2, r.Bottom - s/2),
                new Point(r.Right - s/2, r.Bottom - s/2)
            };

            using (SolidBrush b = new SolidBrush(Color.White))
            using (Pen p = new Pen(Color.FromArgb(59, 130, 246), 2f))
            {
                foreach (Point pt in pts)
                {
                    g.FillRectangle(b, pt.X, pt.Y, s, s);
                    g.DrawRectangle(p, pt.X, pt.Y, s, s);
                }
            }
        }

        private void DrawCardShadow(Graphics g, Rectangle rect, int radius)
        {
            for (int i = 3; i >= 1; i--)
            {
                using (Pen sp = new Pen(Color.FromArgb(10 * (4 - i), 0, 0, 0), 1))
                using (GraphicsPath path = CreateRoundedRectanglePath(new Rectangle(rect.X - i, rect.Y - i + 1, rect.Width + i * 2, rect.Height + i * 2), radius + 1))
                {
                    g.DrawPath(sp, path);
                }
            }
        }

        private static GraphicsPath CreateRoundedRectanglePath(Rectangle rect, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int d = radius * 2;
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private void DrawToolBar(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;

            DrawCardShadow(g, _toolBarBounds, 6);

            using (GraphicsPath path = CreateRoundedRectanglePath(_toolBarBounds, 6))
            using (SolidBrush bg = new SolidBrush(Color.FromArgb(252, 255, 255, 255)))
            using (Pen border = new Pen(Color.FromArgb(226, 232, 240), 1))
            {
                g.FillPath(bg, path);
                g.DrawPath(border, path);
            }

            foreach (ToolButton btn in _toolButtons)
            {
                if (btn.Id.StartsWith("sep"))
                {
                    using (Pen sp = new Pen(Color.FromArgb(226, 232, 240), 1))
                    {
                        int mx = btn.Bounds.Left + btn.Bounds.Width / 2;
                        g.DrawLine(sp, mx, btn.Bounds.Top + 6, mx, btn.Bounds.Bottom - 6);
                    }
                    continue;
                }

                bool isActive = (_currentTool != ToolType.None && btn.Tool == _currentTool);
                bool isHover = (btn == _hoveredBtn && !btn.IsDisabled);

                if (isActive || isHover)
                {
                    Color bgCol;
                    if (btn.Id == "ocr")
                    {
                        bgCol = Color.FromArgb(238, 242, 255); // 醒目微紫蓝
                    }
                    else if (btn.Id == "cancel")
                    {
                        bgCol = Color.FromArgb(254, 226, 226);
                    }
                    else if (isActive)
                    {
                        bgCol = Color.FromArgb(224, 242, 254);
                    }
                    else
                    {
                        bgCol = Color.FromArgb(241, 245, 249);
                    }

                    Color borderCol = isActive ? Color.FromArgb(186, 230, 253) : Color.Transparent;

                    using (GraphicsPath bp = CreateRoundedRectanglePath(btn.Bounds, 4))
                    using (SolidBrush bb = new SolidBrush(bgCol))
                    using (Pen bpPen = new Pen(borderCol, 1))
                    {
                        g.FillPath(bb, bp);
                        if (isActive) g.DrawPath(bpPen, bp);
                    }
                }

                Color iconColor;
                if (btn.IsDisabled)
                {
                    iconColor = Color.FromArgb(203, 213, 225);
                }
                else if (btn.Id == "ocr")
                {
                    iconColor = isHover ? Color.FromArgb(67, 56, 202) : Color.FromArgb(79, 70, 229);
                }
                else if (btn.Id == "cancel" && isHover)
                {
                    iconColor = Color.FromArgb(220, 38, 38);
                }
                else if (isActive)
                {
                    iconColor = Color.FromArgb(2, 132, 199);
                }
                else if (isHover)
                {
                    iconColor = Color.FromArgb(30, 41, 59);
                }
                else
                {
                    iconColor = Color.FromArgb(100, 116, 139);
                }

                DrawVectorIcon(g, btn.Id, btn.Bounds, iconColor);
            }

            if (_hoveredBtn != null && !string.IsNullOrEmpty(_hoveredBtn.Tooltip) && !_hoveredBtn.IsDisabled)
            {
                using (Font tipF = new Font("Microsoft YaHei", 8.5f))
                using (SolidBrush tipBg = new SolidBrush(Color.FromArgb(235, 15, 23, 42)))
                using (SolidBrush tipText = new SolidBrush(Color.White))
                {
                    SizeF sz = g.MeasureString(_hoveredBtn.Tooltip, tipF);
                    float tx = _hoveredBtn.Bounds.Left + (_hoveredBtn.Bounds.Width - sz.Width) / 2;
                    float ty = _toolBarBounds.Top - sz.Height - 8;

                    Rectangle tipRect = new Rectangle((int)tx - 6, (int)ty - 3, (int)sz.Width + 12, (int)sz.Height + 6);
                    using (GraphicsPath tp = CreateRoundedRectanglePath(tipRect, 4))
                    {
                        g.FillPath(tipBg, tp);
                    }
                    g.DrawString(_hoveredBtn.Tooltip, tipF, tipText, tx, ty);
                }
            }
        }

        private void DrawSubBar(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;

            DrawCardShadow(g, _subBarBounds, 5);

            using (GraphicsPath path = CreateRoundedRectanglePath(_subBarBounds, 5))
            using (SolidBrush bg = new SolidBrush(Color.FromArgb(252, 255, 255, 255)))
            using (Pen border = new Pen(Color.FromArgb(226, 232, 240), 1))
            {
                g.FillPath(bg, path);
                g.DrawPath(border, path);
            }

            foreach (SubOptionItem sub in _subOptions)
            {
                if (!sub.IsColor)
                {
                    bool isSelected = (Math.Abs(_penWidth - sub.SizeValue) < 0.1f);
                    bool isHover = (sub == _hoveredSub);

                    if (isSelected || isHover)
                    {
                        Color hbg = isSelected ? Color.FromArgb(224, 242, 254) : Color.FromArgb(241, 245, 249);
                        using (GraphicsPath hp = CreateRoundedRectanglePath(sub.Bounds, 4))
                        using (SolidBrush hb = new SolidBrush(hbg))
                        {
                            g.FillPath(hb, hp);
                        }
                    }

                    int cx = sub.Bounds.Left + sub.Bounds.Width / 2;
                    int cy = sub.Bounds.Top + sub.Bounds.Height / 2;
                    Color itemColor = isSelected ? Color.FromArgb(2, 132, 199) : Color.FromArgb(71, 85, 105);

                    int dotSize = (int)(sub.SizeValue * 1.5f + 4);
                    using (SolidBrush db = new SolidBrush(itemColor))
                    {
                        g.FillEllipse(db, cx - dotSize / 2, cy - dotSize / 2, dotSize, dotSize);
                    }
                }
                else
                {
                    bool isSelected = (_activeColor == sub.ColorValue);
                    bool isHover = (sub == _hoveredSub);

                    using (SolidBrush cb = new SolidBrush(sub.ColorValue))
                    {
                        g.FillEllipse(cb, sub.Bounds);
                    }

                    using (Pen cp = new Pen(Color.FromArgb(203, 213, 225), 1))
                    {
                        g.DrawEllipse(cp, sub.Bounds);
                    }

                    if (isSelected)
                    {
                        using (Pen ringPen = new Pen(Color.FromArgb(2, 132, 199), 2f))
                        {
                            Rectangle ring = new Rectangle(sub.Bounds.X - 3, sub.Bounds.Y - 3, sub.Bounds.Width + 6, sub.Bounds.Height + 6);
                            g.DrawEllipse(ringPen, ring);
                        }
                    }
                    else if (isHover)
                    {
                        using (Pen ringPen = new Pen(Color.FromArgb(148, 163, 184), 1.5f))
                        {
                            Rectangle ring = new Rectangle(sub.Bounds.X - 2, sub.Bounds.Y - 2, sub.Bounds.Width + 4, sub.Bounds.Height + 4);
                            g.DrawEllipse(ringPen, ring);
                        }
                    }
                }
            }
        }

        private void DrawVectorIcon(Graphics g, string id, Rectangle r, Color color)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int cx = r.Left + r.Width / 2;
            int cy = r.Top + r.Height / 2;

            using (Pen p = new Pen(color, 2f))
            {
                p.StartCap = LineCap.Round;
                p.EndCap = LineCap.Round;

                switch (id)
                {
                    case "rect":
                        g.DrawRectangle(p, cx - 7, cy - 6, 14, 12);
                        break;
                    case "ellipse":
                        g.DrawEllipse(p, cx - 7, cy - 6, 14, 12);
                        break;
                    case "arrow":
                        using (AdjustableArrowCap cap = new AdjustableArrowCap(4, 4, true))
                        {
                            p.CustomEndCap = cap;
                            g.DrawLine(p, cx - 6, cy + 5, cx + 5, cy - 6);
                        }
                        break;
                    case "brush":
                        g.DrawLine(p, cx - 5, cy + 5, cx + 4, cy - 4);
                        using (SolidBrush b = new SolidBrush(color))
                        {
                            g.FillEllipse(b, cx + 2, cy - 7, 5, 5);
                        }
                        break;
                    case "mosaic":
                        using (SolidBrush b = new SolidBrush(color))
                        {
                            g.FillRectangle(b, cx - 6, cy - 6, 5, 5);
                            g.FillRectangle(b, cx + 1, cy - 6, 5, 5);
                            g.FillRectangle(b, cx - 6, cy + 1, 5, 5);
                            g.FillRectangle(b, cx + 1, cy + 1, 5, 5);
                        }
                        break;
                    case "text":
                        // 绘制文字标注 "T" 图标
                        g.DrawLine(p, cx - 6, cy - 6, cx + 6, cy - 6);
                        g.DrawLine(p, cx, cy - 6, cx, cy + 6);
                        g.DrawLine(p, cx - 3, cy + 6, cx + 3, cy + 6);
                        break;
                    case "undo":
                        g.DrawArc(p, cx - 7, cy - 5, 13, 11, 0, -210);
                        g.DrawLine(p, cx - 7, cy - 1, cx - 7, cy + 4);
                        g.DrawLine(p, cx - 7, cy - 1, cx - 2, cy - 1);
                        break;
                    case "redo":
                        g.DrawArc(p, cx - 6, cy - 5, 13, 11, 180, 210);
                        g.DrawLine(p, cx + 7, cy - 1, cx + 7, cy + 4);
                        g.DrawLine(p, cx + 7, cy - 1, cx + 2, cy - 1);
                        break;
                    case "save":
                        g.DrawRectangle(p, cx - 6, cy - 6, 12, 12);
                        using (SolidBrush b = new SolidBrush(color))
                        {
                            g.FillRectangle(b, cx - 3, cy - 5, 6, 3);
                            g.FillRectangle(b, cx - 3, cy + 1, 6, 3);
                        }
                        break;
                    case "cancel":
                        // 放弃截图叉号 (✕)
                        g.DrawLine(p, cx - 5, cy - 5, cx + 5, cy + 5);
                        g.DrawLine(p, cx + 5, cy - 5, cx - 5, cy + 5);
                        break;
                    case "ok":
                        // 确定截图并复制对勾 (✔)
                        using (Pen okPen = new Pen(Color.FromArgb(16, 185, 129), 2.5f))
                        {
                            okPen.StartCap = LineCap.Round;
                            okPen.EndCap = LineCap.Round;
                            g.DrawLine(okPen, cx - 6, cy, cx - 2, cy + 4);
                            g.DrawLine(okPen, cx - 2, cy + 4, cx + 6, cy - 5);
                        }
                        break;
                    case "ocr":
                        // 专门的 OCR 识别按钮：精致文字方块图标带有 "文" 或 "OCR"
                        using (Font ocrFont = new Font("Microsoft YaHei", 8.5f, FontStyle.Bold))
                        using (SolidBrush ob = new SolidBrush(color))
                        {
                            string ocrLabel = "文";
                            SizeF sz = g.MeasureString(ocrLabel, ocrFont);
                            g.DrawString(ocrLabel, ocrFont, ob, cx - sz.Width / 2 + 1, cy - sz.Height / 2);
                        }
                        // 周围带一个微圆角方框，呈现专业 OCR 功能质感
                        using (Pen ocrBoxPen = new Pen(color, 1.2f))
                        {
                            g.DrawRectangle(ocrBoxPen, cx - 11, cy - 10, 21, 20);
                        }
                        break;
                }
            }
        }

        private void ClearAnnotations()
        {
            CancelActiveText();
            DeselectAllTexts();
            foreach (var ann in _annotations) ann.Dispose();
            _annotations.Clear();
            ClearRedoStack();
            if (_tempAnnotation != null)
            {
                _tempAnnotation.Dispose();
                _tempAnnotation = null;
            }
        }

        private void ClearRedoStack()
        {
            foreach (var r in _redoStack) r.Dispose();
            _redoStack.Clear();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                CancelActiveText();
                ClearAnnotations();
                _maskBrush.Dispose();
                _borderPen.Dispose();
                _uiFont.Dispose();
                _tipFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
