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
                        return 0; // 成功
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

            return 1;
        }
    }

    public enum ToolType
    {
        None,
        Rect,
        Ellipse,
        Arrow,
        Brush,
        Mosaic
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
        SubBar
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
                using (AdjustableArrowCap cap = new AdjustableArrowCap(Width * 1.5f + 3f, Width * 1.5f + 3f, true))
                {
                    p.CustomEndCap = cap;
                    g.DrawLine(p, Start, End);
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

    public class SnipperForm : Form
    {
        private readonly Bitmap _fullBmp;
        private readonly Rectangle _screenBounds;

        private Point _startPoint;
        private Point _lastDragPoint;
        private bool _isSelecting = false;
        private bool _hasSelected = false;
        private Rectangle _selectedRect;

        // 8点与移动控制
        private HitType _currentHit = HitType.None;
        private bool _isResizingOrMoving = false;
        private Rectangle _initialRect;

        // 标注与撤销/重做
        private ToolType _currentTool = ToolType.None;
        private readonly List<AnnotationItem> _annotations = new List<AnnotationItem>();
        private readonly List<AnnotationItem> _redoStack = new List<AnnotationItem>();
        private AnnotationItem _tempAnnotation = null;

        // 颜色与线宽
        private Color _activeColor = Color.FromArgb(239, 68, 68); // 默认红
        private float _penWidth = 3f; // 细=2, 中=4, 粗=8

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

        // 交互按钮
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
            public float WidthValue;
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

            // 1. 命中工具栏或二级属性栏
            if (_toolBarBounds.Contains(pt)) return HitType.ToolBar;
            if (HasSubBar() && _subBarBounds.Contains(pt)) return HitType.SubBar;

            int handleSize = 10;
            Rectangle r = _selectedRect;

            // 8 个控制把手
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

            // 4条边框附近 (容差 5px)
            int borderEdge = 5;
            if (Math.Abs(pt.Y - r.Top) <= borderEdge && pt.X >= r.Left && pt.X <= r.Right) return HitType.Top;
            if (Math.Abs(pt.Y - r.Bottom) <= borderEdge && pt.X >= r.Left && pt.X <= r.Right) return HitType.Bottom;
            if (Math.Abs(pt.X - r.Left) <= borderEdge && pt.Y >= r.Top && pt.Y <= r.Bottom) return HitType.Left;
            if (Math.Abs(pt.X - r.Right) <= borderEdge && pt.Y >= r.Top && pt.Y <= r.Bottom) return HitType.Right;

            // 选区内部
            if (r.Contains(pt))
            {
                if (_currentTool == ToolType.None)
                {
                    return HitType.Move;
                }
                return HitType.None; // 处于标注绘制模式
            }

            return HitType.None;
        }

        private Cursor GetCursorForHit(HitType hit)
        {
            switch (hit)
            {
                case HitType.TopLeft:
                case HitType.BottomRight:
                    return Cursors.SizeNWSE;
                case HitType.TopRight:
                case HitType.BottomLeft:
                    return Cursors.SizeNESW;
                case HitType.Top:
                case HitType.Bottom:
                    return Cursors.SizeNS;
                case HitType.Left:
                case HitType.Right:
                    return Cursors.SizeWE;
                case HitType.Move:
                    return Cursors.SizeAll;
                case HitType.ToolBar:
                case HitType.SubBar:
                    return Cursors.Hand;
                default:
                    return _hasSelected && _currentTool != ToolType.None ? Cursors.Cross : Cursors.Cross;
            }
        }

        private bool HasSubBar()
        {
            return _hasSelected && (_currentTool == ToolType.Rect ||
                                    _currentTool == ToolType.Ellipse ||
                                    _currentTool == ToolType.Arrow ||
                                    _currentTool == ToolType.Brush);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
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

                // 3. 选区调整大小或平移
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

                // 4. 在选区内绘制标注
                if (_hasSelected && _currentTool != ToolType.None && _selectedRect.Contains(e.Location))
                {
                    _startPoint = e.Location;
                    _lastDragPoint = e.Location;
                    StartNewAnnotation(e.Location);
                    Invalidate();
                    return;
                }

                // 5. 点击外部重新框选
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
                if (_hasSelected)
                {
                    _hasSelected = false;
                    _selectedRect = Rectangle.Empty;
                    ClearAnnotations();
                    Invalidate();
                }
                else
                {
                    this.DialogResult = DialogResult.Cancel;
                    this.Close();
                }
            }
            base.OnMouseDown(e);
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

        protected override void OnMouseMove(MouseEventArgs e)
        {
            // 悬停按钮检测
            if (_hasSelected)
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

                // 鼠标光标切换
                if (!_isSelecting && !_isResizingOrMoving && _tempAnnotation == null)
                {
                    HitType hit = CheckHitTest(e.Location);
                    this.Cursor = GetCursorForHit(hit);
                }
            }

            // 1. 初始框选
            if (_isSelecting)
            {
                _lastDragPoint = e.Location;
                _selectedRect = GetNormalizedRect(_startPoint, _lastDragPoint);
                Invalidate();
            }
            // 2. 调整选区大小或移动位置
            else if (_isResizingOrMoving)
            {
                int dx = e.Location.X - _startPoint.X;
                int dy = e.Location.Y - _startPoint.Y;

                if (_currentHit == HitType.Move)
                {
                    int stepX = e.Location.X - _lastDragPoint.X;
                    int stepY = e.Location.Y - _lastDragPoint.Y;
                    _lastDragPoint = e.Location;

                    // 限制不超出屏幕
                    int newX = _selectedRect.X + stepX;
                    int newY = _selectedRect.Y + stepY;
                    newX = Math.Max(0, Math.Min(this.Width - _selectedRect.Width, newX));
                    newY = Math.Max(0, Math.Min(this.Height - _selectedRect.Height, newY));

                    int actualDx = newX - _selectedRect.X;
                    int actualDy = newY - _selectedRect.Y;

                    _selectedRect.X = newX;
                    _selectedRect.Y = newY;

                    // 画面内所有已有标注物同步位移！
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
                    // 8点缩放调整
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
            // 3. 正在绘制标注
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

                // 产生新标注后，清空重做栈
                ClearRedoStack();
                UpdateBarsLayout();
                Invalidate();
            }
            base.OnMouseUp(e);
        }

        private static Bitmap CreateLocalMosaic(Bitmap source, Rectangle rect, int blockSize)
        {
            Rectangle valid = Rectangle.Intersect(new Rectangle(0, 0, source.Width, source.Height), rect);
            if (valid.Width <= 0 || valid.Height <= 0) return null;

            int sw = Math.Max(1, valid.Width / blockSize);
            int sh = Math.Max(1, valid.Height / blockSize);

            Bitmap snippet = new Bitmap(valid.Width, valid.Height, PixelFormat.Format32bppArgb);
            using (Bitmap small = new Bitmap(sw, sh, PixelFormat.Format32bppArgb))
            {
                using (Graphics gs = Graphics.FromImage(small))
                {
                    gs.InterpolationMode = InterpolationMode.NearestNeighbor;
                    gs.DrawImage(source, new Rectangle(0, 0, sw, sh), valid, GraphicsUnit.Pixel);
                }
                using (Graphics gd = Graphics.FromImage(snippet))
                {
                    gd.InterpolationMode = InterpolationMode.NearestNeighbor;
                    gd.DrawImage(small, 0, 0, valid.Width, valid.Height);
                }
            }
            return snippet;
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            if (_hasSelected && _selectedRect.Contains(e.Location))
            {
                ConfirmCrop();
            }
            base.OnMouseDoubleClick(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
            {
                if (_hasSelected)
                {
                    _hasSelected = false;
                    _selectedRect = Rectangle.Empty;
                    ClearAnnotations();
                    Invalidate();
                }
                else
                {
                    this.DialogResult = DialogResult.Cancel;
                    this.Close();
                }
            }
            else if (e.KeyCode == Keys.Enter)
            {
                if (_hasSelected) ConfirmCrop();
            }
            else if (e.Control && e.KeyCode == Keys.Z)
            {
                PerformUndo();
            }
            else if (e.Control && e.KeyCode == Keys.Y)
            {
                PerformRedo();
            }
            else if (e.Control && e.KeyCode == Keys.S)
            {
                if (_hasSelected) SaveToFile();
            }
            else if (e.Control && e.KeyCode == Keys.C)
            {
                if (_hasSelected) ConfirmCrop();
            }
            base.OnKeyDown(e);
        }

        private void PerformUndo()
        {
            if (_annotations.Count > 0)
            {
                int last = _annotations.Count - 1;
                AnnotationItem item = _annotations[last];
                _annotations.RemoveAt(last);
                _redoStack.Add(item);
                UpdateBarsLayout();
                Invalidate();
            }
        }

        private void PerformRedo()
        {
            if (_redoStack.Count > 0)
            {
                int last = _redoStack.Count - 1;
                AnnotationItem item = _redoStack[last];
                _redoStack.RemoveAt(last);
                _annotations.Add(item);
                UpdateBarsLayout();
                Invalidate();
            }
        }

        private void UpdateBarsLayout()
        {
            _toolButtons.Clear();
            _subOptions.Clear();
            if (!_hasSelected) return;

            int btnSize = 30;
            int margin = 3;

            var defs = new[]
            {
                new { Id = "rect",    Tip = "矩形", Tool = ToolType.Rect },
                new { Id = "ellipse", Tip = "椭圆", Tool = ToolType.Ellipse },
                new { Id = "arrow",   Tip = "箭头", Tool = ToolType.Arrow },
                new { Id = "brush",   Tip = "画笔", Tool = ToolType.Brush },
                new { Id = "mosaic",  Tip = "马赛克", Tool = ToolType.Mosaic },
                new { Id = "sep1",    Tip = "", Tool = ToolType.None },
                new { Id = "undo",    Tip = "撤销 (Ctrl+Z)", Tool = ToolType.None },
                new { Id = "redo",    Tip = "重做/下一步 (Ctrl+Y)", Tool = ToolType.None },
                new { Id = "sep2",    Tip = "", Tool = ToolType.None },
                new { Id = "save",    Tip = "保存图片 (Ctrl+S)", Tool = ToolType.None },
                new { Id = "copy",    Tip = "复制截图 (Ctrl+C)", Tool = ToolType.None },
                new { Id = "ok",      Tip = "完成并识别翻译 (Enter)", Tool = ToolType.None }
            };

            int totalWidth = 8;
            foreach (var d in defs)
            {
                totalWidth += (d.Id.StartsWith("sep") ? 6 : btnSize) + margin;
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
                int w = d.Id.StartsWith("sep") ? 6 : btnSize;
                Rectangle r = new Rectangle(curX, barY + 5, w, btnSize);
                Action act = null;
                bool disabled = false;

                if (d.Id == "rect")
                {
                    act = delegate { _currentTool = (_currentTool == ToolType.Rect) ? ToolType.None : ToolType.Rect; };
                }
                else if (d.Id == "ellipse")
                {
                    act = delegate { _currentTool = (_currentTool == ToolType.Ellipse) ? ToolType.None : ToolType.Ellipse; };
                }
                else if (d.Id == "arrow")
                {
                    act = delegate { _currentTool = (_currentTool == ToolType.Arrow) ? ToolType.None : ToolType.Arrow; };
                }
                else if (d.Id == "brush")
                {
                    act = delegate { _currentTool = (_currentTool == ToolType.Brush) ? ToolType.None : ToolType.Brush; };
                }
                else if (d.Id == "mosaic")
                {
                    act = delegate { _currentTool = (_currentTool == ToolType.Mosaic) ? ToolType.None : ToolType.Mosaic; };
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
                else if (d.Id == "copy" || d.Id == "ok")
                {
                    act = delegate { ConfirmCrop(); };
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

            // 布局二级属性栏（粗细与颜色）
            if (HasSubBar())
            {
                int subH = 34;
                int subW = 8 + (3 * 26 + 6) + 10 + (PRESET_COLORS.Length * 24 + 4) + 8;
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

                // 1. 三种粗细按钮
                for (int i = 0; i < PRESET_WIDTHS.Length; i++)
                {
                    float pw = PRESET_WIDTHS[i];
                    Rectangle br = new Rectangle(sx, sy, 24, 24);
                    _subOptions.Add(new SubOptionItem
                    {
                        Bounds = br,
                        IsColor = false,
                        WidthValue = pw,
                        ClickAction = delegate { _penWidth = pw; }
                    });
                    sx += 26;
                }

                sx += 8; // 分隔空白

                // 2. 8种颜色按钮
                for (int i = 0; i < PRESET_COLORS.Length; i++)
                {
                    Color c = PRESET_COLORS[i];
                    Rectangle cr = new Rectangle(sx, sy + 2, 20, 20);
                    _subOptions.Add(new SubOptionItem
                    {
                        Bounds = cr,
                        IsColor = true,
                        ColorValue = c,
                        ClickAction = delegate { _activeColor = c; }
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
                        ConfirmCrop();
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
                foreach (AnnotationItem ann in _annotations)
                {
                    ann.Draw(g);
                }
                if (_tempAnnotation != null)
                {
                    _tempAnnotation.Draw(g);
                }
            }
            return cropped;
        }

        private void ConfirmCrop()
        {
            if (_selectedRect.Width <= 0 || _selectedRect.Height <= 0) return;
            this.CroppedResult = GenerateFinalBitmap();
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

            // 1. 底图
            g.DrawImageUnscaled(_fullBmp, 0, 0);

            // 2. 四矩形几何遮罩 (0 内存，0.1ms)
            if (_isSelecting || _hasSelected)
            {
                Rectangle r = _selectedRect;
                if (r.Top > 0) g.FillRectangle(_maskBrush, 0, 0, this.Width, r.Top);
                if (this.Height > r.Bottom) g.FillRectangle(_maskBrush, 0, r.Bottom, this.Width, this.Height - r.Bottom);
                if (r.Left > 0 && r.Height > 0) g.FillRectangle(_maskBrush, 0, r.Top, r.Left, r.Height);
                if (this.Width > r.Right && r.Height > 0) g.FillRectangle(_maskBrush, r.Right, r.Top, this.Width - r.Right, r.Height);
            }
            else
            {
                g.FillRectangle(_maskBrush, 0, 0, this.Width, this.Height);
            }

            // 3. 标注
            foreach (AnnotationItem ann in _annotations)
            {
                ann.Draw(g);
            }
            if (_tempAnnotation != null)
            {
                _tempAnnotation.Draw(g);
            }

            // 4. 选区边框与 8 点控制手柄
            if ((_isSelecting || _hasSelected) && _selectedRect.Width > 0 && _selectedRect.Height > 0)
            {
                g.DrawRectangle(_borderPen, _selectedRect);
                DrawHandlePoints(g, _selectedRect);

                // 尺寸信息
                string tip = string.Format("{0} × {1} px", _selectedRect.Width, _selectedRect.Height);
                SizeF size = g.MeasureString(tip, _tipFont);
                float tipY = _selectedRect.Top - size.Height - 8;
                if (tipY < 4) tipY = _selectedRect.Top + 6;
                float tipX = Math.Max(4, _selectedRect.Left);

                using (SolidBrush bgBrush = new SolidBrush(Color.FromArgb(220, 15, 23, 42)))
                using (SolidBrush textBrush = new SolidBrush(Color.White))
                {
                    g.FillRectangle(bgBrush, tipX, tipY, size.Width + 10, size.Height + 4);
                    g.DrawString(tip, _tipFont, textBrush, tipX + 5, tipY + 2);
                }

                // 5. 绘制 Snipaste 风格主工具栏与二级属性栏
                if (_hasSelected && _toolButtons.Count > 0)
                {
                    DrawToolBar(g);
                    if (HasSubBar())
                    {
                        DrawSubBar(g);
                    }
                }
            }
            else if (!_isSelecting && !_hasSelected)
            {
                string guide = "请按住鼠标左键拖拽框选区域 (Esc 退出)";
                SizeF size = g.MeasureString(guide, _uiFont);
                float tipX = (this.Width - size.Width) / 2;
                float tipY = (this.Height - size.Height) / 2;

                using (SolidBrush bgBrush = new SolidBrush(Color.FromArgb(220, 15, 23, 42)))
                using (SolidBrush textBrush = new SolidBrush(Color.White))
                {
                    g.FillRectangle(bgBrush, tipX - 14, tipY - 10, size.Width + 28, size.Height + 20);
                    g.DrawString(guide, _uiFont, textBrush, tipX, tipY);
                }
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
            // 2层平滑微阴影
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

                // 圆角高亮按钮
                if (isActive || isHover)
                {
                    Color bgCol = isActive ? Color.FromArgb(224, 242, 254) : Color.FromArgb(241, 245, 249);
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

            // 浮动 Tooltip 气泡
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
                    // 粗细选项
                    bool isSelected = Math.Abs(_penWidth - sub.WidthValue) < 0.1f;
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

                    // 绘制粗细圆点
                    int dotSize = (int)(sub.WidthValue * 1.5f + 4);
                    int cx = sub.Bounds.Left + sub.Bounds.Width / 2;
                    int cy = sub.Bounds.Top + sub.Bounds.Height / 2;
                    Color dotColor = isSelected ? Color.FromArgb(2, 132, 199) : Color.FromArgb(71, 85, 105);

                    using (SolidBrush db = new SolidBrush(dotColor))
                    {
                        g.FillEllipse(db, cx - dotSize / 2, cy - dotSize / 2, dotSize, dotSize);
                    }
                }
                else
                {
                    // 颜色色块
                    bool isSelected = (_activeColor == sub.ColorValue);
                    bool isHover = (sub == _hoveredSub);

                    using (SolidBrush cb = new SolidBrush(sub.ColorValue))
                    {
                        g.FillEllipse(cb, sub.Bounds);
                    }

                    // 细边框
                    using (Pen cp = new Pen(Color.FromArgb(203, 213, 225), 1))
                    {
                        g.DrawEllipse(cp, sub.Bounds);
                    }

                    // 选中状态外环
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
                    case "copy":
                        g.DrawRectangle(p, cx - 3, cy - 6, 10, 10);
                        using (Pen p2 = new Pen(color, 1.5f))
                        {
                            g.DrawRectangle(p2, cx - 6, cy - 3, 10, 10);
                        }
                        break;
                    case "ok":
                        using (Pen okPen = new Pen(Color.FromArgb(16, 185, 129), 2.5f))
                        {
                            okPen.StartCap = LineCap.Round;
                            okPen.EndCap = LineCap.Round;
                            g.DrawLine(okPen, cx - 6, cy, cx - 2, cy + 4);
                            g.DrawLine(okPen, cx - 2, cy + 4, cx + 6, cy - 5);
                        }
                        break;
                }
            }
        }

        private void ClearAnnotations()
        {
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
