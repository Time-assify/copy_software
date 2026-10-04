using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CopyTool
{
    /// <summary>
    /// DPI 缩放。所有布局坐标都按 96 DPI 的"设计单位"书写，
    /// 在这里统一换算成真实像素；进程启动即声明 DPI 感知，
    /// 避免 125%/150% 缩放下被 Windows 拉伸发虚。
    /// </summary>
    public static class Dpi
    {
        private static float _factor;

        public static float Factor
        {
            get { Ensure(); return _factor; }
        }

        public static void Init() { Ensure(); }

        private static void Ensure()
        {
            if (_factor > 0) return;
            try
            {
                // 必须在创建任何窗口之前调用，越早越好
                SetProcessDPIAware();
            }
            catch { }
            try
            {
                using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
                    _factor = g.DpiX / 96f;
            }
            catch { }
            if (_factor <= 0f) _factor = 1f;
        }

        /// <summary>设计单位（96 DPI 像素）-> 真实像素。</summary>
        public static int S(int v)
        {
            return (int)Math.Round(v * Factor);
        }

        /// <summary>
        /// 把一棵控件树的 Bounds 从设计单位整体缩放到当前 DPI。
        /// 字体不用管：Theme 里的字体在创建时已经按 DPI 缩放过了。
        /// 缩放期间先摘掉锚定再恢复——否则父容器变大时锚定布局会
        /// 把子控件再挪一次，造成双重缩放（实测 RichTextBox 越界 1.2 倍）。
        /// 窗体自身不缩放：它的 ClientSize 在创建时已经按缩放书写。
        /// </summary>
        public static void ScaleTree(Control root)
        {
            if (Math.Abs(Factor - 1f) < 0.01f) return;
            ScaleOne(root);
        }

        private static void ScaleOne(Control c)
        {
            // 先把直接子控件的锚定换成 Top|Left（固定不动）：
            // 注意不能用 None——None 在 WinForms 里是"随父容器成比例移动缩放"，
            // 父容器缩放时子控件会被挪走，随后再缩放一次就双重缩放了。
            List<Control> kids = new List<Control>();
            List<AnchorStyles> saved = new List<AnchorStyles>();
            foreach (Control child in c.Controls)
            {
                kids.Add(child);
                saved.Add(child.Anchor);
                child.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            }

            if (!(c is Form))   // 窗体自身的 ClientSize 在创建时已按缩放书写
                c.SetBounds(S(c.Left), S(c.Top), S(c.Width), S(c.Height));

            for (int i = 0; i < kids.Count; i++) ScaleOne(kids[i]);
            for (int i = 0; i < kids.Count; i++)
                kids[i].Anchor = saved[i];
        }

        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();
    }

    /// <summary>嵌入的背景图（编译时由 build.ps1 用 /res 打进 exe）。</summary>
    internal static class Art
    {
        private const string ResourceName = "B_CopyTool.wallpaper.jpg";
        private static Image _img;

        /// <summary>
        /// 整窗背景模式用的合成位图（壁纸等比铺满 + 白纱 + 页头渐变）。
        /// 只在"透明背景"变体里赋值；普通模式下保持 null，
        /// 卡片/按钮看到 null 就走原来的不透明画法。
        /// </summary>
        public static Image Backdrop;
        public static float BackdropFocusY = 0.38f;   // 竖版壁纸纵向裁剪中心（对准人物面部）
#if BACKDROP
        public static int BackdropVeilAlpha = 178;    // 透明背景版的出厂白纱浓度：0=完全透明，255=看不到图
#else
        public static int BackdropVeilAlpha = 208;
#endif

        /// <summary>
        /// 由主程序注入的"把控件身后的背景画进指定表面"的实现。
        /// 放在委托里是为了不让 UiTheme 反向依赖 MainForm（安装向导/卸载程序
        /// 只编译 UiTheme，不编译 MainForm）。
        /// </summary>
        public static Func<Control, Graphics, bool> BackdropPainter;

        /// <summary>没有资源或解码失败时返回 null，界面要能优雅降级。</summary>
        public static Image Wallpaper
        {
            get
            {
                if (_img == null) _img = Load();
                return _img;
            }
        }

        /// <summary>
        /// 生成整窗背景：壁纸等比铺满（纵向按 FocusY 取景），
        /// 整体罩一层白纱（alpha 208），页头再加一道左浓右淡的白色渐变保证标题可读。
        /// </summary>
        public static void BuildBackdrop(Size client, int headerH)
        {
            Image wallpaper = Wallpaper;
            Bitmap bmp = new Bitmap(Math.Max(1, client.Width), Math.Max(1, client.Height));
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Theme.Page);
                if (wallpaper != null && wallpaper.Width > 0 && wallpaper.Height > 0)
                {
                    double scale = Math.Max((double)bmp.Width / wallpaper.Width, (double)bmp.Height / wallpaper.Height);
                    int dw = (int)Math.Ceiling(wallpaper.Width * scale);
                    int dh = (int)Math.Ceiling(wallpaper.Height * scale);
                    int dx = (bmp.Width - dw) / 2;
                    int dy = (int)Math.Round((bmp.Height - dh) * BackdropFocusY);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(wallpaper, new Rectangle(dx, dy, dw, dh));
                }
                using (SolidBrush veil = new SolidBrush(Color.FromArgb(Math.Max(0, Math.Min(255, BackdropVeilAlpha)), 255, 255, 255)))
                    g.FillRectangle(veil, 0, 0, bmp.Width, bmp.Height);
                using (LinearGradientBrush header = new LinearGradientBrush(
                    new Rectangle(0, 0, Math.Max(2, bmp.Width), Math.Max(2, headerH)),
                    Color.FromArgb(252, 255, 255, 255), Color.FromArgb(110, 255, 255, 255),
                    LinearGradientMode.Horizontal))
                    g.FillRectangle(header, 0, 0, bmp.Width, headerH);
            }
            if (Backdrop != null) Backdrop.Dispose();
            Backdrop = bmp;
        }

        /// <summary>
        /// 控件把自己身后那块背景画到自己的表面（WinForms 没有真正的控件透明，
        /// 用"自绘父背景"的方式模拟；只对直接放在窗体上的控件成立）。
        /// 返回是否走了这条路径。
        /// </summary>
        public static bool PaintBackdropRegion(Control c, Graphics g)
        {
            if (Backdrop == null || BackdropPainter == null) return false;
            return BackdropPainter(c, g);
        }

        private static Image Load()
        {
            try
            {
                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName))
                {
                    if (s == null) return null;
                    byte[] b = new byte[s.Length];
                    s.Read(b, 0, b.Length);
                    // MemoryStream 的生命周期必须比 Image 长，这里交给 Image 自己持有
                    return Image.FromStream(new MemoryStream(b));
                }
            }
            catch { return null; }
        }
    }

    /// <summary>
    /// 配色与字体。整体走"浅色卡片 + 蓝色强调"的现代扁平风格。
    /// 全部自绘，不依赖任何第三方库。
    /// </summary>
    public static class Theme
    {
        public static readonly Color Page = Color.FromArgb(0xF4, 0xF5, 0xF7);   // 页面底色
        public static readonly Color Card = Color.White;                        // 卡片底色
        public static readonly Color CardBorder = Color.FromArgb(0xE3, 0xE6, 0xEA);
        public static readonly Color Shadow = Color.FromArgb(0xE9, 0xEA, 0xED);
        public static readonly Color HeaderLine = Color.FromArgb(0xE8, 0xEA, 0xEE);
        public static readonly Color Track = Color.FromArgb(0xE8, 0xEA, 0xEE);   // 进度条轨道

        public static readonly Color Text = Color.FromArgb(0x1F, 0x23, 0x28);
        public static readonly Color TextMuted = Color.FromArgb(0x6B, 0x72, 0x80);
        public static readonly Color TextFaint = Color.FromArgb(0x9C, 0xA3, 0xAF);

        public static readonly Color Accent = Color.FromArgb(0x2F, 0x6F, 0xEB);
        public static readonly Color AccentLo = Color.FromArgb(0x5B, 0x8D, 0xF5);
        public static readonly Color AccentHot = Color.FromArgb(0x28, 0x62, 0xD6);
        public static readonly Color AccentDown = Color.FromArgb(0x21, 0x53, 0xB8);
        public static readonly Color AccentOff = Color.FromArgb(0xB8, 0xCB, 0xF0);

        public static readonly Color Border = Color.FromArgb(0xD5, 0xD9, 0xE0);
        public static readonly Color BorderHot = Color.FromArgb(0xA8, 0xB2, 0xC0);
        public static readonly Color BtnHover = Color.FromArgb(0xF2, 0xF4, 0xF6);
        public static readonly Color BtnDown = Color.FromArgb(0xE7, 0xEA, 0xEE);
        public static readonly Color InputBorder = Color.FromArgb(0xD5, 0xD9, 0xE0);

        public static readonly Color LogBg = Color.FromArgb(0x1D, 0x21, 0x28);        public static readonly Color LogBorder = Color.FromArgb(0x2C, 0x31, 0x3A);
        public static readonly Color LogText = Color.FromArgb(0xC9, 0xCF, 0xD8);
        public static readonly Color LogDim = Color.FromArgb(0x84, 0x8C, 0x99);
        public static readonly Color LogOk = Color.FromArgb(0x5A, 0xD1, 0x9A);
        public static readonly Color LogErr = Color.FromArgb(0xF8, 0x7B, 0x7B);
        public static readonly Color LogHead = Color.FromArgb(0x8A, 0xB4, 0xFF);

        public static readonly Color Ok = Color.FromArgb(0x1E, 0x9E, 0x66);
        public static readonly Color Warn = Color.FromArgb(0xC4, 0x7D, 0x12);
        public static readonly Color Err = Color.FromArgb(0xD1, 0x3B, 0x3B);

        public static readonly Font FontBase = MakeFont("Microsoft YaHei UI", 9.5F, FontStyle.Regular);
        public static readonly Font FontBold = MakeFont("Microsoft YaHei UI", 9.5F, FontStyle.Bold);
        public static readonly Font FontTitle = MakeFont("Microsoft YaHei UI", 15F, FontStyle.Bold);
        public static readonly Font FontSub = MakeFont("Microsoft YaHei UI", 8.5F, FontStyle.Regular);
        public static readonly Font FontCard = MakeFont("Microsoft YaHei UI", 10F, FontStyle.Bold);
        public static readonly Font FontBtn = MakeFont("Microsoft YaHei UI", 9.5F, FontStyle.Regular);
        public static readonly Font FontBtnBig = MakeFont("Microsoft YaHei UI", 10F, FontStyle.Bold);
        public static readonly Font FontMono = MakeFont("Consolas", 9F, FontStyle.Regular);
        public static readonly Font FontSmall = MakeFont("Microsoft YaHei UI", 8.5F, FontStyle.Regular);

        /// <summary>按当前 DPI 缩放字号后建字体。</summary>
        public static Font MakeFont(string family, float size, FontStyle style)
        {
            size = size * Dpi.Factor;
            try
            {
                return new Font(family, size, style);
            }
            catch
            {
                return new Font(FontFamily.GenericSansSerif, size, style);
            }
        }

        /// <summary>圆角矩形路径。</summary>
        public static GraphicsPath Round(Rectangle r, int radius)
        {
            GraphicsPath p = new GraphicsPath();
            if (r.Width <= 0 || r.Height <= 0) return p;
            int d = radius * 2;
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            if (d <= 2)
            {
                p.AddRectangle(r);
                return p;
            }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        /// <summary>设置控件的双缓冲 + 自绘标志。</summary>
        public static void EnableDoubleBuffer(Control c)
        {
            typeof(Control).GetProperty("DoubleBuffered",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .SetValue(c, true, null);
        }
    }

    // ==================================================================
    // 卡片面板：白色圆角 + 细边框 + 极淡投影 + 左侧强调条标题
    // ==================================================================
    public class CardPanel : Panel
    {
        public Color Fill = Theme.Card;
        public Color BorderColor = Theme.CardBorder;
        public int CornerRadius = 10;
        public string Title = "";
        public Color TitleColor = Theme.Text;
        public bool ShowAccentBar = true;
        public bool ShowTitle = true;

        public CardPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Card;
        }

        /// <summary>圆角外面露出来的颜色，也就是卡片所在容器的底色。</summary>
        private Color OuterColor()
        {
            if (Parent != null) return Parent.BackColor;
            return Theme.Page;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (!Art.PaintBackdropRegion(this, g))
                g.Clear(OuterColor());

            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            if (r.Width <= 2 || r.Height <= 2) { base.OnPaint(e); return; }

            int radius = Dpi.S(CornerRadius);

            // 极淡的投影（往下偏移 2px）；透出背景时用半透明投影，盖不住底下的图
            Rectangle shadowRect = new Rectangle(Dpi.S(1), Dpi.S(2), Width - Dpi.S(2), Height - Dpi.S(1));
            using (GraphicsPath sp = Theme.Round(shadowRect, radius))
            using (SolidBrush sb = new SolidBrush(Art.Backdrop != null
                ? Color.FromArgb(60, Theme.Shadow) : Theme.Shadow))
                g.FillPath(sb, sp);

            using (GraphicsPath p = Theme.Round(r, radius))
            {
                using (SolidBrush b = new SolidBrush(Fill)) g.FillPath(b, p);
                using (Pen pen = new Pen(BorderColor)) g.DrawPath(pen, p);
            }

            if (ShowTitle && Title.Length > 0)
            {
                if (ShowAccentBar)
                {
                    using (GraphicsPath ap = Theme.Round(new Rectangle(Dpi.S(20), Dpi.S(15), Dpi.S(3), Dpi.S(15)), Dpi.S(2)))
                    using (SolidBrush ab = new SolidBrush(Theme.Accent))
                        g.FillPath(ab, ap);
                }
                int tx = ShowAccentBar ? Dpi.S(31) : Dpi.S(20);
                TextRenderer.DrawText(g, Title, Theme.FontCard,
                    new Rectangle(tx, Dpi.S(12), Width - tx - Dpi.S(20), Dpi.S(22)), TitleColor,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
            base.OnPaint(e);
        }
    }

    // ==================================================================
    // 立绘卡片：右侧的背景图展示区。等比填充裁剪（保完整人物），
    // 水平裁剪中心可调，圆角裁切，图片缺失时优雅降级为占位。
    // ==================================================================
    public class ArtPanel : Control
    {
        /// <summary>水平裁剪中心（0=最左，1=最右），用来把人物放在构图中心。</summary>
        public float FocusX = 0.46f;

        public ArtPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Page;
        }

        private Color OuterColor()
        {
            if (Parent != null) return Parent.BackColor;
            return Theme.Page;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(OuterColor());

            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            if (r.Width <= 2 || r.Height <= 2) { base.OnPaint(e); return; }
            int radius = Dpi.S(10);

            // 极淡投影，和其它卡片一致
            using (GraphicsPath sp = Theme.Round(new Rectangle(Dpi.S(1), Dpi.S(2), Width - Dpi.S(2), Height - Dpi.S(1)), radius))
            using (SolidBrush sb = new SolidBrush(Theme.Shadow))
                g.FillPath(sb, sp);

            using (GraphicsPath p = Theme.Round(r, radius))
            {
                Image img = Art.Wallpaper;
                if (img != null && img.Width > 0 && img.Height > 0)
                {
                    // 等比填充：短边贴满，长边裁掉。竖版图 -> 保持人物完整高度，只裁两侧
                    double scale = Math.Max((double)Width / img.Width, (double)Height / img.Height);
                    int dw = (int)Math.Ceiling(img.Width * scale);
                    int dh = (int)Math.Ceiling(img.Height * scale);
                    int dx = (int)Math.Round((Width - dw) * FocusX);
                    int dy = (Height - dh) / 2;

                    g.SetClip(p);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(img, new Rectangle(dx, dy, dw, dh));
                    g.ResetClip();
                }
                else
                {
                    using (SolidBrush b = new SolidBrush(Theme.Card)) g.FillPath(b, p);
                    TextRenderer.DrawText(g, "背景图未找到", Theme.FontSmall, r, Theme.TextFaint,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
                using (Pen pen = new Pen(Theme.CardBorder)) g.DrawPath(pen, p);
            }
            base.OnPaint(e);
        }
    }

    // ==================================================================
    // 扁平按钮：主要（实心强调色）/ 次要（白底细边）
    // ==================================================================
    public enum BtnKind { Primary, Secondary }

    public class FlatButton : Button
    {
        public BtnKind Kind = BtnKind.Secondary;
        private bool _hover;
        private bool _down;

        public FlatButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Font = Theme.FontBtn;
            Cursor = Cursors.Hand;
            UseVisualStyleBackColor = false;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs mevent) { _down = true; Invalidate(); base.OnMouseDown(mevent); }
        protected override void OnMouseUp(MouseEventArgs mevent) { _down = false; Invalidate(); base.OnMouseUp(mevent); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (!Art.PaintBackdropRegion(this, g))
                g.Clear(Parent != null ? Parent.BackColor : Theme.Page);

            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            Color fill, border, text;

            if (Kind == BtnKind.Primary)
            {
                if (!Enabled) fill = Theme.AccentOff;
                else if (_down) fill = Theme.AccentDown;
                else if (_hover) fill = Theme.AccentHot;
                else fill = Theme.Accent;
                border = fill;
                text = Color.White;
            }
            else
            {
                if (!Enabled) { fill = Theme.Card; border = Theme.CardBorder; text = Theme.TextFaint; }
                else
                {
                    fill = _down ? Theme.BtnDown : (_hover ? Theme.BtnHover : Theme.Card);
                    border = _hover ? Theme.BorderHot : Theme.Border;
                    text = Theme.Text;
                }
            }

            using (GraphicsPath p = Theme.Round(r, Dpi.S(8)))
            {
                using (SolidBrush b = new SolidBrush(fill)) g.FillPath(b, p);
                using (Pen pen = new Pen(border)) g.DrawPath(pen, p);
                if (Focused && Enabled)
                {
                    using (Pen fp = new Pen(Kind == BtnKind.Primary ? Color.FromArgb(0x9E, 0xC0, 0xFF) : Theme.Accent))
                    {
                        fp.DashStyle = DashStyle.Dot;
                        Rectangle fr = new Rectangle(Dpi.S(3), Dpi.S(3), Width - Dpi.S(7), Height - Dpi.S(7));
                        using (GraphicsPath fpth = Theme.Round(fr, Dpi.S(6))) g.DrawPath(fp, fpth);
                    }
                }
            }

            TextRenderer.DrawText(g, Text, Font, new Rectangle(0, 0, Width, Height), text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }

    // ==================================================================
    // 扁平进度条：圆角轨道 + 渐变填充 + 居中百分比
    // ==================================================================
    public class FlatProgressBar : Control
    {
        private int _percent;
        private string _caption = "";
        private bool _busy;

        public FlatProgressBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Page;
            Font = Theme.FontSmall;
        }

        public int Percent { get { return _percent; } }

        public void Reset()
        {
            _percent = 0;
            _caption = "";
            _busy = false;
            Invalidate();
        }

        public void SetPercent(int pct)
        {
            if (pct < 0) pct = 0;
            if (pct > 100) pct = 100;
            _busy = false;
            if (pct == _percent && _caption.Length == 0) return;
            _percent = pct;
            _caption = pct + "%";
            Invalidate();
        }

        /// <summary>总量未知时的来回滚动动画表现（这里退化为一段固定宽度）。</summary>
        public void SetBusy(string caption)
        {
            _busy = true;
            _caption = caption;
            Invalidate();
        }

        public void Complete()
        {
            _busy = false;
            _percent = 100;
            _caption = "100%";
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (!Art.PaintBackdropRegion(this, g))
                g.Clear(Parent != null ? Parent.BackColor : Theme.Page);

            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            int radius = Height / 2;

            using (GraphicsPath p = Theme.Round(r, radius))
            using (SolidBrush b = new SolidBrush(Theme.Track))
                g.FillPath(b, p);

            int w = 0;
            if (_busy)
            {
                w = Width / 4;
                if (w < Dpi.S(40)) w = Dpi.S(40);
            }
            else if (_percent > 0)
            {
                w = (int)((Width - 1) * (_percent / 100.0));
            }

            if (w > 0)
            {
                if (w < Height) w = Height;
                Rectangle fr = new Rectangle(0, 0, w, Height - 1);
                using (GraphicsPath fp = Theme.Round(fr, radius))
                {
                    if (Width > 4)
                    {
                        using (LinearGradientBrush lb = new LinearGradientBrush(
                            new Rectangle(0, 0, Math.Max(2, Width - 1), Math.Max(2, Height - 1)),
                            Theme.Accent, Theme.AccentLo, LinearGradientMode.Horizontal))
                            g.FillPath(lb, fp);
                    }
                    else
                    {
                        using (SolidBrush sb = new SolidBrush(Theme.Accent)) g.FillPath(sb, fp);
                    }
                }
            }

            if (_caption.Length > 0)
            {
                bool overFill = (Width / 2) <= w && w > 0;
                Color tc = overFill ? Color.White : Theme.TextMuted;
                TextRenderer.DrawText(g, _caption, Font, new Rectangle(0, 0, Width, Height), tc,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }
    }
}
