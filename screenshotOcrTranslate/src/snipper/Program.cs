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
        // 导入 Win32 API 解决 Windows 高 DPI 缩放屏幕被强行放大的问题
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [STAThread]
        static int Main(string[] args)
        {
            // 1. 强制启用 DPI 感知，以 1:1 物理像素完美捕获高分屏，彻底消除放大和模糊
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
                        return 0; // 成功截取
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 2;
            }

            return 1; // 用户取消
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
        public Color PenColor;
        public float Width;

        public override void Draw(Graphics g)
        {
            using (Pen p = new Pen(PenColor, Width))
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
        public Bitmap SourceBmp;

        public override void Draw(Graphics g)
        {
            if (Rect.Width <= 0 || Rect.Height <= 0 || SourceBmp == null) return;
            int blockSize = 10;
            Rectangle validRect = Rectangle.Intersect(new Rectangle(0, 0, SourceBmp.Width, SourceBmp.Height), Rect);
            if (validRect.Width <= 0 || validRect.Height <= 0) return;

            for (int x = validRect.Left; x < validRect.Right; x += blockSize)
            {
                for (int y = validRect.Top; y < validRect.Bottom; y += blockSize)
                {
                    Color pixelColor = SourceBmp.GetPixel(Math.Min(x + blockSize / 2, SourceBmp.Width - 1),
                                                          Math.Min(y + blockSize / 2, SourceBmp.Height - 1));
                    int bw = Math.Min(blockSize, validRect.Right - x);
                    int bh = Math.Min(blockSize, validRect.Bottom - y);
                    using (SolidBrush b = new SolidBrush(pixelColor))
                    {
                        g.FillRectangle(b, x, y, bw, bh);
                    }
                }
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

        // 标注工具状态
        private ToolType _currentTool = ToolType.None;
        private readonly List<AnnotationItem> _annotations = new List<AnnotationItem>();
        private AnnotationItem _tempAnnotation = null;
        private readonly Color _activeColor = Color.Red;
        private readonly float _penWidth = 3f;

        private class ToolButton
        {
            public Rectangle Bounds;
            public string Label;
            public ToolType Tool;
            public Action ClickAction;
        }
        private readonly List<ToolButton> _toolButtons = new List<ToolButton>();

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

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                // 1. 点击工具栏按钮
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
                    _tempAnnotation = new RectAnnotation { Rect = new Rectangle(pt, new Size(0, 0)), PenColor = _activeColor, Width = _penWidth };
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
                    _tempAnnotation = new MosaicAnnotation { Rect = new Rectangle(pt, new Size(0, 0)), SourceBmp = _fullBmp };
                    break;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
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
                if (ra != null)
                {
                    ra.Rect = GetNormalizedRect(_startPoint, _currentPoint);
                }

                ArrowAnnotation aa = _tempAnnotation as ArrowAnnotation;
                if (aa != null)
                {
                    aa.End = _currentPoint;
                }

                BrushAnnotation ba = _tempAnnotation as BrushAnnotation;
                if (ba != null)
                {
                    ba.Points.Add(_currentPoint);
                }

                MosaicAnnotation ma = _tempAnnotation as MosaicAnnotation;
                if (ma != null)
                {
                    ma.Rect = GetNormalizedRect(_startPoint, _currentPoint);
                }

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

            int btnWidth = 46;
            int btnHeight = 28;
            int margin = 4;

            string[] labels = new string[] { "矩形", "箭头", "画笔", "马赛克", "撤销", "保存", "复制", "完成" };
            int totalWidth = labels.Length * (btnWidth + margin) + 12;

            int barX = _selectedRect.Right - totalWidth;
            if (barX < 10) barX = 10;
            int barY = _selectedRect.Bottom + 8;
            if (barY + btnHeight + 10 > this.Height)
            {
                barY = _selectedRect.Top - btnHeight - 10;
                if (barY < 10) barY = _selectedRect.Top + 8;
            }

            int curX = barX + 6;
            for (int i = 0; i < labels.Length; i++)
            {
                string label = labels[i];
                Rectangle r = new Rectangle(curX, barY + 3, btnWidth, btnHeight);
                ToolType t = ToolType.None;
                Action act = null;

                if (label == "矩形")
                {
                    t = ToolType.Rect;
                    act = delegate { _currentTool = (_currentTool == ToolType.Rect) ? ToolType.None : ToolType.Rect; };
                }
                else if (label == "箭头")
                {
                    t = ToolType.Arrow;
                    act = delegate { _currentTool = (_currentTool == ToolType.Arrow) ? ToolType.None : ToolType.Arrow; };
                }
                else if (label == "画笔")
                {
                    t = ToolType.Brush;
                    act = delegate { _currentTool = (_currentTool == ToolType.Brush) ? ToolType.None : ToolType.Brush; };
                }
                else if (label == "马赛克")
                {
                    t = ToolType.Mosaic;
                    act = delegate { _currentTool = (_currentTool == ToolType.Mosaic) ? ToolType.None : ToolType.Mosaic; };
                }
                else if (label == "撤销")
                {
                    act = delegate { if (_annotations.Count > 0) _annotations.RemoveAt(_annotations.Count - 1); };
                }
                else if (label == "保存")
                {
                    act = delegate { SaveToFile(); };
                }
                else if (label == "复制" || label == "完成")
                {
                    act = delegate { ConfirmCrop(); };
                }

                _toolButtons.Add(new ToolButton { Bounds = r, Label = label, Tool = t, ClickAction = act });
                curX += btnWidth + margin;
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
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // 1. 绘制物理 1:1 无损全景底图
            g.DrawImage(_fullBmp, 0, 0);

            // 2. 绘制全屏半透明暗化遮罩
            using (SolidBrush maskBrush = new SolidBrush(Color.FromArgb(120, 0, 0, 0)))
            {
                if (_isSelecting || _hasSelected)
                {
                    using (Region maskRegion = new Region(new Rectangle(0, 0, this.Width, this.Height)))
                    {
                        maskRegion.Exclude(_selectedRect);
                        g.FillRegion(maskBrush, maskRegion);
                    }
                }
                else
                {
                    g.FillRectangle(maskBrush, 0, 0, this.Width, this.Height);
                }
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

            // 4. 绘制选区边框与控制点
            if ((_isSelecting || _hasSelected) && _selectedRect.Width > 0 && _selectedRect.Height > 0)
            {
                using (Pen borderPen = new Pen(Color.FromArgb(59, 130, 246), 2))
                {
                    g.DrawRectangle(borderPen, _selectedRect);
                }

                DrawHandlePoints(g, _selectedRect);

                // 尺寸提示条
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

                // 5. 绘制 Snipaste 风格浮动工具栏
                if (_hasSelected && _toolButtons.Count > 0)
                {
                    DrawToolBar(g);
                }
            }
            else if (!_isSelecting && !_hasSelected)
            {
                // 中央提示
                string guide = "请按住鼠标左键拖拽框选区域 (按 Esc 取消)";
                using (Font font = new Font("Microsoft YaHei", 12f, FontStyle.Bold))
                using (SolidBrush bgBrush = new SolidBrush(Color.FromArgb(220, 15, 23, 42)))
                using (SolidBrush textBrush = new SolidBrush(Color.White))
                {
                    SizeF size = g.MeasureString(guide, font);
                    float tipX = (this.Width - size.Width) / 2;
                    float tipY = (this.Height - size.Height) / 2;

                    g.FillRectangle(bgBrush, tipX - 12, tipY - 8, size.Width + 24, size.Height + 16);
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
                _toolButtons[0].Bounds.Top - 3,
                _toolButtons[_toolButtons.Count - 1].Bounds.Right - _toolButtons[0].Bounds.Left + 12,
                _toolButtons[0].Bounds.Height + 6
            );

            using (SolidBrush bg = new SolidBrush(Color.FromArgb(245, 255, 255, 255)))
            using (Pen border = new Pen(Color.FromArgb(203, 213, 225), 1))
            {
                g.FillRectangle(bg, barRect);
                g.DrawRectangle(border, barRect);
            }

            using (Font font = new Font("Microsoft YaHei", 9f))
            {
                foreach (ToolButton btn in _toolButtons)
                {
                    bool isActive = (_currentTool != ToolType.None && btn.Tool == _currentTool);
                    Color textColor = isActive ? Color.FromArgb(37, 99, 235) : Color.FromArgb(51, 65, 85);
                    Color btnBg = isActive ? Color.FromArgb(239, 246, 255) : Color.Transparent;

                    if (isActive)
                    {
                        using (SolidBrush bb = new SolidBrush(btnBg))
                        using (Pen bp = new Pen(Color.FromArgb(147, 197, 253), 1))
                        {
                            g.FillRectangle(bb, btn.Bounds);
                            g.DrawRectangle(bp, btn.Bounds);
                        }
                    }

                    using (SolidBrush textBrush = new SolidBrush(textColor))
                    {
                        SizeF sz = g.MeasureString(btn.Label, font);
                        float tx = btn.Bounds.Left + (btn.Bounds.Width - sz.Width) / 2;
                        float ty = btn.Bounds.Top + (btn.Bounds.Height - sz.Height) / 2;
                        g.DrawString(btn.Label, font, textBrush, tx, ty);
                    }
                }
            }
        }
    }
}
