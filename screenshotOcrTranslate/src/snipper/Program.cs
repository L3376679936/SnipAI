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

            // 支持从工作台直接将 AI 修改后的新图钉到桌面 (--pin-image <filePath>)
            if (args != null && args.Length >= 2 && args[0] == "--pin-image")
            {
                string imgPath = args[1];
                if (System.IO.File.Exists(imgPath))
                {
                    try
                    {
                        Bitmap loadedBmp = null;
                        using (Bitmap temp = new Bitmap(imgPath))
                        {
                            loadedBmp = new Bitmap(temp);
                        }
                        try { System.IO.File.Delete(imgPath); } catch { }
                        Rectangle workArea = Screen.PrimaryScreen.WorkingArea;
                        Point initPos = new Point(
                            workArea.Left + Math.Max(20, (workArea.Width - loadedBmp.Width) / 2),
                            workArea.Top + Math.Max(20, (workArea.Height - loadedBmp.Height) / 2));
                        Application.Run(new PinForm(loadedBmp, initPos));
                        return 0;
                    }
                    catch (Exception pinEx)
                    {
                        Console.Error.WriteLine(pinEx.Message);
                        return 2;
                    }
                }
            }

            Bitmap fullScreenBmp = null;
            try
            {
                Rectangle bounds = SystemInformation.VirtualScreen;
                fullScreenBmp = CaptureOpaqueScreen(bounds);

                using (SnipperForm form = new SnipperForm(fullScreenBmp, bounds))
                {
                    DialogResult res = form.ShowDialog();
                    if (res == DialogResult.OK && form.CroppedResult != null)
                    {
                        if (form.IsPinRequested)
                        {
                            // SelectedRect 是覆盖层的客户区坐标（其原点即虚拟屏原点），而 PinForm.Location
                            // 需要的是屏幕坐标：当虚拟屏原点不是 (0,0)（左侧或上方挂了副屏）时，必须补上
                            // bounds 偏移，否则贴图会整体错位、甚至落到另一块显示器上。
                            Point pinPos = new Point(bounds.Left + form.SelectedRect.Left,
                                                     bounds.Top + form.SelectedRect.Top);
                            Console.WriteLine("ACTION:PIN");
                            // 位图所有权转交 PinForm（它直接持有、并在窗体关闭时释放），
                            // 不再像原先那样在 Main 与 PinForm 里各拷贝一次（其中一份从未释放）
                            Application.Run(new PinForm(form.CroppedResult, pinPos));
                            return 0; // 贴图退出后正常返回
                        }

                        if (!TrySetClipboardImage(form.CroppedResult))
                        {
                            // 不中断流程：即使剪贴板被别的程序占用，也照常按用户意图回传动作码，
                            // 由前端决定是否提示，避免整个截图白做。
                            Console.Error.WriteLine("写入剪贴板失败：剪贴板可能被其它程序占用");
                        }
                        form.CroppedResult.Dispose();

                        if (form.IsAiEditRequested && !string.IsNullOrEmpty(form.AiEditBundlePath))
                        {
                            Console.WriteLine("ACTION:AI_EDIT:" + form.AiEditBundlePath);
                            return 11; // 退出码 11 代表触发 AI 智能改图
                        }
                        else if (form.IsOcrRequested)
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

        /// <summary>
        /// 写入剪贴板位图。剪贴板是全局独占资源，其它程序（输入法、截图工具、剪贴板
        /// 管理器）可能瞬时持有它，此时 Clipboard.SetImage 会抛 ExternalException。
        /// 这种竞争通常是毫秒级，做有限次重试即可，避免"复制"操作偶发失败并让整个
        /// 截图流程中断。返回是否最终写入成功。
        /// </summary>
        internal static bool TrySetClipboardImage(Bitmap bmp)
        {
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    Clipboard.SetImage(bmp);
                    return true;
                }
                catch (ExternalException)
                {
                    System.Threading.Thread.Sleep(30);
                }
            }
            return false;
        }

        /// <summary>写入剪贴板文本，重试策略同 <see cref="TrySetClipboardImage"/>。</summary>
        internal static bool TrySetClipboardText(string text)
        {
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    Clipboard.SetText(text);
                    return true;
                }
                catch (ExternalException)
                {
                    System.Threading.Thread.Sleep(30);
                }
            }
            return false;
        }

        /// <summary>
        /// 抓取整个虚拟屏，并一次性产出可直接高速绘制的位图。
        ///
        /// 屏幕 DC 的 alpha 通道没有语义：实测本机副屏 86% 的像素 A=0（与"驱动未写入"
        /// 的纯黑区域严格重合），主屏则全为 255。若原样以 32bppArgb 参与 GDI+ 默认的
        /// SourceOver 混合，这些像素会被整块当作全透明丢弃，副屏渲染成花屏/黑块。
        ///
        /// 同时直接产出 GDI+ 的原生预乘格式 Format32bppPArgb —— 实测整屏 blit
        /// 约 52ms，而非预乘的 Argb 约 155ms（3 倍差距），因为它免去了每像素的
        /// 格式转换。A 已归一为 255，故预乘值与直通值在数值上完全一致，
        /// 取色 (_pixels) 与裁剪输出都不受影响。
        /// </summary>
        private static Bitmap CaptureOpaqueScreen(Rectangle bounds)
        {
            using (Bitmap captured = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(captured))
                {
                    g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
                }
                return ToOpaquePremultiplied(captured);
            }
        }

        /// <summary>
        /// 逐行把 alpha 字节补成 0xFF 并写入预乘格式目标位图（单遍完成，不做额外转换）。
        /// </summary>
        private static Bitmap ToOpaquePremultiplied(Bitmap captured)
        {
            int w = captured.Width;
            int h = captured.Height;
            Bitmap result = new Bitmap(w, h, PixelFormat.Format32bppPArgb);

            BitmapData srcData = captured.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            BitmapData dstData = result.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            try
            {
                // Format32bpp* 在内存中是 BGRA 小端，按 int 读即 0xAARRGGBB，直接或上不透明
                int[] row = new int[w];
                for (int y = 0; y < h; y++)
                {
                    Marshal.Copy(IntPtr.Add(srcData.Scan0, y * srcData.Stride), row, 0, w);
                    for (int x = 0; x < w; x++)
                    {
                        row[x] |= unchecked((int)0xFF000000);
                    }
                    Marshal.Copy(row, 0, IntPtr.Add(dstData.Scan0, y * dstData.Stride), w);
                }
            }
            finally
            {
                captured.UnlockBits(srcData);
                result.UnlockBits(dstData);
            }
            return result;
        }
    }

    public enum ToolType
    {
        None,
        Rect,
        Ellipse,
        Line,
        Arrow,
        Brush,
        Marker,
        Mosaic,
        Blur,
        Text,
        Number,
        Eraser
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
        TextHandleBottomRight,
        /// <summary>文字标注的旋转手柄（悬在文字包围盒上方）</summary>
        TextHandleRotate
    }

    public enum TextHandleType
    {
        None,
        Body,
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight,
        /// <summary>骨架顶点手柄（直线/箭头两端点、自由笔迹的折线顶点）</summary>
        Vertex,
        /// <summary>环绕旋转手柄（悬在包围盒上边中点正上方）</summary>
        Rotate
    }

    /// <summary>
    /// 箭头头部样式（对齐 Snipaste 线型下拉的后三档）。
    /// 下拉里的"直线"一档不带头部，因此不属于本枚举——它对应的是独立的 Line 工具。
    /// </summary>
    public enum ArrowHeadStyle
    {
        /// <summary>细箭头：小而锐的实心凹槽箭头，视觉重量最轻</summary>
        Thin,
        /// <summary>实心箭头：Snipaste 默认的燕尾凹槽实心箭头</summary>
        Solid,
        /// <summary>空心箭头：只描边不填充的等边三角头部，底图能透出来</summary>
        Hollow
    }

    /// <summary>
    /// 一个可拖拽骨架顶点的引用：Index 是图元内部的真实索引，Position 是它当前的坐标。
    /// 自由笔迹的点数可能上千，采样后暴露给 UI 的顶点必须能反查回原始下标。
    /// </summary>
    public struct VertexRef
    {
        public int Index;
        public Point Position;

        public VertexRef(int index, Point position)
        {
            Index = index;
            Position = position;
        }
    }

    public abstract class AnnotationItem : IDisposable
    {
        public bool IsHoveredForEraser = false;

        /// <summary>是否处于选中编辑态（外显虚线包围盒与四角手柄）</summary>
        public bool IsSelected = false;

        /// <summary>
        /// 是否因"正在重新编辑文字内容"而临时隐身。
        /// 重新编辑时原图元必须让位给输入文本框，否则旧文字与文本框会叠成双层；
        /// 命中测试也要一并摘出去，免得点到文本框底下那层旧图元。
        /// </summary>
        public bool IsHiddenForEditing = false;

        /// <summary>
        /// 图元绕自身包围盒中心的旋转角度（单位：度，顺时针为正）。
        /// 旋转统一在基类里以画布变换实现，子类的 DrawCore / HitTestItemCore
        /// 只面对"未旋转"坐标系，屏幕命中、导出成图、失效区域三者天然一致。
        /// </summary>
        public float Angle = 0f;

        /// <summary>旋转手柄相对包围盒上边的偏移量（负方向即上方）</summary>
        public const int RotateHandleOffset = 26;

        /// <summary>是否已发生旋转（用于跳过恒等变换，避免每帧白存一次 Graphics 状态）</summary>
        public bool IsRotated { get { return Math.Abs(Angle) > 0.01f; } }

        /// <summary>
        /// 统一绘制入口：按 Angle 绕包围盒中心旋转画布后，再交给子类绘制。
        /// </summary>
        public void Draw(Graphics g)
        {
            // 隐身态既不出现在画布上，也不会被导出路径画进成品图
            if (IsHiddenForEditing) return;

            if (!IsRotated)
            {
                DrawCore(g);
                return;
            }

            GraphicsState state = g.Save();
            try
            {
                PointF c = GetCenter();
                g.TranslateTransform(c.X, c.Y);
                g.RotateTransform(Angle);
                g.TranslateTransform(-c.X, -c.Y);
                DrawCore(g);
            }
            finally
            {
                // 无论子类绘制是否抛异常都要还原画布，否则后续图元会跟着一起歪
                g.Restore(state);
            }
        }

        /// <summary>子类在此实现未旋转坐标系下的实际绘制</summary>
        protected virtual void DrawCore(Graphics g) { }

        public abstract void Offset(int dx, int dy);

        /// <summary>
        /// 统一命中入口：先把图面上的点逆旋转回图元自身坐标系，再交给子类判断。
        /// </summary>
        public bool HitTestItem(Point pt)
        {
            if (IsHiddenForEditing) return false;
            return HitTestItemCore(IsRotated ? InverseRotate(pt) : pt);
        }

        /// <summary>子类在此实现未旋转坐标系下的本体命中判定</summary>
        protected virtual bool HitTestItemCore(Point pt) { return false; }

        /// <summary>旋转中心：包围盒中心</summary>
        public PointF GetCenter()
        {
            Rectangle b = GetBounds();
            return new PointF(b.Left + b.Width / 2f, b.Top + b.Height / 2f);
        }

        /// <summary>把图面上的点变换回图元自身的未旋转坐标系</summary>
        public Point InverseRotate(Point pt)
        {
            if (!IsRotated) return pt;

            PointF c = GetCenter();
            double rad = -Angle * Math.PI / 180.0;
            double cos = Math.Cos(rad);
            double sin = Math.Sin(rad);
            double dx = pt.X - c.X;
            double dy = pt.Y - c.Y;
            return new Point(
                (int)Math.Round(c.X + dx * cos - dy * sin),
                (int)Math.Round(c.Y + dx * sin + dy * cos));
        }

        /// <summary>
        /// 旋转后在图面上的实际外接矩形（四个角点旋转后的包围盒）。
        /// 失效区域与需要覆盖整个图元的地方必须用它，直接用 GetBounds() 会漏掉
        /// 旋转后甩到原包围盒之外的部分，留下残影。
        /// </summary>
        public Rectangle GetVisualBounds()
        {
            Rectangle b = GetBounds();
            if (!IsRotated || b.Width <= 0 || b.Height <= 0) return b;

            PointF c = GetCenter();
            double rad = Angle * Math.PI / 180.0;
            double cos = Math.Cos(rad);
            double sin = Math.Sin(rad);

            int minX = int.MaxValue, minY = int.MaxValue;
            int maxX = int.MinValue, maxY = int.MinValue;
            int[] xs = new int[] { b.Left, b.Right, b.Right, b.Left };
            int[] ys = new int[] { b.Top, b.Top, b.Bottom, b.Bottom };
            for (int i = 0; i < 4; i++)
            {
                double dx = xs[i] - c.X;
                double dy = ys[i] - c.Y;
                int rx = (int)Math.Round(c.X + dx * cos - dy * sin);
                int ry = (int)Math.Round(c.Y + dx * sin + dy * cos);
                if (rx < minX) minX = rx;
                if (rx > maxX) maxX = rx;
                if (ry < minY) minY = ry;
                if (ry > maxY) maxY = ry;
            }
            return Rectangle.FromLTRB(minX, minY, maxX, maxY);
        }

        /// <summary>图元的轴对齐包围盒（未旋转坐标系），用于选中手柄与整体缩放</summary>
        public abstract Rectangle GetBounds();

        /// <summary>按目标包围盒整体缩放图元（四角拖拽编辑）</summary>
        public abstract void SetBounds(Rectangle bounds);

        /// <summary>该图元是否支持二次编辑换色（马赛克/模糊等无前景色的图元返回 false）</summary>
        public virtual bool SupportsColor { get { return false; } }

        /// <summary>把图元前景色改为指定颜色，仅 SupportsColor 为 true 时有效</summary>
        public virtual void SetColor(Color c) { }

        /// <summary>
        /// 把点从旧包围盒按比例映射到新包围盒，供各图元整体缩放复用
        /// </summary>
        protected static Point MapPoint(Point p, Rectangle from, Rectangle to)
        {
            float sx = (from.Width == 0) ? 1f : (float)to.Width / from.Width;
            float sy = (from.Height == 0) ? 1f : (float)to.Height / from.Height;
            return new Point(
                to.X + (int)Math.Round((p.X - from.X) * sx),
                to.Y + (int)Math.Round((p.Y - from.Y) * sy));
        }

        /// <summary>
        /// 虚线实参：DashPattern 的单位是"笔宽倍数"，所以这两个值本身就是线宽无关的比例 ——
        /// 1px 细线画出来是小碎点，8px 粗线画出来是同比例的长划线，观感随线宽等比放大。
        /// </summary>
        public const float DASH_ON = 2.5f;
        public const float DASH_OFF = 1.6f;

        /// <summary>
        /// 把一支笔改成虚线。线条图元与线条预览（二级栏按钮、下拉菜单项）共用这一份实参，
        /// 免得"画在画布上的虚线"与"按钮上预览的虚线"疏密对不上。
        /// </summary>
        public static void ApplyDashedPen(Pen p)
        {
            p.DashStyle = DashStyle.Custom;
            p.DashPattern = new float[] { DASH_ON, DASH_OFF };
            p.DashCap = DashCap.Flat;
        }

        /// <summary>计算点集的外接矩形，供画笔 / 马克笔等自由曲线图元复用</summary>
        protected static Rectangle GetPointsBounds(List<Point> pts)
        {
            if (pts == null || pts.Count == 0) return Rectangle.Empty;

            int minX = int.MaxValue, minY = int.MaxValue;
            int maxX = int.MinValue, maxY = int.MinValue;
            foreach (Point p in pts)
            {
                if (p.X < minX) minX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.X > maxX) maxX = p.X;
                if (p.Y > maxY) maxY = p.Y;
            }
            return Rectangle.FromLTRB(minX, minY, maxX, maxY);
        }

        /// <summary>
        /// 选中态通用装饰：天蓝虚线包围盒 + 四角白色手柄
        /// </summary>
        protected void DrawSelectionAdorner(Graphics g)
        {
            if (!IsSelected) return;

            Rectangle b = GetBounds();
            if (b.IsEmpty || b.Width <= 0 || b.Height <= 0) return;

            using (Pen dashPen = new Pen(Color.FromArgb(14, 165, 233), 1.5f))
            {
                dashPen.DashStyle = DashStyle.Dash;
                g.DrawRectangle(dashPen, b);
            }

            // 支持顶点编辑的图元（直线 / 箭头 / 自由笔迹）：逐点画出可拖拽的骨架方块
            List<VertexRef> verts = GetEditVertices();
            if (verts.Count >= 2)
            {
                const int vs = 7;
                using (SolidBrush vfill = new SolidBrush(Color.White))
                using (Pen vborder = new Pen(Color.FromArgb(2, 132, 199), 1.5f))
                {
                    foreach (VertexRef vr in verts)
                    {
                        Rectangle hr = new Rectangle(
                            vr.Position.X - vs / 2, vr.Position.Y - vs / 2, vs, vs);
                        g.FillRectangle(vfill, hr);
                        g.DrawRectangle(vborder, hr);
                    }
                }
                // 两端点图元的顶点即角点，再叠四角手柄纯属重复，跳过四角只留旋转手柄
                if (SuppressCornerHandles())
                {
                    DrawRotateHandle(g, b);
                    return;
                }
            }

            int hs = 8;
            Rectangle[] handles = new Rectangle[]
            {
                new Rectangle(b.Left - hs / 2, b.Top - hs / 2, hs, hs),
                new Rectangle(b.Right - hs / 2, b.Top - hs / 2, hs, hs),
                new Rectangle(b.Left - hs / 2, b.Bottom - hs / 2, hs, hs),
                new Rectangle(b.Right - hs / 2, b.Bottom - hs / 2, hs, hs)
            };

            using (SolidBrush fill = new SolidBrush(Color.White))
            using (Pen border = new Pen(Color.FromArgb(2, 132, 199), 1.5f))
            {
                foreach (Rectangle hr in handles)
                {
                    g.FillRectangle(fill, hr);
                    g.DrawRectangle(border, hr);
                }
            }

            DrawRotateHandle(g, b);
        }

        /// <summary>
        /// 旋转手柄：悬在包围盒上边中点正上方的小圆环。
        /// 画在 DrawCore 的旋转变换之内，因此会跟着图元一起转，用户看到的位置
        /// 与实际可拖拽的方位始终一致，转多少度都不会"手柄在左、图形在右"。
        /// </summary>
        protected void DrawRotateHandle(Graphics g, Rectangle b)
        {
            int cx = b.Left + b.Width / 2;
            int cy = b.Top - RotateHandleOffset;

            // 与包围盒上边之间的细连线：暗示这个手柄作用于整个图元
            using (Pen link = new Pen(Color.FromArgb(110, 14, 165, 233), 1f))
            {
                g.DrawLine(link, cx, cy + 6, cx, b.Top);
            }

            using (SolidBrush fill = new SolidBrush(Color.White))
            using (Pen border = new Pen(Color.FromArgb(2, 132, 199), 1.5f))
            {
                g.FillEllipse(fill, cx - 6, cy - 6, 12, 12);
                g.DrawEllipse(border, cx - 6, cy - 6, 12, 12);
            }

            // 圆环内一段带缺口的圆弧，一眼看出"这是旋转"
            using (Pen arc = new Pen(Color.FromArgb(2, 132, 199), 1.5f))
            {
                arc.StartCap = LineCap.Round;
                arc.EndCap = LineCap.ArrowAnchor;
                g.DrawArc(arc, cx - 3, cy - 3, 6, 6, 110, 250);
            }
        }

        /// <summary>
        /// 命中旋转手柄（未选中或图元退化时返回 false）。
        /// 热区是直径 16px 的圆，比 12px 的视觉圆环略大，方便鼠标抓取。
        /// </summary>
        public bool HitTestRotateHandle(Point pt)
        {
            if (!IsSelected) return false;

            Rectangle b = GetBounds();
            if (b.IsEmpty || b.Width <= 0 || b.Height <= 0) return false;

            // 手柄是画在旋转变换里的，命中前必须把点转回未旋转坐标系再比对
            Point local = IsRotated ? InverseRotate(pt) : pt;
            int cx = b.Left + b.Width / 2;
            int cy = b.Top - RotateHandleOffset;
            return (local.X - cx) * (local.X - cx) + (local.Y - cy) * (local.Y - cy) <= 8 * 8;
        }

        /// <summary>
        /// 图元可拖拽的骨架顶点。默认返回空集合，表示该图元只支持整体缩放。
        /// </summary>
        public virtual List<VertexRef> GetEditVertices() { return new List<VertexRef>(); }

        /// <summary>把内部索引为 index 的骨架顶点移动到 pt，仅 GetEditVertices 非空时有效</summary>
        public virtual void MoveVertex(int index, Point pt) { }

        /// <summary>
        /// 自由曲线暴露给 UI 的顶点上限。一条挥洒而过的笔迹动辄上千个采样点，
        /// 若不采样，选中时会在屏幕上糊出几百个小方块，既看不清也没法精确拖拽。
        /// </summary>
        protected const int MAX_VERTEX_HANDLES = 24;

        /// <summary>
        /// 是否隐藏四角缩放手柄。只有两个骨架顶点的图元（直线、箭头、极短笔迹）
        /// 其顶点与包围盒角点重合，两套手柄同时生效会互相抢命中，此时只保留顶点手柄；
        /// 顶点更多的自由笔迹两套并存，缩放与逐点整形都不丢。
        /// </summary>
        protected virtual bool SuppressCornerHandles()
        {
            return GetEditVertices().Count == 2;
        }

        /// <summary>按上限等距采样下标序列（首尾必取），供自由笔迹复用</summary>
        protected static List<VertexRef> SampleVertices(List<Point> pts)
        {
            List<VertexRef> result = new List<VertexRef>();
            int n = pts.Count;
            if (n == 0) return result;
            if (n <= MAX_VERTEX_HANDLES)
            {
                for (int i = 0; i < n; i++) result.Add(new VertexRef(i, pts[i]));
                return result;
            }

            int step = (int)Math.Ceiling((n - 1) / (double)(MAX_VERTEX_HANDLES - 1));
            for (int i = 0; i < n; i += step) result.Add(new VertexRef(i, pts[i]));
            // 末点单独补上：按 step 跳有可能正好跳过终点，而终点恰恰是最常拖的一个
            if (result[result.Count - 1].Index != n - 1) result.Add(new VertexRef(n - 1, pts[n - 1]));
            return result;
        }

        /// <summary>
        /// 命中骨架顶点，返回其内部索引（未命中或该图元不支持顶点编辑时返回 -1）
        /// </summary>
        public int HitTestVertex(Point pt)
        {
            if (!IsSelected) return -1;

            List<VertexRef> verts = GetEditVertices();
            const int hs = 10; // 热区略大于 8px 的视觉方块，方便鼠标抓取
            for (int i = 0; i < verts.Count; i++)
            {
                Rectangle r = new Rectangle(
                    verts[i].Position.X - hs / 2, verts[i].Position.Y - hs / 2, hs, hs);
                if (r.Contains(pt)) return verts[i].Index;
            }
            return -1;
        }

        /// <summary>
        /// 命中四角缩放手柄（未选中时恒返回 None），命中热区略大于视觉手柄
        /// </summary>
        public TextHandleType HitTestHandle(Point pt)
        {
            if (!IsSelected) return TextHandleType.None;

            if (SuppressCornerHandles()) return TextHandleType.None;

            Rectangle b = GetBounds();
            if (b.IsEmpty || b.Width <= 0 || b.Height <= 0) return TextHandleType.None;

            int hs = 10;
            if (new Rectangle(b.Left - hs / 2, b.Top - hs / 2, hs, hs).Contains(pt)) return TextHandleType.TopLeft;
            if (new Rectangle(b.Right - hs / 2, b.Top - hs / 2, hs, hs).Contains(pt)) return TextHandleType.TopRight;
            if (new Rectangle(b.Left - hs / 2, b.Bottom - hs / 2, hs, hs).Contains(pt)) return TextHandleType.BottomLeft;
            if (new Rectangle(b.Right - hs / 2, b.Bottom - hs / 2, hs, hs).Contains(pt)) return TextHandleType.BottomRight;
            return TextHandleType.None;
        }

        public virtual void Dispose() { }
    }

    public class RectAnnotation : AnnotationItem
    {
        public Rectangle Rect;
        public Color Color;
        public float Width;
        public bool IsFilled = false;

        public override bool SupportsColor { get { return true; } }
        public override void SetColor(Color c) { Color = c; }

        protected override void DrawCore(Graphics g)
        {
            if (Rect.Width <= 0 || Rect.Height <= 0) return;
            if (IsFilled)
            {
                using (SolidBrush b = new SolidBrush(Color))
                {
                    g.FillRectangle(b, Rect);
                }
            }
            else
            {
                using (Pen p = new Pen(Color, Width))
                {
                    g.DrawRectangle(p, Rect);
                }
            }

            if (IsHoveredForEraser)
            {
                using (Pen ep = new Pen(Color.FromArgb(220, 239, 68, 68), 2f))
                {
                    ep.DashStyle = DashStyle.Dash;
                    g.DrawRectangle(ep, Rect);
                }
            }

            DrawSelectionAdorner(g);
        }

        public override void Offset(int dx, int dy)
        {
            Rect.Offset(dx, dy);
        }

        public override Rectangle GetBounds()
        {
            return Rect;
        }

        public override void SetBounds(Rectangle bounds)
        {
            Rect = new Rectangle(bounds.X, bounds.Y, Math.Max(1, bounds.Width), Math.Max(1, bounds.Height));
        }

        protected override bool HitTestItemCore(Point pt)
        {
            if (IsFilled) return Rect.Contains(pt);
            int tolerance = (int)(Width / 2) + 4;
            Rectangle outer = new Rectangle(Rect.X - tolerance, Rect.Y - tolerance, Rect.Width + tolerance * 2, Rect.Height + tolerance * 2);
            Rectangle inner = new Rectangle(Rect.X + tolerance, Rect.Y + tolerance, Math.Max(0, Rect.Width - tolerance * 2), Math.Max(0, Rect.Height - tolerance * 2));
            return outer.Contains(pt) && !inner.Contains(pt);
        }
    }

    public class EllipseAnnotation : AnnotationItem
    {
        public Rectangle Rect;
        public Color Color;
        public float Width;
        public bool IsFilled = false;

        public override bool SupportsColor { get { return true; } }
        public override void SetColor(Color c) { Color = c; }

        protected override void DrawCore(Graphics g)
        {
            if (Rect.Width <= 0 || Rect.Height <= 0) return;
            if (IsFilled)
            {
                using (SolidBrush b = new SolidBrush(Color))
                {
                    g.FillEllipse(b, Rect);
                }
            }
            else
            {
                using (Pen p = new Pen(Color, Width))
                {
                    g.DrawEllipse(p, Rect);
                }
            }

            if (IsHoveredForEraser)
            {
                using (Pen ep = new Pen(Color.FromArgb(220, 239, 68, 68), 2f))
                {
                    ep.DashStyle = DashStyle.Dash;
                    g.DrawEllipse(ep, Rect);
                }
            }

            DrawSelectionAdorner(g);
        }

        public override void Offset(int dx, int dy)
        {
            Rect.Offset(dx, dy);
        }

        public override Rectangle GetBounds()
        {
            return Rect;
        }

        public override void SetBounds(Rectangle bounds)
        {
            Rect = new Rectangle(bounds.X, bounds.Y, Math.Max(1, bounds.Width), Math.Max(1, bounds.Height));
        }

        protected override bool HitTestItemCore(Point pt)
        {
            if (Rect.Width <= 0 || Rect.Height <= 0) return false;
            float rx = Rect.Width / 2f;
            float ry = Rect.Height / 2f;
            float cx = Rect.Left + rx;
            float cy = Rect.Top + ry;
            float norm = (float)(Math.Pow(pt.X - cx, 2) / Math.Pow(rx, 2) + Math.Pow(pt.Y - cy, 2) / Math.Pow(ry, 2));
            if (IsFilled) return norm <= 1.05f;
            return norm >= 0.75f && norm <= 1.25f;
        }
    }

    public class LineAnnotation : AnnotationItem
    {
        public Point Start;
        public Point End;
        public Color Color;
        public float Width;

        /// <summary>是否画成虚线（线型下拉之外独立一档开关，直线与箭头共用同一套实参）</summary>
        public bool IsDashed = false;

        public override bool SupportsColor { get { return true; } }
        public override void SetColor(Color c) { Color = c; }

        protected override void DrawCore(Graphics g)
        {
            if (Start == End) return;
            using (Pen p = new Pen(Color, Width))
            {
                // 虚线一律平头端帽：圆头会把每一小段鼓成胶囊，断口被填满就看不出是虚线了
                p.StartCap = IsDashed ? LineCap.Flat : LineCap.Round;
                p.EndCap = IsDashed ? LineCap.Flat : LineCap.Round;
                if (IsDashed) ApplyDashedPen(p);
                g.DrawLine(p, Start, End);
            }

            if (IsHoveredForEraser)
            {
                using (Pen ep = new Pen(Color.FromArgb(220, 239, 68, 68), Width + 3f))
                {
                    ep.DashStyle = DashStyle.Dash;
                    g.DrawLine(ep, Start, End);
                }
            }

            DrawSelectionAdorner(g);
        }

        public override void Offset(int dx, int dy)
        {
            Start.Offset(dx, dy);
            End.Offset(dx, dy);
        }

        public override Rectangle GetBounds()
        {
            return Rectangle.FromLTRB(
                Math.Min(Start.X, End.X), Math.Min(Start.Y, End.Y),
                Math.Max(Start.X, End.X), Math.Max(Start.Y, End.Y));
        }

        public override void SetBounds(Rectangle bounds)
        {
            Rectangle cur = GetBounds();
            Start = MapPoint(Start, cur, bounds);
            End = MapPoint(End, cur, bounds);
        }

        protected override bool HitTestItemCore(Point pt)
        {
            float dist = DistancePointToSegment(pt, Start, End);
            return dist <= (Width / 2f + 5f);
        }

        public override List<VertexRef> GetEditVertices()
        {
            List<VertexRef> v = new List<VertexRef>(2);
            v.Add(new VertexRef(0, Start));
            v.Add(new VertexRef(1, End));
            return v;
        }

        public override void MoveVertex(int index, Point pt)
        {
            if (index == 0) Start = pt;
            else if (index == 1) End = pt;
        }

        public static float DistancePointToSegment(Point p, Point v, Point w)
        {
            float l2 = (float)(Math.Pow(v.X - w.X, 2) + Math.Pow(v.Y - w.Y, 2));
            if (l2 == 0) return (float)Math.Sqrt(Math.Pow(p.X - v.X, 2) + Math.Pow(p.Y - v.Y, 2));
            float t = ((p.X - v.X) * (w.X - v.X) + (p.Y - v.Y) * (w.Y - v.Y)) / l2;
            t = Math.Max(0, Math.Min(1, t));
            PointF proj = new PointF(v.X + t * (w.X - v.X), v.Y + t * (w.Y - v.Y));
            return (float)Math.Sqrt(Math.Pow(p.X - proj.X, 2) + Math.Pow(p.Y - proj.Y, 2));
        }
    }

    public class ArrowAnnotation : AnnotationItem
    {
        public Point Start;
        public Point End;
        public Color Color;
        public float Width;

        /// <summary>箭头头部样式（直线 / 细箭头 / 实心箭头 / 空心箭头），对齐 Snipaste 的线型下拉</summary>
        public ArrowHeadStyle HeadStyle = ArrowHeadStyle.Solid;

        /// <summary>是否画成虚线杆身（只影响杆，不影响头部；头部始终是实心的，否则尖端会碎掉）</summary>
        public bool IsDashed = false;

        public override bool SupportsColor { get { return true; } }
        public override void SetColor(Color c) { Color = c; }

        protected override void DrawCore(Graphics g)
        {
            if (Start == End) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            float dx = End.X - Start.X;
            float dy = End.Y - Start.Y;
            float dist = (float)Math.Sqrt(dx * dx + dy * dy);

            if (dist < 4f) return;

            float angle = (float)Math.Atan2(dy, dx);
            float cosA = (float)Math.Cos(angle);
            float sinA = (float)Math.Sin(angle);

            if (HeadStyle == ArrowHeadStyle.Hollow)
            {
                // 空心箭头：等边三角只描边不填充，底图从三角形内部透出来。
                // 头部与线宽挂钩并设下限，太细的线也能看清三角轮廓。
                float headLen = Math.Min(dist * 0.7f, Math.Max(14f, Width * 4.2f));
                float spreadH = 0.42f;

                PointF baseMid = new PointF(End.X - headLen * cosA, End.Y - headLen * sinA);
                PointF hw1 = new PointF(
                    End.X - headLen * (float)Math.Cos(angle - spreadH),
                    End.Y - headLen * (float)Math.Sin(angle - spreadH));
                PointF hw2 = new PointF(
                    End.X - headLen * (float)Math.Cos(angle + spreadH),
                    End.Y - headLen * (float)Math.Sin(angle + spreadH));

                // 杆身止于三角底边中点：Flat 端帽保证它不会多出一截戳进三角形内部
                using (Pen p = new Pen(Color, Width))
                {
                    p.StartCap = IsDashed ? LineCap.Flat : LineCap.Round;
                    p.EndCap = LineCap.Flat;
                    if (IsDashed) ApplyDashedPen(p);
                    g.DrawLine(p, Start.X, Start.Y, baseMid.X, baseMid.Y);
                }

                using (Pen borderPen = new Pen(Color, Math.Max(Width, 1.4f)))
                {
                    borderPen.LineJoin = LineJoin.Miter;
                    g.DrawPolygon(borderPen, new PointF[] { new PointF(End.X, End.Y), hw1, hw2 });
                }

                DrawEraserHintAndAdorner(g);
                return;
            }

            // 实心系的两种样式只差在头部大小与燕尾深度：细箭头轻、实心箭头沉
            bool isThin = (HeadStyle == ArrowHeadStyle.Thin);
            float arrowLen = isThin
                ? Math.Min(dist * 0.7f, Math.Max(9f, Width * 2.6f))
                : Math.Min(dist * 0.7f, Math.Max(13f, Width * 3.8f));
            float spreadAngle = isThin ? 0.34f : 0.40f;
            float notchDist = arrowLen * (isThin ? 0.78f : 0.72f);

            // 燕尾凹槽中心位置 (杆身在此处收止，严密不穿帮)
            PointF notch = new PointF(End.X - notchDist * cosA, End.Y - notchDist * sinA);

            // 左右翼尖坐标
            PointF wing1 = new PointF(
                End.X - arrowLen * (float)Math.Cos(angle - spreadAngle),
                End.Y - arrowLen * (float)Math.Sin(angle - spreadAngle));
            PointF wing2 = new PointF(
                End.X - arrowLen * (float)Math.Cos(angle + spreadAngle),
                End.Y - arrowLen * (float)Math.Sin(angle + spreadAngle));

            // 1. 绘制箭头杆身 (从 Start 到 notch，末端平整，绝不透出尖端)
            using (Pen p = new Pen(Color, Width))
            {
                p.StartCap = IsDashed ? LineCap.Flat : LineCap.Round;
                p.EndCap = LineCap.Flat;
                if (IsDashed) ApplyDashedPen(p);
                g.DrawLine(p, Start.X, Start.Y, notch.X, notch.Y);
            }

            // 2. 绘制 Snipaste 经典凹槽实心锐角箭头头部
            PointF[] headPoly = new PointF[] { new PointF(End.X, End.Y), wing1, notch, wing2 };
            using (SolidBrush b = new SolidBrush(Color))
            {
                g.FillPolygon(b, headPoly);
            }
            using (Pen borderPen = new Pen(Color, 0.8f))
            {
                g.DrawPolygon(borderPen, headPoly);
            }

            DrawEraserHintAndAdorner(g);
        }

        /// <summary>虚线悬停提示 + 选中装饰：四种样式共用同一套收尾绘制</summary>
        private void DrawEraserHintAndAdorner(Graphics g)
        {
            // 橡皮擦悬停提示
            if (IsHoveredForEraser)
            {
                using (Pen ep = new Pen(Color.FromArgb(220, 239, 68, 68), Width + 3f))
                {
                    ep.DashStyle = DashStyle.Dash;
                    g.DrawLine(ep, Start, End);
                }
            }

            DrawSelectionAdorner(g);
        }

        public override void Offset(int dx, int dy)
        {
            Start.Offset(dx, dy);
            End.Offset(dx, dy);
        }

        public override Rectangle GetBounds()
        {
            return Rectangle.FromLTRB(
                Math.Min(Start.X, End.X), Math.Min(Start.Y, End.Y),
                Math.Max(Start.X, End.X), Math.Max(Start.Y, End.Y));
        }

        public override void SetBounds(Rectangle bounds)
        {
            Rectangle cur = GetBounds();
            Start = MapPoint(Start, cur, bounds);
            End = MapPoint(End, cur, bounds);
        }

        protected override bool HitTestItemCore(Point pt)
        {
            return LineAnnotation.DistancePointToSegment(pt, Start, End) <= (Width / 2f + 6f);
        }

        public override List<VertexRef> GetEditVertices()
        {
            List<VertexRef> v = new List<VertexRef>(2);
            v.Add(new VertexRef(0, Start));
            v.Add(new VertexRef(1, End));
            return v;
        }

        public override void MoveVertex(int index, Point pt)
        {
            // 箭头尖端是 End，拖它即改指向；拖 Start 即改出箭位置
            if (index == 0) Start = pt;
            else if (index == 1) End = pt;
        }
    }

    public class BrushAnnotation : AnnotationItem
    {
        public List<Point> Points = new List<Point>();
        public Color Color;
        public float Width;

        public override bool SupportsColor { get { return true; } }
        public override void SetColor(Color c) { Color = c; }

        protected override void DrawCore(Graphics g)
        {
            if (Points.Count < 2) return;
            using (Pen p = new Pen(Color, Width))
            {
                p.StartCap = LineCap.Round;
                p.EndCap = LineCap.Round;
                p.LineJoin = LineJoin.Round;
                g.DrawLines(p, Points.ToArray());
            }

            if (IsHoveredForEraser)
            {
                using (Pen ep = new Pen(Color.FromArgb(220, 239, 68, 68), Width + 3f))
                {
                    // 与本体笔一致用圆角接头：默认的 Miter 接头会在折线锐角顶点处甩出与
                    // 笔宽成正比的尖刺（30px 笔在约 30° 夹角下可外伸近 60px），既难看，
                    // 也要求失效范围按尖刺长度外扩
                    ep.LineJoin = LineJoin.Round;
                    ep.DashStyle = DashStyle.Dash;
                    g.DrawLines(ep, Points.ToArray());
                }
            }

            DrawSelectionAdorner(g);
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

        public override Rectangle GetBounds()
        {
            return GetPointsBounds(Points);
        }

        public override void SetBounds(Rectangle bounds)
        {
            Rectangle cur = GetBounds();
            if (cur.IsEmpty || (cur.Width == 0 && cur.Height == 0)) return;
            for (int i = 0; i < Points.Count; i++)
            {
                Points[i] = MapPoint(Points[i], cur, bounds);
            }
        }

        protected override bool HitTestItemCore(Point pt)
        {
            for (int i = 0; i < Points.Count - 1; i++)
            {
                if (LineAnnotation.DistancePointToSegment(pt, Points[i], Points[i + 1]) <= (Width / 2f + 5f))
                    return true;
            }
            return false;
        }

        public override List<VertexRef> GetEditVertices()
        {
            return SampleVertices(Points);
        }

        public override void MoveVertex(int index, Point pt)
        {
            if (index >= 0 && index < Points.Count) Points[index] = pt;
        }
    }

    public class MarkerAnnotation : AnnotationItem
    {
        public List<Point> Points = new List<Point>();
        public Color Color;
        public float Width;

        public override bool SupportsColor { get { return true; } }
        public override void SetColor(Color c) { Color = c; }

        protected override void DrawCore(Graphics g)
        {
            if (Points.Count < 2) return;

            Point[] pts = Points.ToArray();

            // 底层：半透明主色铺满整个笔宽，保留底图内容可见（荧光笔的核心特征）
            using (Pen p = new Pen(Color.FromArgb(112, Color.R, Color.G, Color.B), Width))
            {
                p.StartCap = LineCap.Square;
                p.EndCap = LineCap.Square;
                p.LineJoin = LineJoin.Round;
                g.DrawLines(p, pts);
            }

            // 上层：同色提亮后的窄芯线，模拟荧光笔"芯亮边淡"的发光质感
            Color core = Color.FromArgb(
                Color.R + (255 - Color.R) * 55 / 100,
                Color.G + (255 - Color.G) * 55 / 100,
                Color.B + (255 - Color.B) * 55 / 100);
            float coreW = Width * 0.5f;
            if (coreW < 1.5f) coreW = 1.5f;
            using (Pen cp = new Pen(Color.FromArgb(96, core.R, core.G, core.B), coreW))
            {
                cp.StartCap = LineCap.Round;
                cp.EndCap = LineCap.Round;
                cp.LineJoin = LineJoin.Round;
                g.DrawLines(cp, pts);
            }

            if (IsHoveredForEraser)
            {
                using (Pen ep = new Pen(Color.FromArgb(220, 239, 68, 68), Width + 2f))
                {
                    // 同 BrushAnnotation：Miter 接头会在锐角顶点甩出长尖刺，改圆角接头后
                    // 高亮范围不超过骨架包围盒外扩半个笔宽
                    ep.LineJoin = LineJoin.Round;
                    ep.DashStyle = DashStyle.Dash;
                    g.DrawLines(ep, Points.ToArray());
                }
            }

            DrawSelectionAdorner(g);
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

        public override Rectangle GetBounds()
        {
            return GetPointsBounds(Points);
        }

        public override void SetBounds(Rectangle bounds)
        {
            Rectangle cur = GetBounds();
            if (cur.IsEmpty || (cur.Width == 0 && cur.Height == 0)) return;
            for (int i = 0; i < Points.Count; i++)
            {
                Points[i] = MapPoint(Points[i], cur, bounds);
            }
        }

        protected override bool HitTestItemCore(Point pt)
        {
            for (int i = 0; i < Points.Count - 1; i++)
            {
                if (LineAnnotation.DistancePointToSegment(pt, Points[i], Points[i + 1]) <= (Width / 2f + 4f))
                    return true;
            }
            return false;
        }

        public override List<VertexRef> GetEditVertices()
        {
            return SampleVertices(Points);
        }

        public override void MoveVertex(int index, Point pt)
        {
            if (index >= 0 && index < Points.Count) Points[index] = pt;
        }
    }

    /// <summary>
    /// 序号标注：实心圆底 + 白色数字，单击即落点，删除后自动重排序号
    /// </summary>
    public class NumberAnnotation : AnnotationItem
    {
        public Point Center;
        public int Number;
        public Color Color;
        /// <summary>圆形徽标直径</summary>
        public int Diameter = 26;

        public override bool SupportsColor { get { return true; } }
        public override void SetColor(Color c) { Color = c; }

        protected override void DrawCore(Graphics g)
        {
            int d = Math.Max(12, Diameter);
            Rectangle r = new Rectangle(Center.X - d / 2, Center.Y - d / 2, d, d);

            using (SolidBrush b = new SolidBrush(Color))
            {
                g.FillEllipse(b, r);
            }

            if (IsHoveredForEraser)
            {
                using (Pen ep = new Pen(Color.FromArgb(220, 239, 68, 68), 2f))
                {
                    ep.DashStyle = DashStyle.Dash;
                    g.DrawEllipse(ep, r);
                }
            }

            string txt = Number.ToString();
            float fontSize = FitTextSize(txt, d);

            // 必须用点式重载：带 layout rect 的重载在文字宽度超过矩形时会折行，
            // 两位数（11、12）因此只露出首位，看起来"整个不显示"。
            // 点式重载以 Center 为几何中心绘制，不受矩形宽度约束。
            using (Font f = new Font("Arial", fontSize, FontStyle.Bold))
            using (SolidBrush tb = new SolidBrush(Color.White))
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                sf.FormatFlags = StringFormatFlags.NoClip | StringFormatFlags.NoWrap;
                g.DrawString(txt, f, tb, new PointF(Center.X, Center.Y), sf);
            }

            DrawSelectionAdorner(g);
        }

        /// <summary>
        /// 计算能完整落在徽标圆内的字号：以字形轮廓的紧致包围盒量度实际占位，
        /// 按圆的内接方形（0.70 × 直径）限幅等比缩小，位数越多字号越小；
        /// 单位数不缩减，保持原有观感
        /// </summary>
        private static float FitTextSize(string txt, int d)
        {
            // 基准字号沿用原设计（随徽标直径等比缩放）
            float fontSize = Math.Max(8f, d * 0.54f);

            using (Font probeFont = new Font("Arial", 100f, FontStyle.Bold))
            using (GraphicsPath ink = new GraphicsPath())
            {
                ink.AddString(txt, probeFont.FontFamily, (int)FontStyle.Bold, 100f, PointF.Empty, null);
                RectangleF box = ink.GetBounds();
                if (box.Width > 0f && box.Height > 0f)
                {
                    float maxExtent = d * 0.70f;
                    float fit = maxExtent * 100f / box.Width;
                    float fitH = maxExtent * 100f / box.Height;
                    if (fitH < fit) fit = fitH;
                    if (fit < fontSize) fontSize = fit;
                }
            }

            if (fontSize < 6f) fontSize = 6f;
            return fontSize;
        }

        public override void Offset(int dx, int dy)
        {
            Center.Offset(dx, dy);
        }

        public override Rectangle GetBounds()
        {
            int d = Math.Max(12, Diameter);
            return new Rectangle(Center.X - d / 2, Center.Y - d / 2, d, d);
        }

        public override void SetBounds(Rectangle bounds)
        {
            int d = Math.Max(12, Math.Min(bounds.Width, bounds.Height));
            Center = new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
            Diameter = d;
        }

        protected override bool HitTestItemCore(Point pt)
        {
            int d = Math.Max(12, Diameter);
            int dx = pt.X - Center.X;
            int dy = pt.Y - Center.Y;
            return (dx * dx + dy * dy) <= (d / 2 + 2) * (d / 2 + 2);
        }
    }

    public class MosaicAnnotation : AnnotationItem
    {
        public Rectangle Rect;
        public Bitmap MosaicSnippet;

        /// <summary>true = 高斯模糊模式；false = 像素马赛克模式</summary>
        public bool IsBlur = false;

        /// <summary>马赛克颗粒度（像素块边长）；模糊模式下作为模糊半径参考</summary>
        public int BlockSize = 12;

        /// <summary>缩放后需要重新按原图采样（由宿主读取本源位图后重建）</summary>
        public bool NeedsRebuild = false;

        protected override void DrawCore(Graphics g)
        {
            if (MosaicSnippet != null && Rect.Width > 0 && Rect.Height > 0)
            {
                g.DrawImage(MosaicSnippet, Rect);
            }

            if (IsHoveredForEraser)
            {
                using (Pen ep = new Pen(Color.FromArgb(220, 239, 68, 68), 2f))
                {
                    ep.DashStyle = DashStyle.Dash;
                    g.DrawRectangle(ep, Rect);
                }
            }

            DrawSelectionAdorner(g);
        }

        public override void Offset(int dx, int dy)
        {
            Rect.Offset(dx, dy);
        }

        public override Rectangle GetBounds()
        {
            return Rect;
        }

        public override void SetBounds(Rectangle bounds)
        {
            Rect = new Rectangle(bounds.X, bounds.Y, Math.Max(4, bounds.Width), Math.Max(4, bounds.Height));
            // 马赛克/模糊必须按新区域重新采样，否则块大小会失真
            NeedsRebuild = true;
        }

        protected override bool HitTestItemCore(Point pt)
        {
            return Rect.Contains(pt);
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
        public float FontSize = 16f;
        public Color Color = Color.FromArgb(238, 28, 37);

        public bool IsBold = true;
        public bool IsItalic = false;
        public bool HasBackground = false;
        public string FontFamilyName = "Microsoft YaHei UI";

        public override Rectangle GetBounds()
        {
            if (string.IsNullOrEmpty(Text) || TextFont == null) return Rectangle.Empty;
            Size sz = TextRenderer.MeasureText(Text, TextFont);
            return new Rectangle(Location.X - 4, Location.Y - 2, sz.Width + 8, sz.Height + 4);
        }

        public override void SetBounds(Rectangle bounds)
        {
            Rectangle cur = GetBounds();
            if (cur.IsEmpty || cur.Width <= 0) return;

            // 文字缩放以宽度比例为基准等比调整字号，左上角跟随拖拽后的包围盒
            float ratio = (float)bounds.Width / cur.Width;
            float newSize = Math.Max(9f, Math.Min(96f, FontSize * ratio));

            Location = new Point(bounds.X + 4, bounds.Y + 2);
            FontSize = newSize;

            FontStyle fs = FontStyle.Regular;
            if (IsBold) fs |= FontStyle.Bold;
            if (IsItalic) fs |= FontStyle.Italic;
            if (TextFont != null) TextFont.Dispose();
            TextFont = new Font(FontFamilyName, newSize, fs);
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
            if (IsHiddenForEditing) return TextHandleType.None;
            if (string.IsNullOrEmpty(Text) || TextFont == null) return TextHandleType.None;

            // 文字同样参与旋转，而包围盒与手柄坐标全都在"未旋转"的局部坐标系里。
            // 命中前先把图面上的点逆旋转回来，否则转过角度后点哪儿都选不中，
            // 或者选中的是旋转之前的老位置。
            Point local = IsRotated ? InverseRotate(pt) : pt;

            if (IsSelected)
            {
                // 旋转手柄悬在包围盒之外，但拖拽逻辑与四角缩放完全不同，
                // 必须最先判定，避免被包围盒外沿的热区抢走。
                if (HitTestRotateHandle(pt)) return TextHandleType.Rotate;

                if (GetHandleRect(TextHandleType.TopLeft).Contains(local)) return TextHandleType.TopLeft;
                if (GetHandleRect(TextHandleType.TopRight).Contains(local)) return TextHandleType.TopRight;
                if (GetHandleRect(TextHandleType.BottomLeft).Contains(local)) return TextHandleType.BottomLeft;
                if (GetHandleRect(TextHandleType.BottomRight).Contains(local)) return TextHandleType.BottomRight;
            }

            Rectangle b = GetBounds();
            if (b.Contains(local)) return TextHandleType.Body;

            return TextHandleType.None;
        }

        protected override bool HitTestItemCore(Point pt)
        {
            return GetBounds().Contains(pt);
        }

        protected override void DrawCore(Graphics g)
        {
            if (string.IsNullOrEmpty(Text) || TextFont == null) return;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            Rectangle bounds = GetBounds();

            if (HasBackground)
            {
                using (SolidBrush bgBrush = new SolidBrush(Color.FromArgb(230, 20, 24, 33)))
                {
                    g.FillRectangle(bgBrush, bounds);
                }
            }
            else
            {
                bool isDarkColor = (Color.R * 0.299 + Color.G * 0.587 + Color.B * 0.114) < 128;
                Color shadowColor = isDarkColor ? Color.FromArgb(160, 255, 255, 255) : Color.FromArgb(160, 15, 23, 42);
                using (SolidBrush shadowBrush = new SolidBrush(shadowColor))
                {
                    g.DrawString(Text, TextFont, shadowBrush, Location.X + 1, Location.Y + 1);
                }
            }

            using (SolidBrush mainBrush = new SolidBrush(Color))
            {
                g.DrawString(Text, TextFont, mainBrush, Location.X, Location.Y);
            }

            // 选中装饰统一走基类：虚线框 + 四角缩放手柄 + 旋转手柄。
            // 文字没有骨架顶点（GetEditVertices 返回空），因此不会多出顶点方块。
            DrawSelectionAdorner(g);

            if (IsHoveredForEraser)
            {
                using (Pen ep = new Pen(Color.FromArgb(220, 239, 68, 68), 2f))
                {
                    ep.DashStyle = DashStyle.Dash;
                    g.DrawRectangle(ep, bounds);
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

        private HitType _currentHit = HitType.None;
        private bool _isResizingOrMoving = false;
        private Rectangle _initialRect;

        // 工具与图元
        private ToolType _currentTool = ToolType.None;
        private readonly List<AnnotationItem> _annotations = new List<AnnotationItem>();
        private readonly List<AnnotationItem> _redoStack = new List<AnnotationItem>();
        private AnnotationItem _tempAnnotation = null;

        // 文字标注高级编辑状态
        private TextAnnotation _selectedText = null;
        private bool _isManipulatingText = false;
        private TextHandleType _textManipulatingHandle = TextHandleType.None;
        private Point _textDragStartMouse;
        private Point _textInitialLocation;
        private float _textInitialFontSize;
        private Rectangle _textInitialBounds;
        private TextBox _activeTextBox = null;
        private bool _isCommittingText = false;

        /// <summary>
        /// 正在被"重新编辑内容"的既有文字标注（null 表示当前文本框是新建文字）。
        /// 提交时把文本写回这一个图元，而不是新加一个，才能实现"改完内容还是同一处标注"。
        /// </summary>
        private TextAnnotation _editingText = null;

        /// <summary>图元四角缩放时允许的最小边长</summary>
        private const int MinShapeSize = 6;

        /// <summary>正在拖拽的骨架顶点在图元内部的索引（-1 表示当前不是顶点拖拽）</summary>
        private int _shapeVertexIndex = -1;

        // 滚轮 / 快捷键调参共用的档位表
        private static readonly float[] PEN_WIDTH_STEPS = new float[] { 2f, 4f, 8f };
        private static readonly float[] MARKER_WIDTH_STEPS = new float[] { 10f, 18f, 28f };
        private static readonly float[] MOSAIC_SIZE_STEPS = new float[] { 6f, 12f, 20f };

        // 马赛克 / 模糊强度滑杆的取值范围 (Snipaste 二级栏滑杆的物理量程)
        private const int MOSAIC_MIN = 4;
        private const int MOSAIC_MAX = 40;

        // 各工具的出厂默认参数。副栏主参数胶囊在数值偏离这些默认值时点亮红点，
        // 常量与字段初始值共用同一份定义，避免两处魔数各改各的。
        private const float DEFAULT_PEN_WIDTH = 4f;
        private const float DEFAULT_MARKER_WIDTH = 18f;
        private const int DEFAULT_MOSAIC_BLOCK_SIZE = 12;

        // 全局画笔与样式属性 (与 Snipaste 1:1 对齐)
        private Color _activeColor = Color.FromArgb(238, 28, 37); // 正红默认
        private float _penWidth = DEFAULT_PEN_WIDTH; // 2, 4, 8
        private int _mosaicBlockSize = DEFAULT_MOSAIC_BLOCK_SIZE; // 马赛克颗粒度 / 模糊半径
        private float _markerWidth = DEFAULT_MARKER_WIDTH; // 马克笔独立笔宽 (荧光笔专用，10/18/28)
        private int _nextNumber = 1; // 序号标注的下一个递增编号
        private bool _isFilled = false; // 实心填充开关 (用于矩形/椭圆)
        // 箭头头部样式：出厂默认实心箭头，与既有观感保持一致
        private ArrowHeadStyle _arrowStyle = ArrowHeadStyle.Solid;
        // 线型虚实：直线与箭头共用一档开关，默认实线（改默认值会动到既有用户的手感）
        private bool _lineDashed = false;
        private bool _isFontBold = true;
        private bool _isFontItalic = false;
        private bool _hasTextBackground = false;
        private float _activeFontSize = 16f;

        private static readonly string[] FONT_FAMILIES = new string[]
        {
            "Microsoft YaHei UI",
            "Segoe UI",
            "Arial",
            "Consolas",
            "SimSun",
            "SimHei",
            "KaiTi"
        };
        private int _fontFamilyIndex = 0;
        private string _activeFontFamily = "Microsoft YaHei UI";

        // 空格键平移选区
        private bool _isSpaceKeyDown = false;
        private bool _isSpaceDragging = false;
        private Point _spaceDragStartMouse;
        private Rectangle _spaceDragInitialRect;

        // 橡皮擦悬停目标
        private AnnotationItem _hoveredEraserTarget = null;

        // 图元二次编辑 (选中 / 平移 / 四角缩放)
        private AnnotationItem _selectedShape = null;
        private bool _isManipulatingShape = false;
        private TextHandleType _shapeHandle = TextHandleType.None;
        private Point _shapeDragStartMouse;
        private Rectangle _shapeInitialBounds;

        // 渲染画笔
        private readonly SolidBrush _maskBrush = new SolidBrush(Color.FromArgb(120, 0, 0, 0));
        private readonly Pen _borderPen = new Pen(Color.FromArgb(14, 165, 233), 2f);
        private readonly Font _uiFont = new Font("Microsoft YaHei", 9f);
        private readonly Font _tipFont = new Font("Microsoft YaHei", 8f);

        // 工具栏与二级属性栏
        public class ToolButton
        {
            public Rectangle Bounds;
            public string Id;
            public string Tooltip;
            public ToolType Tool;
            public bool IsDisabled;
            public Action ClickAction;
        }

        public class SubOptionItem
        {
            public Rectangle Bounds;
            public string Id;
            public string Text;
            public Color ColorValue;
            public bool IsColor;
            public float SizeValue;
            /// <summary>由调用方显式指定的激活态（如马赛克/模糊模式、颗粒度档位），优先于内部状态推断</summary>
            public bool IsActiveFlag;
            /// <summary>是否在右上角点一颗红点，提示"该参数已偏离默认值"</summary>
            public bool ShowBadge;
            public Action ClickAction;
        }

        private readonly List<ToolButton> _toolButtons = new List<ToolButton>();
        private Rectangle _toolBarBounds;
        private ToolButton _hoveredBtn = null;

        // 选区尺寸标签 (Snipaste 同款：贴在选区左上角上方，深色胶囊 "928 × 302 px")
        private Rectangle _selSizeLabelBounds;
        private string _sizeInfoText = "";

        // 复合工具按钮（矩形/椭圆、折线/箭头、马赛克/模糊）右下角的小三角标记
        private static readonly string[] COMPOUND_BTN_IDS = new string[] { "rect_ellipse", "line_arrow", "mosaic" };

        private readonly List<SubOptionItem> _subOptions = new List<SubOptionItem>();
        private Rectangle _subBarBounds;
        private SubOptionItem _hoveredSub = null;

        // 马赛克/模糊强度滑杆的拖拽状态 (Snipaste 二级栏中唯一的滑杆控件)
        private SubOptionItem _draggingSlider = null;

        // 二级属性栏内细分隔线占用的水平宽度
        private const int SUB_SEP_W = 8;

        // 20 色预设调色板矩阵 (两行十列，与用户 Snipaste 截图完全对齐)
        public static readonly Color[] PRESET_COLORS_ROW1 = new Color[]
        {
            Color.FromArgb(0, 0, 0),        // 纯黑
            Color.FromArgb(127, 127, 127),  // 灰色
            Color.FromArgb(136, 0, 21),     // 栗褐/深红
            Color.FromArgb(237, 28, 36),    // 大红
            Color.FromArgb(255, 127, 39),   // 橙色
            Color.FromArgb(255, 242, 0),    // 明黄
            Color.FromArgb(34, 177, 76),    // 鲜绿
            Color.FromArgb(0, 162, 232),    // 天蓝/青色
            Color.FromArgb(63, 72, 204),    // 深蓝
            Color.FromArgb(163, 73, 164)    // 紫色
        };

        public static readonly Color[] PRESET_COLORS_ROW2 = new Color[]
        {
            Color.FromArgb(255, 255, 255),  // 纯白
            Color.FromArgb(195, 195, 195),  // 浅灰
            Color.FromArgb(185, 122, 87),   // 浅褐
            Color.FromArgb(255, 174, 201),  // 粉红
            Color.FromArgb(255, 201, 14),   // 浅橙/金黄
            Color.FromArgb(239, 228, 176),  // 淡黄
            Color.FromArgb(181, 230, 29),   // 嫩绿/黄绿
            Color.FromArgb(153, 217, 234),  // 浅青
            Color.FromArgb(112, 146, 190),  // 灰蓝/钢蓝
            Color.FromArgb(200, 191, 231)   // 浅紫/淡紫
        };

        public Bitmap CroppedResult { get; private set; }
        public bool IsOcrRequested { get; private set; }
        public bool IsAiEditRequested { get; private set; }
        public string AiEditBundlePath { get; private set; }
        public bool IsPinRequested { get; private set; }
        public bool IsColorCopied { get; private set; }
        public string CopiedColorString { get; private set; }
        public Rectangle SelectedRect { get { return _selectedRect; } }

        // 放大镜与取色 HUD
        private Point _currentMousePos;
        private bool _isColorHexMode = false;

        // Win32 API 与高精度结构体
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
            public POINT(int x, int y) { X = x; Y = y; }
        }

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);
        private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int X, int Y);

        // 历史选区持久化
        private static readonly string RECT_HISTORY_FILE = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".rubick_snipper_rects.txt");
        private readonly List<Rectangle> _rectHistory = new List<Rectangle>();
        private int _rectHistoryIndex = -1;

        // Snipaste 颜色与窗口智能探测
        private bool _detectElementMode = false; // false=局部元素/色块优先(默认), true=强制整窗
        private Rectangle _hoverWindowRect = Rectangle.Empty;
        private Point _mouseDownPos = Point.Empty;
        private bool _showHelpOverlay = false;

        // 像素内存加速缓存
        private readonly int[] _pixels;
        private readonly int _bmpWidth;
        private readonly int _bmpHeight;

        private Color GetPixelColorSafe(int x, int y)
        {
            if (_pixels == null || x < 0 || x >= _bmpWidth || y < 0 || y >= _bmpHeight) return Color.Black;
            int argb = _pixels[y * _bmpWidth + x];
            return Color.FromArgb(argb);
        }

        public SnipperForm(Bitmap fullBmp, Rectangle bounds)
        {
            _fullBmp = fullBmp;
            _screenBounds = bounds;

            _bmpWidth = fullBmp.Width;
            _bmpHeight = fullBmp.Height;
            _pixels = new int[_bmpWidth * _bmpHeight];

            // 抓屏位图现在是不透明的预乘格式（见 CaptureOpaqueScreen）；A 恒为 255，
            // 因此这里的原始字节能与直通 ARGB 混用，取色结果不变
            BitmapData bmpData = fullBmp.LockBits(new Rectangle(0, 0, _bmpWidth, _bmpHeight),
                ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                Marshal.Copy(bmpData.Scan0, _pixels, 0, _pixels.Length);
            }
            finally
            {
                // 拷贝过程异常（如内存不足）时也必须解锁，否则这张被长期持有的
                // 抓屏位图会一直处于锁定状态，后续 Dispose 会再抛异常掩盖原始错误。
                fullBmp.UnlockBits(bmpData);
            }

            this.FormBorderStyle = FormBorderStyle.None;
            this.StartPosition = FormStartPosition.Manual;
            this.Location = bounds.Location;
            this.Size = bounds.Size;
            this.TopMost = true;
            this.ShowInTaskbar = false;
            this.DoubleBuffered = true;
            this.Cursor = Cursors.Cross;
            this.KeyPreview = true;

            LoadRectHistory();
        }

        private void LoadRectHistory()
        {
            try
            {
                if (System.IO.File.Exists(RECT_HISTORY_FILE))
                {
                    string[] lines = System.IO.File.ReadAllLines(RECT_HISTORY_FILE);
                    foreach (string line in lines)
                    {
                        string[] parts = line.Split(',');
                        if (parts.Length == 4)
                        {
                            int x = int.Parse(parts[0].Trim());
                            int y = int.Parse(parts[1].Trim());
                            int w = int.Parse(parts[2].Trim());
                            int h = int.Parse(parts[3].Trim());
                            if (w > 10 && h > 10)
                            {
                                _rectHistory.Add(new Rectangle(x, y, w, h));
                            }
                        }
                    }
                }
            }
            catch { }
        }

        private void SaveRectToHistory(Rectangle rect)
        {
            try
            {
                if (rect.Width <= 10 || rect.Height <= 10) return;
                _rectHistory.Add(rect);
                while (_rectHistory.Count > 10) _rectHistory.RemoveAt(0);

                List<string> lines = new List<string>();
                foreach (Rectangle r in _rectHistory)
                {
                    lines.Add(string.Format("{0},{1},{2},{3}", r.X, r.Y, r.Width, r.Height));
                }
                System.IO.File.WriteAllLines(RECT_HISTORY_FILE, lines.ToArray());
            }
            catch { }
        }

        private int ColorDist(int c1, int c2)
        {
            int r1 = (c1 >> 16) & 0xFF, g1 = (c1 >> 8) & 0xFF, b1 = c1 & 0xFF;
            int r2 = (c2 >> 16) & 0xFF, g2 = (c2 >> 8) & 0xFF, b2 = c2 & 0xFF;
            return Math.Abs(r1 - r2) + Math.Abs(g1 - g2) + Math.Abs(b1 - b2);
        }

        /// <summary>
        /// Z-Order 顶层窗口精准穿透枚举 (跳过 SnipperForm 自身句柄)
        /// 100% 能够获取 Antigravity、IDE、Edge 等应用真实的完整主窗口矩形
        /// </summary>
        private Rectangle FindTopLevelWindowUnderPoint(int screenX, int screenY)
        {
            Rectangle result = Rectangle.Empty;
            try
            {
                EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
                {
                    if (hWnd == this.Handle) return true; // 关键：主动跳过自身全屏遮罩窗体！
                    if (!IsWindowVisible(hWnd) || IsIconic(hWnd)) return true;

                    RECT r;
                    int hr = DwmGetWindowAttribute(hWnd, DWMWA_EXTENDED_FRAME_BOUNDS, out r, Marshal.SizeOf(typeof(RECT)));
                    if (hr != 0)
                    {
                        GetWindowRect(hWnd, out r);
                    }

                    int w = r.Right - r.Left;
                    int h = r.Bottom - r.Top;
                    if (w <= 40 || h <= 40) return true;

                    Rectangle screenRect = new Rectangle(r.Left, r.Top, w, h);
                    if (screenRect.Contains(screenX, screenY))
                    {
                        int lx = screenRect.Left - _screenBounds.Left;
                        int ly = screenRect.Top - _screenBounds.Top;
                        Rectangle localRect = new Rectangle(lx, ly, w, h);
                        localRect.Intersect(new Rectangle(0, 0, this.Width, this.Height));
                        if (localRect.Width > 40 && localRect.Height > 40)
                        {
                            result = localRect;
                            return false; // 命中第一个（即最顶层的实际应用窗口），停止枚举
                        }
                    }
                    return true;
                }, IntPtr.Zero);
            }
            catch { }

            return result;
        }

        /// <summary>
        /// 限制在宿主窗口边界内部的色彩边界射线探测
        /// </summary>
        private Rectangle DetectColorBoundaryRect(Point pt, Rectangle container)
        {
            if (_pixels == null || pt.X < 2 || pt.X >= _bmpWidth - 2 || pt.Y < 2 || pt.Y >= _bmpHeight - 2)
                return Rectangle.Empty;

            if (container.IsEmpty) container = new Rectangle(0, 0, _bmpWidth, _bmpHeight);

            int baseColor = _pixels[pt.Y * _bmpWidth + pt.X];
            int threshold = 28;

            int left = pt.X;
            while (left > container.Left && ColorDist(_pixels[pt.Y * _bmpWidth + left], baseColor) <= threshold) left--;

            int right = pt.X;
            while (right < container.Right - 1 && ColorDist(_pixels[pt.Y * _bmpWidth + right], baseColor) <= threshold) right++;

            int top = pt.Y;
            while (top > container.Top && ColorDist(_pixels[top * _bmpWidth + pt.X], baseColor) <= threshold) top--;

            int bottom = pt.Y;
            while (bottom < container.Bottom - 1 && ColorDist(_pixels[bottom * _bmpWidth + pt.X], baseColor) <= threshold) bottom++;

            int w = right - left + 1;
            int h = bottom - top + 1;

            if (w >= 40 && h >= 24 && (w < container.Width - 30 || h < container.Height - 30))
            {
                // 剔除失真异常纵横比 (如 500x20 或 20x500)
                if ((float)h / w > 3.0f || (float)w / h > 6.0f) return Rectangle.Empty;
                return new Rectangle(left, top, w, h);
            }

            return Rectangle.Empty;
        }

        /// <summary>窗口/元素探测的位移节流阈值：光标挪动不足该像素数时复用上次结果</summary>
        private const int ElementDetectThreshold = 3;

        /// <summary>上次执行窗口探测时的光标位置，供节流判断；int.MinValue 表示无缓存</summary>
        private Point _lastDetectPoint = new Point(int.MinValue, int.MinValue);

        /// <param name="pt">当前光标位置（窗体坐标）</param>
        /// <param name="force">true 时忽略节流强制重探（如切换元素/整窗模式）</param>
        private void DetectWindowUnderPoint(Point pt, bool force = false)
        {
            if (_hasSelected || _isSelecting)
            {
                _hoverWindowRect = Rectangle.Empty;
                _lastDetectPoint = new Point(int.MinValue, int.MinValue);
                return;
            }

            // 节流：光标位移不足阈值且已有高亮结果时直接复用。每一次探测要跑一轮
            // EnumWindows 加一次横纵射线扫描（实测约 1.3ms），60Hz 鼠标事件下开销可观；
            // 高亮滞后 2px 肉眼不可辨。尚无结果时（高亮被清空）不受节流限制，保证必能重新命中。
            if (!force
                && !_hoverWindowRect.IsEmpty
                && Math.Abs(pt.X - _lastDetectPoint.X) < ElementDetectThreshold
                && Math.Abs(pt.Y - _lastDetectPoint.Y) < ElementDetectThreshold)
            {
                return;
            }
            _lastDetectPoint = pt;

            try
            {
                int screenX = _screenBounds.Left + pt.X;
                int screenY = _screenBounds.Top + pt.Y;

                // 1. 获取物理顶层应用窗口 (穿透自身遮罩)
                Rectangle topWin = FindTopLevelWindowUnderPoint(screenX, screenY);

                // 2. 如果是智能局部色块优先模式 (未按 Tab 强制整窗)
                if (!_detectElementMode && !topWin.IsEmpty)
                {
                    Rectangle innerCard = DetectColorBoundaryRect(pt, topWin);
                    if (!innerCard.IsEmpty && innerCard.Width >= 40 && innerCard.Height >= 24)
                    {
                        _hoverWindowRect = innerCard;
                        return;
                    }
                }

                // 3. 稳妥、精准地回退到当前完整的顶级应用主窗口 (Antigravity 窗口 100% 能够高亮整窗！)
                if (!topWin.IsEmpty && topWin.Width > 20 && topWin.Height > 20)
                {
                    _hoverWindowRect = topWin;
                    return;
                }
            }
            catch { }

            _hoverWindowRect = Rectangle.Empty;
        }

        /// <summary>
        /// 弹出文字输入框。existing 为 null 时是新建；非 null 时是重新编辑既有标注，
        /// 此时预填原有内容与字体、沿用原有位置，并在提交时写回同一个图元。
        /// </summary>
        private void SpawnTextInput(Point pt, TextAnnotation existing)
        {
            if (_activeTextBox != null)
            {
                CommitActiveText();
            }

            TextBox tb = new TextBox();
            tb.Multiline = true;
            tb.BorderStyle = BorderStyle.FixedSingle;
            tb.BackColor = Color.FromArgb(248, 250, 252);

            // 编辑已有文字时，后续样式一律以该图元自身为准，
            // 否则"改个错字"会顺手把用户调好的字号/颜色重置成副栏当前值。
            _editingText = existing;

            FontStyle fs = FontStyle.Regular;
            float editFontSize = _activeFontSize;
            if (existing != null)
            {
                if (existing.IsBold) fs |= FontStyle.Bold;
                if (existing.IsItalic) fs |= FontStyle.Italic;
                editFontSize = existing.FontSize;
                tb.ForeColor = existing.Color;
                tb.Font = new Font(existing.FontFamilyName, existing.FontSize, fs);
            }
            else
            {
                if (_isFontBold) fs |= FontStyle.Bold;
                if (_isFontItalic) fs |= FontStyle.Italic;
                tb.ForeColor = _activeColor;
                tb.Font = new Font(_activeFontFamily, _activeFontSize, fs);
            }

            int initialW = 160;
            int initialH = (int)(editFontSize * 2f + 16);
            int posX;
            int posY;
            if (existing != null)
            {
                // 原样落回旧位置：编辑时所见位置与提交后一致，不会"点一下跳一下"
                posX = existing.Location.X;
                posY = existing.Location.Y;
                Size measure = TextRenderer.MeasureText(existing.Text + "  \n ", tb.Font);
                initialW = Math.Max(initialW, measure.Width + 24);
                initialH = Math.Max(initialH, measure.Height + 16);
            }
            else
            {
                posX = Math.Max(_selectedRect.Left + 4, Math.Min(_selectedRect.Right - initialW - 4, pt.X));
                posY = Math.Max(_selectedRect.Top + 4, Math.Min(_selectedRect.Bottom - initialH - 4, pt.Y));
            }

            tb.Location = new Point(posX, posY);
            tb.Size = new Size(initialW, initialH);

            if (existing != null)
            {
                // 旧文字立刻隐身：它和文本框叠在一起会变成看不清的双层字
                existing.IsHiddenForEditing = true;
                tb.Text = existing.Text;
                tb.SelectAll();

                DeselectAllAnnotations();
                UpdateBarsLayout();
                Invalidate();
            }

            tb.TextChanged += delegate
            {
                Size sz = TextRenderer.MeasureText(tb.Text + "  \n ", tb.Font);
                int nw = Math.Max(140, sz.Width + 24);
                int nh = Math.Max((int)(editFontSize * 2f + 16), sz.Height + 16);
                if (tb.Left + nw > _selectedRect.Right - 4) nw = _selectedRect.Right - 4 - tb.Left;
                if (tb.Top + nh > _selectedRect.Bottom - 4) nh = _selectedRect.Bottom - 4 - tb.Top;
                tb.Size = new Size(nw, nh);
            };

            tb.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape)
                {
                    CancelActiveText();
                    e.Handled = true;
                }
                else if (e.Control && e.KeyCode == Keys.Enter)
                {
                    CommitActiveText();
                    e.Handled = true;
                }
            };

            tb.LostFocus += delegate
            {
                if (!_isCommittingText)
                {
                    CommitActiveText();
                }
            };

            _activeTextBox = tb;
            this.Controls.Add(tb);
            tb.Focus();
        }

        private void CommitActiveText()
        {
            if (_activeTextBox == null || _isCommittingText) return;
            _isCommittingText = true;

            try
            {
                string text = _activeTextBox.Text.Trim();
                Point loc = _activeTextBox.Location;
                Font f = new Font(_activeTextBox.Font.FontFamily, _activeTextBox.Font.Size, _activeTextBox.Font.Style);
                Color col = _activeColor;

                TextAnnotation editing = _editingText;
                _editingText = null;

                TextBox tb = _activeTextBox;
                _activeTextBox = null;
                this.Controls.Remove(tb);
                tb.Dispose();

                // 重新编辑既有标注：只把内容写回同一个图元，字号/颜色/旋转等一概保留，
                // 位置也不跟着文本框走（文本框是原地弹出来的，没有位移）。
                if (editing != null)
                {
                    editing.IsHiddenForEditing = false;

                    if (string.IsNullOrEmpty(text))
                    {
                        // 内容被清空等价于删除该标注，避免留下一个点不中的空图元
                        _annotations.Remove(editing);
                        if (_selectedText == editing) _selectedText = null;
                        editing.Dispose();
                    }
                    else
                    {
                        editing.Text = text;
                        DeselectAllAnnotations();
                        editing.IsSelected = true;
                        _selectedText = editing;
                    }

                    f.Dispose(); // 编辑不改字体，这份克隆出来的 Font 用不上
                    ClearRedoStack();
                    return;
                }

                if (!string.IsNullOrEmpty(text))
                {
                    TextAnnotation ta = new TextAnnotation
                    {
                        Location = loc,
                        Text = text,
                        TextFont = f,
                        FontSize = f.Size,
                        Color = col,
                        IsBold = _isFontBold,
                        IsItalic = _isFontItalic,
                        HasBackground = _hasTextBackground,
                        FontFamilyName = f.FontFamily.Name,
                        IsSelected = true
                    };
                    DeselectAllAnnotations();
                    _annotations.Add(ta);
                    _selectedText = ta;
                    ClearRedoStack();
                }
                else
                {
                    // 空文本不生成图元，上面预先克隆的 Font 必须就地释放，
                    // 否则每敲一次空文字就泄漏一个 GDI Font 句柄。
                    f.Dispose();
                }
            }
            catch (Exception ex)
            {
                // 文本提交失败会静默丢掉用户刚输入的文字，必须留下线索；
                // 但仍要保证 finally 里的界面状态复位照常执行。
                Console.Error.WriteLine("提交文字标注失败: " + ex.Message);
            }
            finally
            {
                _isCommittingText = false;
                UpdateBarsLayout();
                Invalidate();
            }
        }

        private void CancelActiveText()
        {
            if (_activeTextBox != null)
            {
                _isCommittingText = true;
                try
                {
                    TextBox tb = _activeTextBox;
                    _activeTextBox = null;
                    this.Controls.Remove(tb);
                    tb.Dispose();

                    // 放弃编辑：原图元内容一个字没动，重新显形即可
                    if (_editingText != null)
                    {
                        _editingText.IsHiddenForEditing = false;
                        _editingText.IsSelected = true;
                        _selectedText = _editingText;
                        _editingText = null;
                    }
                }
                catch { }
                finally
                {
                    _isCommittingText = false;
                    UpdateBarsLayout();
                    Invalidate();
                }
            }
        }

        /// <summary>清空全部编辑选中态（文字与图形图元一并取消）</summary>
        private void DeselectAllAnnotations()
        {
            foreach (var ann in _annotations) ann.IsSelected = false;
            _selectedText = null;
            _selectedShape = null;
        }

        /// <summary>
        /// 命中非文字图元：先看已选中图元的骨架顶点与四角手柄，再自上而下找本体。
        /// handle 返回 Body 表示整体平移，返回四角表示缩放，返回 Vertex 表示拖拽顶点
        /// （具体是哪一个顶点由 vertexIndex 给出），None 表示未命中。
        /// </summary>
        private AnnotationItem HitTestShape(Point pt, out TextHandleType handle, out int vertexIndex)
        {
            handle = TextHandleType.None;
            vertexIndex = -1;

            if (_selectedShape != null)
            {
                // 旋转手柄优先于一切：它悬在包围盒之外，与其它手柄不会重叠，
                // 但命中顺序放在最前可以避免被"包围盒外 4px 的角点热区"抢先。
                if (_selectedShape.HitTestRotateHandle(pt))
                {
                    handle = TextHandleType.Rotate;
                    return _selectedShape;
                }

                vertexIndex = _selectedShape.HitTestVertex(pt);
                if (vertexIndex >= 0)
                {
                    handle = TextHandleType.Vertex;
                    return _selectedShape;
                }

                handle = _selectedShape.HitTestHandle(pt);
                if (handle != TextHandleType.None) return _selectedShape;
                if (_selectedShape.HitTestItem(pt))
                {
                    handle = TextHandleType.Body;
                    return _selectedShape;
                }
            }

            for (int i = _annotations.Count - 1; i >= 0; i--)
            {
                AnnotationItem ann = _annotations[i];
                if (ann is TextAnnotation) continue; // 文字图元走独立编辑流程
                if (ann.HitTestItem(pt))
                {
                    handle = TextHandleType.Body;
                    return ann;
                }
            }
            return null;
        }

        /// <summary>把图元设为唯一选中项（同时取消文字选中与橡皮擦悬停高亮）</summary>
        private void SelectShape(AnnotationItem shape)
        {
            if (shape == null) return;

            DeselectAllAnnotations();

            if (_hoveredEraserTarget != null && _hoveredEraserTarget != shape)
            {
                _hoveredEraserTarget.IsHoveredForEraser = false;
                _hoveredEraserTarget = null;
            }

            _selectedShape = shape;
            shape.IsSelected = true;
        }

        /// <summary>取消图元选中态</summary>
        private void DeselectShape()
        {
            if (_selectedShape == null) return;
            _selectedShape.IsSelected = false;
            _selectedShape = null;
        }

        /// <summary>把图元包围盒约束在选区范围内，避免拖出可视区域</summary>
        private void ClampShapeBounds(ref Rectangle bounds)
        {
            if (!_hasSelected) return;

            int maxX = Math.Max(_selectedRect.Left, _selectedRect.Right - bounds.Width);
            int maxY = Math.Max(_selectedRect.Top, _selectedRect.Bottom - bounds.Height);
            int x = Math.Max(_selectedRect.Left, Math.Min(maxX, bounds.X));
            int y = Math.Max(_selectedRect.Top, Math.Min(maxY, bounds.Y));
            bounds = new Rectangle(x, y, bounds.Width, bounds.Height);
        }

        /// <summary>统一设置激活色：同步到当前选中的可换色图元</summary>
        private void ApplyActiveColor(Color c)
        {
            _activeColor = c;
            if (_selectedShape != null && _selectedShape.SupportsColor)
            {
                _selectedShape.SetColor(c);
            }
        }

        private void StartNewAnnotation(Point pt)
        {
            switch (_currentTool)
            {
                case ToolType.Rect:
                    _tempAnnotation = new RectAnnotation { Rect = new Rectangle(pt, new Size(0, 0)), Color = _activeColor, Width = _penWidth, IsFilled = _isFilled };
                    break;
                case ToolType.Ellipse:
                    _tempAnnotation = new EllipseAnnotation { Rect = new Rectangle(pt, new Size(0, 0)), Color = _activeColor, Width = _penWidth, IsFilled = _isFilled };
                    break;
                case ToolType.Line:
                    _tempAnnotation = new LineAnnotation { Start = pt, End = pt, Color = _activeColor, Width = _penWidth, IsDashed = _lineDashed };
                    break;
                case ToolType.Arrow:
                    _tempAnnotation = new ArrowAnnotation { Start = pt, End = pt, Color = _activeColor, Width = _penWidth, HeadStyle = _arrowStyle, IsDashed = _lineDashed };
                    break;
                case ToolType.Brush:
                    BrushAnnotation brush = new BrushAnnotation { Color = _activeColor, Width = _penWidth };
                    brush.Points.Add(pt);
                    _tempAnnotation = brush;
                    break;
                case ToolType.Marker:
                    // 马克笔使用独立笔宽 (_markerWidth)，与画笔 _penWidth 完全解耦
                    MarkerAnnotation marker = new MarkerAnnotation { Color = _activeColor, Width = _markerWidth };
                    marker.Points.Add(pt);
                    _tempAnnotation = marker;
                    break;
                case ToolType.Mosaic:
                    _tempAnnotation = new MosaicAnnotation
                    {
                        Rect = new Rectangle(pt, new Size(0, 0)),
                        IsBlur = false,
                        BlockSize = _mosaicBlockSize
                    };
                    break;
                case ToolType.Blur:
                    _tempAnnotation = new MosaicAnnotation
                    {
                        Rect = new Rectangle(pt, new Size(0, 0)),
                        IsBlur = true,
                        BlockSize = _mosaicBlockSize
                    };
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

            // 先把像素整块读进托管数组并立刻解锁：后续运算不再持有位图锁，
            // 即使计算过程抛异常也不会留下一张永远锁定的位图。
            int width = snippet.Width;
            int height = snippet.Height;
            int stride;
            byte[] pixelBuffer;
            BitmapData bmpData = snippet.LockBits(new Rectangle(0, 0, width, height),
                ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            try
            {
                stride = bmpData.Stride;
                pixelBuffer = new byte[stride * height];
                Marshal.Copy(bmpData.Scan0, pixelBuffer, 0, pixelBuffer.Length);
            }
            finally
            {
                snippet.UnlockBits(bmpData);
            }

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

            // 结果写回也同样用 try/finally 兜底解锁（重新锁定只会读，Stride 与首轮一致）
            BitmapData outData = snippet.LockBits(new Rectangle(0, 0, width, height),
                ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                Marshal.Copy(pixelBuffer, 0, outData.Scan0, pixelBuffer.Length);
            }
            finally
            {
                snippet.UnlockBits(outData);
            }
            return snippet;
        }

        /// <summary>
        /// 对指定区域生成局部高斯模糊（3 次盒式模糊逼近高斯核），radius 越大越糊
        /// </summary>
        private Bitmap CreateLocalBlur(Bitmap sourceBmp, Rectangle area, int radius)
        {
            Rectangle safeArea = Rectangle.Intersect(new Rectangle(0, 0, sourceBmp.Width, sourceBmp.Height), area);
            if (safeArea.Width <= 0 || safeArea.Height <= 0) return null;

            Bitmap snippet = new Bitmap(safeArea.Width, safeArea.Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(snippet))
            {
                g.DrawImage(sourceBmp, new Rectangle(0, 0, safeArea.Width, safeArea.Height), safeArea, GraphicsUnit.Pixel);
            }

            // 同 CreateLocalMosaic：读入托管数组后立即解锁，计算阶段不持有位图锁
            int width = snippet.Width;
            int height = snippet.Height;
            int stride;
            byte[] buffer;
            BitmapData bmpData = snippet.LockBits(new Rectangle(0, 0, width, height),
                ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            try
            {
                stride = bmpData.Stride;
                buffer = new byte[stride * height];
                Marshal.Copy(bmpData.Scan0, buffer, 0, buffer.Length);
            }
            finally
            {
                snippet.UnlockBits(bmpData);
            }

            byte[] scratch = new byte[buffer.Length];
            int r = Math.Max(1, radius);
            // 3 次盒式模糊 = 近似高斯；每次先横向后纵向
            for (int pass = 0; pass < 3; pass++)
            {
                BoxBlurPass(buffer, scratch, width, height, stride, r, true);
                BoxBlurPass(scratch, buffer, width, height, stride, r, false);
            }

            // 结果写回同样兜底解锁
            BitmapData outData = snippet.LockBits(new Rectangle(0, 0, width, height),
                ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                Marshal.Copy(buffer, 0, outData.Scan0, buffer.Length);
            }
            finally
            {
                snippet.UnlockBits(outData);
            }
            return snippet;
        }

        /// <summary>
        /// 按图元自身的模式（像素马赛克 / 高斯模糊）与颗粒度，从原始截图重建局部贴片
        /// </summary>
        private Bitmap BuildMosaicSnippet(Bitmap sourceBmp, MosaicAnnotation ma)
        {
            if (sourceBmp == null || ma == null) return null;
            if (ma.Rect.Width <= 4 || ma.Rect.Height <= 4) return null;

            if (ma.IsBlur)
            {
                // 颗粒度档位映射到模糊半径：细/中/粗 = 3/6/10
                int radius = Math.Max(2, ma.BlockSize / 2);
                return CreateLocalBlur(sourceBmp, ma.Rect, radius);
            }
            return CreateLocalMosaic(sourceBmp, ma.Rect, Math.Max(2, ma.BlockSize));
        }

        /// <summary>
        /// 单次盒式模糊（按方向做滑动窗口累加），结果写入 dst
        /// </summary>
        private static void BoxBlurPass(byte[] src, byte[] dst, int width, int height, int stride, int radius, bool horizontal)
        {
            if (horizontal)
            {
                for (int y = 0; y < height; y++)
                {
                    int row = y * stride;
                    for (int c = 0; c < 3; c++) BoxBlurChannel(src, dst, row + c, width, 4, radius);
                    for (int x = 0; x < width; x++) dst[row + x * 4 + 3] = src[row + x * 4 + 3];
                }
            }
            else
            {
                for (int x = 0; x < width; x++)
                {
                    int col = x * 4;
                    for (int c = 0; c < 3; c++) BoxBlurChannel(src, dst, col + c, height, stride, radius);
                    for (int y = 0; y < height; y++) dst[y * stride + col + 3] = src[y * stride + col + 3];
                }
            }
        }

        /// <summary>
        /// 单个颜色通道的一维盒式模糊，边界采用夹取（clamp）延拓
        /// </summary>
        private static void BoxBlurChannel(byte[] src, byte[] dst, int start, int count, int step, int radius)
        {
            if (count <= 0) return;

            int window = radius * 2 + 1;
            long sum = 0;

            // 预热窗口：越界索引夹取到首尾样本
            for (int i = -radius; i <= radius; i++)
            {
                int idx = i;
                if (idx < 0) idx = 0;
                else if (idx >= count) idx = count - 1;
                sum += src[start + idx * step];
            }

            for (int i = 0; i < count; i++)
            {
                dst[start + i * step] = (byte)(sum / window);

                int dropIdx = i - radius;
                if (dropIdx < 0) dropIdx = 0;
                int addIdx = i + radius + 1;
                if (addIdx >= count) addIdx = count - 1;

                sum += src[start + addIdx * step] - src[start + dropIdx * step];
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            // 局部失效需要"旧 ∪ 新"两个位置，因此先留一份上一个光标位置
            Point previousMousePos = _currentMousePos;
            _currentMousePos = e.Location;

            // 0. 强度滑杆拖拽中：按水平位置反算数值，并抢占其余交互
            if (_draggingSlider != null)
            {
                SubOptionItem slider = FindSubOption("mosaic_slider");
                if (slider != null)
                {
                    ApplySliderValue(slider, e.X);
                    UpdateBarsLayout();
                }
                InvalidateScreen(SubBarDamageBounds());
                return;
            }

            // 1. 尚未确认选区：智能识别候选窗口与元素高亮
            if (!_isSelecting && !_hasSelected)
            {
                Rectangle previousHover = _hoverWindowRect;
                Rectangle previousCard = ColorPickerCardBounds(previousMousePos);

                DetectWindowUnderPoint(e.Location);

                if (_showHelpOverlay)
                {
                    // 帮助卡片会随光标进出在左右两侧翻面，失效范围不连续，这里退回全屏重绘
                    Invalidate();
                }
                else
                {
                    Rectangle damage = UnionVisible(HoverDamageBounds(previousHover), HoverDamageBounds(_hoverWindowRect));
                    damage = UnionVisible(damage, previousCard);
                    damage = UnionVisible(damage, ColorPickerCardBounds(e.Location));
                    InvalidateScreen(damage);
                }
            }

            // 2. 选区已确认：工具栏悬停检测与橡皮擦检测
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
                    InvalidateScreen(ToolBarDamageBounds());
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
                    InvalidateScreen(SubBarDamageBounds());
                }

                // 橡皮擦模式悬停高亮
                if (_currentTool == ToolType.Eraser)
                {
                    AnnotationItem target = null;
                    for (int i = _annotations.Count - 1; i >= 0; i--)
                    {
                        if (_annotations[i].HitTestItem(e.Location))
                        {
                            target = _annotations[i];
                            break;
                        }
                    }
                    if (_hoveredEraserTarget != target)
                    {
                        Rectangle damage = Rectangle.Empty;
                        if (_hoveredEraserTarget != null)
                        {
                            damage = StrokeDamageBounds(_hoveredEraserTarget);
                            _hoveredEraserTarget.IsHoveredForEraser = false;
                        }
                        _hoveredEraserTarget = target;
                        if (_hoveredEraserTarget != null)
                        {
                            _hoveredEraserTarget.IsHoveredForEraser = true;
                            damage = UnionVisible(damage, StrokeDamageBounds(_hoveredEraserTarget));
                        }
                        InvalidateScreen(damage);
                    }
                }

                // 更新光标
                if (!_isSelecting && !_isResizingOrMoving && _tempAnnotation == null && !_isManipulatingText
                    && !_isManipulatingShape && !_isSpaceDragging)
                {
                    Cursor shapeCursor = GetShapeCursor(e.Location);
                    if (shapeCursor != null)
                    {
                        this.Cursor = shapeCursor;
                    }
                    else
                    {
                        HitType hit = CheckHitTest(e.Location);
                        this.Cursor = GetCursorForHit(hit);
                    }
                }
            }

            // 3. 空格键平移选区
            if (_isSpaceDragging)
            {
                Rectangle damageBefore = SelectionAffectedBounds(_selectedRect);

                int dx = e.Location.X - _spaceDragStartMouse.X;
                int dy = e.Location.Y - _spaceDragStartMouse.Y;

                int nx = _spaceDragInitialRect.X + dx;
                int ny = _spaceDragInitialRect.Y + dy;

                nx = Math.Max(0, Math.Min(this.Width - _spaceDragInitialRect.Width, nx));
                ny = Math.Max(0, Math.Min(this.Height - _spaceDragInitialRect.Height, ny));

                int actualDx = nx - _selectedRect.X;
                int actualDy = ny - _selectedRect.Y;

                _selectedRect.X = nx;
                _selectedRect.Y = ny;

                if (actualDx != 0 || actualDy != 0)
                {
                    foreach (var ann in _annotations) ann.Offset(actualDx, actualDy);
                }

                UpdateBarsLayout();
                InvalidateScreen(UnionVisible(damageBefore, SelectionAffectedBounds(_selectedRect)));
                return;
            }

            // 4. 正在初次拉框
            if (_isSelecting)
            {
                Rectangle damageBefore = _selectedRect;
                _lastDragPoint = e.Location;
                _selectedRect = GetNormalizedRect(_startPoint, _lastDragPoint);
                InvalidateScreen(UnionVisible(SelectionAffectedBounds(damageBefore), SelectionAffectedBounds(_selectedRect)));
            }
            // 5. 正在移动或等比缩放文字标注
            else if (_isManipulatingText && _selectedText != null)
            {
                if (_textManipulatingHandle == TextHandleType.Rotate)
                {
                    // 绕文字包围盒中心旋转，角度算法与非文字图元保持完全一致
                    Rectangle damageBefore = ItemDamageBounds(_selectedText);

                    PointF c = _selectedText.GetCenter();
                    double deg = Math.Atan2(e.Location.Y - c.Y, e.Location.X - c.X) * 180.0 / Math.PI + 90.0;
                    while (deg <= -180.0) deg += 360.0;
                    while (deg > 180.0) deg -= 360.0;
                    if ((Control.ModifierKeys & Keys.Shift) == Keys.Shift) deg = Math.Round(deg / 15.0) * 15.0;

                    _selectedText.Angle = (float)deg;
                    InvalidateScreen(UnionVisible(damageBefore, ItemDamageBounds(_selectedText)));
                }
                else if (_textManipulatingHandle == TextHandleType.Body)
                {
                    Rectangle damageBefore = ItemDamageBounds(_selectedText);

                    int dx = e.Location.X - _textDragStartMouse.X;
                    int dy = e.Location.Y - _textDragStartMouse.Y;

                    int newX = _textInitialLocation.X + dx;
                    int newY = _textInitialLocation.Y + dy;

                    newX = Math.Max(_selectedRect.Left + 2, Math.Min(_selectedRect.Right - 20, newX));
                    newY = Math.Max(_selectedRect.Top + 2, Math.Min(_selectedRect.Bottom - 20, newY));

                    _selectedText.Location = new Point(newX, newY);
                    InvalidateScreen(UnionVisible(damageBefore, ItemDamageBounds(_selectedText)));
                }
                else
                {
                    Rectangle damageBefore = ItemDamageBounds(_selectedText);

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
                            FontStyle fs = FontStyle.Regular;
                            if (_selectedText.IsBold) fs |= FontStyle.Bold;
                            if (_selectedText.IsItalic) fs |= FontStyle.Italic;
                            if (_selectedText.TextFont != null) _selectedText.TextFont.Dispose();
                            _selectedText.TextFont = new Font(_selectedText.FontFamilyName, newFontSize, fs);
                        }
                    }
                    InvalidateScreen(UnionVisible(damageBefore, ItemDamageBounds(_selectedText)));
                }
            }
            // 5.5 正在移动 / 缩放非文字图元
            else if (_isManipulatingShape && _selectedShape != null)
            {
                // 图元包围盒只含骨架点，粗笔迹（马克笔最粗 28px）会溢出一大截，按笔宽外扩
                Rectangle damageBefore = StrokeDamageBounds(_selectedShape);

                int dx = e.Location.X - _shapeDragStartMouse.X;
                int dy = e.Location.Y - _shapeDragStartMouse.Y;

                if (_shapeHandle == TextHandleType.Rotate)
                {
                    // 旋转：手柄默认停在中心正上方，对应 Angle=0，因此
                    // Angle = 鼠标方位角 + 90° 就能让手柄始终吸在鼠标下，且起始无跳变。
                    PointF c = _selectedShape.GetCenter();
                    double deg = Math.Atan2(e.Location.Y - c.Y, e.Location.X - c.X) * 180.0 / Math.PI + 90.0;
                    // 归一化到 (-180, 180]，避免角度无限累加导致浮点精度退化
                    while (deg <= -180.0) deg += 360.0;
                    while (deg > 180.0) deg -= 360.0;
                    // 按住 Shift 吸附到 15° 整数倍，方便摆正水平/垂直
                    if ((Control.ModifierKeys & Keys.Shift) == Keys.Shift) deg = Math.Round(deg / 15.0) * 15.0;
                    _selectedShape.Angle = (float)deg;
                }
                else if (_shapeHandle == TextHandleType.Vertex)
                {
                    // 拖拽骨架顶点：直线/箭头调整端点，自由笔迹调整采样点。
                    // 不做 ClampShapeBounds——顶点本就允许拖到选区任意位置。
                    _selectedShape.MoveVertex(_shapeVertexIndex, e.Location);
                }
                else if (_shapeHandle == TextHandleType.Body)
                {
                    // 整体平移：以拖拽起点包围盒为基准，避免累积误差
                    Rectangle target = _shapeInitialBounds;
                    target.Offset(dx, dy);
                    ClampShapeBounds(ref target);
                    _selectedShape.SetBounds(target);
                }
                else
                {
                    // 四角缩放：对角点保持不动，拖拽角跟随鼠标
                    Rectangle b = _shapeInitialBounds;
                    int left = b.Left, top = b.Top, right = b.Right, bottom = b.Bottom;
                    switch (_shapeHandle)
                    {
                        case TextHandleType.TopLeft:
                            left = Math.Min(e.Location.X, right - MinShapeSize);
                            top = Math.Min(e.Location.Y, bottom - MinShapeSize);
                            break;
                        case TextHandleType.TopRight:
                            right = Math.Max(e.Location.X, left + MinShapeSize);
                            top = Math.Min(e.Location.Y, bottom - MinShapeSize);
                            break;
                        case TextHandleType.BottomLeft:
                            left = Math.Min(e.Location.X, right - MinShapeSize);
                            bottom = Math.Max(e.Location.Y, top + MinShapeSize);
                            break;
                        case TextHandleType.BottomRight:
                        default:
                            right = Math.Max(e.Location.X, left + MinShapeSize);
                            bottom = Math.Max(e.Location.Y, top + MinShapeSize);
                            break;
                    }
                    _selectedShape.SetBounds(Rectangle.FromLTRB(left, top, right, bottom));
                }

                UpdateBarsLayout();
                InvalidateScreen(UnionVisible(damageBefore, StrokeDamageBounds(_selectedShape)));
            }
            // 6. 选区 8 点缩放或平移
            else if (_isResizingOrMoving)
            {
                Rectangle damageBefore = SelectionAffectedBounds(_selectedRect);

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
                        foreach (var ann in _annotations) ann.Offset(actualDx, actualDy);
                    }
                }
                else
                {
                    Rectangle r = _initialRect;
                    int left = r.Left, top = r.Top, right = r.Right, bottom = r.Bottom;

                    switch (_currentHit)
                    {
                        case HitType.TopLeft: left += dx; top += dy; break;
                        case HitType.Top: top += dy; break;
                        case HitType.TopRight: right += dx; top += dy; break;
                        case HitType.Right: right += dx; break;
                        case HitType.BottomRight: right += dx; bottom += dy; break;
                        case HitType.Bottom: bottom += dy; break;
                        case HitType.BottomLeft: left += dx; bottom += dy; break;
                        case HitType.Left: left += dx; break;
                    }

                    int finalX = Math.Min(left, right);
                    int finalY = Math.Min(top, bottom);
                    int finalW = Math.Abs(right - left);
                    int finalH = Math.Abs(bottom - top);

                    _selectedRect = new Rectangle(finalX, finalY, finalW, finalH);
                }

                UpdateBarsLayout();
                InvalidateScreen(UnionVisible(damageBefore, SelectionAffectedBounds(_selectedRect)));
            }
            // 7. 正在绘制标注图元
            else if (_tempAnnotation != null)
            {
                Rectangle damageBefore = StrokeDamageBounds(_tempAnnotation);

                Point targetPt = e.Location;

                // Shift 键约束逻辑 (正方形 / 正圆 / 45° 直线)
                bool isShiftDown = (Control.ModifierKeys & Keys.Shift) == Keys.Shift;

                RectAnnotation ra = _tempAnnotation as RectAnnotation;
                if (ra != null)
                {
                    Rectangle nr = GetNormalizedRect(_startPoint, targetPt);
                    if (isShiftDown)
                    {
                        int side = Math.Min(nr.Width, nr.Height);
                        nr.Width = side;
                        nr.Height = side;
                    }
                    ra.Rect = nr;
                }

                EllipseAnnotation ea = _tempAnnotation as EllipseAnnotation;
                if (ea != null)
                {
                    Rectangle nr = GetNormalizedRect(_startPoint, targetPt);
                    if (isShiftDown)
                    {
                        int side = Math.Min(nr.Width, nr.Height);
                        nr.Width = side;
                        nr.Height = side;
                    }
                    ea.Rect = nr;
                }

                LineAnnotation la = _tempAnnotation as LineAnnotation;
                if (la != null)
                {
                    if (isShiftDown)
                    {
                        int adx = Math.Abs(targetPt.X - _startPoint.X);
                        int ady = Math.Abs(targetPt.Y - _startPoint.Y);
                        if (adx > ady * 2) targetPt = new Point(targetPt.X, _startPoint.Y); // 水平
                        else if (ady > adx * 2) targetPt = new Point(_startPoint.X, targetPt.Y); // 垂直
                        else // 45°
                        {
                            int dist = Math.Min(adx, ady);
                            int signX = targetPt.X >= _startPoint.X ? 1 : -1;
                            int signY = targetPt.Y >= _startPoint.Y ? 1 : -1;
                            targetPt = new Point(_startPoint.X + dist * signX, _startPoint.Y + dist * signY);
                        }
                    }
                    la.End = targetPt;
                }

                ArrowAnnotation aa = _tempAnnotation as ArrowAnnotation;
                if (aa != null)
                {
                    if (isShiftDown)
                    {
                        int adx = Math.Abs(targetPt.X - _startPoint.X);
                        int ady = Math.Abs(targetPt.Y - _startPoint.Y);
                        if (adx > ady * 2) targetPt = new Point(targetPt.X, _startPoint.Y); // 水平
                        else if (ady > adx * 2) targetPt = new Point(_startPoint.X, targetPt.Y); // 垂直
                        else // 45°
                        {
                            int dist = Math.Min(adx, ady);
                            int signX = targetPt.X >= _startPoint.X ? 1 : -1;
                            int signY = targetPt.Y >= _startPoint.Y ? 1 : -1;
                            targetPt = new Point(_startPoint.X + dist * signX, _startPoint.Y + dist * signY);
                        }
                    }
                    aa.End = targetPt;
                }

                BrushAnnotation ba = _tempAnnotation as BrushAnnotation;
                if (ba != null) ba.Points.Add(targetPt);

                MarkerAnnotation ma = _tempAnnotation as MarkerAnnotation;
                if (ma != null) ma.Points.Add(targetPt);

                MosaicAnnotation msa = _tempAnnotation as MosaicAnnotation;
                if (msa != null) msa.Rect = GetNormalizedRect(_startPoint, targetPt);

                InvalidateScreen(UnionVisible(damageBefore, StrokeDamageBounds(_tempAnnotation)));
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                if (_activeTextBox != null)
                {
                    CommitActiveText();
                }

                // 0. 空格键按住时：进入选区自由平移
                if (_isSpaceKeyDown && _hasSelected)
                {
                    _isSpaceDragging = true;
                    _spaceDragStartMouse = e.Location;
                    _spaceDragInitialRect = _selectedRect;
                    return;
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

                // 2. 点击二级属性栏
                if (HasSubBar() && _subBarBounds.Contains(e.Location))
                {
                    foreach (SubOptionItem sub in _subOptions)
                    {
                        if (sub.Bounds.Contains(e.Location))
                        {
                            // 强度滑杆：按下即进入拖拽态，并按落点立刻定位滑块
                            if (sub.Id == "mosaic_slider")
                            {
                                _draggingSlider = sub;
                                ApplySliderValue(sub, e.X);
                            }
                            else if (sub.ClickAction != null)
                            {
                                sub.ClickAction();
                            }
                            UpdateBarsLayout();
                            Invalidate();
                            return;
                        }
                    }
                    return;
                }

                // 3. 橡皮擦模式：点击删除图元
                if (_hasSelected && _currentTool == ToolType.Eraser)
                {
                    for (int i = _annotations.Count - 1; i >= 0; i--)
                    {
                        if (_annotations[i].HitTestItem(e.Location))
                        {
                            AnnotationItem removed = _annotations[i];
                            _annotations.RemoveAt(i);
                            _redoStack.Add(removed);
                            if (removed == _selectedText) _selectedText = null;
                            if (removed == _selectedShape) _selectedShape = null;
                            // 被擦除的图元进了重做栈，务必连它的悬停高亮标志一起复位，
                            // 否则撤销恢复后它会顶着"待擦除"红框重新出现。
                            removed.IsHoveredForEraser = false;
                            _hoveredEraserTarget = null;
                            RenumberAnnotations();
                            UpdateBarsLayout();
                            Invalidate();
                            return;
                        }
                    }
                    return;
                }

                // 4. 文字标注平移或缩放检测
                if (_hasSelected)
                {
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
                        DeselectAllAnnotations();
                        clickedTa.IsSelected = true;
                        _selectedText = clickedTa;
                        _activeColor = clickedTa.Color;
                        _activeFontSize = clickedTa.FontSize;
                        _isFontBold = clickedTa.IsBold;
                        _isFontItalic = clickedTa.IsItalic;
                        _hasTextBackground = clickedTa.HasBackground;

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

                if (_selectedText != null)
                {
                    DeselectAllAnnotations();
                    UpdateBarsLayout();
                    Invalidate();
                }

                // 4.5 非文字图元的二次编辑：选中 / 平移 / 四角缩放
                //     仅在未激活绘图工具时生效，避免抢占正常绘制操作
                if (_hasSelected && _currentTool == ToolType.None)
                {
                    TextHandleType shapeHandle;
                    int vertexIndex;
                    AnnotationItem hitShape = HitTestShape(e.Location, out shapeHandle, out vertexIndex);
                    if (hitShape != null)
                    {
                        SelectShape(hitShape);
                        _isManipulatingShape = true;
                        _shapeHandle = shapeHandle;
                        _shapeVertexIndex = vertexIndex;
                        _shapeDragStartMouse = e.Location;
                        _shapeInitialBounds = hitShape.GetBounds();
                        UpdateBarsLayout();
                        Invalidate();
                        return;
                    }

                    // 点空白处取消图元选中，随后继续走选区调整逻辑
                    if (_selectedShape != null)
                    {
                        DeselectShape();
                        UpdateBarsLayout();
                        Invalidate();
                    }
                }

                // 5. 选区调整大小或平移
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

                // 6. 文字标注工具：点击选区位置生成文本框
                if (_hasSelected && _currentTool == ToolType.Text && _selectedRect.Contains(e.Location))
                {
                    SpawnTextInput(e.Location, null);
                    return;
                }

                // 6.5 序号标注工具：单击即落点，自动递增编号（Snipaste 序号工具交互）
                if (_hasSelected && _currentTool == ToolType.Number && _selectedRect.Contains(e.Location))
                {
                    _annotations.Add(new NumberAnnotation
                    {
                        Center = e.Location,
                        Number = _nextNumber,
                        Color = _activeColor,
                        Diameter = 18 + (int)_penWidth * 2
                    });
                    _nextNumber++;
                    ClearRedoStack();
                    UpdateBarsLayout();
                    Invalidate();
                    return;
                }

                // 7. 在选区内绘制图形标注
                if (_hasSelected && _currentTool != ToolType.None && _selectedRect.Contains(e.Location))
                {
                    _startPoint = e.Location;
                    _lastDragPoint = e.Location;
                    StartNewAnnotation(e.Location);
                    Invalidate();
                    return;
                }

                // 8. 点击外部重新框选
                _startPoint = e.Location;
                _lastDragPoint = e.Location;
                _mouseDownPos = e.Location;
                _isSelecting = true;
                _hasSelected = false;
                _currentTool = ToolType.None;
                ClearAnnotations();
                Invalidate();
            }
            else if (e.Button == MouseButtons.Right)
            {
                // Snipaste 经典右键交互：单击右键秒退！退出当前截图，无任何多余弹窗
                CancelCrop();
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            // 强度滑杆拖拽结束
            if (_draggingSlider != null)
            {
                _draggingSlider = null;
                Invalidate();
                return;
            }

            if (_isSpaceDragging)
            {
                _isSpaceDragging = false;
                UpdateBarsLayout();
                Invalidate();
                return;
            }

            if (_isSelecting)
            {
                _isSelecting = false;
                int moveDist = Math.Abs(e.Location.X - _mouseDownPos.X) + Math.Abs(e.Location.Y - _mouseDownPos.Y);

                // 单击且命中了窗口/元素探测矩形：一键直接锁定该窗口！
                if (moveDist < 6 && !_hoverWindowRect.IsEmpty && _hoverWindowRect.Width > 15 && _hoverWindowRect.Height > 15)
                {
                    _selectedRect = _hoverWindowRect;
                    _hasSelected = true;
                    _hoverWindowRect = Rectangle.Empty;
                    UpdateBarsLayout();
                    Invalidate();
                    return;
                }

                _selectedRect = GetNormalizedRect(_startPoint, _lastDragPoint);
                if (_selectedRect.Width > 15 && _selectedRect.Height > 15)
                {
                    _hasSelected = true;
                    _hoverWindowRect = Rectangle.Empty;
                    UpdateBarsLayout();
                }
                else
                {
                    _hasSelected = false;
                    _selectedRect = Rectangle.Empty;
                }
                Invalidate();
            }
            else if (_isManipulatingShape)
            {
                _isManipulatingShape = false;
                _shapeHandle = TextHandleType.None;

                // 马赛克 / 模糊被缩放后需要按原图重新采样，否则贴片内容与框不符
                MosaicAnnotation maEdit = _selectedShape as MosaicAnnotation;
                if (maEdit != null && maEdit.NeedsRebuild)
                {
                    maEdit.MosaicSnippet = BuildMosaicSnippet(_fullBmp, maEdit);
                    maEdit.NeedsRebuild = false;
                }

                UpdateBarsLayout();
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
                    ma.MosaicSnippet = BuildMosaicSnippet(_fullBmp, ma);
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
            if (_activeTextBox != null)
            {
                // 文本框内双击是选词，绝不能顺手把截图确认掉
                base.OnMouseDoubleClick(e);
                return;
            }

            if (_hasSelected && _selectedRect.Contains(e.Location) && !_toolBarBounds.Contains(e.Location))
            {
                // 双击已有文字标注 = 重新编辑它的内容（Snipaste 同款交互）。
                // 必须先于"双击确认截图"判定，否则想改字的人会被直接踢出截图流程。
                TextAnnotation ta = HitTestTextAnnotation(e.Location);
                if (ta != null)
                {
                    SpawnTextInput(e.Location, ta);
                    base.OnMouseDoubleClick(e);
                    return;
                }

                // 双击选区直接完成并复制到剪贴板，平稳退出
                ConfirmCrop(false);
            }
            base.OnMouseDoubleClick(e);
        }

        /// <summary>自上而下找落点处的文字标注（不含正在重新编辑、已隐身的那个）</summary>
        private TextAnnotation HitTestTextAnnotation(Point pt)
        {
            for (int i = _annotations.Count - 1; i >= 0; i--)
            {
                TextAnnotation ta = _annotations[i] as TextAnnotation;
                if (ta != null && ta.HitTestItem(pt)) return ta;
            }
            return null;
        }

        /// <summary>
        /// 滚轮快捷调参 (Snipaste 交互)：选中文字时调字号，激活绘图工具时调笔宽/颗粒度
        /// </summary>
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            // 正在输入文字时不拦截，交给文本框处理
            if (_activeTextBox != null || !_hasSelected)
            {
                base.OnMouseWheel(e);
                return;
            }

            int dir = (e.Delta > 0) ? 1 : -1;

            if (_selectedText != null)
            {
                float newSize = Math.Max(9f, Math.Min(96f, _selectedText.FontSize + dir));
                if (Math.Abs(newSize - _selectedText.FontSize) > 0.01f)
                {
                    _selectedText.FontSize = newSize;
                    _activeFontSize = newSize;
                    UpdateSelectedTextFont();
                    Invalidate();
                }
                return;
            }

            switch (_currentTool)
            {
                case ToolType.Marker:
                    _markerWidth = StepValue(_markerWidth, dir, MARKER_WIDTH_STEPS);
                    break;
                case ToolType.Mosaic:
                case ToolType.Blur:
                    _mosaicBlockSize = (int)StepValue(_mosaicBlockSize, dir, MOSAIC_SIZE_STEPS);
                    break;
                case ToolType.Number:
                case ToolType.Brush:
                case ToolType.Rect:
                case ToolType.Ellipse:
                case ToolType.Line:
                case ToolType.Arrow:
                    _penWidth = StepValue(_penWidth, dir, PEN_WIDTH_STEPS);
                    break;
                default:
                    base.OnMouseWheel(e);
                    return;
            }

            UpdateBarsLayout();
            Invalidate();
        }

        /// <summary>在预设档位数组中按方向取相邻档位</summary>
        private static float StepValue(float current, int direction, float[] steps)
        {
            int nearest = 0;
            float best = float.MaxValue;
            for (int i = 0; i < steps.Length; i++)
            {
                float d = Math.Abs(steps[i] - current);
                if (d < best)
                {
                    best = d;
                    nearest = i;
                }
            }
            int next = nearest + direction;
            if (next < 0) next = 0;
            if (next >= steps.Length) next = steps.Length - 1;
            return steps[next];
        }

        /// <summary>
        /// 切换标注工具 (Snipaste 标准交互：再次按同一快捷键取消激活)
        /// </summary>
        private void SwitchTool(ToolType tool)
        {
            if (!_hasSelected || _activeTextBox != null) return;

            _currentTool = (_currentTool == tool) ? ToolType.None : tool;

            // 切换工具时清空文字选中态与橡皮擦悬停高亮，避免残留手柄
            DeselectAllAnnotations();
            if (_hoveredEraserTarget != null)
            {
                _hoveredEraserTarget.IsHoveredForEraser = false;
                _hoveredEraserTarget = null;
            }

            UpdateBarsLayout();
            Invalidate();
        }

        /// <summary>
        /// 已框选后用方向键微调整个选区位置，全部标注图元同步位移
        /// </summary>
        private void MoveSelectedRect(int dx, int dy)
        {
            int nx = Math.Max(0, Math.Min(this.Width - _selectedRect.Width, _selectedRect.X + dx));
            int ny = Math.Max(0, Math.Min(this.Height - _selectedRect.Height, _selectedRect.Y + dy));

            int actualDx = nx - _selectedRect.X;
            int actualDy = ny - _selectedRect.Y;
            if (actualDx == 0 && actualDy == 0) return;

            _selectedRect.X = nx;
            _selectedRect.Y = ny;
            foreach (AnnotationItem ann in _annotations) ann.Offset(actualDx, actualDy);

            UpdateBarsLayout();
            Invalidate();
        }

        /// <summary>
        /// 用 [ ] 或数字键快速步进当前工具的尺寸 (字号 / 颗粒度 / 线宽)
        /// </summary>
        private void StepActiveSize(int direction)
        {
            if (!_hasSelected) return;

            // 文字工具：按字号阶梯步进
            if (_currentTool == ToolType.Text || _selectedText != null)
            {
                float[] fontSteps = new float[] { 9f, 12f, 16f, 20f, 24f, 32f, 48f, 64f, 96f };
                float current = (_selectedText != null) ? _selectedText.FontSize : _activeFontSize;
                int idx = 0;
                for (int i = 0; i < fontSteps.Length; i++)
                {
                    if (fontSteps[i] <= current + 0.01f) idx = i;
                }
                idx = Math.Max(0, Math.Min(fontSteps.Length - 1, idx + direction));

                _activeFontSize = fontSteps[idx];
                if (_selectedText != null)
                {
                    _selectedText.FontSize = _activeFontSize;
                    UpdateSelectedTextFont();
                }
                UpdateBarsLayout();
                Invalidate();
                return;
            }

            // 马赛克 / 模糊：颗粒度步进（与滚轮调参共用三档表）
            if (_currentTool == ToolType.Mosaic || _currentTool == ToolType.Blur)
            {
                _mosaicBlockSize = (int)StepValue(_mosaicBlockSize, direction, MOSAIC_SIZE_STEPS);
                UpdateBarsLayout();
                Invalidate();
                return;
            }

            // 马克笔：走独立的荧光笔笔宽档位 (10 / 18 / 28)
            if (_currentTool == ToolType.Marker)
            {
                _markerWidth = StepValue(_markerWidth, direction, MARKER_WIDTH_STEPS);
                UpdateBarsLayout();
                Invalidate();
                return;
            }

            // 画笔 / 图形线宽：三档循环
            _penWidth = StepValue(_penWidth, direction, PEN_WIDTH_STEPS);
            UpdateBarsLayout();
            Invalidate();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (_activeTextBox != null)
            {
                if (e.KeyCode == Keys.Escape)
                {
                    CancelActiveText();
                    return;
                }
                base.OnKeyDown(e);
                return;
            }

            // 空格键按下：准备平移选区
            if (e.KeyCode == Keys.Space)
            {
                _isSpaceKeyDown = true;
                if (_hasSelected) this.Cursor = Cursors.Hand;
                return;
            }

            // 1. 已框选后：标注工具快捷键 (Snipaste 标准键位，再按同键取消激活)
            //    注意排除 Ctrl/Alt，避免吞掉 Ctrl+A 全屏、Ctrl+S 保存等组合键
            if (_hasSelected && !_isSelecting && _activeTextBox == null && !e.Control && !e.Alt)
            {
                ToolType? shortcutTool = null;
                switch (e.KeyCode)
                {
                    case Keys.R: shortcutTool = ToolType.Rect; break;      // 矩形
                    case Keys.E: shortcutTool = ToolType.Ellipse; break;   // 椭圆
                    case Keys.L: shortcutTool = ToolType.Line; break;      // 直线 / 折线
                    case Keys.A: shortcutTool = ToolType.Arrow; break;     // 箭头
                    case Keys.B: shortcutTool = ToolType.Brush; break;     // 画笔 (Snipaste 键位)
                    case Keys.M: shortcutTool = ToolType.Marker; break;    // 马克笔
                    case Keys.T: shortcutTool = ToolType.Text; break;      // 文字
                    case Keys.N: shortcutTool = ToolType.Number; break;    // 序号标注
                    case Keys.K: shortcutTool = ToolType.Mosaic; break;    // 马赛克
                    case Keys.G: shortcutTool = ToolType.Blur; break;      // 高斯模糊
                    case Keys.X: shortcutTool = ToolType.Eraser; break;    // 橡皮擦
                }
                if (shortcutTool.HasValue)
                {
                    SwitchTool(shortcutTool.Value);
                    return;
                }

                // 已框选时，[ 与 ] 或数字 1/2/3 快速调节线宽 / 字号 / 颗粒度
                if (e.KeyCode == Keys.OemOpenBrackets || e.KeyCode == Keys.D1)
                {
                    StepActiveSize(-1);
                    return;
                }
                if (e.KeyCode == Keys.OemCloseBrackets || e.KeyCode == Keys.D2 || e.KeyCode == Keys.D3)
                {
                    StepActiveSize(1);
                    return;
                }
            }

            // 2. 方向键 / WASD：未框选时移动鼠标指针精确定位；已框选时方向键微调选区
            int step = e.Shift ? 10 : 1;
            int dx = 0, dy = 0;
            bool isArrowKey = (e.KeyCode == Keys.Up || e.KeyCode == Keys.Down || e.KeyCode == Keys.Left || e.KeyCode == Keys.Right);
            bool isWasdKey = (e.KeyCode == Keys.W || e.KeyCode == Keys.S || e.KeyCode == Keys.A || e.KeyCode == Keys.D);

            if (e.KeyCode == Keys.W || e.KeyCode == Keys.Up) dy = -step;
            else if (e.KeyCode == Keys.S || e.KeyCode == Keys.Down) dy = step;
            else if (e.KeyCode == Keys.A || e.KeyCode == Keys.Left) dx = -step;
            else if (e.KeyCode == Keys.D || e.KeyCode == Keys.Right) dx = step;

            if (dx != 0 || dy != 0)
            {
                if (!_hasSelected && !_isSelecting)
                {
                    // 未框选：移动鼠标指针，用于贴合窗口边缘精确定位
                    Point curScreen = Cursor.Position;
                    Point newScreen = new Point(curScreen.X + dx, curScreen.Y + dy);
                    SetCursorPos(newScreen.X, newScreen.Y);

                    Point newLocal = new Point(_currentMousePos.X + dx, _currentMousePos.Y + dy);
                    _currentMousePos = newLocal;
                    DetectWindowUnderPoint(newLocal);
                    Invalidate();
                    return;
                }

                if (_hasSelected && !_isSelecting && isArrowKey)
                {
                    // 已框选：方向键微调整个选区（含全部标注图元同步位移）
                    MoveSelectedRect(dx, dy);
                    return;
                }

                // 已框选时的 WASD 已让位给工具快捷键，此处不响应，避免误移指针
                if (_hasSelected && isWasdKey) return;
            }

            // 2. Tab 键：未选区时切换检测窗口/元素；已选区时在关联工具间快速切换 (箭头 <-> 折线/直线，矩形 <-> 椭圆)
            if (e.KeyCode == Keys.Tab)
            {
                if (!_hasSelected && !_isSelecting)
                {
                    _detectElementMode = !_detectElementMode;
                    DetectWindowUnderPoint(_currentMousePos, true);   // 模式切换必须立刻重探
                    Invalidate();
                    return;
                }
                else if (_hasSelected && _activeTextBox == null)
                {
                    if (_currentTool == ToolType.Arrow)
                    {
                        _currentTool = ToolType.Line;
                        UpdateBarsLayout();
                        Invalidate();
                        return;
                    }
                    else if (_currentTool == ToolType.Line)
                    {
                        _currentTool = ToolType.Arrow;
                        UpdateBarsLayout();
                        Invalidate();
                        return;
                    }
                    else if (_currentTool == ToolType.Rect)
                    {
                        _currentTool = ToolType.Ellipse;
                        UpdateBarsLayout();
                        Invalidate();
                        return;
                    }
                    else if (_currentTool == ToolType.Ellipse)
                    {
                        _currentTool = ToolType.Rect;
                        UpdateBarsLayout();
                        Invalidate();
                        return;
                    }
                }
            }

            // 3. Ctrl + A：设置截屏区域为当前全屏
            if (e.Control && e.KeyCode == Keys.A)
            {
                _selectedRect = new Rectangle(0, 0, this.Width, this.Height);
                _hasSelected = true;
                _isSelecting = false;
                _hoverWindowRect = Rectangle.Empty;
                UpdateBarsLayout();
                Invalidate();
                return;
            }

            // 4. R：使用上一次截屏区域 (仅未框选时；已框选时的 R 已让位给矩形工具)
            if (e.KeyCode == Keys.R && !_hasSelected)
            {
                if (_rectHistory.Count > 0)
                {
                    _selectedRect = _rectHistory[_rectHistory.Count - 1];
                    _selectedRect.Intersect(new Rectangle(0, 0, this.Width, this.Height));
                    if (_selectedRect.Width > 10 && _selectedRect.Height > 10)
                    {
                        _hasSelected = true;
                        _isSelecting = false;
                        _hoverWindowRect = Rectangle.Empty;
                        UpdateBarsLayout();
                        Invalidate();
                        return;
                    }
                }
            }

            // 5. , 和 . 回溯历史
            if (e.KeyCode == Keys.Oemcomma || e.KeyCode == Keys.OemPeriod)
            {
                if (_rectHistory.Count > 0)
                {
                    if (e.KeyCode == Keys.Oemcomma)
                    {
                        if (_rectHistoryIndex == -1) _rectHistoryIndex = _rectHistory.Count - 1;
                        else if (_rectHistoryIndex > 0) _rectHistoryIndex--;
                    }
                    else
                    {
                        if (_rectHistoryIndex >= 0 && _rectHistoryIndex < _rectHistory.Count - 1)
                            _rectHistoryIndex++;
                    }

                    if (_rectHistoryIndex >= 0 && _rectHistoryIndex < _rectHistory.Count)
                    {
                        _selectedRect = _rectHistory[_rectHistoryIndex];
                        _selectedRect.Intersect(new Rectangle(0, 0, this.Width, this.Height));
                        _hasSelected = true;
                        _isSelecting = false;
                        _hoverWindowRect = Rectangle.Empty;
                        UpdateBarsLayout();
                        Invalidate();
                        return;
                    }
                }
            }

            // 6. F1：显示/隐藏快捷键卡片
            if (e.KeyCode == Keys.Oemtilde || e.KeyCode == Keys.F1)
            {
                _showHelpOverlay = !_showHelpOverlay;
                Invalidate();
                return;
            }

            // 7. F3 键：钉在桌面上 / 贴图
            if (e.KeyCode == Keys.F3)
            {
                if (_hasSelected && _activeTextBox == null)
                {
                    PinCrop();
                    return;
                }
            }

            // 8. 框选前取色器复制
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
                    // 只有真正写进剪贴板才标记成功：否则 Main 会回传 ACTION:COPY_COLOR
                    // 谎报"已复制"，而用户粘贴出来仍是旧内容。
                    if (Program.TrySetClipboardText(colorStr))
                    {
                        this.IsColorCopied = true;
                        this.CopiedColorString = colorStr;
                    }
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
                    DeselectAllAnnotations();
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
                    ConfirmCrop(false);
                }
            }
            else if (e.Control && e.KeyCode == Keys.Z)
            {
                if (_activeTextBox == null) PerformUndo();
            }
            else if (e.Control && e.KeyCode == Keys.Y)
            {
                if (_activeTextBox == null) PerformRedo();
            }
            else if (e.Control && e.KeyCode == Keys.S)
            {
                if (_hasSelected && _activeTextBox == null) SaveToFile();
            }
            else if (e.Control && e.KeyCode == Keys.C)
            {
                if (_hasSelected && _activeTextBox == null) ConfirmCrop(false);
            }
            else if (e.KeyCode == Keys.Delete || e.KeyCode == Keys.Back)
            {
                if (_activeTextBox != null) return;

                if (_selectedText != null)
                {
                    // 删除当前选中的文字标注（放入重做栈，Ctrl+Y 可恢复，故此处不 Dispose）
                    AnnotationItem doomedText = _selectedText;
                    _selectedText = null;
                    _annotations.Remove(doomedText);
                    _redoStack.Add(doomedText);
                    RenumberAnnotations();
                    UpdateBarsLayout();
                    Invalidate();
                }
                else if (_selectedShape != null)
                {
                    // 删除当前选中的图形图元（同样支持 Ctrl+Y 恢复）
                    AnnotationItem doomed = _selectedShape;
                    _selectedShape = null;
                    _annotations.Remove(doomed);
                    _redoStack.Add(doomed);
                    RenumberAnnotations();
                    UpdateBarsLayout();
                    Invalidate();
                }
                else if (_hasSelected)
                {
                    // 无选中图元时，Del 直接切换到橡皮擦工具
                    SwitchTool(ToolType.Eraser);
                }
            }
            base.OnKeyDown(e);
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space)
            {
                _isSpaceKeyDown = false;
                if (_isSpaceDragging)
                {
                    _isSpaceDragging = false;
                    UpdateBarsLayout();
                    Invalidate();
                }
            }
            base.OnKeyUp(e);
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
                if (item == _selectedShape)
                {
                    _selectedShape = null;
                }
                // 撤销掉的图元若仍带着选中态，重做时会画出孤立的手柄框，这里一并清掉
                item.IsSelected = false;
                // 橡皮擦悬停态同理：若被撤销的正是当前悬停目标，重做后它会带着
                // "即将被擦除"的红色高亮复活，这里顺手复位引用与标志。
                if (item == _hoveredEraserTarget)
                {
                    item.IsHoveredForEraser = false;
                    _hoveredEraserTarget = null;
                }
                RenumberAnnotations();

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
                RenumberAnnotations();

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

        private void ConfirmCrop(bool isOcr)
        {
            if (_activeTextBox != null) CommitActiveText();
            if (_selectedRect.Width <= 0 || _selectedRect.Height <= 0) return;
            SaveRectToHistory(_selectedRect);
            this.CroppedResult = GenerateFinalBitmap();
            this.AiEditBundlePath = ExportAiEditBundle(this.CroppedResult);
            this.IsOcrRequested = isOcr;
            this.DialogResult = DialogResult.OK;
            this.Close();
        }

        private void ConfirmAiEdit()
        {
            if (_activeTextBox != null) CommitActiveText();
            if (_selectedRect.Width <= 0 || _selectedRect.Height <= 0) return;
            SaveRectToHistory(_selectedRect);
            this.CroppedResult = GenerateFinalBitmap();
            this.AiEditBundlePath = ExportAiEditBundle(this.CroppedResult);
            this.IsAiEditRequested = true;
            this.DialogResult = DialogResult.OK;
            this.Close();
        }

        private string ExportAiEditBundle(Bitmap annotatedBmp)
        {
            try
            {
                string tempDir = System.IO.Path.GetTempPath();
                string bundlePath = System.IO.Path.Combine(
                    tempDir,
                    "rubick_ai_edit_" + Guid.NewGuid().ToString("N") + ".json");
                string latestPath = System.IO.Path.Combine(tempDir, "rubick_latest_capture_bundle.json");

                using (Bitmap cleanBmp = GenerateCleanBitmap())
                {
                    bool hasMaskRegion;
                    using (Bitmap maskBmp = GenerateAutoMaskBitmap(out hasMaskRegion))
                    {
                        string cleanDataUrl = BitmapToDataUrl(cleanBmp);
                        string annotatedDataUrl = BitmapToDataUrl(annotatedBmp);
                        string maskDataUrl = BitmapToDataUrl(maskBmp);

                        List<string> textNotes = new List<string>();
                        List<string> boxesJson = new List<string>();

                        foreach (AnnotationItem ann in _annotations)
                        {
                            Rectangle b = ann.GetVisualBounds();
                            int rx = Math.Max(0, b.Left - _selectedRect.Left);
                            int ry = Math.Max(0, b.Top - _selectedRect.Top);
                            int rw = Math.Min(_selectedRect.Width - rx, Math.Max(4, b.Width));
                            int rh = Math.Min(_selectedRect.Height - ry, Math.Max(4, b.Height));

                            TextAnnotation ta = ann as TextAnnotation;
                            if (ta != null)
                            {
                                string txt = (ta.Text ?? "").Trim();
                                if (!string.IsNullOrEmpty(txt))
                                {
                                    textNotes.Add("\"" + EscapeJson(txt) + "\"");
                                    boxesJson.Add(string.Format(
                                        "{{\"type\":\"Text\",\"x\":{0},\"y\":{1},\"width\":{2},\"height\":{3},\"text\":\"{4}\"}}",
                                        rx, ry, rw, rh, EscapeJson(txt)));
                                }
                            }
                            else
                            {
                                string typeName = ann.GetType().Name.Replace("Annotation", "");
                                boxesJson.Add(string.Format(
                                    "{{\"type\":\"{0}\",\"x\":{1},\"y\":{2},\"width\":{3},\"height\":{4}}}",
                                    EscapeJson(typeName), rx, ry, rw, rh));
                            }
                        }

                        System.Text.StringBuilder sb = new System.Text.StringBuilder();
                        sb.Append("{");
                        sb.AppendFormat("\"timestamp\":{0},", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                        sb.AppendFormat("\"width\":{0},\"height\":{1},", _selectedRect.Width, _selectedRect.Height);
                        sb.AppendFormat("\"hasMaskRegion\":{0},", hasMaskRegion ? "true" : "false");
                        sb.AppendFormat("\"cleanDataUrl\":\"{0}\",", cleanDataUrl);
                        sb.AppendFormat("\"annotatedDataUrl\":\"{0}\",", annotatedDataUrl);
                        sb.AppendFormat("\"maskDataUrl\":\"{0}\",", maskDataUrl);
                        sb.AppendFormat("\"textNotes\":[{0}],", string.Join(",", textNotes.ToArray()));
                        sb.AppendFormat("\"boxes\":[{0}]", string.Join(",", boxesJson.ToArray()));
                        sb.Append("}");

                        string jsonContent = sb.ToString();
                        System.Text.UTF8Encoding utf8NoBom = new System.Text.UTF8Encoding(false);
                        System.IO.File.WriteAllText(bundlePath, jsonContent, utf8NoBom);
                        try { System.IO.File.WriteAllText(latestPath, jsonContent, utf8NoBom); } catch { }
                        return bundlePath;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("ExportAiEditBundle Error: " + ex.Message);
                return null;
            }
        }

        private Bitmap GenerateCleanBitmap()
        {
            Bitmap clean = new Bitmap(_selectedRect.Width, _selectedRect.Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(clean))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(_fullBmp, new Rectangle(0, 0, _selectedRect.Width, _selectedRect.Height),
                    _selectedRect, GraphicsUnit.Pixel);
            }
            return clean;
        }

        private Bitmap GenerateAutoMaskBitmap(out bool hasMaskRegion)
        {
            hasMaskRegion = false;
            Bitmap mask = new Bitmap(_selectedRect.Width, _selectedRect.Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(mask))
            {
                g.Clear(Color.Black);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (SolidBrush whiteBrush = new SolidBrush(Color.White))
                {
                    foreach (AnnotationItem ann in _annotations)
                    {
                        // 文字 T、箭头、直线、序号均视为指示性批注，不计入需要被局部重绘覆盖的遮罩区
                        if (ann is TextAnnotation || ann is ArrowAnnotation || ann is LineAnnotation || ann is NumberAnnotation)
                            continue;

                        Rectangle b = ann.GetVisualBounds();
                        if (b.Width <= 0 || b.Height <= 0) continue;

                        Rectangle local = new Rectangle(
                            b.Left - _selectedRect.Left,
                            b.Top - _selectedRect.Top,
                            b.Width,
                            b.Height);
                        local.Inflate(8, 8);
                        local.Intersect(new Rectangle(0, 0, _selectedRect.Width, _selectedRect.Height));
                        if (local.Width > 0 && local.Height > 0)
                        {
                            hasMaskRegion = true;
                            if (ann is EllipseAnnotation)
                            {
                                g.FillEllipse(whiteBrush, local);
                            }
                            else
                            {
                                g.FillRectangle(whiteBrush, local);
                            }
                        }
                    }
                }
            }
            return mask;
        }

        private static string BitmapToDataUrl(Bitmap bmp)
        {
            using (System.IO.MemoryStream ms = new System.IO.MemoryStream())
            {
                bmp.Save(ms, ImageFormat.Png);
                return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
            }
        }

        private static string EscapeJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\")
                    .Replace("\"", "\\\"")
                    .Replace("\r", "\\r")
                    .Replace("\n", "\\n")
                    .Replace("\t", "\\t");
        }

        private void PinCrop()
        {
            if (_activeTextBox != null) CommitActiveText();
            if (_selectedRect.Width <= 0 || _selectedRect.Height <= 0) return;
            SaveRectToHistory(_selectedRect);
            this.CroppedResult = GenerateFinalBitmap();
            this.IsPinRequested = true;
            this.DialogResult = DialogResult.OK;
            this.Close();
        }

        private void SaveToFile()
        {
            if (_activeTextBox != null) CommitActiveText();
            using (SaveFileDialog sfd = new SaveFileDialog())
            {
                sfd.Filter = "PNG 图像|*.png|JPEG 图像|*.jpg|BMP 图像|*.bmp";
                sfd.FileName = "Screenshot_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png";
                if (sfd.ShowDialog() == DialogResult.OK)
                {
                    using (Bitmap cropped = GenerateFinalBitmap())
                    {
                        cropped.Save(sfd.FileName);
                    }
                    this.DialogResult = DialogResult.OK;
                    this.Close();
                }
            }
        }

        private Bitmap GenerateFinalBitmap()
        {
            Bitmap cropped = new Bitmap(_selectedRect.Width, _selectedRect.Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(cropped))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;

                g.DrawImage(_fullBmp, new Rectangle(0, 0, _selectedRect.Width, _selectedRect.Height),
                    _selectedRect, GraphicsUnit.Pixel);

                g.TranslateTransform(-_selectedRect.Left, -_selectedRect.Top);

                // 导出图必须"干净"：临时关掉所有图元的选中态，否则 DrawSelectionAdorner 会把
                // 虚线框与四角手柄一并画进成品图。此前只处理了 _selectedText、漏掉 _selectedShape，
                // 于是选中任意矩形/箭头/序号后保存、复制到剪贴板或送去 OCR，成品图里都带着编辑手柄。
                // 这里改为遍历全部图元统一保存/恢复，不再依赖具体是哪个字段处于选中态。
                bool[] prevSelected = new bool[_annotations.Count];
                for (int i = 0; i < _annotations.Count; i++)
                {
                    prevSelected[i] = _annotations[i].IsSelected;
                    _annotations[i].IsSelected = false;
                }

                foreach (AnnotationItem ann in _annotations)
                {
                    ann.Draw(g);
                }
                if (_tempAnnotation != null)
                {
                    _tempAnnotation.Draw(g);
                }

                for (int i = 0; i < _annotations.Count && i < prevSelected.Length; i++)
                {
                    _annotations[i].IsSelected = prevSelected[i];
                }
            }
            return cropped;
        }

        private Rectangle GetNormalizedRect(Point p1, Point p2)
        {
            int x = Math.Min(p1.X, p2.X);
            int y = Math.Min(p1.Y, p2.Y);
            int w = Math.Abs(p1.X - p2.X);
            int h = Math.Abs(p1.Y - p2.Y);
            return new Rectangle(x, y, w, h);
        }

        private bool HasSubBar()
        {
            return _hasSelected && (_currentTool == ToolType.Rect ||
                                    _currentTool == ToolType.Ellipse ||
                                    _currentTool == ToolType.Line ||
                                    _currentTool == ToolType.Arrow ||
                                    _currentTool == ToolType.Brush ||
                                    _currentTool == ToolType.Marker ||
                                    _currentTool == ToolType.Mosaic ||
                                    _currentTool == ToolType.Blur ||
                                    _currentTool == ToolType.Eraser ||
                                    _currentTool == ToolType.Text ||
                                    _currentTool == ToolType.Number ||
                                    _selectedText != null ||
                                    _selectedShape != null);
        }

        // ---- 局部失效（脏区重绘）----
        // 覆盖层窗体覆盖整个虚拟屏（本机 6720×2078 = 14.0 M px）。无参 Invalidate()
        // 会让 OnPaint 把整块抓屏位图连同遮罩重画一遍，实测约 78ms(blit) + 56ms(遮罩)，
        // 鼠标移动时完全跟不上（不足 5 fps），这正是拖拽"不丝滑"的根源。
        // 移动/悬停路径改为只失效真正变化的矩形；GDI+ 会自动按 DC 裁剪区收敛
        // blit 与填充开销（实测裁到 1.53 M px 的框只需 6.2ms，裁到细带只需 0.1ms）。

        /// <summary>把矩形裁剪进窗体范围后失效；空矩形直接忽略</summary>
        private void InvalidateScreen(Rectangle r)
        {
            r.Inflate(4, 4);   // 描边、抗锯齿与卡片阴影会略微溢出计算边界
            r.Intersect(new Rectangle(0, 0, this.Width, this.Height));
            if (r.Width > 0 && r.Height > 0)
            {
                Invalidate(r);
            }
        }

        /// <summary>并集时跳过空矩形，避免 Rectangle.Empty 的 (0,0) 原点把范围无谓地拉大</summary>
        private static Rectangle UnionVisible(Rectangle r, Rectangle other)
        {
            if (other.Width <= 0 || other.Height <= 0) return r;
            if (r.Width <= 0 || r.Height <= 0) return other;
            return Rectangle.Union(r, other);
        }

        /// <summary>
        /// 选区相关的全部可视元素：选区本体、手柄、锚在选区左上角的实时尺寸胶囊，
        /// 以及已确认选区时才存在的尺寸标签、主工具栏与二级栏。
        /// 拖动/缩放选区时必须按"旧 ∪ 新"失效，否则工具栏与胶囊会留下残影。
        /// </summary>
        private Rectangle SelectionAffectedBounds(Rectangle sel)
        {
            if (sel.Width <= 0 || sel.Height <= 0) return Rectangle.Empty;

            Rectangle r = sel;
            // 双层描边、8px 手柄（外溢 4px），以及可能向左右翻转的尺寸胶囊（宽度上界约 110px）
            r.Inflate(120, 32);

            if (_hasSelected)
            {
                r = UnionVisible(r, _selSizeLabelBounds);
                // 必须用带外扩的 ToolBar/SubBarDamageBounds：DrawCardShadow 会在卡片四周
                // 各画出约 4px 的投影，只并上 _toolBarBounds 本体的话，投影下沿那几行会留残影
                r = UnionVisible(r, ToolBarDamageBounds());
                if (HasSubBar())
                {
                    r = UnionVisible(r, SubBarDamageBounds());
                }
            }
            return r;
        }

        /// <summary>候选窗口高亮的失效范围：外沿描边之外还要留出上方或下方的尺寸提示条</summary>
        private static Rectangle HoverDamageBounds(Rectangle r)
        {
            if (r.Width <= 0 || r.Height <= 0) return Rectangle.Empty;
            Rectangle d = r;
            d.Inflate(8, 30);
            return d;
        }

        /// <summary>
        /// 单个标注图元的失效范围。GetBounds() 之外还要外扩：选中态虚线包围盒与
        /// 8px 四角手柄（各外溢 4px）、文字的 1px 描边阴影。
        /// </summary>
        private static Rectangle ItemDamageBounds(AnnotationItem item)
        {
            if (item == null) return Rectangle.Empty;
            Rectangle r = item.GetVisualBounds();
            if (r.Width <= 0 || r.Height <= 0) return Rectangle.Empty;
            // 选中态除了虚线框与四角手柄，上方 26px 处还悬着旋转手柄，
            // 只外扩 6px 会把手柄留在失效区之外，旋转后原地留下一个旧圆环。
            r.Inflate(item.IsSelected ? AnnotationItem.RotateHandleOffset + 8 : 6, item.IsSelected ? AnnotationItem.RotateHandleOffset + 8 : 6);
            return r;
        }

        /// <summary>
        /// 按笔迹宽度外扩的失效范围，用于"绘制中的临时图元"与"橡皮擦悬停高亮"两处。
        /// 二者的包围盒都只由骨架点 / 图元几何算出，不含笔迹宽度：最粗的马克笔 28px（半宽 14），
        /// 橡皮擦高亮用 Width + 2 描边（半宽 15），再算上箭头翼展与抗锯齿，统一外扩 40px。
        /// 临时图元也刻意不裁剪到选区——绘制走的是无裁剪绘图，笔迹可以越过选区边界。
        /// </summary>
        private static Rectangle StrokeDamageBounds(AnnotationItem item)
        {
            if (item == null) return Rectangle.Empty;
            // 用旋转后的实际外接矩形：直接拿未旋转包围盒会让斜置图元甩出去的部分留在画面上
            Rectangle r = item.GetVisualBounds();
            if (r.Width < 0 || r.Height < 0) return Rectangle.Empty;
            r.Inflate(40, 40);
            return r;
        }

        /// <summary>主工具栏的失效范围，额外留出悬停提示气泡的位置（气泡绘制在栏体上方、可能横向超出）</summary>
        private Rectangle ToolBarDamageBounds()
        {
            Rectangle r = _toolBarBounds;
            r.Inflate(100, 50);
            return r;
        }

        /// <summary>二级属性栏的失效范围</summary>
        private Rectangle SubBarDamageBounds()
        {
            Rectangle r = _subBarBounds;
            r.Inflate(100, 50);
            return r;
        }

        /// <summary>
        /// 取色放大镜卡片的精确矩形。绘制与局部失效共用同一套布局公式，
        /// 避免两处各算一遍导致位移后残留旧 HUD。
        /// </summary>
        private Rectangle ColorPickerCardBounds(Point pt)
        {
            const int gridCols = 17;
            const int gridRows = 11;
            const int cellSize = 8;
            int hudW = gridCols * cellSize + 8;
            int hudH = gridRows * cellSize + 88;

            int hx = pt.X + 18;
            int hy = pt.Y + 18;
            if (hx + hudW > this.Width - 6) hx = pt.X - hudW - 18;
            if (hy + hudH > this.Height - 6) hy = pt.Y - hudH - 18;
            if (hx < 4) hx = 4;
            if (hy < 4) hy = 4;

            return new Rectangle(hx, hy, hudW, hudH);
        }

        /// <summary>
        /// 布局主工具栏与二级属性栏 (完全 1:1 对齐 Snipaste 真实界面与用户实截图)
        /// </summary>
        private void UpdateBarsLayout()
        {
            _toolButtons.Clear();
            _subOptions.Clear();

            if (!_hasSelected) return;

            int btnSize = 30;
            int margin = 3;

            // 0. 选区尺寸标签：Snipaste 把它贴在【选区左上角上方】，深色胶囊 "928 × 302 px"
            _sizeInfoText = _selectedRect.Width + " × " + _selectedRect.Height + " px";
            int selLabelW;
            using (Font sif = new Font("Segoe UI", 8.5f, FontStyle.Bold))
            {
                selLabelW = TextRenderer.MeasureText(_sizeInfoText, sif).Width + 14;
            }
            int selLabelH = 20;
            int selLabelX = _selectedRect.Left;
            // 左上角有 8px 手柄，其 2px 描边还会向外溢到包围盒外 5px。
            // 标签与包围盒之间只留 2px 的话，压在上层画的标签会把角手柄的上沿啃掉一条；
            // 留 6px 才能让两者彻底错开。
            int selLabelY = _selectedRect.Top - selLabelH - 6;
            if (selLabelY < 0)
            {
                // 贴顶放不下时翻到选区内侧：同样要让开角手柄，
                // 否则标签左沿会把左侧手柄与左上角手柄一起盖住。
                selLabelX = _selectedRect.Left + 6;
                selLabelY = _selectedRect.Top + 6;
            }
            _selSizeLabelBounds = new Rectangle(selLabelX, selLabelY, selLabelW, selLabelH);

            // 1. 主工具栏定义 (1:1 对齐 Snipaste 实拍顺序：
            //    形状(矩形/椭圆)、线条(折线/箭头)、画笔、马克笔、马赛克、文字 T、橡皮擦
            //    | 撤销、重做 | ✨改图、OCR、取消(×)、贴图(pin)、保存(save)、复制(copy))
            var defs = new[]
            {
                new { Id = "rect_ellipse", Tip = "矩形 / 椭圆 (R/E)", Tool = (_currentTool == ToolType.Ellipse ? ToolType.Ellipse : ToolType.Rect) },
                new { Id = "line_arrow",   Tip = "折线 / 箭头 (L/A)", Tool = (_currentTool == ToolType.Line ? ToolType.Line : ToolType.Arrow) },
                new { Id = "brush",        Tip = "画笔 (B)", Tool = ToolType.Brush },
                new { Id = "marker",       Tip = "记号笔 / 荧光高亮 (M)", Tool = ToolType.Marker },
                new { Id = "mosaic",       Tip = "马赛克 / 模糊 (K/G)", Tool = ToolType.Mosaic },
                new { Id = "text",         Tip = "文字标注 (T)；改图前可在图上直接打字作为修改指令", Tool = ToolType.Text },
                new { Id = "number",       Tip = "序号标注 (N)", Tool = ToolType.Number },
                new { Id = "eraser",       Tip = "橡皮擦 (X / Del)", Tool = ToolType.Eraser },
                new { Id = "sep1",         Tip = "", Tool = ToolType.None },
                new { Id = "undo",         Tip = "撤销 (Ctrl+Z)", Tool = ToolType.None },
                new { Id = "redo",         Tip = "重做 (Ctrl+Y)", Tool = ToolType.None },
                new { Id = "sep2",         Tip = "", Tool = ToolType.None },
                new { Id = "ai_edit",      Tip = "AI 智能改图 (✨)：圈选或打字标注后一键改字或局部重绘", Tool = ToolType.None },
                new { Id = "ocr",          Tip = "识别文字并流式翻译", Tool = ToolType.None },
                new { Id = "cancel",       Tip = "放弃截图 (Esc / 单击右键)", Tool = ToolType.None },
                new { Id = "pin",          Tip = "钉在桌面上 / 贴图 (F3)", Tool = ToolType.None },
                new { Id = "save",         Tip = "保存图片 (Ctrl+S)", Tool = ToolType.None },
                new { Id = "copy",         Tip = "复制到剪贴板 (Enter / 空白处双击)", Tool = ToolType.None }
            };

            int totalWidth = 8;
            foreach (var d in defs)
            {
                totalWidth += (d.Id.StartsWith("sep") ? 6 : (d.Id == "ocr" ? 38 : btnSize)) + margin;
            }
            totalWidth += 6;

            int barH = btnSize + 8;
            int barX = _selectedRect.Right - totalWidth;
            if (barX < 8) barX = 8;
            if (barX + totalWidth > this.Width - 8) barX = this.Width - totalWidth - 8;

            int barY = _selectedRect.Bottom + 8;
            if (barY + barH + 45 > this.Height)
            {
                barY = _selectedRect.Top - barH - 8;
                if (barY < 8) barY = _selectedRect.Top + 8;
            }

            _toolBarBounds = new Rectangle(barX, barY, totalWidth, barH);

            int curX = barX + 5;
            foreach (var d in defs)
            {
                int w = d.Id.StartsWith("sep") ? 6 : (d.Id == "ocr" ? 38 : btnSize);
                Rectangle r = new Rectangle(curX, barY + 4, w, btnSize);
                Action act = null;
                bool disabled = false;

                if (d.Id == "rect_ellipse")
                {
                    act = delegate
                    {
                        if (_currentTool == ToolType.Rect || _currentTool == ToolType.Ellipse)
                            _currentTool = ToolType.None;
                        else
                            _currentTool = ToolType.Rect;
                        DeselectAllAnnotations();
                    };
                }
                else if (d.Id == "line_arrow")
                {
                    act = delegate
                    {
                        if (_currentTool == ToolType.Line || _currentTool == ToolType.Arrow)
                            _currentTool = ToolType.None;
                        else
                            _currentTool = ToolType.Arrow;
                        DeselectAllAnnotations();
                    };
                }
                else if (d.Id == "brush")
                {
                    act = delegate { _currentTool = (_currentTool == ToolType.Brush) ? ToolType.None : ToolType.Brush; DeselectAllAnnotations(); };
                }
                else if (d.Id == "marker")
                {
                    act = delegate { _currentTool = (_currentTool == ToolType.Marker) ? ToolType.None : ToolType.Marker; DeselectAllAnnotations(); };
                }
                else if (d.Id == "mosaic")
                {
                    act = delegate { _currentTool = (_currentTool == ToolType.Mosaic) ? ToolType.None : ToolType.Mosaic; DeselectAllAnnotations(); };
                }
                else if (d.Id == "text")
                {
                    act = delegate { _currentTool = (_currentTool == ToolType.Text) ? ToolType.None : ToolType.Text; };
                }
                else if (d.Id == "number")
                {
                    act = delegate { _currentTool = (_currentTool == ToolType.Number) ? ToolType.None : ToolType.Number; DeselectAllAnnotations(); };
                }
                else if (d.Id == "eraser")
                {
                    act = delegate { _currentTool = (_currentTool == ToolType.Eraser) ? ToolType.None : ToolType.Eraser; DeselectAllAnnotations(); };
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
                else if (d.Id == "pin")
                {
                    act = delegate { PinCrop(); };
                }
                else if (d.Id == "save")
                {
                    act = delegate { SaveToFile(); };
                }
                else if (d.Id == "copy")
                {
                    act = delegate { ConfirmCrop(false); };
                }
                else if (d.Id == "cancel")
                {
                    act = delegate { CancelCrop(); };
                }
                else if (d.Id == "ai_edit")
                {
                    act = delegate { ConfirmAiEdit(); };
                }
                else if (d.Id == "ocr")
                {
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

            // 2. 二级属性栏布局 (Sub-Options Bar)
            if (HasSubBar())
            {
                bool isTextMode = (_currentTool == ToolType.Text || _selectedText != null);
                bool isMarkerMode = (_currentTool == ToolType.Marker);
                // 只选中了图元、未激活任何绘图工具：二级栏仅提供换色，不显示线宽等无关项
                bool isShapeEditOnly = (!isTextMode && _selectedShape != null && _currentTool == ToolType.None);

                // Snipaste 实拍：二级栏最左是一个"主参数"蓝色胶囊（笔宽 / 颗粒度），
                // 其后才是模式类图标按钮、形状按钮、色块与调色板。
                // 宽度一律按内部控件实际排布核算，避免手写魔数卡边裁切。
                const int subPadLeft = 4;
                const int subPadRight = 4;
                const int paletteW = 10 * (11 + 2);   // 20 色通用调色板：两行十列
                const int paramW = 32;                // 主参数胶囊（蓝色实心，显示笔宽/颗粒度数值）
                const int iconBtnW = 34;              // 模式类图标按钮（填充 / 马赛克-模糊）
                const int arrowStyleBtnW = 52;        // 箭头样式下拉按钮：要放下样式预览 + 右下角小三角
                const int shapeBtnW = 30;             // 形状直选按钮（矩形 / 椭圆）
                const int swatchW = 25;               // 主色块
                const int sliderW = 110;              // 强度滑杆
                const int slideValW = 34;             // 滑杆数值

                bool isEraserMode = (_currentTool == ToolType.Eraser);
                bool isMosaicMode = (_currentTool == ToolType.Mosaic || _currentTool == ToolType.Blur);
                bool isShapeMode = (_currentTool == ToolType.Rect || _currentTool == ToolType.Ellipse);
                bool isLineMode = (_currentTool == ToolType.Line || _currentTool == ToolType.Arrow);

                int subH = 32;
                int subW;
                if (isTextMode)
                {
                    // B/I/A + 分隔 + 字体(142) + 字号(50) + 分隔 + 主色块(25) + 调色板
                    subW = subPadLeft + 26 + 24 + 26 + 6 + 142 + 50 + 6 + swatchW + paletteW + subPadRight;
                }
                else if (isShapeEditOnly)
                {
                    subW = subPadLeft + swatchW + paletteW + subPadRight; // 主色块 + 20 色预设调色板
                }
                else if (isMosaicMode)
                {
                    subW = subPadLeft + paramW + iconBtnW + SUB_SEP_W + sliderW + slideValW + subPadRight;
                }
                else if (isEraserMode)
                {
                    subW = subPadLeft + paramW + iconBtnW + subPadRight;
                }
                else if (isShapeMode)
                {
                    subW = subPadLeft + paramW + iconBtnW + SUB_SEP_W + shapeBtnW + shapeBtnW + SUB_SEP_W + swatchW + paletteW + subPadRight;
                }
                else if (isLineMode)
                {
                    // 主参数胶囊 + 线型下拉(52) + 虚实开关(34) + 分隔 + 主色块 + 调色板
                    subW = subPadLeft + paramW + arrowStyleBtnW + iconBtnW + SUB_SEP_W + swatchW + paletteW + subPadRight;
                }
                else
                {
                    subW = subPadLeft + paramW + SUB_SEP_W + swatchW + paletteW + subPadRight; // Marker / Brush
                }

                // Snipaste 实拍：二级栏【左边缘】与主工具栏左边缘对齐，宽度随内容自适应
                int subX = _toolBarBounds.Left;
                if (subX < 8) subX = 8;
                if (subX + subW > this.Width - 8) subX = this.Width - subW - 8;

                // 优先置于主工具栏上方紧密贴合 (间距 3px)，若靠顶则置于下方
                int subY = _toolBarBounds.Top - subH - 3;
                if (subY < 8) subY = _toolBarBounds.Bottom + 3;

                _subBarBounds = new Rectangle(subX, subY, subW, subH);

                int sx = subX + 4;
                int sy = subY + 3;

                if (isTextMode)
                {
                    // === 1:1 对齐用户实截图的【文字标注】8大属性模块 ===

                    // 1. 粗体 [ B ]
                    Rectangle bRect = new Rectangle(sx, sy, 24, 26);
                    _subOptions.Add(new SubOptionItem
                    {
                        Bounds = bRect,
                        Id = "text_bold",
                        Text = "B",
                        ClickAction = delegate
                        {
                            _isFontBold = !_isFontBold;
                            if (_selectedText != null)
                            {
                                _selectedText.IsBold = _isFontBold;
                                UpdateSelectedTextFont();
                            }
                        }
                    });
                    sx += 26;

                    // 2. 斜体 [ I ]
                    Rectangle iRect = new Rectangle(sx, sy, 22, 26);
                    _subOptions.Add(new SubOptionItem
                    {
                        Bounds = iRect,
                        Id = "text_italic",
                        Text = "I",
                        ClickAction = delegate
                        {
                            _isFontItalic = !_isFontItalic;
                            if (_selectedText != null)
                            {
                                _selectedText.IsItalic = _isFontItalic;
                                UpdateSelectedTextFont();
                            }
                        }
                    });
                    sx += 24;

                    // 3. 轮廓/底衬 [ A ]
                    Rectangle aRect = new Rectangle(sx, sy, 24, 26);
                    _subOptions.Add(new SubOptionItem
                    {
                        Bounds = aRect,
                        Id = "text_bg",
                        Text = "A",
                        ClickAction = delegate
                        {
                            _hasTextBackground = !_hasTextBackground;
                            if (_selectedText != null)
                            {
                                _selectedText.HasBackground = _hasTextBackground;
                            }
                        }
                    });
                    sx += 26;

                    // 细分隔线
                    _subOptions.Add(new SubOptionItem
                    {
                        Bounds = new Rectangle(sx + 1, sy + 3, 2, 20),
                        Id = "sub_sep1"
                    });
                    sx += 6;

                    // 4. 字体选择下拉框 [ Microsoft YaHei UI ∨ ] 宽 138px，永不截断
                    Rectangle fontRect = new Rectangle(sx, sy, 138, 26);
                    _subOptions.Add(new SubOptionItem
                    {
                        Bounds = fontRect,
                        Id = "text_font_family",
                        Text = _activeFontFamily,
                        ClickAction = delegate
                        {
                            ShowFontFamilyDropdown(fontRect);
                        }
                    });
                    sx += 142;

                    // 5. 字号微调/下拉框 [ 16.0 ∨ ]
                    Rectangle sizeRect = new Rectangle(sx, sy, 46, 26);
                    _subOptions.Add(new SubOptionItem
                    {
                        Bounds = sizeRect,
                        Id = "text_font_size",
                        Text = string.Format("{0:0}", _activeFontSize),
                        ClickAction = delegate
                        {
                            ShowFontSizeDropdown(sizeRect);
                        }
                    });
                    sx += 50;

                    // 细分隔线
                    _subOptions.Add(new SubOptionItem
                    {
                        Bounds = new Rectangle(sx + 1, sy + 3, 2, 20),
                        Id = "sub_sep2"
                    });
                    sx += 6;

                    // 6. 主颜色大方块 [ █ ◢ ]
                    Rectangle mainColorRect = new Rectangle(sx, sy + 3, 20, 20);
                    _subOptions.Add(new SubOptionItem
                    {
                        Bounds = mainColorRect,
                        Id = "main_color_swatch",
                        ColorValue = _activeColor,
                        IsColor = true,
                        ClickAction = delegate
                        {
                            using (ColorDialog cd = new ColorDialog())
                            {
                                cd.Color = _activeColor;
                                if (cd.ShowDialog() == DialogResult.OK)
                                {
                                    ApplyActiveColor(cd.Color);
                                    if (_selectedText != null) _selectedText.Color = _activeColor;
                                }
                            }
                        }
                    });
                    sx += 25;

                    // 7. 20 色双行预设调色板矩阵 (两行十列)
                    AddPresetColorMatrix(sx, sy);
                }
                else
                {
                    // === 绘图工具（Snipaste 实拍布局：最左为主参数胶囊）===

                    if (!isShapeEditOnly)
                    {
                        // 1. 主参数胶囊：蓝色实心，显示当前笔宽 / 颗粒度数值
                        //    点击循环档位，滚轮同样可调（与 [ ] 键共用同一套档位表）
                        Rectangle paramRect = new Rectangle(sx, sy + 2, paramW - 2, 26);
                        _subOptions.Add(new SubOptionItem
                        {
                            Bounds = paramRect,
                            Id = "param_size",
                            Text = ActiveParamText(),
                            ShowBadge = IsActiveParamModified(),
                            ClickAction = delegate { StepActiveSize(1); }
                        });
                        sx += paramW;

                        if (isMosaicMode)
                        {
                            // 马赛克 / 高斯模糊：图标按钮切换模式 + 强度滑杆
                            Rectangle modeRect = new Rectangle(sx, sy + 2, iconBtnW - 2, 26);
                            _subOptions.Add(new SubOptionItem
                            {
                                Bounds = modeRect,
                                Id = "mosaic_mode_toggle",
                                IsActiveFlag = (_currentTool == ToolType.Blur),
                                ClickAction = delegate
                                {
                                    // 模式切换不做开关取反，避免误关闭工具
                                    _currentTool = (_currentTool == ToolType.Mosaic) ? ToolType.Blur : ToolType.Mosaic;
                                }
                            });
                            sx += iconBtnW;

                            AddSubSeparator(ref sx, sy);

                            // 强度滑杆：拖拽实时调节颗粒度 / 模糊半径 (4 ~ 40)
                            Rectangle sliderRect = new Rectangle(sx, sy + 7, sliderW - 8, 18);
                            _subOptions.Add(new SubOptionItem
                            {
                                Bounds = sliderRect,
                                Id = "mosaic_slider",
                                SizeValue = _mosaicBlockSize
                            });
                            sx += sliderW;

                            Rectangle valRect = new Rectangle(sx, sy + 4, slideValW - 2, 24);
                            _subOptions.Add(new SubOptionItem
                            {
                                Bounds = valRect,
                                Id = "mosaic_slider_value",
                                Text = _mosaicBlockSize.ToString()
                            });
                            sx += slideValW;
                        }
                        else if (isEraserMode)
                        {
                            // 橡皮擦：透明棋盘格预览块
                            Rectangle erRect = new Rectangle(sx, sy + 2, iconBtnW - 2, 26);
                            _subOptions.Add(new SubOptionItem
                            {
                                Bounds = erRect,
                                Id = "eraser_preview"
                            });
                            sx += iconBtnW;
                        }
                        else if (isShapeMode)
                        {
                            // 形状：填充方式图标 + 矩形 / 椭圆 直选（Snipaste 是直选而非下拉）
                            Rectangle fillRect = new Rectangle(sx, sy + 2, iconBtnW - 2, 26);
                            _subOptions.Add(new SubOptionItem
                            {
                                Bounds = fillRect,
                                Id = "toggle_fill",
                                IsActiveFlag = _isFilled,
                                ClickAction = delegate { ShowFillDropdown(fillRect); }
                            });
                            sx += iconBtnW;

                            AddSubSeparator(ref sx, sy);

                            Rectangle rectBtn = new Rectangle(sx, sy + 2, shapeBtnW - 2, 26);
                            _subOptions.Add(new SubOptionItem
                            {
                                Bounds = rectBtn,
                                Id = "shape_rect",
                                IsActiveFlag = (_currentTool == ToolType.Rect),
                                ClickAction = delegate { _currentTool = ToolType.Rect; }
                            });
                            sx += shapeBtnW;

                            Rectangle ellipseBtn = new Rectangle(sx, sy + 2, shapeBtnW - 2, 26);
                            _subOptions.Add(new SubOptionItem
                            {
                                Bounds = ellipseBtn,
                                Id = "shape_ellipse",
                                IsActiveFlag = (_currentTool == ToolType.Ellipse),
                                ClickAction = delegate { _currentTool = ToolType.Ellipse; }
                            });
                            sx += shapeBtnW;

                            AddSubSeparator(ref sx, sy);
                        }
                        else if (isLineMode)
                        {
                            // 线条：箭头样式下拉（直线 / 细箭头 / 实心箭头 / 空心箭头），1:1 对齐 Snipaste
                            Rectangle lineRect = new Rectangle(sx, sy + 2, arrowStyleBtnW - 2, 26);
                            _subOptions.Add(new SubOptionItem
                            {
                                Bounds = lineRect,
                                Id = "toggle_line_arrow",
                                ClickAction = delegate { ShowArrowStyleDropdown(lineRect); }
                            });
                            sx += arrowStyleBtnW;

                            // 虚实开关：与线型下拉正交，所以不塞进下拉凑成八档（直线与箭头共用一档）
                            Rectangle dashRect = new Rectangle(sx, sy + 2, iconBtnW - 4, 26);
                            _subOptions.Add(new SubOptionItem
                            {
                                Bounds = dashRect,
                                Id = "toggle_dash",
                                IsActiveFlag = _lineDashed,
                                ClickAction = delegate { ApplyDashStyle(!_lineDashed); }
                            });
                            sx += iconBtnW;

                            AddSubSeparator(ref sx, sy);
                        }
                        else
                        {
                            // 画笔 / 马克笔：主参数胶囊后直接一条分隔线
                            AddSubSeparator(ref sx, sy);
                        }
                    }

                    // 主颜色方块
                    Rectangle mainColorRect = new Rectangle(sx, sy + 3, 20, 20);
                    _subOptions.Add(new SubOptionItem
                    {
                        Bounds = mainColorRect,
                        Id = "main_color_swatch",
                        ColorValue = _activeColor,
                        IsColor = true,
                        ClickAction = delegate
                        {
                            using (ColorDialog cd = new ColorDialog())
                            {
                                cd.Color = _activeColor;
                                if (cd.ShowDialog() == DialogResult.OK) ApplyActiveColor(cd.Color);
                            }
                        }
                    });
                    sx += swatchW;

                    // 20 色双行调色板：马克笔复用同一套色板（绘制时按半透明荧光渲染）
                    AddPresetColorMatrix(sx, sy);
                }
            }
        }

        private void UpdateSelectedTextFont()
        {
            if (_selectedText == null) return;
            FontStyle fs = FontStyle.Regular;
            if (_selectedText.IsBold) fs |= FontStyle.Bold;
            if (_selectedText.IsItalic) fs |= FontStyle.Italic;
            if (_selectedText.TextFont != null) _selectedText.TextFont.Dispose();
            _selectedText.TextFont = new Font(_selectedText.FontFamilyName, _selectedText.FontSize, fs);
        }

        private void AddPresetColorMatrix(int startX, int startY)
        {
            int cs = 11;
            int gap = 2;

            // Row 1 (10 色：黑 / 灰 / 深红 / 大红 / 橙 / 明黄 / 鲜绿 / 青 / 深蓝 / 紫)
            for (int i = 0; i < PRESET_COLORS_ROW1.Length; i++)
            {
                Rectangle r = new Rectangle(startX + i * (cs + gap), startY + 2, cs, cs);
                Color c = PRESET_COLORS_ROW1[i];
                _subOptions.Add(new SubOptionItem
                {
                    Bounds = r,
                    Id = "color_row1",
                    ColorValue = c,
                    IsColor = true,
                    ClickAction = delegate
                    {
                        ApplyActiveColor(c);
                        if (_selectedText != null) _selectedText.Color = c;
                    }
                });
            }

            // Row 2 (10 色：白 / 浅灰 / 浅褐 / 粉红 / 金黄 / 淡黄 / 黄绿 / 浅青 / 钢蓝 / 淡紫)
            for (int i = 0; i < PRESET_COLORS_ROW2.Length; i++)
            {
                Rectangle r = new Rectangle(startX + i * (cs + gap), startY + 2 + cs + gap, cs, cs);
                Color c = PRESET_COLORS_ROW2[i];
                _subOptions.Add(new SubOptionItem
                {
                    Bounds = r,
                    Id = "color_row2",
                    ColorValue = c,
                    IsColor = true,
                    ClickAction = delegate
                    {
                        ApplyActiveColor(c);
                        if (_selectedText != null) _selectedText.Color = c;
                    }
                });
            }
        }

        /// <summary>
        /// 二级属性栏内的细分隔线：追加一个分隔控件并推进水平游标 (Snipaste 各功能块之间同款竖线)
        /// </summary>
        private void AddSubSeparator(ref int sx, int sy)
        {
            _subOptions.Add(new SubOptionItem
            {
                Bounds = new Rectangle(sx + 2, sy + 5, 2, 18),
                Id = "sub_sep_draw"
            });
            sx += SUB_SEP_W;
        }

        /// <summary>
        /// 当前工具"主参数"的显示文本：笔宽取整数值，马赛克/模糊取颗粒度
        /// </summary>
        private string ActiveParamText()
        {
            if (_currentTool == ToolType.Mosaic || _currentTool == ToolType.Blur)
                return _mosaicBlockSize.ToString();
            if (_currentTool == ToolType.Marker)
                return ((int)Math.Round(_markerWidth)).ToString();
            return ((int)Math.Round(_penWidth)).ToString();
        }

        /// <summary>
        /// 当前工具的主参数是否已偏离出厂默认值。
        /// 用于在副栏主参数胶囊右上角点亮红点，让用户一眼看出"这个数值我调过"，
        /// 而不必记住默认档位是多少。
        /// </summary>
        private bool IsActiveParamModified()
        {
            if (_currentTool == ToolType.Mosaic || _currentTool == ToolType.Blur)
                return _mosaicBlockSize != DEFAULT_MOSAIC_BLOCK_SIZE;
            if (_currentTool == ToolType.Marker)
                return Math.Abs(_markerWidth - DEFAULT_MARKER_WIDTH) > 0.01f;
            return Math.Abs(_penWidth - DEFAULT_PEN_WIDTH) > 0.01f;
        }

        /// <summary>
        /// 按 Id 在二级栏中查找控件；布局重建后控件对象会更换，因此拖拽时需每次重新取
        /// </summary>
        private SubOptionItem FindSubOption(string id)
        {
            for (int i = 0; i < _subOptions.Count; i++)
            {
                if (_subOptions[i].Id == id) return _subOptions[i];
            }
            return null;
        }

        /// <summary>
        /// 按鼠标横坐标反算强度滑杆数值（夹取到量程内），并同步马赛克颗粒度 / 模糊半径
        /// </summary>
        private void ApplySliderValue(SubOptionItem slider, int mouseX)
        {
            if (slider == null) return;

            // 轨道两端各留 2px 内边距，与 DrawSubBar 中的绘制保持一致
            float usable = slider.Bounds.Width - 4;
            if (usable <= 0f) return;

            float ratio = (mouseX - slider.Bounds.Left - 2) / usable;
            if (ratio < 0f) ratio = 0f;
            if (ratio > 1f) ratio = 1f;

            int value = MOSAIC_MIN + (int)Math.Round((MOSAIC_MAX - MOSAIC_MIN) * ratio);
            if (value < MOSAIC_MIN) value = MOSAIC_MIN;
            if (value > MOSAIC_MAX) value = MOSAIC_MAX;
            _mosaicBlockSize = value;
        }

        private HitType CheckHitTest(Point pt)
        {
            if (!_hasSelected) return HitType.None;

            if (_toolBarBounds.Contains(pt)) return HitType.ToolBar;
            if (HasSubBar() && _subBarBounds.Contains(pt)) return HitType.SubBar;

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
                    case TextHandleType.Rotate: return HitType.TextHandleRotate;
                }
            }

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
                if (_currentTool == ToolType.None) return HitType.Move;
                return HitType.None;
            }

            return HitType.None;
        }

        /// <summary>
        /// 图元编辑态光标：四角手柄返回对角缩放光标，本体返回移动光标，未命中返回 null。
        /// 仅在未激活绘图工具时参与，避免抢占绘制光标。
        /// </summary>
        private Cursor GetShapeCursor(Point pt)
        {
            if (!_hasSelected || _currentTool != ToolType.None) return null;

            TextHandleType handle;
            int vertexIndex;
            AnnotationItem shape = HitTestShape(pt, out handle, out vertexIndex);
            if (shape == null) return null;

            switch (handle)
            {
                case TextHandleType.TopLeft:
                case TextHandleType.BottomRight:
                    return Cursors.SizeNWSE;
                case TextHandleType.TopRight:
                case TextHandleType.BottomLeft:
                    return Cursors.SizeNESW;
                case TextHandleType.Vertex:
                    // 顶点可沿任意方向拖，四向箭头即可表达"可拖拽"
                    return Cursors.SizeAll;
                case TextHandleType.Rotate:
                    return Cursors.Hand;
                default:
                    return Cursors.SizeAll;
            }
        }

        private Cursor GetCursorForHit(HitType hit)
        {
            if (_isSpaceKeyDown && _hasSelected) return Cursors.Hand;

            switch (hit)
            {
                case HitType.TextHandleRotate:
                    return Cursors.Hand;

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
                    if (_currentTool == ToolType.Eraser) return Cursors.Hand;
                    return _hasSelected && _currentTool != ToolType.None ? Cursors.Cross : Cursors.Cross;
            }
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

                if (r.Width > 0 && r.Height > 0)
                {
                    using (Pen outerPen = new Pen(Color.FromArgb(2, 132, 199), 2f))
                    {
                        g.DrawRectangle(outerPen, r);
                    }
                    using (Pen innerPen = new Pen(Color.FromArgb(160, 255, 255, 255), 1f))
                    {
                        Rectangle inner = new Rectangle(r.X + 1, r.Y + 1, Math.Max(0, r.Width - 2), Math.Max(0, r.Height - 2));
                        g.DrawRectangle(innerPen, inner);
                    }

                    // 实时尺寸胶囊：只在"正在拖拽框选"这一段显示。
                    // 选区一旦确认，尺寸标签就完全交给 DrawSizeInfo 负责——
                    // 两处同时画会在同一个左上角叠出重影（"817 × 497" 与 "817 × 497 px" 几乎重合）。
                    if (_isSelecting && r.Width > 20 && r.Height > 15)
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
                            if (tipY < 4) tipY = r.Top + 6;

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

                // 探测到窗口/色块时高亮候选框
                if (!_hoverWindowRect.IsEmpty && _hoverWindowRect.Width > 0 && _hoverWindowRect.Height > 0)
                {
                    using (SolidBrush hoverFill = new SolidBrush(Color.FromArgb(40, 14, 165, 233)))
                    {
                        g.FillRectangle(hoverFill, _hoverWindowRect);
                    }
                    using (Pen outerPen = new Pen(Color.FromArgb(220, 14, 165, 233), 2f))
                    using (Pen innerPen = new Pen(Color.FromArgb(180, 255, 255, 255), 1f))
                    {
                        g.DrawRectangle(outerPen, _hoverWindowRect);
                        Rectangle inner = new Rectangle(_hoverWindowRect.X + 1, _hoverWindowRect.Y + 1, Math.Max(0, _hoverWindowRect.Width - 2), Math.Max(0, _hoverWindowRect.Height - 2));
                        g.DrawRectangle(innerPen, inner);
                    }

                    string hoverTip = string.Format("{0} × {1} px", _hoverWindowRect.Width, _hoverWindowRect.Height);
                    using (Font tipFont = new Font("Segoe UI", 8.5f, FontStyle.Bold))
                    {
                        SizeF tipSize = g.MeasureString(hoverTip, tipFont);
                        int tipW = (int)tipSize.Width + 12;
                        int tipH = (int)tipSize.Height + 4;
                        int tipX = _hoverWindowRect.Left;
                        int tipY = _hoverWindowRect.Top - tipH - 2;
                        if (tipY < 4) tipY = _hoverWindowRect.Top + 4;
                        if (tipX + tipW > this.Width - 4) tipX = this.Width - tipW - 4;

                        Rectangle tipR = new Rectangle(tipX, tipY, tipW, tipH);
                        using (SolidBrush tipBg = new SolidBrush(Color.FromArgb(220, 2, 132, 199)))
                        using (SolidBrush tipTxt = new SolidBrush(Color.White))
                        {
                            g.FillRectangle(tipBg, tipR);
                            g.DrawString(hoverTip, tipFont, tipTxt, tipX + 5, tipY + 2);
                        }
                    }
                }
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
                DrawSizeInfo(g);
                DrawToolBar(g);

                if (HasSubBar())
                {
                    DrawSubBar(g);
                }
            }
            else if (!_isSelecting && !_hasSelected)
            {
                DrawColorPickerMagnifier(g, _currentMousePos);

                if (_showHelpOverlay)
                {
                    DrawShortcutHelpCard(g);
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
            for (int i = 3; i >= 1; i--)
            {
                using (Pen sp = new Pen(Color.FromArgb(10 * (4 - i), 0, 0, 0), 1))
                using (GraphicsPath path = CreateRoundedRectanglePath(new Rectangle(rect.X - i, rect.Y - i + 1, rect.Width + i * 2, rect.Height + i * 2), radius + 1))
                {
                    g.DrawPath(sp, path);
                }
            }
        }

        public static GraphicsPath CreateRoundedRectanglePath(Rectangle rect, int radius)
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

        private void ShowFontFamilyDropdown(Rectangle anchor)
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Renderer = new ModernDropdownRenderer();
            menu.ShowImageMargin = false;
            menu.ShowCheckMargin = true;
            menu.Font = new Font("Microsoft YaHei UI", 9f);

            foreach (string fam in FONT_FAMILIES)
            {
                string currentFam = fam;
                ToolStripMenuItem item = new ToolStripMenuItem(currentFam);
                try
                {
                    item.Font = new Font(currentFam, 9.5f);
                }
                catch { }

                if (currentFam == _activeFontFamily)
                {
                    item.Checked = true;
                }
                item.Click += delegate
                {
                    _activeFontFamily = currentFam;
                    for (int i = 0; i < FONT_FAMILIES.Length; i++)
                    {
                        if (FONT_FAMILIES[i] == currentFam) { _fontFamilyIndex = i; break; }
                    }
                    if (_selectedText != null)
                    {
                        _selectedText.FontFamilyName = _activeFontFamily;
                        UpdateSelectedTextFont();
                    }
                    UpdateBarsLayout();
                    Invalidate();
                };
                menu.Items.Add(item);
            }

            int menuH = menu.PreferredSize.Height;
            int y = (anchor.Bottom + 2 + menuH > this.Height - 10) ? (anchor.Top - menuH - 2) : (anchor.Bottom + 2);
            // 下拉菜单是每次点击临时创建的（不进 Controls 树），关闭后要自己释放，
            // 否则反复弹菜单会持续累积 GDI 句柄。释放时机由 DisposeDropdownOnClose 统一把关。
            DisposeDropdownOnClose(menu, null);
            menu.Show(this, new Point(anchor.Left, y));
        }

        private void ShowFontSizeDropdown(Rectangle anchor)
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Renderer = new ModernDropdownRenderer();
            menu.ShowImageMargin = false;
            menu.ShowCheckMargin = true;
            menu.Font = new Font("Segoe UI", 9f);

            float[] sizes = new float[] { 9f, 10f, 12f, 14f, 16f, 18f, 20f, 24f, 28f, 32f, 36f, 48f, 64f };
            foreach (float sz in sizes)
            {
                float currentSz = sz;
                ToolStripMenuItem item = new ToolStripMenuItem(string.Format("{0:0} pt", currentSz));
                if (Math.Abs(currentSz - _activeFontSize) < 0.5f)
                {
                    item.Checked = true;
                }
                item.Click += delegate
                {
                    _activeFontSize = currentSz;
                    if (_selectedText != null)
                    {
                        _selectedText.FontSize = _activeFontSize;
                        UpdateSelectedTextFont();
                    }
                    UpdateBarsLayout();
                    Invalidate();
                };
                menu.Items.Add(item);
            }

            int menuH = menu.PreferredSize.Height;
            int y = (anchor.Bottom + 2 + menuH > this.Height - 10) ? (anchor.Top - menuH - 2) : (anchor.Bottom + 2);
            // 下拉菜单是每次点击临时创建的（不进 Controls 树），关闭后要自己释放，
            // 否则反复弹菜单会持续累积 GDI 句柄。释放时机由 DisposeDropdownOnClose 统一把关。
            DisposeDropdownOnClose(menu, null);
            menu.Show(this, new Point(anchor.Left, y));
        }

        private void ShowFillDropdown(Rectangle anchor)
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Renderer = new ModernDropdownRenderer();
            menu.ShowImageMargin = false;
            menu.ShowCheckMargin = true;
            menu.Font = new Font("Microsoft YaHei UI", 9f);

            ToolStripMenuItem outlineItem = new ToolStripMenuItem("□  空心描边 (Outline)");
            outlineItem.Checked = !_isFilled;
            outlineItem.Click += delegate
            {
                _isFilled = false;
                UpdateBarsLayout();
                Invalidate();
            };
            menu.Items.Add(outlineItem);

            ToolStripMenuItem filledItem = new ToolStripMenuItem("■  实心填充 (Filled)");
            filledItem.Checked = _isFilled;
            filledItem.Click += delegate
            {
                _isFilled = true;
                UpdateBarsLayout();
                Invalidate();
            };
            menu.Items.Add(filledItem);

            int menuH = menu.PreferredSize.Height;
            int y = (anchor.Bottom + 2 + menuH > this.Height - 10) ? (anchor.Top - menuH - 2) : (anchor.Bottom + 2);
            // 下拉菜单是每次点击临时创建的（不进 Controls 树），关闭后要自己释放，
            // 否则反复弹菜单会持续累积 GDI 句柄。释放时机由 DisposeDropdownOnClose 统一把关。
            DisposeDropdownOnClose(menu, null);
            menu.Show(this, new Point(anchor.Left, y));
        }

        /// <summary>箭头样式下拉的图标尺寸：要能一眼分辨凹槽实心与空心三角形</summary>
        private const int ARROW_PREVIEW_W = 48;
        private const int ARROW_PREVIEW_H = 20;

        /// <summary>
        /// 箭头样式下拉（1:1 对齐 Snipaste 的线型菜单）：
        /// 每项都带一张真实渲染的样式预览图，而不是纯文字描述。
        /// </summary>
        private void ShowArrowStyleDropdown(Rectangle anchor)
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Renderer = new ModernDropdownRenderer();
            menu.ShowImageMargin = true;
            menu.ShowCheckMargin = true;
            menu.Font = new Font("Microsoft YaHei UI", 9f);
            menu.ImageScalingSize = new Size(ARROW_PREVIEW_W, ARROW_PREVIEW_H);

            // 预览图是本次菜单私有的 Bitmap，闭包一并交给释放流程，避免反复弹菜单漏 GDI 对象
            List<Image> previews = new List<Image>();

            // 第 1 档：直线（不带头部）——选中它等于切到"直线 / 折线"工具
            menu.Items.Add(BuildArrowStyleItem(
                previews, "直线 / 折线", null, _lineDashed, (_currentTool == ToolType.Line),
                delegate { ApplyLineStyle(null); }));

            menu.Items.Add(BuildArrowStyleItem(
                previews, "细箭头", ArrowHeadStyle.Thin, _lineDashed,
                (_currentTool == ToolType.Arrow && _arrowStyle == ArrowHeadStyle.Thin),
                delegate { ApplyLineStyle(ArrowHeadStyle.Thin); }));

            menu.Items.Add(BuildArrowStyleItem(
                previews, "实心箭头", ArrowHeadStyle.Solid, _lineDashed,
                (_currentTool == ToolType.Arrow && _arrowStyle == ArrowHeadStyle.Solid),
                delegate { ApplyLineStyle(ArrowHeadStyle.Solid); }));

            menu.Items.Add(BuildArrowStyleItem(
                previews, "空心箭头", ArrowHeadStyle.Hollow, _lineDashed,
                (_currentTool == ToolType.Arrow && _arrowStyle == ArrowHeadStyle.Hollow),
                delegate { ApplyLineStyle(ArrowHeadStyle.Hollow); }));

            int menuH = menu.PreferredSize.Height;
            int y = (anchor.Bottom + 2 + menuH > this.Height - 10) ? (anchor.Top - menuH - 2) : (anchor.Bottom + 2);
            DisposeDropdownOnClose(menu, previews.ToArray());
            menu.Show(this, new Point(anchor.Left, y));
        }

        /// <summary>造一条带样式预览图的菜单项（预览图登记进 previews 由调用方统一释放）</summary>
        private ToolStripMenuItem BuildArrowStyleItem(List<Image> previews, string text, ArrowHeadStyle? style, bool dashed, bool isChecked, Action onClick)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(text);
            item.Image = CreateLineStylePreview(style, dashed);
            previews.Add(item.Image);
            item.Checked = isChecked;
            item.Click += delegate { onClick(); };
            return item;
        }

        /// <summary>把某个线型渲染成下拉菜单里的小预览图（style 为 null 表示无头部的直线）</summary>
        private static Bitmap CreateLineStylePreview(ArrowHeadStyle? style, bool dashed)
        {
            Bitmap bmp = new Bitmap(ARROW_PREVIEW_W, ARROW_PREVIEW_H);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                // 四周各留一点余量，箭头尖端与线端不会被裁掉
                RectangleF box = new RectangleF(3f, 3f, ARROW_PREVIEW_W - 7f, ARROW_PREVIEW_H - 6f);
                DrawLineStyleGlyph(g, box, style, Color.FromArgb(51, 65, 85), 1.6f, dashed);
            }
            return bmp;
        }

        /// <summary>
        /// 切换线型虚实：直线与箭头共用这一档开关，改了之后同步落到当前选中的线条图元上，
        /// 免得"开关已亮但画布上那根线没变"。
        /// </summary>
        private void ApplyDashStyle(bool dashed)
        {
            _lineDashed = dashed;

            LineAnnotation selectedLine = _selectedShape as LineAnnotation;
            if (selectedLine != null) selectedLine.IsDashed = dashed;

            ArrowAnnotation selectedArrow = _selectedShape as ArrowAnnotation;
            if (selectedArrow != null) selectedArrow.IsDashed = dashed;

            UpdateBarsLayout();
            Invalidate();
        }

        /// <summary>
        /// 选中某个线型：null = 直线（切到 Line 工具），其余 = 箭头工具 + 对应头部样式。
        /// 若此刻正选中着一支箭头，就地改它的样式，不必删掉重画。
        /// </summary>
        private void ApplyLineStyle(ArrowHeadStyle? style)
        {
            if (style == null)
            {
                _currentTool = ToolType.Line;
            }
            else
            {
                _currentTool = ToolType.Arrow;
                _arrowStyle = style.Value;

                ArrowAnnotation selectedArrow = _selectedShape as ArrowAnnotation;
                if (selectedArrow != null) selectedArrow.HeadStyle = style.Value;
            }

            UpdateBarsLayout();
            Invalidate();
        }

        /// <summary>
        /// 在给定矩形内画一段线条预览：style 为 null 表示纯直线。
        /// 二级栏的下拉按钮与下拉菜单里每一项共用这一份画法，
        /// 保证"按钮上显示的"和"菜单里列出的"永远对得上。
        /// dashed 为真时杆身画成虚线（头部仍为实心，与画布上的真实图元一致）。
        /// </summary>
        private static void DrawLineStyleGlyph(Graphics g, RectangleF box, ArrowHeadStyle? style, Color color, float lineWidth, bool dashed)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;

            float cy = box.Top + box.Height / 2f;
            if (style == null)
            {
                using (Pen p = new Pen(color, lineWidth))
                {
                    p.StartCap = dashed ? LineCap.Flat : LineCap.Round;
                    p.EndCap = dashed ? LineCap.Flat : LineCap.Round;
                    if (dashed) AnnotationItem.ApplyDashedPen(p);
                    g.DrawLine(p, box.Left, cy, box.Right, cy);
                }
                return;
            }

            ArrowHeadStyle head = style.Value;
            bool isThin = (head == ArrowHeadStyle.Thin);

            // 头部尺寸按预览框高度反推，按钮 / 菜单项两种尺寸下比例一致
            float headLen = box.Height * (isThin ? 0.42f : 0.55f);
            float spread = isThin ? 0.34f : 0.42f;
            float notchRatio = isThin ? 0.78f : 0.72f;

            float tipX = box.Right;
            float baseX = tipX - headLen;
            // 空心箭头的杆身止于三角底边；实心系的杆身止于燕尾凹槽，末端绝不透过尖端
            float shaftEndX = (head == ArrowHeadStyle.Hollow) ? baseX : (tipX - headLen * notchRatio);

            using (Pen p = new Pen(color, lineWidth))
            {
                p.StartCap = dashed ? LineCap.Flat : LineCap.Round;
                p.EndCap = LineCap.Flat;
                if (dashed) AnnotationItem.ApplyDashedPen(p);
                g.DrawLine(p, box.Left, cy, shaftEndX, cy);
            }

            float half = headLen * (float)Math.Tan(spread);
            PointF tip = new PointF(tipX, cy);
            PointF w1 = new PointF(baseX, cy - half);
            PointF w2 = new PointF(baseX, cy + half);

            if (head == ArrowHeadStyle.Hollow)
            {
                using (Pen hp = new Pen(color, Math.Max(lineWidth * 0.9f, 1.2f)))
                {
                    hp.LineJoin = LineJoin.Miter;
                    g.DrawPolygon(hp, new PointF[] { tip, w1, w2 });
                }
                return;
            }

            PointF[] poly = new PointF[] { tip, w1, new PointF(shaftEndX, cy), w2 };
            using (SolidBrush b = new SolidBrush(color))
            {
                g.FillPolygon(b, poly);
            }
            using (Pen bp = new Pen(color, 0.8f))
            {
                g.DrawPolygon(bp, poly);
            }
        }

        /// <summary>
        /// 给临时下拉菜单挂上"关闭后释放"。
        /// 关键点：不能在 Closed 事件里直接 Dispose——Closed 返回之后，WinForms 的
        /// ToolStripDropDown 收尾流程（注销模态菜单过滤器、拆引用链）还会再访问这个菜单，
        /// 此时对象已释放就会抛 "无法访问已释放的对象。对象名:"ContextMenuStrip""。
        /// 所以把释放推迟到消息循环的下一帧，等收尾彻底结束再动它。
        /// </summary>
        private void DisposeDropdownOnClose(ContextMenuStrip menu, Image[] ownedImages)
        {
            menu.Closed += delegate
            {
                if (IsDisposed || !IsHandleCreated)
                {
                    // 主窗体已在关闭流程里，BeginInvoke 无窗口可投递，只能当场释放
                    ReleaseDropdown(menu, ownedImages);
                    return;
                }
                BeginInvoke(new MethodInvoker(delegate { ReleaseDropdown(menu, ownedImages); }));
            };
        }

        private static void ReleaseDropdown(ContextMenuStrip menu, Image[] ownedImages)
        {
            // 菜单先释放：它会连带释放菜单项持有的字体等句柄
            if (menu != null && !menu.IsDisposed) menu.Dispose();
            if (ownedImages == null) return;
            foreach (Image img in ownedImages)
            {
                if (img != null) img.Dispose();
            }
        }

        private void DrawToolBar(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;

            DrawCardShadow(g, _toolBarBounds, 5);

            using (GraphicsPath path = CreateRoundedRectanglePath(_toolBarBounds, 5))
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
                        g.DrawLine(sp, mx, btn.Bounds.Top + 5, mx, btn.Bounds.Bottom - 5);
                    }
                    continue;
                }

                bool isActive = false;
                if (btn.Id == "rect_ellipse")
                    isActive = (_currentTool == ToolType.Rect || _currentTool == ToolType.Ellipse);
                else if (btn.Id == "line_arrow")
                    isActive = (_currentTool == ToolType.Line || _currentTool == ToolType.Arrow);
                else if (btn.Id == "mosaic")
                    isActive = (_currentTool == ToolType.Mosaic || _currentTool == ToolType.Blur);
                else
                    isActive = (_currentTool != ToolType.None && btn.Tool == _currentTool);

                bool isHover = (btn == _hoveredBtn && !btn.IsDisabled);

                if (isActive || isHover)
                {
                    Color bgCol;
                    Color edgeCol;
                    // Snipaste 核心：所有选中工具按钮统一实心亮天蓝底 (#0084FF)！
                    if (isActive)
                    {
                        bgCol = Color.FromArgb(0, 132, 255);
                        edgeCol = Color.FromArgb(0, 108, 214); // 描边加深，制造微内阴影的立体感
                    }
                    else if (btn.Id == "cancel")
                    {
                        bgCol = Color.FromArgb(254, 226, 226);
                        edgeCol = Color.FromArgb(252, 186, 186);
                    }
                    else
                    {
                        bgCol = Color.FromArgb(232, 240, 250); // 悬停底色加强，比原 #F1F5F9 更明确
                        edgeCol = Color.FromArgb(203, 219, 238);
                    }

                    using (GraphicsPath bp = CreateRoundedRectanglePath(btn.Bounds, 5))
                    using (SolidBrush bb = new SolidBrush(bgCol))
                    using (Pen ep = new Pen(edgeCol, 1))
                    {
                        g.FillPath(bb, bp);
                        g.DrawPath(ep, bp);
                    }
                }

                Color iconColor;
                if (btn.IsDisabled)
                {
                    iconColor = Color.FromArgb(203, 213, 225);
                }
                else if (isActive)
                {
                    iconColor = Color.White; // 激活时统一实心天蓝底配纯白图标！
                }
                else if (btn.Id == "cancel" && isHover)
                {
                    iconColor = Color.FromArgb(220, 38, 38);
                }
                else if (isHover)
                {
                    iconColor = Color.FromArgb(17, 24, 39);
                }
                else
                {
                    iconColor = Color.FromArgb(38, 38, 38); // 原生质感炭黑，清晰锐利！
                }

                DrawVectorIcon(g, btn.Id, btn.Bounds, iconColor);

                // 复合工具按钮右下角的实心小三角 (表示"长按/点击可切换子模式")
                if (!btn.IsDisabled && Array.IndexOf(COMPOUND_BTN_IDS, btn.Id) >= 0)
                {
                    DrawCompoundTriangle(g, btn.Bounds, isActive);
                }
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

        /// <summary>
        /// 绘制选区左上角上方的深色尺寸胶囊 "928 × 302 px"，Snipaste 实拍同款位置
        /// </summary>
        private void DrawSizeInfo(Graphics g)
        {
            if (_selSizeLabelBounds.Width <= 0 || string.IsNullOrEmpty(_sizeInfoText)) return;

            using (GraphicsPath p = CreateRoundedRectanglePath(_selSizeLabelBounds, 4))
            using (SolidBrush bg = new SolidBrush(Color.FromArgb(232, 32, 32, 32)))
            using (Font sf = new Font("Segoe UI", 8.5f, FontStyle.Bold))
            using (SolidBrush tb = new SolidBrush(Color.White))
            using (StringFormat fmt = new StringFormat())
            {
                fmt.Alignment = StringAlignment.Center;
                fmt.LineAlignment = StringAlignment.Center;
                g.FillPath(bg, p);
                g.DrawString(_sizeInfoText, sf, tb, _selSizeLabelBounds, fmt);
            }
        }

        /// <summary>
        /// 复合工具按钮右下角的实心小三角，提示该按钮可切换子模式 (矩形/椭圆、折线/箭头、马赛克/模糊)
        /// </summary>
        private void DrawCompoundTriangle(Graphics g, Rectangle bounds, bool isActive)
        {
            const int t = 4; // 三角边长
            int right = bounds.Right - 4;
            int bottom = bounds.Bottom - 4;

            Color c = isActive ? Color.FromArgb(220, 255, 255, 255) : Color.FromArgb(148, 163, 184);
            using (SolidBrush tb = new SolidBrush(c))
            {
                g.FillPolygon(tb, new Point[]
                {
                    new Point(right, bottom),
                    new Point(right - t, bottom),
                    new Point(right, bottom - t)
                });
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
                bool isHover = (sub == _hoveredSub);

                if (sub.Id.StartsWith("sub_sep"))
                {
                    // 属性栏内部细竖分隔线
                    using (Pen sp = new Pen(Color.FromArgb(226, 232, 240), 1))
                    {
                        int mx = sub.Bounds.Left + sub.Bounds.Width / 2;
                        g.DrawLine(sp, mx, sub.Bounds.Top + 4, mx, sub.Bounds.Bottom - 4);
                    }
                    continue;
                }

                if (sub.Id == "text_bold")
                {
                    // 粗体按钮 B (激活时实心亮天蓝背景 + 纯白大写粗体 B，完全对齐用户实截图！)
                    bool isBoldActive = _isFontBold;
                    if (isBoldActive)
                    {
                        using (GraphicsPath bp = CreateRoundedRectanglePath(sub.Bounds, 3))
                        using (SolidBrush bb = new SolidBrush(Color.FromArgb(0, 132, 255)))
                        {
                            g.FillPath(bb, bp);
                        }
                    }
                    else if (isHover)
                    {
                        using (GraphicsPath bp = CreateRoundedRectanglePath(sub.Bounds, 3))
                        using (SolidBrush bb = new SolidBrush(Color.FromArgb(241, 245, 249)))
                        {
                            g.FillPath(bb, bp);
                        }
                    }

                    Color textColor = isBoldActive ? Color.White : Color.FromArgb(51, 65, 85);
                    using (Font bf = new Font("Segoe UI", 10.5f, FontStyle.Bold))
                    using (SolidBrush tb = new SolidBrush(textColor))
                    {
                        SizeF bsz = g.MeasureString("B", bf);
                        g.DrawString("B", bf, tb, sub.Bounds.Left + (sub.Bounds.Width - bsz.Width) / 2f + 1, sub.Bounds.Top + (sub.Bounds.Height - bsz.Height) / 2f);
                    }
                }
                else if (sub.Id == "text_italic")
                {
                    // 斜体按钮 I (激活时实心亮天蓝背景 + 纯白字)
                    bool isItalicActive = _isFontItalic;
                    if (isItalicActive)
                    {
                        using (GraphicsPath bp = CreateRoundedRectanglePath(sub.Bounds, 3))
                        using (SolidBrush bb = new SolidBrush(Color.FromArgb(0, 132, 255)))
                        {
                            g.FillPath(bb, bp);
                        }
                    }
                    else if (isHover)
                    {
                        using (GraphicsPath bp = CreateRoundedRectanglePath(sub.Bounds, 3))
                        using (SolidBrush bb = new SolidBrush(Color.FromArgb(241, 245, 249)))
                        {
                            g.FillPath(bb, bp);
                        }
                    }

                    Color textColor = isItalicActive ? Color.White : Color.FromArgb(51, 65, 85);
                    using (Font ift = new Font("Georgia", 10.5f, FontStyle.Italic | FontStyle.Bold))
                    using (SolidBrush tb = new SolidBrush(textColor))
                    {
                        SizeF isz = g.MeasureString("I", ift);
                        g.DrawString("I", ift, tb, sub.Bounds.Left + (sub.Bounds.Width - isz.Width) / 2f, sub.Bounds.Top + (sub.Bounds.Height - isz.Height) / 2f);
                    }
                }
                else if (sub.Id == "text_bg")
                {
                    // 轮廓/底衬按钮 A (激活时实心亮天蓝背景 + 纯白字)
                    bool isBgActive = _hasTextBackground;
                    if (isBgActive)
                    {
                        using (GraphicsPath bp = CreateRoundedRectanglePath(sub.Bounds, 3))
                        using (SolidBrush bb = new SolidBrush(Color.FromArgb(0, 132, 255)))
                        {
                            g.FillPath(bb, bp);
                        }
                    }
                    else if (isHover)
                    {
                        using (GraphicsPath bp = CreateRoundedRectanglePath(sub.Bounds, 3))
                        using (SolidBrush bb = new SolidBrush(Color.FromArgb(241, 245, 249)))
                        {
                            g.FillPath(bb, bp);
                        }
                    }

                    Color textColor = isBgActive ? Color.White : Color.FromArgb(51, 65, 85);
                    using (Font af = new Font("Segoe UI", 10f, FontStyle.Regular))
                    using (SolidBrush tb = new SolidBrush(textColor))
                    {
                        SizeF asz = g.MeasureString("A", af);
                        g.DrawString("A", af, tb, sub.Bounds.Left + (sub.Bounds.Width - asz.Width) / 2f, sub.Bounds.Top + (sub.Bounds.Height - asz.Height) / 2f);
                    }
                }
                else if (sub.Id == "text_font_family")
                {
                    // 字体选择下拉框 [ Microsoft YaHei UI ∨ ]
                    using (GraphicsPath bp = CreateRoundedRectanglePath(sub.Bounds, 3))
                    using (SolidBrush bb = new SolidBrush(isHover ? Color.FromArgb(248, 250, 252) : Color.White))
                    using (Pen bpPen = new Pen(Color.FromArgb(203, 213, 225), 1))
                    {
                        g.FillPath(bb, bp);
                        g.DrawPath(bpPen, bp);
                    }

                    using (Font ff = new Font("Microsoft YaHei", 8.5f))
                    using (SolidBrush tb = new SolidBrush(Color.FromArgb(30, 41, 59)))
                    {
                        g.DrawString(sub.Text, ff, tb, sub.Bounds.Left + 6, sub.Bounds.Top + 5);
                    }

                    // 极简矢量下拉三角折角 ∨
                    int arrX = sub.Bounds.Right - 11;
                    int arrY = sub.Bounds.Top + sub.Bounds.Height / 2;
                    using (Pen arrPen = new Pen(Color.FromArgb(100, 116, 139), 1.5f))
                    {
                        arrPen.StartCap = LineCap.Round;
                        arrPen.EndCap = LineCap.Round;
                        g.DrawLine(arrPen, arrX - 3, arrY - 2, arrX, arrY + 1);
                        g.DrawLine(arrPen, arrX, arrY + 1, arrX + 3, arrY - 2);
                    }
                }
                else if (sub.Id == "text_font_size")
                {
                    // 字号选择输入框 [ 16.0 ∨ ]
                    using (GraphicsPath bp = CreateRoundedRectanglePath(sub.Bounds, 3))
                    using (SolidBrush bb = new SolidBrush(isHover ? Color.FromArgb(248, 250, 252) : Color.White))
                    using (Pen bpPen = new Pen(Color.FromArgb(203, 213, 225), 1))
                    {
                        g.FillPath(bb, bp);
                        g.DrawPath(bpPen, bp);
                    }

                    using (Font ff = new Font("Segoe UI", 9f))
                    using (SolidBrush tb = new SolidBrush(Color.FromArgb(30, 41, 59)))
                    {
                        g.DrawString(sub.Text, ff, tb, sub.Bounds.Left + 6, sub.Bounds.Top + 4);
                    }

                    // 极简矢量下拉三角折角 ∨
                    int arrX = sub.Bounds.Right - 9;
                    int arrY = sub.Bounds.Top + sub.Bounds.Height / 2;
                    using (Pen arrPen = new Pen(Color.FromArgb(100, 116, 139), 1.5f))
                    {
                        arrPen.StartCap = LineCap.Round;
                        arrPen.EndCap = LineCap.Round;
                        g.DrawLine(arrPen, arrX - 3, arrY - 2, arrX, arrY + 1);
                        g.DrawLine(arrPen, arrX, arrY + 1, arrX + 3, arrY - 2);
                    }
                }
                else if (sub.Id == "main_color_swatch")
                {
                    // 主颜色大方块 [ █ ◢ ] (带彩虹调色盘三角标)
                    using (SolidBrush cb = new SolidBrush(sub.ColorValue))
                    {
                        g.FillRectangle(cb, sub.Bounds);
                    }
                    using (Pen cbp = new Pen(Color.FromArgb(203, 213, 225), 1))
                    {
                        g.DrawRectangle(cbp, sub.Bounds);
                    }

                    // 右下角彩虹调色板小三角标
                    Point p1 = new Point(sub.Bounds.Right - 6, sub.Bounds.Bottom);
                    Point p2 = new Point(sub.Bounds.Right, sub.Bounds.Bottom - 6);
                    Point p3 = new Point(sub.Bounds.Right, sub.Bounds.Bottom);
                    using (SolidBrush rb = new SolidBrush(Color.FromArgb(245, 158, 11)))
                    {
                        g.FillPolygon(rb, new Point[] { p1, p2, p3 });
                    }
                }
                else if (sub.IsColor)
                {
                    // 24 色小方块
                    using (SolidBrush cb = new SolidBrush(sub.ColorValue))
                    {
                        g.FillRectangle(cb, sub.Bounds);
                    }

                    using (Pen cp = new Pen(Color.FromArgb(210, 220, 230), 1))
                    {
                        g.DrawRectangle(cp, sub.Bounds);
                    }

                    if (_activeColor == sub.ColorValue)
                    {
                        using (Pen selPen = new Pen(Color.FromArgb(0, 132, 255), 1.5f))
                        {
                            Rectangle ring = new Rectangle(sub.Bounds.X - 1, sub.Bounds.Y - 1, sub.Bounds.Width + 2, sub.Bounds.Height + 2);
                            g.DrawRectangle(selPen, ring);
                        }
                    }
                }
                else if (sub.Id == "param_size")
                {
                    // 主参数胶囊：Snipaste 同款实心亮蓝底 + 纯白数值 (笔宽 / 颗粒度)
                    using (GraphicsPath pp = CreateRoundedRectanglePath(sub.Bounds, 3))
                    using (SolidBrush pb = new SolidBrush(Color.FromArgb(0, 132, 255)))
                    {
                        g.FillPath(pb, pp);
                    }

                    using (Font pf = new Font("Segoe UI", 9.5f, FontStyle.Bold))
                    using (SolidBrush ptb = new SolidBrush(Color.White))
                    using (StringFormat pfmt = new StringFormat())
                    {
                        pfmt.Alignment = StringAlignment.Center;
                        pfmt.LineAlignment = StringAlignment.Center;
                        g.DrawString(sub.Text, pf, ptb, sub.Bounds, pfmt);
                    }

                    // 参数已被改动：右上角点一颗带白圈的红点（白圈是为了压在蓝色胶囊上也有足够对比）
                    if (sub.ShowBadge)
                    {
                        const int dotR = 4;
                        int cx = sub.Bounds.Right - dotR;
                        int cy = sub.Bounds.Top + dotR;
                        using (SolidBrush ring = new SolidBrush(Color.White))
                        {
                            g.FillEllipse(ring, cx - dotR - 1, cy - dotR - 1, (dotR + 1) * 2, (dotR + 1) * 2);
                        }
                        using (SolidBrush dot = new SolidBrush(Color.FromArgb(239, 68, 68)))
                        {
                            g.FillEllipse(dot, cx - dotR, cy - dotR, dotR * 2, dotR * 2);
                        }
                    }
                }
                else if (sub.Id == "mosaic_mode_toggle")
                {
                    // 马赛克 / 高斯模糊 图标切换（模糊态染实心蓝）
                    bool isBlur = sub.IsActiveFlag;
                    if (isBlur || isHover)
                    {
                        Color hbg = isBlur ? Color.FromArgb(0, 132, 255) : Color.FromArgb(241, 245, 249);
                        using (GraphicsPath mp = CreateRoundedRectanglePath(sub.Bounds, 3))
                        using (SolidBrush mb = new SolidBrush(hbg))
                        {
                            g.FillPath(mb, mp);
                        }
                    }
                    DrawMosaicModeIcon(g, sub.Bounds, isBlur ? Color.White : Color.FromArgb(51, 65, 85), isBlur);
                }
                else if (sub.Id == "mosaic_slider")
                {
                    // 强度滑杆：蓝色轨道 + 白色方块滑块（Snipaste 实拍同款）
                    int trackY = sub.Bounds.Top + sub.Bounds.Height / 2;

                    using (Pen tp = new Pen(Color.FromArgb(191, 219, 254), 4f))
                    {
                        tp.StartCap = LineCap.Round;
                        tp.EndCap = LineCap.Round;
                        g.DrawLine(tp, sub.Bounds.Left + 2, trackY, sub.Bounds.Right - 2, trackY);
                    }

                    float ratio = (_mosaicBlockSize - MOSAIC_MIN) / (float)(MOSAIC_MAX - MOSAIC_MIN);
                    if (ratio < 0f) ratio = 0f;
                    if (ratio > 1f) ratio = 1f;
                    int knobX = sub.Bounds.Left + 2 + (int)Math.Round((sub.Bounds.Width - 4) * ratio);

                    using (Pen tp2 = new Pen(Color.FromArgb(0, 132, 255), 4f))
                    {
                        tp2.StartCap = LineCap.Round;
                        tp2.EndCap = LineCap.Round;
                        g.DrawLine(tp2, sub.Bounds.Left + 2, trackY, knobX, trackY);
                    }

                    const int kw = 11;
                    using (SolidBrush kb = new SolidBrush(Color.White))
                    using (Pen kp = new Pen(Color.FromArgb(0, 132, 255), 2f))
                    {
                        g.FillRectangle(kb, knobX - kw / 2, trackY - kw / 2, kw, kw);
                        g.DrawRectangle(kp, knobX - kw / 2, trackY - kw / 2, kw, kw);
                    }
                }
                else if (sub.Id == "mosaic_slider_value")
                {
                    // 滑杆右侧的强度数值
                    using (Font vf = new Font("Segoe UI", 9f, FontStyle.Bold))
                    using (SolidBrush vb = new SolidBrush(Color.FromArgb(51, 65, 85)))
                    using (StringFormat vfmt = new StringFormat())
                    {
                        vfmt.Alignment = StringAlignment.Center;
                        vfmt.LineAlignment = StringAlignment.Center;
                        g.DrawString(sub.Text, vf, vb, sub.Bounds, vfmt);
                    }
                }
                else if (sub.Id == "eraser_preview")
                {
                    // 橡皮擦：透明棋盘格预览，表示"擦成透明"
                    DrawCheckerboardIcon(g, sub.Bounds, 6, Color.FromArgb(71, 85, 105));
                }
                else if (sub.Id == "shape_rect" || sub.Id == "shape_ellipse")
                {
                    // 形状直选按钮：矩形 / 椭圆（Snipaste 是直接点选）
                    bool isShapeActive = sub.IsActiveFlag;
                    if (isShapeActive || isHover)
                    {
                        Color hbg = isShapeActive ? Color.FromArgb(0, 132, 255) : Color.FromArgb(241, 245, 249);
                        using (GraphicsPath sp = CreateRoundedRectanglePath(sub.Bounds, 3))
                        using (SolidBrush sb = new SolidBrush(hbg))
                        {
                            g.FillPath(sb, sp);
                        }
                    }

                    Color sc = isShapeActive ? Color.White : Color.FromArgb(51, 65, 85);
                    using (Pen sp2 = new Pen(sc, 1.6f))
                    {
                        int w = 15, h = 11;
                        int bx = sub.Bounds.Left + (sub.Bounds.Width - w) / 2;
                        int by = sub.Bounds.Top + (sub.Bounds.Height - h) / 2;
                        if (sub.Id == "shape_rect") g.DrawRectangle(sp2, bx, by, w, h);
                        else g.DrawEllipse(sp2, bx, by, w, h);
                    }
                }
                else if (sub.Id == "toggle_fill")
                {
                    // 填充方式：空心 / 实心 / 透明（用棋盘格表达可切换的填充）
                    bool isFillActive = sub.IsActiveFlag;
                    if (isFillActive || isHover)
                    {
                        Color hbg = isFillActive ? Color.FromArgb(0, 132, 255) : Color.FromArgb(241, 245, 249);
                        using (GraphicsPath fp = CreateRoundedRectanglePath(sub.Bounds, 3))
                        using (SolidBrush fb = new SolidBrush(hbg))
                        {
                            g.FillPath(fb, fp);
                        }
                    }
                    DrawFillIcon(g, sub.Bounds, isFillActive ? Color.White : Color.FromArgb(51, 65, 85));
                }
                else if (sub.Id == "toggle_line_arrow")
                {
                    // 线型下拉：预览当前线型 + 右下角小三角
                    if (isHover)
                    {
                        using (GraphicsPath lp = CreateRoundedRectanglePath(sub.Bounds, 3))
                        using (SolidBrush lb = new SolidBrush(Color.FromArgb(241, 245, 249)))
                        {
                            g.FillPath(lb, lp);
                        }
                    }
                    // 按钮上直接预览当前线型：直线工具画一段直线，箭头工具画当前头部样式
                    ArrowHeadStyle? previewStyle = (_currentTool == ToolType.Arrow) ? (ArrowHeadStyle?)_arrowStyle : null;
                    RectangleF glyphBox = new RectangleF(sub.Bounds.Left + 6f, sub.Bounds.Top + 5f, sub.Bounds.Width - 18f, sub.Bounds.Height - 10f);
                    DrawLineStyleGlyph(g, glyphBox, previewStyle, Color.FromArgb(51, 65, 85), 1.8f, _lineDashed);

                    using (SolidBrush ab = new SolidBrush(Color.FromArgb(148, 163, 184)))
                    {
                        int rx = sub.Bounds.Right - 9;
                        int ry = sub.Bounds.Bottom - 9;
                        g.FillPolygon(ab, new Point[]
                        {
                            new Point(rx + 5, ry + 5),
                            new Point(rx, ry + 5),
                            new Point(rx + 5, ry)
                        });
                    }
                }
                else if (sub.Id == "toggle_dash")
                {
                    // 虚实开关：亮起时给个蓝底，图形直接用当前线型的虚线段表达
                    bool isDashActive = sub.IsActiveFlag;
                    if (isDashActive || isHover)
                    {
                        Color dbg = isDashActive ? Color.FromArgb(0, 132, 255) : Color.FromArgb(241, 245, 249);
                        using (GraphicsPath dp = CreateRoundedRectanglePath(sub.Bounds, 3))
                        using (SolidBrush db = new SolidBrush(dbg))
                        {
                            g.FillPath(db, dp);
                        }
                    }
                    // 开关上的图形恒为虚线：它表达的是"这一档能给你什么"，
                    // 当前是否启用由底色表达，两者互不干扰
                    RectangleF dashBox = new RectangleF(sub.Bounds.Left + 6f, sub.Bounds.Top + 7f, sub.Bounds.Width - 12f, sub.Bounds.Height - 14f);
                    DrawLineStyleGlyph(g, dashBox, null, isDashActive ? Color.White : Color.FromArgb(51, 65, 85), 1.8f, true);
                }
                else
                {
                    // 通用小文本按钮 (如空心/实心切换、矩形/椭圆切换、马赛克颗粒度档位)
                    bool isFlagActive = sub.IsActiveFlag;
                    if (isFlagActive)
                    {
                        using (GraphicsPath hp = CreateRoundedRectanglePath(sub.Bounds, 3))
                        using (SolidBrush hb = new SolidBrush(Color.FromArgb(224, 242, 254)))
                        {
                            g.FillPath(hb, hp);
                        }
                    }
                    else if (isHover)
                    {
                        using (GraphicsPath hp = CreateRoundedRectanglePath(sub.Bounds, 3))
                        using (SolidBrush hb = new SolidBrush(Color.FromArgb(241, 245, 249)))
                        {
                            g.FillPath(hb, hp);
                        }
                    }
                    Color flagTextColor = isFlagActive ? Color.FromArgb(2, 132, 199) : Color.FromArgb(51, 65, 85);
                    using (Font tf = new Font("Microsoft YaHei", 9f, isFlagActive ? FontStyle.Bold : FontStyle.Regular))
                    using (SolidBrush tb = new SolidBrush(flagTextColor))
                    {
                        SizeF tsz = g.MeasureString(sub.Text, tf);
                        g.DrawString(sub.Text, tf, tb, sub.Bounds.Left + (sub.Bounds.Width - tsz.Width) / 2f, sub.Bounds.Top + (sub.Bounds.Height - tsz.Height) / 2f);
                    }
                }
            }
        }

        private void DrawVectorIcon(Graphics g, string id, Rectangle r, Color color)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // 所有图标的绘制坐标都以 26px 按钮为基准写成固定偏移，
            // 这里整体缩放 + 平移到按钮中心，按钮尺寸调整时图标自动等比跟随。
            GraphicsState iconState = g.Save();
            float iconScale = r.Height / 26f;
            g.TranslateTransform(r.Left + r.Width / 2f, r.Top + r.Height / 2f);
            g.ScaleTransform(iconScale, iconScale);

            int cx = 0;
            int cy = 0;

            using (Pen p = new Pen(color, 1.8f))
            {
                p.StartCap = LineCap.Round;
                p.EndCap = LineCap.Round;

                switch (id)
                {
                    case "rect_ellipse":
                        // 矩形/椭圆复合图标 (左上方矩形，右下方重叠椭圆，1:1 对齐用户实截图！)
                        g.DrawRectangle(p, cx - 8, cy - 7, 10, 8);
                        g.DrawEllipse(p, cx - 3, cy - 2, 11, 9);
                        break;

                    case "line_arrow":
                        // 折线/箭头复合图标 (带转折的直线且末端为锐角箭头，1:1 对齐用户实截图！)
                        g.DrawLine(p, cx - 8, cy + 5, cx - 3, cy);
                        g.DrawLine(p, cx - 3, cy, cx + 5, cy - 6);
                        // 箭头尖头
                        g.DrawLine(p, cx + 1, cy - 6, cx + 5, cy - 6);
                        g.DrawLine(p, cx + 5, cy - 2, cx + 5, cy - 6);
                        break;

                    case "brush":
                        // 铅笔/画笔 (斜向铅笔，1:1 对齐用户实截图！)
                        g.DrawLine(p, cx - 6, cy + 5, cx + 4, cy - 5);
                        g.DrawLine(p, cx - 4, cy + 6, cx + 6, cy - 4);
                        g.DrawLine(p, cx + 4, cy - 5, cx + 6, cy - 4);
                        g.DrawLine(p, cx - 7, cy + 7, cx - 6, cy + 5);
                        g.DrawLine(p, cx - 7, cy + 7, cx - 4, cy + 6);
                        break;

                    case "marker":
                        // 记号笔/马克笔 (粗斜平头笔尖，1:1 对齐用户实截图！)
                        using (SolidBrush mb = new SolidBrush(color))
                        using (GraphicsPath tipPath = new GraphicsPath())
                        {
                            tipPath.AddPolygon(new Point[] {
                                new Point(cx - 3, cy + 7),
                                new Point(cx - 7, cy + 3),
                                new Point(cx - 2, cy - 2),
                                new Point(cx + 2, cy + 2)
                            });
                            g.FillPath(mb, tipPath);
                        }
                        g.DrawLine(p, cx - 1, cy + 1, cx + 6, cy - 6);
                        g.DrawLine(p, cx + 2, cy + 4, cx + 9, cy - 3);
                        g.DrawLine(p, cx + 6, cy - 6, cx + 9, cy - 3);
                        break;

                    case "mosaic":
                        // 马赛克/模糊 (黑白相间 2x2 网格，1:1 对齐用户实截图！)
                        using (SolidBrush b = new SolidBrush(color))
                        {
                            g.FillRectangle(b, cx - 6, cy - 6, 6, 6);
                            g.FillRectangle(b, cx + 1, cy + 1, 6, 6);
                        }
                        using (Pen bp = new Pen(color, 1f))
                        {
                            g.DrawRectangle(bp, cx - 7, cy - 7, 14, 14);
                        }
                        break;

                    case "text":
                        // 经典衬线大写文字 "T"
                        g.DrawLine(p, cx - 6, cy - 6, cx + 6, cy - 6);
                        g.DrawLine(p, cx, cy - 6, cx, cy + 6);
                        g.DrawLine(p, cx - 3, cy + 6, cx + 3, cy + 6);
                        break;

                    case "number":
                        // 序号标注：带边框的圆 + 内部数字 "1" 竖笔
                        g.DrawEllipse(p, cx - 7, cy - 7, 14, 14);
                        g.DrawLine(p, cx - 1, cy - 4, cx + 1, cy - 5);
                        g.DrawLine(p, cx + 1, cy - 5, cx + 1, cy + 4);
                        break;

                    case "eraser":
                        // 斜向橡皮擦图标 (1:1 对齐用户实截图！)
                        g.DrawLine(p, cx - 6, cy + 2, cx - 2, cy + 6);
                        g.DrawLine(p, cx - 2, cy + 6, cx + 6, cy - 2);
                        g.DrawLine(p, cx + 6, cy - 2, cx + 2, cy - 6);
                        g.DrawLine(p, cx + 2, cy - 6, cx - 6, cy + 2);
                        g.DrawLine(p, cx - 2, cy - 2, cx + 2, cy + 2);
                        break;

                    case "undo":
                        // 撤销 ↶
                        g.DrawArc(p, cx - 7, cy - 5, 13, 11, 0, -210);
                        g.DrawLine(p, cx - 7, cy - 1, cx - 7, cy + 4);
                        g.DrawLine(p, cx - 7, cy - 1, cx - 2, cy - 1);
                        break;

                    case "redo":
                        // 重做 ↷
                        g.DrawArc(p, cx - 6, cy - 5, 13, 11, 180, 210);
                        g.DrawLine(p, cx + 7, cy - 1, cx + 7, cy + 4);
                        g.DrawLine(p, cx + 7, cy - 1, cx + 2, cy - 1);
                        break;

                    case "cancel":
                        // 细叉 ✕
                        g.DrawLine(p, cx - 5, cy - 5, cx + 5, cy + 5);
                        g.DrawLine(p, cx + 5, cy - 5, cx - 5, cy + 5);
                        break;

                    case "pin":
                        // 45度斜向图钉 📌
                        using (SolidBrush pinHead = new SolidBrush(color))
                        {
                            g.FillEllipse(pinHead, cx + 2, cy - 7, 5, 5);
                        }
                        g.DrawLine(p, cx - 1, cy - 4, cx + 4, cy + 1);
                        g.DrawLine(p, cx - 4, cy - 1, cx + 1, cy + 4);
                        g.DrawLine(p, cx - 2, cy + 2, cx - 6, cy + 6);
                        break;

                    case "save":
                        // 软盘 💾
                        g.DrawRectangle(p, cx - 6, cy - 6, 12, 12);
                        using (SolidBrush b = new SolidBrush(color))
                        {
                            g.FillRectangle(b, cx - 3, cy - 5, 6, 3);
                            g.FillRectangle(b, cx - 3, cy + 1, 6, 3);
                        }
                        break;

                    case "copy":
                        // 双重叠矩形 ⧉ (Snipaste 标志性复制图标，1:1 对齐用户实截图！)
                        g.DrawRectangle(p, cx - 5, cy - 3, 9, 9);
                        g.DrawLine(p, cx - 2, cy - 6, cx + 6, cy - 6);
                        g.DrawLine(p, cx + 6, cy - 6, cx + 6, cy + 2);
                        g.DrawLine(p, cx + 4, cy + 2, cx + 6, cy + 2);
                        g.DrawLine(p, cx - 2, cy - 6, cx - 2, cy - 4);
                        break;

                    case "ai_edit":
                        {
                            // 纯矢量 AI 星芒图标 (✨ Sparkles)：彻底去除汉字，完美对齐 Snipaste 极简图标规范
                            Color aiCol = (color == Color.White) ? Color.White : Color.FromArgb(124, 58, 237);
                            using (SolidBrush sb = new SolidBrush(aiCol))
                            {
                                // 1. 主 AI 菱芒大星 (位于偏左中心)
                                PointF[] mainStar = new PointF[]
                                {
                                    new PointF(cx - 1.5f, cy - 8.5f),
                                    new PointF(cx + 0.6f, cy - 1.6f),
                                    new PointF(cx + 6.6f, cy + 0.5f),
                                    new PointF(cx + 0.6f, cy + 2.6f),
                                    new PointF(cx - 1.5f, cy + 9.5f),
                                    new PointF(cx - 3.6f, cy + 2.6f),
                                    new PointF(cx - 9.6f, cy + 0.5f),
                                    new PointF(cx - 3.6f, cy - 1.6f)
                                };
                                g.FillPolygon(sb, mainStar);

                                // 2. 右上方次级伴生星芒
                                PointF[] subStar = new PointF[]
                                {
                                    new PointF(cx + 5.2f, cy - 8.2f),
                                    new PointF(cx + 6.3f, cy - 5.8f),
                                    new PointF(cx + 8.8f, cy - 4.8f),
                                    new PointF(cx + 6.3f, cy - 3.8f),
                                    new PointF(cx + 5.2f, cy - 1.4f),
                                    new PointF(cx + 4.1f, cy - 3.8f),
                                    new PointF(cx + 1.6f, cy - 4.8f),
                                    new PointF(cx + 4.1f, cy - 5.8f)
                                };
                                g.FillPolygon(sb, subStar);

                                // 3. 左下方灵动小微光点
                                PointF[] tinyStar = new PointF[]
                                {
                                    new PointF(cx - 5.6f, cy + 4.8f),
                                    new PointF(cx - 4.2f, cy + 6.2f),
                                    new PointF(cx - 5.6f, cy + 7.6f),
                                    new PointF(cx - 7.0f, cy + 6.2f)
                                };
                                g.FillPolygon(sb, tinyStar);
                            }
                        }
                        break;

                    case "ocr":
                        using (Font ocrFont = new Font("Microsoft YaHei", 8.5f, FontStyle.Bold))
                        using (SolidBrush ob = new SolidBrush(color))
                        {
                            string ocrLabel = "文";
                            SizeF sz = g.MeasureString(ocrLabel, ocrFont);
                            g.DrawString(ocrLabel, ocrFont, ob, cx - sz.Width / 2 + 1, cy - sz.Height / 2);
                        }
                        using (Pen ocrBoxPen = new Pen(color, 1.2f))
                        {
                            g.DrawRectangle(ocrBoxPen, cx - 11, cy - 10, 21, 20);
                        }
                        break;
                }
            }

            g.Restore(iconState);
        }

        /// <summary>
        /// 马赛克 / 高斯模糊 模式图标：马赛克画 2×2 方块阵（像素化），模糊画同心圆（柔和扩散）
        /// </summary>
        private void DrawMosaicModeIcon(Graphics g, Rectangle b, Color color, bool isBlur)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int cx = b.Left + b.Width / 2;
            int cy = b.Top + b.Height / 2;

            if (isBlur)
            {
                using (Pen bp = new Pen(color, 1.5f))
                {
                    g.DrawEllipse(bp, cx - 3, cy - 3, 6, 6);
                    g.DrawEllipse(bp, cx - 7, cy - 7, 14, 14);
                }
            }
            else
            {
                using (SolidBrush mb = new SolidBrush(color))
                {
                    g.FillRectangle(mb, cx - 7, cy - 7, 6, 6);
                    g.FillRectangle(mb, cx + 1, cy - 7, 6, 6);
                    g.FillRectangle(mb, cx - 7, cy + 1, 6, 6);
                    g.FillRectangle(mb, cx + 1, cy + 1, 6, 6);
                }
            }
        }

        /// <summary>
        /// 透明棋盘格图标：Snipaste 用"灰白小方格"表达透明/无填充状态
        /// </summary>
        private void DrawCheckerboardIcon(Graphics g, Rectangle b, int cell, Color color)
        {
            if (cell < 2) cell = 2;
            int cols = Math.Max(1, b.Width / cell);
            int rows = Math.Max(1, b.Height / cell);
            int ox = b.Left + (b.Width - cols * cell) / 2;
            int oy = b.Top + (b.Height - rows * cell) / 2;

            using (SolidBrush cb = new SolidBrush(color))
            {
                for (int y = 0; y < rows; y++)
                {
                    for (int x = 0; x < cols; x++)
                    {
                        if (((x + y) & 1) == 0) continue;
                        g.FillRectangle(cb, ox + x * cell, oy + y * cell, cell, cell);
                    }
                }
            }

            using (Pen border = new Pen(color, 1f))
            {
                g.DrawRectangle(border, ox, oy, cols * cell - 1, rows * cell - 1);
            }
        }

        /// <summary>
        /// 填充方式图标：方框 + 左下对角实心三角，表达"实心 / 空心"可切换
        /// </summary>
        private void DrawFillIcon(Graphics g, Rectangle b, Color color)
        {
            const int w = 13;
            const int h = 13;
            int bx = b.Left + (b.Width - w) / 2;
            int by = b.Top + (b.Height - h) / 2;

            using (Pen fp = new Pen(color, 1.6f))
            {
                g.DrawRectangle(fp, bx, by, w, h);
            }

            using (SolidBrush fb = new SolidBrush(color))
            {
                g.FillPolygon(fb, new Point[]
                {
                    new Point(bx + 1, by + h - 1),
                    new Point(bx + w - 1, by + h - 1),
                    new Point(bx + 1, by + 1)
                });
            }
        }

        private void DrawColorPickerMagnifier(Graphics g, Point pt)
        {
            int gridCols = 17;
            int gridRows = 11;
            int cellSize = 8;
            int magW = gridCols * cellSize;
            int magH = gridRows * cellSize;

            // 定位公式与 ColorPickerCardBounds 共用，确保绘制范围与局部失效范围严格一致
            Rectangle cardRect = ColorPickerCardBounds(pt);
            int hx = cardRect.X;
            int hy = cardRect.Y;
            int hudW = cardRect.Width;
            int hudH = cardRect.Height;

            DrawCardShadow(g, cardRect, 3);
            using (SolidBrush bg = new SolidBrush(Color.FromArgb(248, 10, 15, 26)))
            using (Pen borderPen = new Pen(Color.FromArgb(100, 148, 163, 184), 1f))
            {
                g.FillRectangle(bg, cardRect);
                g.DrawRectangle(borderPen, cardRect);
            }

            int magX = hx + 4;
            int magY = hy + 4;
            Rectangle magRect = new Rectangle(magX, magY, magW, magH);

            int centerCol = gridCols / 2;
            int centerRow = gridRows / 2;

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

            using (SolidBrush crossBrush = new SolidBrush(Color.FromArgb(50, 59, 130, 246)))
            {
                g.FillRectangle(crossBrush, magX, magY + centerRow * cellSize, magW, cellSize);
                g.FillRectangle(crossBrush, magX + centerCol * cellSize, magY, cellSize, magH);
            }

            Rectangle centerCell = new Rectangle(magX + centerCol * cellSize, magY + centerRow * cellSize, cellSize, cellSize);
            using (Pen wp = new Pen(Color.White, 1.5f))
            using (Pen bp = new Pen(Color.Black, 1f))
            {
                g.DrawRectangle(wp, centerCell);
                g.DrawRectangle(bp, centerCell.X - 1, centerCell.Y - 1, centerCell.Width + 2, centerCell.Height + 2);
            }

            using (Pen magBorder = new Pen(Color.White, 1.2f))
            {
                g.DrawRectangle(magBorder, magRect);
            }

            Color centerColor = GetPixelColorSafe(pt.X, pt.Y);
            int infoY = magY + magH + 8;

            string coordStr = string.Format("({0} , {1})", pt.X, pt.Y);
            using (Font cf = new Font("Segoe UI", 9.5f, FontStyle.Bold))
            using (SolidBrush cw = new SolidBrush(Color.White))
            {
                SizeF csz = g.MeasureString(coordStr, cf);
                g.DrawString(coordStr, cf, cw, hx + (hudW - csz.Width) / 2, infoY);
            }

            infoY += 19;

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

        private void DrawShortcutHelpCard(Graphics g)
        {
            int cardW = 310;
            int cardH = 288;

            int cardX = this.Width - cardW - 24;
            int cardY = 24;

            Rectangle cardRect = new Rectangle(cardX, cardY, cardW, cardH);
            if (cardRect.Contains(_currentMousePos))
            {
                cardX = 24;
                cardRect = new Rectangle(cardX, cardY, cardW, cardH);
            }

            DrawCardShadow(g, cardRect, 4);

            using (SolidBrush bg = new SolidBrush(Color.FromArgb(235, 20, 24, 33)))
            using (Pen borderPen = new Pen(Color.FromArgb(90, 148, 163, 184), 1f))
            {
                g.FillRectangle(bg, cardRect);
                g.DrawRectangle(borderPen, cardRect);
            }

            int lineY = cardY + 12;
            int capH = 18;

            using (Font textFont = new Font("Microsoft YaHei", 8.5f))
            using (SolidBrush textBrush = new SolidBrush(Color.FromArgb(245, 245, 245)))
            using (SolidBrush dimBrush = new SolidBrush(Color.FromArgb(160, 175, 195)))
            {
                int startX = cardX + 12;
                DrawKeyCap(g, "W", startX, lineY, capH);
                DrawKeyCap(g, "A", startX + 22, lineY, capH);
                DrawKeyCap(g, "S", startX + 44, lineY, capH);
                DrawKeyCap(g, "D", startX + 66, lineY, capH);
                g.DrawString("将鼠标指针移动 1 像素", textFont, textBrush, cardX + 115, lineY + 1);
                lineY += 24;

                DrawKeyCap(g, "Tab", cardX + 60, lineY, capH);
                string modeDesc = _detectElementMode ? "整窗检测模式" : "元素/色块识别";
                g.DrawString("切换 " + modeDesc, textFont, textBrush, cardX + 115, lineY + 1);
                lineY += 24;

                DrawKeyCap(g, "Ctrl", cardX + 38, lineY, capH);
                DrawKeyCap(g, "A", cardX + 78, lineY, capH);
                g.DrawString("设置截屏区域为当前全屏", textFont, textBrush, cardX + 115, lineY + 1);
                lineY += 24;

                DrawKeyCap(g, "Space", cardX + 45, lineY, capH);
                g.DrawString("按住空格键拖动平移选区", textFont, textBrush, cardX + 115, lineY + 1);
                lineY += 24;

                DrawKeyCap(g, "右键", cardX + 60, lineY, capH);
                g.DrawString("单击鼠标右键秒退截图", textFont, textBrush, cardX + 115, lineY + 1);
                lineY += 24;

                DrawKeyCap(g, "C", cardX + 78, lineY, capH);
                string colFmt = _isColorHexMode ? "HEX" : "RGB";
                g.DrawString("复制颜色 (Shift切格式: " + colFmt + ")", textFont, textBrush, cardX + 115, lineY + 1);
                lineY += 24;

                DrawKeyCap(g, "F1", cardX + 78, lineY, capH);
                g.DrawString("显示/隐藏此按键指南", textFont, textBrush, cardX + 115, lineY + 1);
                lineY += 24;

                // 框选之后的标注工具快捷体系 (Snipaste 同款键位)
                g.DrawString("框选后：", textFont, dimBrush, startX, lineY + 1);
                DrawKeyCap(g, "R", cardX + 60, lineY, capH);
                DrawKeyCap(g, "E", cardX + 82, lineY, capH);
                DrawKeyCap(g, "L", cardX + 104, lineY, capH);
                DrawKeyCap(g, "A", cardX + 126, lineY, capH);
                g.DrawString("矩形/椭圆/折线/箭头", textFont, textBrush, cardX + 152, lineY + 1);
                lineY += 24;

                DrawKeyCap(g, "P", cardX + 60, lineY, capH);
                DrawKeyCap(g, "M", cardX + 82, lineY, capH);
                DrawKeyCap(g, "K", cardX + 104, lineY, capH);
                DrawKeyCap(g, "G", cardX + 126, lineY, capH);
                g.DrawString("画笔/荧光笔/马赛克/模糊", textFont, textBrush, cardX + 152, lineY + 1);
                lineY += 24;

                DrawKeyCap(g, "T", cardX + 60, lineY, capH);
                DrawKeyCap(g, "N", cardX + 82, lineY, capH);
                DrawKeyCap(g, "X", cardX + 104, lineY, capH);
                DrawKeyCap(g, "Del", cardX + 126, lineY, capH);
                g.DrawString("文字/序号/橡皮擦/删除", textFont, textBrush, cardX + 152, lineY + 1);
                lineY += 24;

                g.DrawString("滚轮 或 [  ] ：调笔宽 / 字号 / 颗粒度", textFont, dimBrush, startX, lineY + 1);
                lineY += 24;

                g.DrawString("点击图元可选中，拖动四角缩放，Del 删除", textFont, dimBrush, startX, lineY + 1);
            }
        }

        private void DrawKeyCap(Graphics g, string key, int x, int y, int h)
        {
            using (Font capFont = new Font("Segoe UI", 8f, FontStyle.Bold))
            {
                SizeF sz = g.MeasureString(key, capFont);
                int w = Math.Max(18, (int)sz.Width + 6);
                Rectangle r = new Rectangle(x, y, w, h);

                using (SolidBrush fill = new SolidBrush(Color.FromArgb(40, 255, 255, 255)))
                using (Pen border = new Pen(Color.FromArgb(210, 255, 255, 255), 1f))
                using (SolidBrush textBrush = new SolidBrush(Color.White))
                {
                    g.FillRectangle(fill, r);
                    g.DrawRectangle(border, r);
                    g.DrawString(key, capFont, textBrush, r.X + (w - sz.Width) / 2f, r.Y + (h - sz.Height) / 2f);
                }
            }
        }

        private void ClearAnnotations()
        {
            CancelActiveText();
            DeselectAllAnnotations();
            _selectedShape = null;
            foreach (var ann in _annotations) ann.Dispose();
            _annotations.Clear();
            ClearRedoStack();
            _nextNumber = 1;
            if (_tempAnnotation != null)
            {
                _tempAnnotation.Dispose();
                _tempAnnotation = null;
            }
        }

        /// <summary>
        /// 按图元加入顺序重排序号标注的编号（删除/撤销后保持 1..N 连续）
        /// </summary>
        private void RenumberAnnotations()
        {
            int seq = 1;
            foreach (AnnotationItem ann in _annotations)
            {
                NumberAnnotation na = ann as NumberAnnotation;
                if (na == null) continue;
                na.Number = seq;
                seq++;
            }
            _nextNumber = seq;
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

    /// <summary>
    /// Snipaste 经典【钉图 / 贴图】独立桌面置顶浮窗
    /// 包含：置顶停靠、鼠标拖拽移动、滚轮无级缩放、Ctrl+滚轮无级调节透明度、双击关闭/复制、右键菜单与微阴影边框
    /// </summary>
    public class PinForm : Form
    {
        private Bitmap _pinImage;
        private Point _dragStart;
        private bool _isDragging = false;
        private float _zoom = 1.0f;

        public PinForm(Bitmap img, Point initialPos)
        {
            // 直接持有调用方移交过来的位图（所有权随之转移），不再二次拷贝
            _pinImage = img;
            this.FormBorderStyle = FormBorderStyle.None;
            this.StartPosition = FormStartPosition.Manual;
            this.Location = initialPos;
            this.Size = _pinImage.Size;
            this.TopMost = true;
            this.ShowInTaskbar = false;
            this.DoubleBuffered = true;
            this.Cursor = Cursors.SizeAll;

            this.MouseDown += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left)
                {
                    _isDragging = true;
                    _dragStart = e.Location;
                }
                else if (e.Button == MouseButtons.Right)
                {
                    ContextMenu menu = new ContextMenu();
                    menu.MenuItems.Add("复制图片 (Ctrl+C)", delegate { Program.TrySetClipboardImage(_pinImage); });
                    menu.MenuItems.Add("保存图片...", delegate
                    {
                        using (SaveFileDialog sfd = new SaveFileDialog())
                        {
                            sfd.Filter = "PNG 图像|*.png|JPEG 图像|*.jpg";
                            sfd.FileName = "Pin_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png";
                            if (sfd.ShowDialog() == DialogResult.OK)
                            {
                                _pinImage.Save(sfd.FileName);
                            }
                        }
                    });
                    menu.MenuItems.Add("-");
                    menu.MenuItems.Add("关闭贴图 (Esc / 双击)", delegate { this.Close(); });
                    // 右键菜单用的是原生 ContextMenu（手工创建，不受 Form 托管），
                    // 折叠后显式释放，避免反复右键累积 MenuItem 句柄。
                    // 注意旧式 ContextMenu 的关闭事件叫 Collapse，没有 Closed。
                    menu.Collapse += delegate { menu.Dispose(); };
                    menu.Show(this, e.Location);
                }
            };

            this.MouseMove += delegate(object s, MouseEventArgs e)
            {
                if (_isDragging && e.Button == MouseButtons.Left)
                {
                    this.Left += e.X - _dragStart.X;
                    this.Top += e.Y - _dragStart.Y;
                }
            };

            this.MouseUp += delegate { _isDragging = false; };

            this.MouseDoubleClick += delegate
            {
                Program.TrySetClipboardImage(_pinImage);
                this.Close();
            };

            // 鼠标滚轮缩放与 Ctrl+滚轮透明度调节
            this.MouseWheel += delegate(object s, MouseEventArgs e)
            {
                if ((Control.ModifierKeys & Keys.Control) == Keys.Control)
                {
                    // Ctrl + 滚轮：调节透明度 (20% ~ 100%)
                    double delta = (e.Delta > 0) ? 0.05 : -0.05;
                    this.Opacity = Math.Max(0.2, Math.Min(1.0, this.Opacity + delta));
                    return;
                }

                // 普通滚轮：缩放贴图 (0.2x ~ 4.0x)
                float oldZoom = _zoom;
                if (e.Delta > 0) _zoom = Math.Min(4.0f, _zoom + 0.1f);
                else if (e.Delta < 0) _zoom = Math.Max(0.2f, _zoom - 0.1f);

                if (Math.Abs(oldZoom - _zoom) > 0.01f)
                {
                    int nw = Math.Max(20, (int)(_pinImage.Width * _zoom));
                    int nh = Math.Max(20, (int)(_pinImage.Height * _zoom));
                    this.Size = new Size(nw, nh);
                    this.Invalidate();
                }
            };

            this.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape)
                {
                    this.Close();
                }
                else if (e.Control && e.KeyCode == Keys.C)
                {
                    Program.TrySetClipboardImage(_pinImage);
                }
            };
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(_pinImage, new Rectangle(0, 0, this.Width, this.Height));

            using (Pen p = new Pen(Color.FromArgb(180, 14, 165, 233), 1.5f))
            {
                g.DrawRectangle(p, 0, 0, this.Width - 1, this.Height - 1);
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (_pinImage != null)
            {
                _pinImage.Dispose();
                _pinImage = null;
            }
            base.OnFormClosed(e);
        }

        protected override void Dispose(bool disposing)
        {
            // OnFormClosed 之外再兜一层：若窗体被直接 Dispose（未经正常关闭流程），
            // 这里同样要释放贴图位图，避免长期驻留的贴图窗口泄漏 GDI 位图。
            if (disposing && _pinImage != null)
            {
                _pinImage.Dispose();
                _pinImage = null;
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Snipaste / Fluent 风格现代下拉菜单渲染器 (抗锯齿微圆角、清爽天蓝悬浮、纯白卡片式)
    /// </summary>
    public class ModernDropdownRenderer : ToolStripProfessionalRenderer
    {
        public ModernDropdownRenderer() : base(new ModernColorTable()) { }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (e.Item.Selected)
            {
                Rectangle r = new Rectangle(3, 1, e.Item.Width - 6, e.Item.Height - 2);
                using (GraphicsPath path = SnipperForm.CreateRoundedRectanglePath(r, 3))
                using (SolidBrush b = new SolidBrush(Color.FromArgb(224, 242, 254)))
                {
                    e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    e.Graphics.FillPath(b, path);
                }
            }
            else
            {
                base.OnRenderMenuItemBackground(e);
            }
        }
    }

    public class ModernColorTable : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground { get { return Color.White; } }
        public override Color MenuBorder { get { return Color.FromArgb(226, 232, 240); } }
        public override Color MenuItemSelected { get { return Color.FromArgb(224, 242, 254); } }
        public override Color MenuItemSelectedGradientBegin { get { return Color.FromArgb(224, 242, 254); } }
        public override Color MenuItemSelectedGradientEnd { get { return Color.FromArgb(224, 242, 254); } }
        public override Color MenuItemBorder { get { return Color.Transparent; } }
        public override Color ImageMarginGradientBegin { get { return Color.White; } }
        public override Color ImageMarginGradientMiddle { get { return Color.White; } }
        public override Color ImageMarginGradientEnd { get { return Color.White; } }
        public override Color CheckBackground { get { return Color.FromArgb(224, 242, 254); } }
        public override Color CheckSelectedBackground { get { return Color.FromArgb(186, 230, 253); } }
        public override Color ButtonCheckedHighlight { get { return Color.FromArgb(224, 242, 254); } }
        public override Color ButtonCheckedGradientBegin { get { return Color.FromArgb(224, 242, 254); } }
        public override Color ButtonCheckedGradientMiddle { get { return Color.FromArgb(224, 242, 254); } }
        public override Color ButtonCheckedGradientEnd { get { return Color.FromArgb(224, 242, 254); } }
    }
}
