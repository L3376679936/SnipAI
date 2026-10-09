using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace RubickSnipper
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
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

            return 1; // 取消
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
                _startPoint = e.Location;
                _currentPoint = e.Location;
                _isSelecting = true;
                _hasSelected = false;
                Invalidate();
            }
            else if (e.Button == MouseButtons.Right)
            {
                if (_hasSelected)
                {
                    _hasSelected = false;
                    _selectedRect = Rectangle.Empty;
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

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_isSelecting)
            {
                _currentPoint = e.Location;
                _selectedRect = GetNormalizedRect(_startPoint, _currentPoint);
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
                if (_selectedRect.Width > 8 && _selectedRect.Height > 8)
                {
                    _hasSelected = true;
                }
                else
                {
                    _hasSelected = false;
                    _selectedRect = Rectangle.Empty;
                }
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
            base.OnKeyDown(e);
        }

        private void ConfirmCrop()
        {
            if (_selectedRect.Width <= 0 || _selectedRect.Height <= 0) return;

            Bitmap cropped = new Bitmap(_selectedRect.Width, _selectedRect.Height);
            using (Graphics g = Graphics.FromImage(cropped))
            {
                g.DrawImage(_fullBmp,
                    new Rectangle(0, 0, _selectedRect.Width, _selectedRect.Height),
                    _selectedRect,
                    GraphicsUnit.Pixel);
            }

            this.CroppedResult = cropped;
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

            // 1. 绘制高清全景底图
            g.DrawImage(_fullBmp, 0, 0);

            // 2. 绘制全屏半透明暗化层 (遮罩)
            using (SolidBrush maskBrush = new SolidBrush(Color.FromArgb(110, 0, 0, 0)))
            {
                if (_isSelecting || _hasSelected)
                {
                    // 挖空选区区域：用 Region 只暗化选区之外的部分
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

            // 3. 绘制选区边框与提示信息
            if ((_isSelecting || _hasSelected) && _selectedRect.Width > 0 && _selectedRect.Height > 0)
            {
                using (Pen borderPen = new Pen(Color.FromArgb(59, 130, 246), 2))
                {
                    g.DrawRectangle(borderPen, _selectedRect);
                }

                // 绘制尺寸与操作提示条
                string tip = string.Format("{0} × {1}  [双击或按Enter完成，按Esc取消]", _selectedRect.Width, _selectedRect.Height);
                using (Font font = new Font("Microsoft YaHei", 9f))
                using (SolidBrush bgBrush = new SolidBrush(Color.FromArgb(200, 30, 41, 59)))
                using (SolidBrush textBrush = new SolidBrush(Color.White))
                {
                    SizeF size = g.MeasureString(tip, font);
                    float tipY = _selectedRect.Top - size.Height - 6;
                    if (tipY < 4) tipY = _selectedRect.Bottom + 6;
                    float tipX = Math.Max(4, _selectedRect.Left);

                    g.FillRectangle(bgBrush, tipX, tipY, size.Width + 8, size.Height + 4);
                    g.DrawString(tip, font, textBrush, tipX + 4, tipY + 2);
                }
            }
            else if (!_isSelecting && !_hasSelected)
            {
                // 刚启动时的中央引导提示
                string guide = "请按住鼠标左键拖拽框选文字区域 (按 Esc 取消)";
                using (Font font = new Font("Microsoft YaHei", 12f, FontStyle.Bold))
                using (SolidBrush bgBrush = new SolidBrush(Color.FromArgb(210, 15, 23, 42)))
                using (SolidBrush textBrush = new SolidBrush(Color.White))
                {
                    SizeF size = g.MeasureString(guide, font);
                    float tipX = (this.Width - size.Width) / 2;
                    float tipY = (this.Height - size.Height) / 2;

                    g.FillRectangle(bgBrush, tipX - 10, tipY - 6, size.Width + 20, size.Height + 12);
                    g.DrawString(guide, font, textBrush, tipX, tipY);
                }
            }
        }
    }
}
