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
            // 彻底解决高 DPI 缩放下的放大模糊问题
            try { SetProcessDPIAware(); } catch { }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            try
            {
                Rectangle bounds = SystemInformation.VirtualScreen;
                Bitmap fullScreenBmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
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
                        return 0; // 成功
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 2;
            }

            return 1; // 取消
        }
    }

    public enum ToolType
    {
        None,
        Rect,
        Arrow,
        Brush,
        Mosaic
    }

    public abstract class AnnotationItem
    {
        public abstract void Draw(Graphics g);
    }

    public class RectAnnotation : AnnotationItem
    {
        public Rectangle Rect;
        public Color Color;
        public float Width;

        public override void Draw(Graphics g)
        {
            using (Pen p = new Pen(Color, Width))
            {
                g.DrawRectangle(p, Rect);
            }
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
                using (AdjustableArrowCap cap = new AdjustableArrowCap(5, 5, true))
                {
                    p.CustomEndCap = cap;
                    g.DrawLine(p, Start, End);
                }
            }
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
    }

    public class MosaicAnnotation : AnnotationItem
    {
        public Rectangle Rect;
        public Bitmap MosaicSourceBmp;

        public override void Draw(Graphics g)
        {
            if (Rect.Width <= 0 || Rect.Height <= 0 || MosaicSourceBmp == null) return;
            Rectangle valid = Rectangle.Intersect(new Rectangle(0, 0, MosaicSourceBmp.Width, MosaicSourceBmp.Height), Rect);
            if (valid.Width <= 0 || valid.Height <= 0) return;

            // 极速 Blit 预生成的马赛克位图，零 CPU 逐像素开销
            g.DrawImage(MosaicSourceBmp, valid, valid, GraphicsUnit.Pixel);
        }
    }

    public class SnipperForm : Form
    {
        private readonly Bitmap _fullBmp;
        private readonly Bitmap _darkBmp;          // 预渲染暗化底图（极速消除卡顿）
        private readonly Bitmap _mosaicBmp;        // 预渲染马赛克底图
        private readonly Rectangle _screenBounds;

        private Point _startPoint;
        private Point _currentPoint;
        private bool _isSelecting = false;
        private bool _hasSelected = false;
        private Rectangle _selectedRect;

        private ToolType _currentTool = ToolType.None;
        private readonly List<AnnotationItem> _annotations = new List<AnnotationItem>();
        private AnnotationItem _tempAnnotation = null;
        private readonly Color _activeColor = Color.FromArgb(239, 68, 68); // 鲜明红色
        private readonly float _penWidth = 3f;

        // 图标工具栏
        private class ToolButton
        {
            public Rectangle Bounds;
            public string Id;
            public string Tooltip;
            public ToolType Tool;
            public Action ClickAction;
        }
        private readonly List<ToolButton> _toolButtons = new List<ToolButton>();
        private ToolButton _hoveredBtn = null;
        private Point _lastMousePos;

        public Bitmap CroppedResult { get; private set; }

        public SnipperForm(Bitmap fullBmp, Rectangle bounds)
        {
            _fullBmp = fullBmp;
            _screenBounds = bounds;

            // 1. 预先生成暗化底图（一次性生成，彻底解决鼠标移动时计算 Region 的严重卡顿）
            _darkBmp = new Bitmap(fullBmp.Width, fullBmp.Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(_darkBmp))
            {
                g.DrawImage(fullBmp, 0, 0);
                using (SolidBrush mask = new SolidBrush(Color.FromArgb(130, 0, 0, 0)))
                {
                    g.FillRectangle(mask, 0, 0, _darkBmp.Width, _darkBmp.Height);
                }
            }

            // 2. 预先生成全局低分辨率马赛克底图（极速无延迟贴图）
            _mosaicBmp = GenerateMosaicPreview(fullBmp, 12);

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

        private static Bitmap GenerateMosaicPreview(Bitmap src, int scale)
        {
            int w = Math.Max(1, src.Width / scale);
            int h = Math.Max(1, src.Height / scale);
            Bitmap small = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(small))
            {
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.DrawImage(src, 0, 0, w, h);
            }

            Bitmap mosaic = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(mosaic))
            {
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.DrawImage(small, 0, 0, src.Width, src.Height);
            }
            small.Dispose();
            return mosaic;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                // 1. 检查是否点击了工具栏
                if (_hasSelected)
                {
                    foreach (ToolButton btn in _toolButtons)
                    {
                        if (btn.Bounds.Contains(e.Location))
                        {
                            if (btn.ClickAction != null)
                            {
                                btn.ClickAction();
                            }
                            Invalidate();
                            return;
                        }
                    }
                }

                // 2. 选区内标注
                if (_hasSelected && _currentTool != ToolType.None)
                {
                    if (_selectedRect.Contains(e.Location))
                    {
                        _startPoint = e.Location;
                        _currentPoint = e.Location;
                        StartNewAnnotation(e.Location);
                        Invalidate();
                        return;
                    }
                }

                // 3. 重新拖拽拉框
                _startPoint = e.Location;
                _currentPoint = e.Location;
                _isSelecting = true;
                _hasSelected = false;
                _currentTool = ToolType.None;
                _annotations.Clear();
                Invalidate();
            }
            else if (e.Button == MouseButtons.Right)
            {
                if (_hasSelected)
                {
                    _hasSelected = false;
                    _selectedRect = Rectangle.Empty;
                    _annotations.Clear();
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
                case ToolType.Arrow:
                    _tempAnnotation = new ArrowAnnotation { Start = pt, End = pt, Color = _activeColor, Width = _penWidth };
                    break;
                case ToolType.Brush:
                    BrushAnnotation brush = new BrushAnnotation { Color = _activeColor, Width = _penWidth };
                    brush.Points.Add(pt);
                    _tempAnnotation = brush;
                    break;
                case ToolType.Mosaic:
                    _tempAnnotation = new MosaicAnnotation { Rect = new Rectangle(pt, new Size(0, 0)), MosaicSourceBmp = _mosaicBmp };
                    break;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            _lastMousePos = e.Location;

            // 悬停检测工具栏
            if (_hasSelected)
            {
                ToolButton found = null;
                foreach (ToolButton btn in _toolButtons)
                {
                    if (btn.Bounds.Contains(e.Location))
                    {
                        found = btn;
                        break;
                    }
                }
                if (_hoveredBtn != found)
                {
                    _hoveredBtn = found;
                    Invalidate();
                }
            }

            if (_isSelecting)
            {
                _currentPoint = e.Location;
                _selectedRect = GetNormalizedRect(_startPoint, _currentPoint);
                Invalidate();
            }
            else if (_tempAnnotation != null)
            {
                _currentPoint = e.Location;

                RectAnnotation ra = _tempAnnotation as RectAnnotation;
                if (ra != null) ra.Rect = GetNormalizedRect(_startPoint, _currentPoint);

                ArrowAnnotation aa = _tempAnnotation as ArrowAnnotation;
                if (aa != null) aa.End = _currentPoint;

                BrushAnnotation ba = _tempAnnotation as BrushAnnotation;
                if (ba != null) ba.Points.Add(_currentPoint);

                MosaicAnnotation ma = _tempAnnotation as MosaicAnnotation;
                if (ma != null) ma.Rect = GetNormalizedRect(_startPoint, _currentPoint);

                Invalidate();
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (_isSelecting)
            {
                _isSelecting = false;
                _selectedRect = GetNormalizedRect(_startPoint, _currentPoint);
                if (_selectedRect.Width > 15 && _selectedRect.Height > 15)
                {
                    _hasSelected = true;
                    UpdateToolButtonsPosition();
                }
                else
                {
                    _hasSelected = false;
                    _selectedRect = Rectangle.Empty;
                }
                Invalidate();
            }
            else if (_tempAnnotation != null)
            {
                _annotations.Add(_tempAnnotation);
                _tempAnnotation = null;
                Invalidate();
            }
            base.OnMouseUp(e);
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
                this.DialogResult = DialogResult.Cancel;
                this.Close();
            }
            else if (e.KeyCode == Keys.Enter)
            {
                if (_hasSelected)
                {
                    ConfirmCrop();
                }
            }
            else if (e.Control && e.KeyCode == Keys.Z)
            {
                if (_annotations.Count > 0)
                {
                    _annotations.RemoveAt(_annotations.Count - 1);
                    Invalidate();
                }
            }
            base.OnKeyDown(e);
        }

        private void UpdateToolButtonsPosition()
        {
            _toolButtons.Clear();
            if (!_hasSelected) return;

            int btnSize = 32;
            int margin = 5;

            // 定义图标按钮项 (对齐 Snipaste)
            var defs = new[]
            {
                new { Id = "rect",   Tip = "矩形框", Tool = ToolType.Rect },
                new { Id = "arrow",  Tip = "箭头",   Tool = ToolType.Arrow },
                new { Id = "brush",  Tip = "画笔",   Tool = ToolType.Brush },
                new { Id = "mosaic", Tip = "马赛克", Tool = ToolType.Mosaic },
                new { Id = "sep1",   Tip = "",       Tool = ToolType.None },
                new { Id = "undo",   Tip = "撤销 (Ctrl+Z)", Tool = ToolType.None },
                new { Id = "save",   Tip = "保存到文件", Tool = ToolType.None },
                new { Id = "copy",   Tip = "复制到剪贴板", Tool = ToolType.None },
                new { Id = "ok",     Tip = "完成并进行OCR翻译 (Enter)", Tool = ToolType.None }
            };

            int totalWidth = 0;
            foreach (var d in defs)
            {
                totalWidth += (d.Id.StartsWith("sep") ? 8 : btnSize) + margin;
            }
            totalWidth += 6;

            int barX = _selectedRect.Right - totalWidth;
            if (barX < 10) barX = 10;
            int barY = _selectedRect.Bottom + 8;
            if (barY + btnSize + 10 > this.Height)
            {
                barY = _selectedRect.Top - btnSize - 10;
                if (barY < 10) barY = _selectedRect.Top + 8;
            }

            int curX = barX + 6;
            foreach (var d in defs)
            {
                int w = d.Id.StartsWith("sep") ? 8 : btnSize;
                Rectangle r = new Rectangle(curX, barY + 4, w, btnSize);
                Action act = null;

                if (d.Id == "rect")
                {
                    act = delegate { _currentTool = (_currentTool == ToolType.Rect) ? ToolType.None : ToolType.Rect; };
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
                    act = delegate { if (_annotations.Count > 0) _annotations.RemoveAt(_annotations.Count - 1); };
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
                    ClickAction = act
                });

                curX += w + margin;
            }
        }

        private void SaveToFile()
        {
            Bitmap result = GenerateFinalBitmap();
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

        private Bitmap GenerateFinalBitmap()
        {
            Bitmap cropped = new Bitmap(_selectedRect.Width, _selectedRect.Height);
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

            // 1. 极速渲染：直接绘制预处理好的暗化底图（毫秒级，零运算）
            g.DrawImageUnscaled(_darkBmp, 0, 0);

            // 2. 选区内部直接贴回高清原图
            if ((_isSelecting || _hasSelected) && _selectedRect.Width > 0 && _selectedRect.Height > 0)
            {
                g.DrawImage(_fullBmp, _selectedRect, _selectedRect, GraphicsUnit.Pixel);

                // 3. 绘制标注
                foreach (AnnotationItem ann in _annotations)
                {
                    ann.Draw(g);
                }
                if (_tempAnnotation != null)
                {
                    _tempAnnotation.Draw(g);
                }

                // 4. 选区边框（Snipaste 浅蓝精细虚线+控制点）
                using (Pen borderPen = new Pen(Color.FromArgb(59, 130, 246), 2))
                {
                    g.DrawRectangle(borderPen, _selectedRect);
                }
                DrawHandlePoints(g, _selectedRect);

                // 5. 尺寸标签
                string tip = string.Format("{0} × {1} px", _selectedRect.Width, _selectedRect.Height);
                using (Font font = new Font("Segoe UI", 9f, FontStyle.Bold))
                using (SolidBrush bgBrush = new SolidBrush(Color.FromArgb(220, 15, 23, 42)))
                using (SolidBrush textBrush = new SolidBrush(Color.White))
                {
                    SizeF size = g.MeasureString(tip, font);
                    float tipY = _selectedRect.Top - size.Height - 8;
                    if (tipY < 4) tipY = _selectedRect.Top + 6;
                    float tipX = Math.Max(4, _selectedRect.Left);

                    g.FillRectangle(bgBrush, tipX, tipY, size.Width + 10, size.Height + 4);
                    g.DrawString(tip, font, textBrush, tipX + 5, tipY + 2);
                }

                // 6. Snipaste 风格图标工具栏
                if (_hasSelected && _toolButtons.Count > 0)
                {
                    DrawToolBar(g);
                }
            }
            else if (!_isSelecting && !_hasSelected)
            {
                string guide = "请按住鼠标左键拖拽框选区域 (按 Esc 取消)";
                using (Font font = new Font("Microsoft YaHei", 12f, FontStyle.Bold))
                using (SolidBrush bgBrush = new SolidBrush(Color.FromArgb(220, 15, 23, 42)))
                using (SolidBrush textBrush = new SolidBrush(Color.White))
                {
                    SizeF size = g.MeasureString(guide, font);
                    float tipX = (this.Width - size.Width) / 2;
                    float tipY = (this.Height - size.Height) / 2;

                    g.FillRectangle(bgBrush, tipX - 14, tipY - 10, size.Width + 28, size.Height + 20);
                    g.DrawString(guide, font, textBrush, tipX, tipY);
                }
            }
        }

        private void DrawHandlePoints(Graphics g, Rectangle r)
        {
            int s = 6;
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
            using (Pen p = new Pen(Color.FromArgb(59, 130, 246), 1.5f))
            {
                foreach (Point pt in pts)
                {
                    g.FillRectangle(b, pt.X, pt.Y, s, s);
                    g.DrawRectangle(p, pt.X, pt.Y, s, s);
                }
            }
        }

        private void DrawToolBar(Graphics g)
        {
            if (_toolButtons.Count == 0) return;

            Rectangle barRect = new Rectangle(
                _toolButtons[0].Bounds.Left - 6,
                _toolButtons[0].Bounds.Top - 4,
                _toolButtons[_toolButtons.Count - 1].Bounds.Right - _toolButtons[0].Bounds.Left + 12,
                _toolButtons[0].Bounds.Height + 8
            );

            // 绘制工具栏圆角背景底板与轻微投影
            using (SolidBrush bg = new SolidBrush(Color.FromArgb(250, 255, 255, 255)))
            using (Pen border = new Pen(Color.FromArgb(203, 213, 225), 1))
            {
                g.FillRectangle(bg, barRect);
                g.DrawRectangle(border, barRect);
            }

            // 遍历绘制图标按钮
            foreach (ToolButton btn in _toolButtons)
            {
                if (btn.Id.StartsWith("sep"))
                {
                    // 分割线
                    using (Pen sp = new Pen(Color.FromArgb(226, 232, 240), 1))
                    {
                        int mx = btn.Bounds.Left + btn.Bounds.Width / 2;
                        g.DrawLine(sp, mx, btn.Bounds.Top + 6, mx, btn.Bounds.Bottom - 6);
                    }
                    continue;
                }

                bool isActive = (_currentTool != ToolType.None && btn.Tool == _currentTool);
                bool isHover = (btn == _hoveredBtn);

                if (isActive || isHover)
                {
                    Color bgCol = isActive ? Color.FromArgb(219, 234, 254) : Color.FromArgb(241, 245, 249);
                    using (SolidBrush bb = new SolidBrush(bgCol))
                    {
                        g.FillRectangle(bb, btn.Bounds);
                    }
                }

                // 绘制矢量图标
                Color iconColor = isActive ? Color.FromArgb(37, 99, 235) : Color.FromArgb(71, 85, 105);
                DrawVectorIcon(g, btn.Id, btn.Bounds, iconColor);
            }

            // 绘制悬停气泡提示
            if (_hoveredBtn != null && !string.IsNullOrEmpty(_hoveredBtn.Tooltip))
            {
                using (Font tipFont = new Font("Microsoft YaHei", 8.5f))
                using (SolidBrush tipBg = new SolidBrush(Color.FromArgb(220, 15, 23, 42)))
                using (SolidBrush tipText = new SolidBrush(Color.White))
                {
                    SizeF sz = g.MeasureString(_hoveredBtn.Tooltip, tipFont);
                    float tx = _hoveredBtn.Bounds.Left + (_hoveredBtn.Bounds.Width - sz.Width) / 2;
                    float ty = barRect.Top - sz.Height - 6;

                    g.FillRectangle(tipBg, tx - 4, ty - 2, sz.Width + 8, sz.Height + 4);
                    g.DrawString(_hoveredBtn.Tooltip, tipFont, tipText, tx, ty);
                }
            }
        }

        // 精致矢量图标绘制函数
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
                        // 矩形图标
                        g.DrawRectangle(p, cx - 8, cy - 7, 16, 14);
                        break;

                    case "arrow":
                        // 箭头图标
                        using (AdjustableArrowCap cap = new AdjustableArrowCap(4, 4, true))
                        {
                            p.CustomEndCap = cap;
                            g.DrawLine(p, cx - 7, cy + 6, cx + 6, cy - 7);
                        }
                        break;

                    case "brush":
                        // 画笔图标 (斜向线条+笔头)
                        g.DrawLine(p, cx - 6, cy + 6, cx + 5, cy - 5);
                        using (SolidBrush b = new SolidBrush(color))
                        {
                            g.FillEllipse(b, cx + 3, cy - 8, 5, 5);
                        }
                        break;

                    case "mosaic":
                        // 马赛克图标 (网格像素)
                        using (SolidBrush b = new SolidBrush(color))
                        {
                            g.FillRectangle(b, cx - 7, cy - 7, 6, 6);
                            g.FillRectangle(b, cx + 1, cy - 7, 6, 6);
                            g.FillRectangle(b, cx - 7, cy + 1, 6, 6);
                            g.FillRectangle(b, cx + 1, cy + 1, 6, 6);
                        }
                        break;

                    case "undo":
                        // 撤销图标 (弧形箭头)
                        g.DrawArc(p, cx - 8, cy - 6, 15, 13, 0, -210);
                        g.DrawLine(p, cx - 8, cy - 2, cx - 8, cy + 4);
                        g.DrawLine(p, cx - 8, cy - 2, cx - 2, cy - 2);
                        break;

                    case "save":
                        // 磁盘保存图标
                        g.DrawRectangle(p, cx - 7, cy - 7, 14, 14);
                        using (SolidBrush b = new SolidBrush(color))
                        {
                            g.FillRectangle(b, cx - 4, cy - 6, 8, 4);
                            g.FillRectangle(b, cx - 4, cy + 1, 8, 4);
                        }
                        break;

                    case "copy":
                        // 复制图标 (双层卡片)
                        g.DrawRectangle(p, cx - 4, cy - 7, 11, 11);
                        using (Pen p2 = new Pen(color, 1.5f))
                        {
                            g.DrawRectangle(p2, cx - 7, cy - 4, 11, 11);
                        }
                        break;

                    case "ok":
                        // 确认/完成图标 (醒目绿色对勾)
                        using (Pen okPen = new Pen(Color.FromArgb(16, 185, 129), 2.5f))
                        {
                            okPen.StartCap = LineCap.Round;
                            okPen.EndCap = LineCap.Round;
                            g.DrawLine(okPen, cx - 7, cy, cx - 2, cy + 5);
                            g.DrawLine(okPen, cx - 2, cy + 5, cx + 7, cy - 5);
                        }
                        break;
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_darkBmp != null) _darkBmp.Dispose();
                if (_mosaicBmp != null) _mosaicBmp.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
