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
                // 强制垃圾回收与工作集释放，杜绝内存堆积
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
        Arrow,
        Brush,
        Mosaic
    }

    public abstract class AnnotationItem : IDisposable
    {
        public abstract void Draw(Graphics g);
        public virtual void Dispose() { }
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
        public Bitmap MosaicSnippet;

        public override void Draw(Graphics g)
        {
            if (MosaicSnippet != null && Rect.Width > 0 && Rect.Height > 0)
            {
                g.DrawImage(MosaicSnippet, Rect);
            }
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
        private Point _currentPoint;
        private bool _isSelecting = false;
        private bool _hasSelected = false;
        private Rectangle _selectedRect;

        private ToolType _currentTool = ToolType.None;
        private readonly List<AnnotationItem> _annotations = new List<AnnotationItem>();
        private AnnotationItem _tempAnnotation = null;
        private readonly Color _activeColor = Color.FromArgb(239, 68, 68);
        private readonly float _penWidth = 3f;

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

        // 静态画刷与字体复用，零内存分配
        private readonly SolidBrush _maskBrush = new SolidBrush(Color.FromArgb(125, 0, 0, 0));
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
            this.DoubleBuffered = true; // 开启 WinForms 内部双缓冲
            this.Cursor = Cursors.Cross;
            this.KeyPreview = true;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                // 1. 点击工具栏
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
            // 悬停检测
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
                // 如果是马赛克，只对局部该小矩形生成一次马赛克位图（极小内存，毫秒级）
                MosaicAnnotation ma = _tempAnnotation as MosaicAnnotation;
                if (ma != null && ma.Rect.Width > 4 && ma.Rect.Height > 4)
                {
                    ma.MosaicSnippet = CreateLocalMosaic(_fullBmp, ma.Rect, 10);
                }

                _annotations.Add(_tempAnnotation);
                _tempAnnotation = null;
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
                    int last = _annotations.Count - 1;
                    _annotations[last].Dispose();
                    _annotations.RemoveAt(last);
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
                    act = delegate {
                        if (_annotations.Count > 0)
                        {
                            int last = _annotations.Count - 1;
                            _annotations[last].Dispose();
                            _annotations.RemoveAt(last);
                        }
                    };
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

            // 1. 绘制高清物理底图 (单次内存复用)
            g.DrawImageUnscaled(_fullBmp, 0, 0);

            // 2. 【核心优化：四矩形几何遮罩法】
            // 彻底去除 Region 运算和第二张底图，零内存消耗，毫秒级直接绘制 4 个纯色矩形包围选区！
            if (_isSelecting || _hasSelected)
            {
                Rectangle r = _selectedRect;
                // 上
                if (r.Top > 0) g.FillRectangle(_maskBrush, 0, 0, this.Width, r.Top);
                // 下
                if (this.Height > r.Bottom) g.FillRectangle(_maskBrush, 0, r.Bottom, this.Width, this.Height - r.Bottom);
                // 左
                if (r.Left > 0 && r.Height > 0) g.FillRectangle(_maskBrush, 0, r.Top, r.Left, r.Height);
                // 右
                if (this.Width > r.Right && r.Height > 0) g.FillRectangle(_maskBrush, r.Right, r.Top, this.Width - r.Right, r.Height);
            }
            else
            {
                g.FillRectangle(_maskBrush, 0, 0, this.Width, this.Height);
            }

            // 3. 绘制所有标注内容
            foreach (AnnotationItem ann in _annotations)
            {
                ann.Draw(g);
            }
            if (_tempAnnotation != null)
            {
                _tempAnnotation.Draw(g);
            }

            // 4. 选区边框与控制点
            if ((_isSelecting || _hasSelected) && _selectedRect.Width > 0 && _selectedRect.Height > 0)
            {
                g.DrawRectangle(_borderPen, _selectedRect);
                DrawHandlePoints(g, _selectedRect);

                // 尺寸提示
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

                // 5. 绘制 Snipaste 风格图标工具栏
                if (_hasSelected && _toolButtons.Count > 0)
                {
                    DrawToolBar(g);
                }
            }
            else if (!_isSelecting && !_hasSelected)
            {
                string guide = "请按住鼠标左键拖拽框选区域 (按 Esc 取消)";
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

            using (SolidBrush bg = new SolidBrush(Color.FromArgb(250, 255, 255, 255)))
            using (Pen border = new Pen(Color.FromArgb(203, 213, 225), 1))
            {
                g.FillRectangle(bg, barRect);
                g.DrawRectangle(border, barRect);
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
                bool isHover = (btn == _hoveredBtn);

                if (isActive || isHover)
                {
                    Color bgCol = isActive ? Color.FromArgb(219, 234, 254) : Color.FromArgb(241, 245, 249);
                    using (SolidBrush bb = new SolidBrush(bgCol))
                    {
                        g.FillRectangle(bb, btn.Bounds);
                    }
                }

                Color iconColor = isActive ? Color.FromArgb(37, 99, 235) : Color.FromArgb(71, 85, 105);
                DrawVectorIcon(g, btn.Id, btn.Bounds, iconColor);
            }

            if (_hoveredBtn != null && !string.IsNullOrEmpty(_hoveredBtn.Tooltip))
            {
                using (Font tipF = new Font("Microsoft YaHei", 8.5f))
                using (SolidBrush tipBg = new SolidBrush(Color.FromArgb(220, 15, 23, 42)))
                using (SolidBrush tipText = new SolidBrush(Color.White))
                {
                    SizeF sz = g.MeasureString(_hoveredBtn.Tooltip, tipF);
                    float tx = _hoveredBtn.Bounds.Left + (_hoveredBtn.Bounds.Width - sz.Width) / 2;
                    float ty = barRect.Top - sz.Height - 6;

                    g.FillRectangle(tipBg, tx - 4, ty - 2, sz.Width + 8, sz.Height + 4);
                    g.DrawString(_hoveredBtn.Tooltip, tipF, tipText, tx, ty);
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
                        g.DrawRectangle(p, cx - 8, cy - 7, 16, 14);
                        break;
                    case "arrow":
                        using (AdjustableArrowCap cap = new AdjustableArrowCap(4, 4, true))
                        {
                            p.CustomEndCap = cap;
                            g.DrawLine(p, cx - 7, cy + 6, cx + 6, cy - 7);
                        }
                        break;
                    case "brush":
                        g.DrawLine(p, cx - 6, cy + 6, cx + 5, cy - 5);
                        using (SolidBrush b = new SolidBrush(color))
                        {
                            g.FillEllipse(b, cx + 3, cy - 8, 5, 5);
                        }
                        break;
                    case "mosaic":
                        using (SolidBrush b = new SolidBrush(color))
                        {
                            g.FillRectangle(b, cx - 7, cy - 7, 6, 6);
                            g.FillRectangle(b, cx + 1, cy - 7, 6, 6);
                            g.FillRectangle(b, cx - 7, cy + 1, 6, 6);
                            g.FillRectangle(b, cx + 1, cy + 1, 6, 6);
                        }
                        break;
                    case "undo":
                        g.DrawArc(p, cx - 8, cy - 6, 15, 13, 0, -210);
                        g.DrawLine(p, cx - 8, cy - 2, cx - 8, cy + 4);
                        g.DrawLine(p, cx - 8, cy - 2, cx - 2, cy - 2);
                        break;
                    case "save":
                        g.DrawRectangle(p, cx - 7, cy - 7, 14, 14);
                        using (SolidBrush b = new SolidBrush(color))
                        {
                            g.FillRectangle(b, cx - 4, cy - 6, 8, 4);
                            g.FillRectangle(b, cx - 4, cy + 1, 8, 4);
                        }
                        break;
                    case "copy":
                        g.DrawRectangle(p, cx - 4, cy - 7, 11, 11);
                        using (Pen p2 = new Pen(color, 1.5f))
                        {
                            g.DrawRectangle(p2, cx - 7, cy - 4, 11, 11);
                        }
                        break;
                    case "ok":
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

        private void ClearAnnotations()
        {
            foreach (var ann in _annotations)
            {
                ann.Dispose();
            }
            _annotations.Clear();
            if (_tempAnnotation != null)
            {
                _tempAnnotation.Dispose();
                _tempAnnotation = null;
            }
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
