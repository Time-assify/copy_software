using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace CopyTool
{
    public class MainForm : Form
    {
        // ---- 布局设计单位（96 DPI）。窗口分左右两栏：左边功能卡片，右边立绘。 ----
        private const int DesignW = 1100;
        private const int DesignH = 860;
        private const int HeaderH = 150;     // 页头高度
        private const int HeaderW = 796;     // 页头只铺到左栏右侧，右边留给立绘卡片
        private const int LeftColW = 780;    // 左栏卡片宽度
        private const int ArtW = 272;        // 右侧立绘卡片宽度

        // ---- 控件 ----
        private RadioButton rbFile;
        private RadioButton rbFolder;
        private RadioButton rbVerify;
        private TextBox txtSource;
        private TextBox txtDest;
        private FlatButton btnBrowseSource;
        private FlatButton btnBrowseDest;
        private Label lblPathHint;
        private CheckBox chkMirror;
        private CheckBox chkVerify;
        private CheckBox chkDryRun;
        private CheckBox chkBigFile;
        private TextBox txtXD;
        private TextBox txtXF;
        private NumericUpDown numThreads;
        private FlatButton btnStart;
        private FlatButton btnStop;
        private FlatButton btnOpenDest;
        private FlatButton btnCopyCmd;
        private FlatButton btnSaveLog;
        private FlatProgressBar bar;
        private Label lblStatus;
        private Label lblCount;
        private Label lblSpeed;
        private RichTextBox txtLog;
        private ArtPanel art;
        private CardPanel cardPath;
        private CardPanel cardOpt;
        private CardPanel cardLog;

        // ---- 状态 ----
        private System.Windows.Forms.Timer uiTimer;
        private readonly StringBuilder pendingLog = new StringBuilder();
        private volatile bool running;
        private volatile bool cancelFlag;
        private RobocopyRunner runner;
        private CopyRequest request;
        private SourceScan scan;
        private RunResult lastResult;
        private ProgressInfo lastProgress;
        private DateTime startedAt;
        private bool verifyPhase;
        private bool reported;          // VerifyOnly 在 Worker 里自己汇报，finally 就不再走通用 Report
        private string lastLogPath;
        private DateTime verifyStartedAt;

#if BACKDROP
        /// <summary>
        /// 透明背景版（build.ps1 用 /define:BACKDROP 编译出的独立 exe）：
        /// 壁纸铺满整个窗口、卡片半透明，没有右侧立绘卡。
        /// </summary>
        internal static bool BackdropMode = true;
#else
        /// <summary>
        /// 默认版：左侧功能卡片 + 右侧立绘卡。
        /// 命令行 --backdrop 可临时体验透明背景版式。
        /// </summary>
        internal static bool BackdropMode = false;
#endif

        static MainForm()
        {
            // 让卡片/进度条/主按钮能"透出"它们身后的背景（委托注入，UiTheme 不依赖 MainForm）
            Art.BackdropPainter = delegate(Control c, Graphics g)
            {
                if (!(c.Parent is MainForm)) return false;
                Rectangle src = new Rectangle(c.Left, c.Top, Math.Max(1, c.Width), Math.Max(1, c.Height));
                if (src.Right > Art.Backdrop.Width || src.Bottom > Art.Backdrop.Height) return false;
                g.DrawImage(Art.Backdrop, new Rectangle(0, 0, c.Width, c.Height), src, GraphicsUnit.Pixel);
                return true;
            };
        }

        public MainForm()
        {
            BuildUi();
            LoadSettings();
            Theme.EnableDoubleBuffer(this);   // 透明标签/背景图重绘频繁，双缓冲减少闪烁
            uiTimer = new System.Windows.Forms.Timer();
            uiTimer.Interval = 120;
            uiTimer.Tick += delegate { FlushLog(); ApplyProgress(); };
            uiTimer.Start();
            AppendLog("就绪。选择源和目标后点“开始复制”。");
            AppendLog("引擎：robocopy（Windows 自带，多线程 + 断点重试 + UTF-16 日志解析）");
        }

        // ==============================================================
        // 界面搭建
        // ==============================================================
        private void BuildUi()
        {
            // 透明背景变体：没有右侧立绘卡，卡片占满整个窗口宽度
            int colW = BackdropMode ? DesignW - 32 : LeftColW;
            int innerRight = colW;   // 左栏内容的右缘 x

            Text = "复制工具 - robocopy 图形界面";
            ClientSize = new Size(Dpi.S(DesignW), Dpi.S(DesignH));
            MinimumSize = new Size(Dpi.S(DesignW + 16), Dpi.S(DesignH + 39));
            StartPosition = FormStartPosition.CenterScreen;
            Font = Theme.FontBase;
            BackColor = Theme.Page;
            AutoScaleMode = AutoScaleMode.None;
            AllowDrop = true;
            DragEnter += OnDragEnter;
            DragDrop += OnDragDrop;
            FormClosing += OnFormClosing;

            // ---------- 卡片一：复制内容 + 路径 ----------
            cardPath = new CardPanel();
            cardPath.Title = "复制内容与路径";
            cardPath.SetBounds(16, HeaderH + 16, colW, 176);
            cardPath.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            Controls.Add(cardPath);

            rbFolder = new RadioButton();
            rbFolder.Text = "复制文件夹（含所有子目录）";
            rbFolder.SetBounds(20, 42, 240, 22);
            rbFolder.Checked = true;
            rbFolder.BackColor = Theme.Card;
            rbFolder.ForeColor = Theme.Text;
            rbFolder.Font = Theme.FontBase;
            rbFolder.CheckedChanged += delegate { OnModeChanged(); };
            cardPath.Controls.Add(rbFolder);

            rbFile = new RadioButton();
            rbFile.Text = "复制单个文件";
            rbFile.SetBounds(270, 42, 130, 22);
            rbFile.BackColor = Theme.Card;
            rbFile.ForeColor = Theme.Text;
            rbFile.Font = Theme.FontBase;
            rbFile.CheckedChanged += delegate { OnModeChanged(); };
            cardPath.Controls.Add(rbFile);

            rbVerify = new RadioButton();
            rbVerify.Text = "只校验（不复制）";
            rbVerify.SetBounds(410, 42, 200, 22);
            rbVerify.BackColor = Theme.Card;
            rbVerify.ForeColor = Theme.Text;
            rbVerify.Font = Theme.FontBase;
            rbVerify.CheckedChanged += delegate { OnModeChanged(); };
            cardPath.Controls.Add(rbVerify);

            Label lblSrc = new Label();
            lblSrc.Text = "源路径";
            lblSrc.SetBounds(20, 80, 78, 22);
            lblSrc.ForeColor = Theme.TextMuted;
            lblSrc.BackColor = Theme.Card;
            cardPath.Controls.Add(lblSrc);

            txtSource = new TextBox();
            txtSource.SetBounds(102, 76, 570, 24);
            txtSource.BorderStyle = BorderStyle.FixedSingle;
            txtSource.Font = Theme.FontBase;
            txtSource.AllowDrop = true;
            txtSource.TextChanged += delegate { UpdatePathHint(); };
            txtSource.DragEnter += OnDragEnter;
            txtSource.DragDrop += OnDragDrop;
            cardPath.Controls.Add(txtSource);

            btnBrowseSource = new FlatButton();
            btnBrowseSource.Text = "浏览...";
            btnBrowseSource.Kind = BtnKind.Secondary;
            btnBrowseSource.SetBounds(688, 74, 72, 28);
            btnBrowseSource.Click += delegate { BrowseSource(); };
            cardPath.Controls.Add(btnBrowseSource);

            Label lblDst = new Label();
            lblDst.Text = "目标路径";
            lblDst.SetBounds(20, 116, 78, 22);
            lblDst.ForeColor = Theme.TextMuted;
            lblDst.BackColor = Theme.Card;
            cardPath.Controls.Add(lblDst);

            txtDest = new TextBox();
            txtDest.SetBounds(102, 112, 570, 24);
            txtDest.BorderStyle = BorderStyle.FixedSingle;
            txtDest.Font = Theme.FontBase;
            txtDest.AllowDrop = true;
            txtDest.TextChanged += delegate { UpdatePathHint(); };
            txtDest.DragEnter += OnDragEnter;
            txtDest.DragDrop += OnDragDropDest;
            cardPath.Controls.Add(txtDest);

            btnBrowseDest = new FlatButton();
            btnBrowseDest.Text = "浏览...";
            btnBrowseDest.Kind = BtnKind.Secondary;
            btnBrowseDest.SetBounds(688, 110, 72, 28);
            btnBrowseDest.Click += delegate { BrowseDest(); };
            cardPath.Controls.Add(btnBrowseDest);

            lblPathHint = new Label();
            lblPathHint.ForeColor = Theme.TextFaint;
            lblPathHint.BackColor = Theme.Card;
            lblPathHint.Font = Theme.FontSmall;
            lblPathHint.SetBounds(20, 146, 740, 20);
            cardPath.Controls.Add(lblPathHint);

            // ---------- 卡片二：选项（常用开关 + 高级选项） ----------
            cardOpt = new CardPanel();
            cardOpt.Title = "选项";
            cardOpt.SetBounds(16, HeaderH + 16 + 176 + 16, colW, 168);
            cardOpt.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            Controls.Add(cardOpt);

            chkMirror = MakeCheck(cardOpt, "镜像同步（会删除目标里多余的文件！）", 20, 40, 320, false);
            chkVerify = MakeCheck(cardOpt, "复制后 SHA-256 校验（慢但可靠）", 350, 40, 286, false);

            Label lblT = new Label();
            lblT.Text = "线程";
            lblT.SetBounds(646, 43, 44, 22);
            lblT.ForeColor = Theme.TextMuted;
            lblT.BackColor = Theme.Card;
            cardOpt.Controls.Add(lblT);

            numThreads = new NumericUpDown();
            numThreads.SetBounds(694, 39, 60, 26);
            numThreads.Minimum = 1;
            numThreads.Maximum = 128;
            numThreads.Value = 32;
            numThreads.BorderStyle = BorderStyle.FixedSingle;
            numThreads.Font = Theme.FontBase;
            cardOpt.Controls.Add(numThreads);

            chkDryRun = MakeCheck(cardOpt, "演练模式 /L（只看会复制什么，不真复制）", 20, 72, 340, false);
            chkBigFile = MakeCheck(cardOpt, "大文件模式 /J（单个超大文件更流畅）", 370, 72, 310, false);

            Label lblXD = new Label();
            lblXD.Text = "排除目录";
            lblXD.SetBounds(20, 107, 78, 22);
            lblXD.ForeColor = Theme.TextMuted;
            lblXD.BackColor = Theme.Card;
            cardOpt.Controls.Add(lblXD);

            txtXD = new TextBox();
            txtXD.SetBounds(102, 103, 250, 24);
            txtXD.BorderStyle = BorderStyle.FixedSingle;
            txtXD.Font = Theme.FontBase;
            cardOpt.Controls.Add(txtXD);

            Label lblXF = new Label();
            lblXF.Text = "排除文件";
            lblXF.SetBounds(366, 107, 78, 22);
            lblXF.ForeColor = Theme.TextMuted;
            lblXF.BackColor = Theme.Card;
            cardOpt.Controls.Add(lblXF);

            txtXF = new TextBox();
            txtXF.SetBounds(452, 103, 308, 24);
            txtXF.BorderStyle = BorderStyle.FixedSingle;
            txtXF.Font = Theme.FontBase;
            cardOpt.Controls.Add(txtXF);

            // ---------- 进度 ----------
            int progY = HeaderH + 16 + 176 + 16 + 168 + 16;   // 选项卡片下方 16px = 542
            bar = new FlatProgressBar();
            bar.SetBounds(16, progY, colW, 20);
            bar.Name = "bar";
            Controls.Add(bar);

            lblStatus = new Label();
            lblStatus.Text = "等待开始";
            lblStatus.SetBounds(16, progY + 24, 416, 22);
            lblStatus.ForeColor = Theme.Text;
            lblStatus.Font = Theme.FontBold;
            // 透明：普通模式下透出页面底色（和原来一样），透明背景模式下透出壁纸
            lblStatus.BackColor = Color.Transparent;
            Controls.Add(lblStatus);

            lblCount = new Label();
            lblCount.SetBounds(innerRight - 344, progY + 24, 344, 22);
            lblCount.TextAlign = ContentAlignment.MiddleRight;
            lblCount.ForeColor = Theme.TextMuted;
            lblCount.Font = Theme.FontSmall;
            lblCount.BackColor = Color.Transparent;
            Controls.Add(lblCount);

            lblSpeed = new Label();
            lblSpeed.SetBounds(16, progY + 48, colW, 20);
            lblSpeed.ForeColor = Theme.TextFaint;
            lblSpeed.Font = Theme.FontSmall;
            lblSpeed.BackColor = Color.Transparent;
            Controls.Add(lblSpeed);

            // ---------- 操作按钮（靠内容区右缘对齐） ----------
            int by = progY + 74;   // 542 + 74 = 616，在速度行（542+48+20=610）之下
            btnStart = MakeButton("开始复制", innerRight - 684, by, 140, 38);
            btnStart.Kind = BtnKind.Primary;
            btnStart.Font = Theme.FontBtnBig;
            btnStart.Click += delegate { StartCopy(); };

            btnStop = MakeButton("停止", innerRight - 536, by, 90, 38);
            btnStop.Enabled = false;
            btnStop.Click += delegate { StopCopy(); };

            btnOpenDest = MakeButton("打开目标文件夹", innerRight - 438, by, 140, 38);
            btnOpenDest.Click += delegate { OpenDest(); };

            btnCopyCmd = MakeButton("查看 robocopy 命令", innerRight - 290, by, 172, 38);
            btnCopyCmd.Click += delegate { ShowCommand(); };

            btnSaveLog = MakeButton("保存日志", innerRight - 110, by, 110, 38);
            btnSaveLog.Click += delegate { SaveLog(); };

            // ---------- 日志（深色卡片） ----------
            int logY = progY + 128;                        // 542 + 128 = 670
            cardLog = new CardPanel();
            cardLog.Title = "运行日志";
            cardLog.TitleColor = Color.FromArgb(0x9A, 0xA3, 0xB2);
            cardLog.Fill = Theme.LogBg;
            cardLog.BorderColor = Theme.LogBorder;
            cardLog.SetBounds(16, logY, colW, DesignH - logY - 16);
            cardLog.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left;
            Controls.Add(cardLog);

            txtLog = new RichTextBox();
            txtLog.ReadOnly = true;
            txtLog.BorderStyle = BorderStyle.None;
            txtLog.ScrollBars = RichTextBoxScrollBars.Both;
            txtLog.WordWrap = false;
            txtLog.BackColor = Theme.LogBg;
            txtLog.ForeColor = Theme.LogText;
            txtLog.Font = Theme.FontMono;
            txtLog.DetectUrls = false;
            txtLog.SetBounds(18, 36, colW - 36, cardLog.Height - 36 - 16);
            txtLog.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            cardLog.Controls.Add(txtLog);

            // ---------- 右栏：背景立绘卡片（透明背景变体没有它，壁纸本身就是背景） ----------
            if (!BackdropMode)
            {
                art = new ArtPanel();
                art.SetBounds(HeaderW + 16, 16, ArtW, DesignH - 32);
                art.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Right;
                art.AllowDrop = true;
                art.DragEnter += OnDragEnter;
                art.DragDrop += OnDragDrop;
                Controls.Add(art);
            }

            // 透明背景变体：卡片蒙一层半透明白（比白纱再实一点，保证卡内文字可读），
            // 圆角处能真正透出底下的图
            if (BackdropMode)
            {
                Color wash = Color.FromArgb(Math.Min(250, Art.BackdropVeilAlpha + 28), 255, 255, 255);
                cardPath.Fill = wash;
                cardOpt.Fill = wash;
            }

            // 命名关键控件，便于界面预览/自测程序按名字查找
            rbFile.Name = "rbFile";
            rbFolder.Name = "rbFolder";
            rbVerify.Name = "rbVerify";
            txtSource.Name = "txtSource";
            txtDest.Name = "txtDest";
            txtXD.Name = "txtXD";
            txtXF.Name = "txtXF";
            txtLog.Name = "txtLog";
            numThreads.Name = "numThreads";
            chkVerify.Name = "chkVerify";
            chkMirror.Name = "chkMirror";
            chkDryRun.Name = "chkDryRun";
            chkBigFile.Name = "chkBigFile";
            btnStart.Name = "btnStart";
            btnStop.Name = "btnStop";
            btnSaveLog.Name = "btnSaveLog";
            bar.Name = "bar";
            lblStatus.Name = "lblStatus";
            lblCount.Name = "lblCount";
            lblSpeed.Name = "lblSpeed";
            cardPath.Name = "cardPath";
            cardOpt.Name = "cardOpt";
            cardLog.Name = "cardLog";
            if (art != null) art.Name = "art";   // 透明背景变体没有立绘卡

            // 所有坐标按设计单位书写，这里统一换算成当前 DPI 的真实像素
            Dpi.ScaleTree(this);

            OnModeChanged();
        }

        /// <summary>页头与背景：普通模式是"白色页头（只铺左栏）"；透明背景模式是壁纸垫底 + 白纱 + 页头渐变。</summary>
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;

            if (BackdropMode)
            {
                if (Art.Backdrop == null || Art.Backdrop.Size != ClientSize)
                    Art.BuildBackdrop(ClientSize, Dpi.S(HeaderH));
                g.DrawImage(Art.Backdrop, 0, 0);
                using (Pen pen = new Pen(Theme.HeaderLine))
                    g.DrawLine(pen, 0, Dpi.S(HeaderH), Width, Dpi.S(HeaderH));
            }
            else
            {
                int w = Dpi.S(HeaderW);
                int h = Dpi.S(HeaderH);
                using (SolidBrush b = new SolidBrush(Color.White))
                    g.FillRectangle(b, new Rectangle(0, 0, w, h));
                using (Pen pen = new Pen(Theme.HeaderLine))
                    g.DrawLine(pen, 0, h, w, h);
            }

            using (GraphicsPath gp = Theme.Round(new Rectangle(Dpi.S(24), Dpi.S(53), Dpi.S(4), Dpi.S(25)), Dpi.S(2)))
            using (SolidBrush ab = new SolidBrush(Theme.Accent))
                g.FillPath(ab, gp);

            TextRenderer.DrawText(g, "复制工具", Theme.FontTitle, new Point(Dpi.S(38), Dpi.S(41)), Theme.Text);
            TextRenderer.DrawText(g, "robocopy 图形界面 · 多线程 · 失败重试 · SHA-256 校验",
                Theme.FontSub, new Point(Dpi.S(39), Dpi.S(84)), Theme.TextMuted);
        }

        private CheckBox MakeCheck(Control parent, string text, int x, int y, int w, bool check)
        {
            CheckBox c = new CheckBox();
            c.Text = text;
            c.SetBounds(x, y, w, 22);
            c.Checked = check;
            c.BackColor = Theme.Card;
            c.ForeColor = Theme.Text;
            c.Font = Theme.FontBase;
            c.FlatStyle = FlatStyle.Standard;
            parent.Controls.Add(c);
            return c;
        }

        private FlatButton MakeButton(string text, int x, int y, int w, int h)
        {
            FlatButton b = new FlatButton();
            b.Text = text;
            b.Kind = BtnKind.Secondary;
            b.SetBounds(x, y, w, h);
            b.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            Controls.Add(b);
            return b;
        }

        // ==============================================================
        // 交互
        // ==============================================================
        private void OnModeChanged()
        {
            // 只校验模式下镜像没有意义，禁用以免误导
            chkMirror.Enabled = !rbVerify.Checked;
            UpdatePathHint();
        }

        private void UpdatePathHint()
        {
            if (rbVerify.Checked)
                lblPathHint.Text = "只校验模式：不复制任何文件，只对源和目标里的每个文件做 SHA-256 比对并报告差异。";
            else if (rbFile.Checked)
                lblPathHint.Text = "文件模式：源选一个文件，目标选一个文件夹，文件按原名复制进去。文件或文件夹也可以直接拖进窗口。";
            else
            {
                string eff = TryResolveFolderDest();
                lblPathHint.Text = "文件夹模式：整个文件夹会复制到目标里面" + (eff != null ? "，实际复制到：" + eff : "（目标\\源文件夹名）")
                                 + "，文件不会散落。";
            }
        }

        // 预览"文件夹最终会复制到哪"，让用户在点开始之前就能看到
        private string TryResolveFolderDest()
        {
            try
            {
                string s = PathUtil.Clean(txtSource.Text);
                string d = PathUtil.Clean(txtDest.Text);
                if (s.Length == 0 || d.Length == 0 || !Directory.Exists(s)) return null;
                string name = Path.GetFileName(s.TrimEnd('\\', '/'));
                if (string.IsNullOrEmpty(name)) return null;
                return Path.Combine(d, name);
            }
            catch { return null; }
        }

        private void OnDragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy;
        }

        private void OnDragDrop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            string[] items = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (items == null || items.Length == 0) return;
            if (Directory.Exists(items[0]))
            {
                rbFolder.Checked = true;
                txtSource.Text = items[0];
            }
            else
            {
                rbFile.Checked = true;
                txtSource.Text = items[0];
            }
            if (items.Length > 1) AppendLog("（拖入了 " + items.Length + " 项，只取第一项）");
        }

        private void OnDragDropDest(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            string[] items = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (items == null || items.Length == 0) return;
            string p = items[0];
            if (!Directory.Exists(p)) p = Path.GetDirectoryName(p);
            txtDest.Text = p;
        }

        private void BrowseSource()
        {
            if (rbFile.Checked)
            {
                OpenFileDialog d = new OpenFileDialog();
                d.Title = "选择要复制的文件";
                d.Filter = "所有文件 (*.*)|*.*";
                d.CheckFileExists = true;
                if (d.ShowDialog(this) == DialogResult.OK) txtSource.Text = d.FileName;
            }
            else
            {
                FolderBrowserDialog d = new FolderBrowserDialog();
                d.Description = "选择要复制的源文件夹";
                d.ShowNewFolderButton = false;
                if (d.ShowDialog(this) == DialogResult.OK) txtSource.Text = d.SelectedPath;
            }
        }

        private void BrowseDest()
        {
            FolderBrowserDialog d = new FolderBrowserDialog();
            d.Description = "选择目标文件夹（不存在会自动创建）";
            d.ShowNewFolderButton = true;
            if (d.ShowDialog(this) == DialogResult.OK) txtDest.Text = d.SelectedPath;
        }

        private void OpenDest()
        {
            string p = PathUtil.Clean(txtDest.Text);
            if (p.Length == 0) { Warn("还没有填目标路径。"); return; }
            try
            {
                if (Directory.Exists(p)) System.Diagnostics.Process.Start("explorer.exe", "\"" + p + "\"");
                else Warn("目标文件夹还不存在：\r\n" + p);
            }
            catch (Exception ex) { Warn("打开失败：" + ex.Message); }
        }

        private void ShowCommand()
        {
            string cmd;
            try { cmd = "robocopy.exe " + RobocopyRunner.BuildArguments(CollectRequest(), NextLogPath()); }
            catch (Exception ex) { cmd = "（无法生成命令：" + ex.Message + "）"; }

            Form f = new Form();
            f.Text = "robocopy 命令行";
            f.ClientSize = new Size(Dpi.S(820), Dpi.S(300));
            f.StartPosition = FormStartPosition.CenterParent;
            f.Font = Font;
            f.MinimizeBox = false;
            f.MaximizeBox = false;

            Label hint = new Label();
            hint.Text = "这就是本工具实际执行的命令，可以直接粘到命令行里用。想看效果又不真复制，在末尾加 /L。";
            hint.SetBounds(Dpi.S(12), Dpi.S(10), Dpi.S(796), Dpi.S(40));
            hint.ForeColor = Color.DimGray;
            f.Controls.Add(hint);

            TextBox t = new TextBox();
            t.Multiline = true;
            t.ReadOnly = true;
            t.ScrollBars = ScrollBars.Both;
            t.WordWrap = true;
            t.Font = Theme.MakeFont("Consolas", 9F, FontStyle.Regular);
            t.SetBounds(Dpi.S(12), Dpi.S(52), Dpi.S(796), Dpi.S(190));
            t.Text = cmd;
            f.Controls.Add(t);

            Button copy = new Button();
            copy.Text = "复制到剪贴板";
            copy.SetBounds(Dpi.S(12), Dpi.S(252), Dpi.S(130), Dpi.S(32));
            copy.Click += delegate
            {
                try { Clipboard.SetText(cmd); copy.Text = "已复制"; }
                catch { }
            };
            f.Controls.Add(copy);

            Button close = new Button();
            close.Text = "关闭";
            close.SetBounds(Dpi.S(678), Dpi.S(252), Dpi.S(130), Dpi.S(32));
            close.DialogResult = DialogResult.OK;
            f.Controls.Add(close);
            f.AcceptButton = close;
            f.CancelButton = close;
            f.ShowDialog(this);
        }

        private void SaveLog()
        {
            SaveFileDialog d = new SaveFileDialog();
            d.Title = "保存日志";
            d.Filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*";
            d.FileName = "复制日志_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt";
            if (d.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                File.WriteAllText(d.FileName, txtLog.Text, Encoding.UTF8);
                AppendLog("日志已保存到：" + d.FileName);
            }
            catch (Exception ex) { Warn("保存失败：" + ex.Message); }
        }

        // ==============================================================
        // 开始 / 停止
        // ==============================================================
        private CopyRequest CollectRequest()
        {
            CopyRequest r = new CopyRequest();
            r.Mode = rbVerify.Checked ? CopyMode.VerifyOnly
                   : (rbFile.Checked ? CopyMode.File : CopyMode.Folder);
            r.Source = PathUtil.Clean(txtSource.Text);
            r.Dest = PathUtil.Clean(txtDest.Text);
            // 只校验模式下，源是文件夹时和复制一样按"目标\源文件夹名"解析
            if (r.Mode == CopyMode.VerifyOnly && Directory.Exists(r.Source))
                r.Dest = ResolveFolderDest(r.Source, r.Dest);
            if (r.Mode == CopyMode.Folder)
                r.Dest = ResolveFolderDest(r.Source, r.Dest);
            CopyOptions o = new CopyOptions();
            o.Threads = (int)numThreads.Value;   // /MT:n，默认 32
            o.Retries = 2;
            o.WaitSeconds = 2;
            o.SkipNewer = true;             // 增量：目标里已有且没变的文件自动跳过
            o.IncludeEmptyDirs = true;
            o.BigFileMode = chkBigFile.Checked;
            o.Mirror = chkMirror.Checked;
            o.DryRun = chkDryRun.Checked;
            o.Verify = chkVerify.Checked;
            o.ExcludeDirs = txtXD.Text.Trim();
            o.ExcludeFiles = txtXF.Text.Trim();
            r.Options = o;
            return r;
        }

        /// <summary>
        /// 文件夹模式的目标解析（和资源管理器一致）：
        /// 源文件夹整个复制到目标里面，变成 目标\源文件夹名，而不是把内容散落一地。
        /// 源是盘符根目录（没有文件夹名）时保持原样。
        /// </summary>
        private static string ResolveFolderDest(string source, string dest)
        {
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(dest)) return dest;
            string name = Path.GetFileName(source.TrimEnd('\\', '/'));
            if (string.IsNullOrEmpty(name)) return dest;
            return Path.Combine(dest, name);
        }

        private bool Validate(CopyRequest r)
        {
            if (r.Source.Length == 0) { Warn("请先选择源路径。"); return false; }
            if (r.Dest.Length == 0) { Warn("请先选择目标路径。"); return false; }
            if (r.Mode == CopyMode.File)
            {
                if (!File.Exists(r.Source)) { Warn("源文件不存在：\r\n" + r.Source); return false; }
            }
            else
            {
                if (!File.Exists(r.Source) && !Directory.Exists(r.Source))
                { Warn("源文件或文件夹不存在：\r\n" + r.Source); return false; }
            }
            if (r.Source.Equals(r.Dest, StringComparison.OrdinalIgnoreCase))
            {
                Warn("源和目标不能是同一个路径。");
                return false;
            }
            if (r.Mode != CopyMode.File && PathUtil.IsInside(r.Source, r.Dest))
            {
                Warn("目标文件夹不能放在源文件夹里面，否则会无限自我复制。\r\n\r\n源：" + r.Source + "\r\n目标：" + r.Dest);
                return false;
            }
            if (PathUtil.IsInside(r.Dest, r.Source))
            {
                Warn("源文件夹不能放在目标文件夹里面。\r\n\r\n源：" + r.Source + "\r\n目标：" + r.Dest);
                return false;
            }
            if (r.Options.Mirror)
            {
                string msg = "镜像同步（/MIR）会删除目标文件夹里所有“源里没有”的文件和文件夹。\r\n\r\n"
                           + "源：" + r.Source + "\r\n目标：" + r.Dest + "\r\n\r\n"
                           + "这个操作不可撤销。确定继续吗？";
                if (MessageBox.Show(this, msg, "危险操作确认", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                        MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                    return false;
            }
            return true;
        }

        private string NextLogPath()
        {
            // 不用 Path.GetTempPath() 直接拼：系统 TEMP 可能不存在或不可写，
            // 那样 robocopy 会连日志都建不出来（错误还只打在它自己的控制台上）。
            string dir = LogPathPicker.PickDirectory();
            if (string.IsNullOrEmpty(dir)) dir = LogPathPicker.SafeTemp();
            if (string.IsNullOrEmpty(dir)) dir = ".";
            return Path.Combine(dir, "robocopy_gui_" + Guid.NewGuid().ToString("N") + ".log");
        }

        private void StartCopy()
        {
            if (running) return;
            CopyRequest r = CollectRequest();
            if (!Validate(r)) return;

            request = r;
            scan = null;
            lastResult = null;
            lastProgress = null;
            verifyPhase = false;
            reported = false;
            cancelFlag = false;
            startedAt = DateTime.Now;
            bar.Reset();
            lblCount.Text = "";
            lblSpeed.Text = "";

            AppendLog("");
            AppendLog("========== " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " 开始 ==========");
            AppendLog((r.Mode == CopyMode.File ? "模式：复制单个文件"
                     : r.Mode == CopyMode.VerifyOnly ? "模式：只校验（不复制）"
                     : "模式：复制文件夹")
                     + (r.Options.DryRun ? "（仅演练）" : "")
                     + (r.Options.Mirror ? "（镜像）" : ""));
            AppendLog("源　：" + r.Source);
            AppendLog("目标：" + r.Dest);
            if (r.Options.ExcludeDirs.Length > 0)
                AppendLog("排除目录：" + r.Options.ExcludeDirs);
            if (r.Options.ExcludeFiles.Length > 0)
                AppendLog("排除文件：" + r.Options.ExcludeFiles);

            RememberHistory(txtSource);
            RememberHistory(txtDest);

            SetRunning(true);
            Thread t = new Thread(Worker);
            t.IsBackground = true;
            t.Start();
        }

        private void StopCopy()
        {
            if (!running) return;
            cancelFlag = true;
            if (runner != null) runner.Cancel();
            lblStatus.Text = "正在停止...";
            AppendLog("收到停止指令，正在结束当前阶段...");
        }

        private void SetRunning(bool on)
        {
            running = on;
            btnStart.Enabled = !on;
            btnStop.Enabled = on;
            rbFile.Enabled = !on;
            rbFolder.Enabled = !on;
            rbVerify.Enabled = !on;
            btnBrowseSource.Enabled = !on;
            btnBrowseDest.Enabled = !on;
            if (!on)
            {
                uiTimer.Stop();
                FlushLog();
                uiTimer.Start();
            }
        }

        // ==============================================================
        // 工作线程
        // ==============================================================
        private void Worker()
        {
            RunResult result = null;
            VerifyResult vr = null;
            try
            {
                // ---- 1. 扫描源，统计总大小（算真进度用）----
                Ui(delegate { lblStatus.Text = "正在扫描源目录..."; });
                bool singleFile = request.Mode == CopyMode.File
                    || (request.Mode == CopyMode.VerifyOnly
                        && File.Exists(request.Source) && !Directory.Exists(request.Source));
                if (singleFile)
                {
                    scan = Scanner.ScanSingleFile(request.Source);
                }
                else
                {
                    scan = Scanner.ScanFolder(request.Source, request.Options, delegate(int files, long bytes)
                    {
                        Ui(delegate
                        {
                            lblStatus.Text = "正在扫描源目录...";
                            lblCount.Text = "已发现 " + Fmt.Count(files) + " 个文件 / " + Fmt.Size(bytes);
                        });
                    }, delegate { return cancelFlag; });
                }

                if (scan.Cancelled || cancelFlag)
                {
                    AppendLog("已在扫描阶段停止。");
                    Ui(delegate { lblStatus.Text = "已停止"; lblCount.Text = ""; });
                    reported = true;
                    return;
                }

                if (scan.Error != null)
                {
                    AppendLog("扫描失败：" + scan.Error);
                    Ui(delegate { Warn(scan.Error); });
                    return;
                }

                AppendLog("扫描完成：共 " + Fmt.Count(scan.TotalFiles) + " 个文件，"
                          + Fmt.Size(scan.TotalBytes) + (scan.Truncated ? "（文件过多，进度按字节近似估算）" : ""));
                if (scan.TotalFiles == 0 && request.Mode != CopyMode.File)
                    AppendLog("注意：源文件夹里没有文件（空文件夹本身仍会被创建）。");

                if (request.Mode == CopyMode.VerifyOnly)
                {
                    RunVerifyOnly();
                    reported = true;
                    return;
                }

                // ---- 2. 跑 robocopy ----
                lastLogPath = NextLogPath();
                runner = new RobocopyRunner();
                runner.Log += delegate(string line) { AppendLog(line); };
                runner.Progress += delegate(ProgressInfo pi) { lastProgress = pi; };

                Ui(delegate { lblStatus.Text = "正在复制..."; });
                AppendLog("命令：robocopy.exe " + RobocopyRunner.BuildArguments(request, lastLogPath));

                result = runner.Run(request, scan, lastLogPath);
                lastResult = result;

                // ---- 3. 校验 ----
                if (result.Success && !result.Cancelled && request.Options.Verify && !request.Options.DryRun)
                {
                    vr = RunVerify();
                }
            }
            catch (Exception ex)
            {
                AppendLog("发生异常：" + ex.Message);
                AppendLog(ex.StackTrace);
            }
            finally
            {
                if (!reported)
                {
                    Report(result, vr);
                }
                if (lastLogPath != null)
                {
                    try
                    {
                        // 失败日志留下来，才能诊断具体路径、系统错误和重试记录。
                        if (File.Exists(lastLogPath))
                        {
                            if (result != null && result.Success) File.Delete(lastLogPath);
                            else AppendLog("诊断日志：" + lastLogPath);
                        }
                    }
                    catch { }
                }
                Ui(delegate { SetRunning(false); });
            }
        }

        /// <summary>只校验模式：不调 robocopy，直接逐文件比对 SHA-256。</summary>
        private void RunVerifyOnly()
        {
            if (scan.Truncated)
                AppendLog("注意：文件数超过 " + Fmt.Count(Scanner.MaxTrackedFiles)
                          + "，校验只覆盖清单里的前 " + Fmt.Count(Scanner.MaxTrackedFiles) + " 个文件。");
            VerifyResult vr = RunVerify();
            ReportVerifyOnly(vr);
        }

        /// <summary>校验阶段（复制后的校验和只校验模式共用）。</summary>
        private VerifyResult RunVerify()
        {
            verifyPhase = true;
            int vtotal = scan.Files.Count;
            AppendLog("");
            AppendLog("开始 SHA-256 校验，共 " + Fmt.Count(vtotal) + " 个文件...");
            Ui(delegate { lblStatus.Text = "正在校验..."; });
            verifyStartedAt = DateTime.Now;
            VerifyResult r = Verifier.Run(request, scan, delegate { return cancelFlag; }, VerifyProgress);
            verifyPhase = false;
            return r;
        }

        /// <summary>校验进度：带字节数，能显示速度和剩余时间。</summary>
        private void VerifyProgress(int done, int total, string name, long bytes)
        {
            ProgressInfo pi = new ProgressInfo();
            pi.Phase = "正在校验";
            pi.DoneFiles = done;
            pi.TotalFiles = total;
            pi.CurrentFile = name;
            pi.DoneBytes = bytes;
            pi.TotalBytes = scan != null ? scan.TotalBytes : 0;
            double sec = (DateTime.Now - verifyStartedAt).TotalSeconds;
            if (sec > 0.5) pi.BytesPerSecond = bytes / sec;
            if (pi.BytesPerSecond > 1 && pi.TotalBytes > bytes)
            {
                double remain = (pi.TotalBytes - bytes) / pi.BytesPerSecond;
                if (remain < 86400 * 30) pi.EtaSeconds = (long)remain;
            }
            lastProgress = pi;
        }

        private void ReportVerifyOnly(VerifyResult vr)
        {
            AppendLog("");
            AppendLog("========== 校验结果 ==========");

            if (vr == null)
            {
                AppendLog("校验没有完成。");
                Ui(delegate { lblStatus.Text = "未完成"; });
                return;
            }

            if (vr.Cancelled)
            {
                AppendLog("状态：已手动停止（已校验 " + Fmt.Count(vr.Checked) + " 个）。");
                Ui(delegate { lblStatus.Text = "已停止"; lblCount.Text = ""; });
                return;
            }

            AppendLog("校验：已校验 " + Fmt.Count(vr.Checked)
                      + "，一致 " + Fmt.Count(vr.Matched)
                      + "，不一致 " + Fmt.Count(vr.Mismatched.Count)
                      + "，缺失 " + Fmt.Count(vr.Missing.Count)
                      + "，读取错误 " + Fmt.Count(vr.Errors.Count));
            for (int i = 0; i < vr.Mismatched.Count && i < 20; i++)
                AppendLog("  不一致：" + vr.Mismatched[i]);
            for (int i = 0; i < vr.Missing.Count && i < 20; i++)
                AppendLog("  缺失　：" + vr.Missing[i]);

            bool ok = vr.Mismatched.Count == 0 && vr.Missing.Count == 0 && vr.Errors.Count == 0;
            AppendLog(ok ? "校验通过：源和目标的内容完全一致。" : "校验发现问题，请查看上面的明细。");

            Ui(delegate
            {
                bar.Complete();
                lblStatus.Text = ok ? "校验通过" : "校验发现问题";
                lblSpeed.Text = "总耗时 " + Fmt.Duration((long)(DateTime.Now - startedAt).TotalSeconds);
                if (ok)
                {
                    if (MessageBox.Show(this, "校验通过：源和目标的内容完全一致。\r\n耗时 "
                            + Fmt.Duration((long)(DateTime.Now - startedAt).TotalSeconds)
                            + "\r\n\r\n要打开目标文件夹吗？", "校验通过",
                            MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                        OpenDest();
                }
                else
                {
                    MessageBox.Show(this, "不一致 " + vr.Mismatched.Count + " 个，缺失 "
                        + vr.Missing.Count + " 个，读取错误 " + vr.Errors.Count + " 个。\r\n请查看日志明细。",
                        "校验发现问题", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            });
        }

        private void Report(RunResult result, VerifyResult vr)
        {
            AppendLog("");
            AppendLog("========== 结果 ==========");

            if (result == null)
            {
                AppendLog("复制没有完成（提前退出）。");
                Ui(delegate { lblStatus.Text = "未完成"; });
                return;
            }

            if (result.Cancelled)
            {
                AppendLog("状态：已手动停止。退出码 " + result.ExitCode);
                AppendLog("注意：正在复制的那个文件可能不完整，需要重新复制一遍。");
                Ui(delegate { lblStatus.Text = "已停止"; lblCount.Text = ""; });
                return;
            }

            if (result.ExitCode >= 0)
                AppendLog("robocopy 原始退出码：" + result.ExitCode + "（最终状态同时检查日志和失败汇总）");
            if (result.HasSummary)
            {
                AppendLog("文件：总计 " + Fmt.Count(result.SumTotalFiles)
                          + "，复制 " + Fmt.Count(result.SumCopiedFiles)
                          + "，跳过 " + Fmt.Count(result.SumSkippedFiles)
                          + "，不匹配 " + Fmt.Count(result.SumMismatch)
                          + "，失败 " + Fmt.Count(result.SumFailedFiles));
                AppendLog("字节：总计 " + Fmt.Size(result.SumTotalBytes)
                          + "，复制 " + Fmt.Size(result.SumCopiedBytes));
            }
            AppendLog("耗时：" + Fmt.Duration((long)(DateTime.Now - startedAt).TotalSeconds));

            if (result.LogMissing)
            {
                AppendLog("异常：" + result.Error);
                AppendLog("提示：点“查看 robocopy 命令”可以拿到完整命令行，粘到 CMD 里跑一遍会看到同样的提示。");
                Ui(delegate
                {
                    lblStatus.Text = "失败（没生成日志）";
                    lblCount.Text = "";
                    lblSpeed.Text = "";
                    bar.Reset();
                    Warn(result.Error);
                });
                return;
            }

            if (!string.IsNullOrEmpty(result.Error))
            {
                // 提前失败（目标目录写不进去，robocopy 都没跑），
                // 或“退出码 0 但其实一个文件都没复制”的纠正——报错里已带上原因和建议。
                AppendLog("失败：" + result.Error);
                Ui(delegate
                {
                    lblStatus.Text = "失败";
                    lblCount.Text = "";
                    lblSpeed.Text = "";
                    bar.Reset();
                    Warn(result.Error);
                });
                return;
            }

            if (result.ErrorLines > 0)
                AppendLog("日志中有 " + result.ErrorLines + " 行错误记录（包含重试过程中发生的错误）。");

            if (result.Success && request.Options.DryRun)
            {
                AppendLog("这是演练模式，没有真正复制任何文件。");
                Ui(delegate
                {
                    lblStatus.Text = "演练完成";
                    bar.Complete();
                    MessageBox.Show(this, "演练完成，没有真正复制文件。\r\n\r\n请查看下方日志确认会做什么。",
                        "演练完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
                });
                return;
            }

            if (!result.Success)
            {
                AppendLog("复制失败。常见原因：路径无权限、磁盘满、路径过长、目标被占用。");
                string msg = "robocopy 返回退出码 " + result.ExitCode + "（>=8 表示有文件复制失败）。\r\n\r\n"
                           + "常见原因：\r\n· 权限不足（试试以管理员身份运行）\r\n· 磁盘空间不足\r\n· 有文件被其它程序占用\r\n\r\n详情见日志。";
                Ui(delegate
                {
                    lblStatus.Text = "失败";
                    MessageBox.Show(this, msg, "复制失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                });
                return;
            }

            // 成功
            if (vr != null)
            {
                AppendLog("校验：已校验 " + Fmt.Count(vr.Checked)
                          + "，一致 " + Fmt.Count(vr.Matched)
                          + "，不一致 " + Fmt.Count(vr.Mismatched.Count)
                          + "，缺失 " + Fmt.Count(vr.Missing.Count)
                          + "，读取错误 " + Fmt.Count(vr.Errors.Count));
                if (vr.Cancelled) AppendLog("校验被中断。");
                for (int i = 0; i < vr.Mismatched.Count && i < 20; i++)
                    AppendLog("  不一致：" + vr.Mismatched[i]);
                for (int i = 0; i < vr.Missing.Count && i < 20; i++)
                    AppendLog("  缺失　：" + vr.Missing[i]);
            }

            bool verifyOk = vr == null || (vr.Mismatched.Count == 0 && vr.Missing.Count == 0 && vr.Errors.Count == 0);

            Ui(delegate
            {
                bar.Complete();
                lblStatus.Text = verifyOk ? "完成" : "完成（但校验有问题）";
                lblSpeed.Text = "总耗时 " + Fmt.Duration((long)(DateTime.Now - startedAt).TotalSeconds);

                string msg;
                if (!verifyOk)
                {
                    msg = "复制完成，但校验发现问题！\r\n\r\n不一致 " + vr.Mismatched.Count
                        + " 个，缺失 " + vr.Missing.Count + " 个。\r\n请查看日志。";
                    MessageBox.Show(this, msg, "校验未通过", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                else
                {
                    string body = result.HasSummary
                        ? "共复制 " + Fmt.Count(result.SumCopiedFiles) + " 个文件，跳过 " + Fmt.Count(result.SumSkippedFiles) + " 个。"
                        : "复制已完成。";
                    msg = body + "\r\n耗时 " + Fmt.Duration((long)(DateTime.Now - startedAt).TotalSeconds)
                        + (vr != null ? "\r\nSHA-256 校验通过。" : "")
                        + "\r\n\r\n要打开目标文件夹吗？";
                    if (MessageBox.Show(this, msg, "复制完成", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                        OpenDest();
                }
            });
        }

        // ==============================================================
        // 进度刷新（UI 线程）
        // ==============================================================
        private void ApplyProgress()
        {
            ProgressInfo pi = lastProgress;
            if (pi == null || !running) return;

            if (verifyPhase || pi.Phase == "正在校验")
            {
                int pct = 0;
                if (pi.TotalBytes > 0 && pi.DoneBytes > 0)
                {
                    // 按字节算的进度更真实（小文件多时按文件数会跳变）
                    pct = (int)(pi.DoneBytes * 100 / pi.TotalBytes);
                    SetBar(pi.DoneBytes, pi.TotalBytes);
                }
                else if (pi.TotalFiles > 0)
                {
                    pct = pi.DoneFiles * 100 / Math.Max(1, pi.TotalFiles);
                    SetBar(pi.DoneFiles, pi.TotalFiles);
                }
                lblStatus.Text = "正在校验 " + pct + "%";
                lblCount.Text = Fmt.Count(pi.DoneFiles) + " / " + Fmt.Count(pi.TotalFiles) + " 个文件";

                string extra = pi.BytesPerSecond > 0 ? "速度 " + Fmt.Speed(pi.BytesPerSecond) : "";
                if (pi.EtaSeconds >= 0) extra += "　剩余约 " + Fmt.Duration(pi.EtaSeconds);
                if (!string.IsNullOrEmpty(pi.CurrentFile)) extra += "　当前：" + pi.CurrentFile;
                lblSpeed.Text = extra;
                return;
            }

            int p = 0;
            if (pi.TotalBytes > 0)
            {
                p = (int)(pi.DoneBytes * 100 / pi.TotalBytes);
                if (p > 100) p = 100;
                SetBar(pi.DoneBytes, pi.TotalBytes);
            }

            lblStatus.Text = "正在复制 " + p + "%";
            lblCount.Text = "已处理 " + Fmt.Count(pi.DoneFiles) + " / " + Fmt.Count(pi.TotalFiles) + " 个文件"
                          + "　" + Fmt.Size(pi.DoneBytes) + " / " + Fmt.Size(pi.TotalBytes);

            string e = pi.BytesPerSecond > 0 ? "速度 " + Fmt.Speed(pi.BytesPerSecond) : "";
            if (pi.EtaSeconds >= 0) e += "　剩余约 " + Fmt.Duration(pi.EtaSeconds);
            if (!string.IsNullOrEmpty(pi.CurrentFile))
            {
                string f = pi.CurrentFile;
                if (scan != null && scan.Root != null && f.Length > scan.Root.Length)
                    f = f.Substring(scan.Root.Length).TrimStart('\\');
                e += "　当前：" + f;
            }
            lblSpeed.Text = e;
        }

        private void SetBar(long done, long total)
        {
            if (total <= 0)
            {
                bar.SetBusy("复制中");
                return;
            }
            int v = (int)(done * 100 / total);
            if (v < 0) v = 0;
            if (v > 100) v = 100;
            bar.SetPercent(v);
        }

        // ==============================================================
        // 日志
        // ==============================================================
        private void AppendLog(string line)
        {
            lock (pendingLog)
            {
                pendingLog.Append(line);
                pendingLog.Append("\r\n");
            }
        }

        private void FlushLog()
        {
            string s;
            lock (pendingLog)
            {
                if (pendingLog.Length == 0) return;
                s = pendingLog.ToString();
                pendingLog.Length = 0;
            }

            // 按内容上色：进度条目灰、成功绿、报错红、阶段标题蓝
            string[] lines = s.Split('\n');
            if (lines.Length > 400)
            {
                // 一次刷进来上千行（海量小文件）时不分段上色，
                // 否则会触发上万次 AppendText，把界面卡住。
                AppendColored(s, Theme.LogText);
            }
            else
            {
                StringBuilder batch = new StringBuilder();
                Color batchColor = Theme.LogText;
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].TrimEnd('\r');
                    if (line.Length == 0 && i == lines.Length - 1) break;
                    Color c = ClassifyLogLine(line);
                    if (c != batchColor && batch.Length > 0)
                    {
                        AppendColored(batch.ToString(), batchColor);
                        batch.Length = 0;
                    }
                    batchColor = c;
                    batch.Append(line);
                    batch.Append('\n');
                }
                if (batch.Length > 0) AppendColored(batch.ToString(), batchColor);
            }

            // 限制日志长度，避免长时间大复制把内存吃满
            if (txtLog.TextLength > 600000)
            {
                string[] all = txtLog.Lines;
                if (all.Length > 2000)
                {
                    string[] sub = new string[2000];
                    Array.Copy(all, all.Length - 2000, sub, 0, 2000);
                    txtLog.Lines = sub;
                }
            }
            txtLog.SelectionStart = txtLog.TextLength;
            txtLog.SelectionLength = 0;
            txtLog.ScrollToCaret();
        }

        private void AppendColored(string text, Color color)
        {
            txtLog.SelectionStart = txtLog.TextLength;
            txtLog.SelectionLength = 0;
            txtLog.SelectionColor = color;
            txtLog.AppendText(text);
        }

        // 着色规则用的正则。关键点：关键字后面必须跟**非零**数字才算报错，
        // 否则 "失败 0"、"不一致 0" 这种成功的汇总行会被误标成红色。
        private static readonly Regex ErrCodeRe =
            new Regex(@"\(0x[0-9A-Fa-f]{8}\)", RegexOptions.Compiled);
        private static readonly Regex ErrCountRe =
            new Regex(@"(失败|错误|異常|异常|不一致|缺失|ERROR)\s*[:：]?\s*([1-9][0-9]*)",
                      RegexOptions.Compiled);
        private static readonly string[] ErrPhrases = new string[]
        {
            "复制失败", "校验失败", "扫描失败", "打开失败", "保存失败",
            "发生异常", "复制没有完成", "拒绝访问", "无法访问", "已中断"
        };

        internal static Color ClassifyLogLine(string line)
        {
            if (line == null) return Theme.LogText;
            string t = line.Trim();
            if (t.Length == 0) return Theme.LogText;

            if (t.StartsWith("=====")) return Theme.LogHead;

            // 退出码行单独判断：只有 >=8 才是真失败（0=无变化，1~7 都算成功）
            if (t.StartsWith("退出码：") || t.StartsWith("退出码:"))
                return FirstNumber(t) >= 8 ? Theme.LogErr : Theme.LogOk;

            if (t.StartsWith("状态：") && t.IndexOf("停止") >= 0) return Theme.Warn;

            // 1) robocopy 的错误码，英文版是 "ERROR 32 (0x00000020)"，中文版是 "错误 32 (0x00000020)"
            if (ErrCodeRe.IsMatch(t)) return Theme.LogErr;

            // 2) 关键字后面跟着非零数字才是报错
            if (ErrCountRe.IsMatch(t)) return Theme.LogErr;

            // 3) 明确的失败描述
            for (int i = 0; i < ErrPhrases.Length; i++)
                if (t.IndexOf(ErrPhrases[i]) >= 0) return Theme.LogErr;

            // 4) 校验明细行（"  不一致：D:\..." / "  缺失　：D:\..."）
            if (t.StartsWith("不一致") || t.StartsWith("缺失")) return Theme.LogErr;

            // 5) 细节行用灰色，弱化显示
            if (t.StartsWith("[目录]") || t.StartsWith("命令：") || t.StartsWith("源　：")
                || t.StartsWith("目标：") || t.StartsWith("模式：") || t.StartsWith("扫描完成")
                || t.StartsWith("耗时：") || t.StartsWith("引擎：") || t.StartsWith("就绪")
                || t.StartsWith("排除目录：") || t.StartsWith("排除文件："))
                return Theme.LogDim;

            // 6) 成功
            if (t.IndexOf("完成") >= 0 || t.IndexOf("成功") >= 0 || t.IndexOf("通过") >= 0
                || t.IndexOf("一致") >= 0 || t.IndexOf("已保存") >= 0)
                return Theme.LogOk;

            return Theme.LogText;
        }

        /// <summary>取出字符串里第一个整数，取不到返回 -1。</summary>
        private static int FirstNumber(string s)
        {
            for (int i = 0; i < s.Length; i++)
            {
                if (char.IsDigit(s[i]))
                {
                    int j = i;
                    while (j < s.Length && char.IsDigit(s[j])) j++;
                    int v;
                    if (int.TryParse(s.Substring(i, j - i), out v)) return v;
                    i = j;
                }
            }
            return -1;
        }

        private void Ui(Action a)
        {
            if (a == null) return;
            if (IsDisposed) return;
            try
            {
                if (InvokeRequired) BeginInvoke(a);
                else a();
            }
            catch { }
        }

        private void Warn(string msg)
        {
            MessageBox.Show(this, msg, "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        // ==============================================================
        // 设置持久化 + 路径历史 + 关闭
        // ==============================================================
        private static string SettingsPath()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RobocopyGuiTool");
            return Path.Combine(dir, "settings.ini");
        }

        private void LoadSettings()
        {
            try
            {
                string p = SettingsPath();
                if (!File.Exists(p)) return;
                string[] lines = File.ReadAllLines(p, Encoding.UTF8);
                foreach (string raw in lines)
                {
                    int eq = raw.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = raw.Substring(0, eq).Trim();
                    string v = raw.Substring(eq + 1).Trim();
                    switch (k)
                    {
                        case "mode":
                            if (v == "File") rbFile.Checked = true;
                            else if (v == "Verify") rbVerify.Checked = true;
                            else rbFolder.Checked = true;
                            break;
                        case "source": txtSource.Text = v; break;
                        case "dest": txtDest.Text = v; break;
                        case "verify": chkVerify.Checked = v == "1"; break;
                        case "mirror": chkMirror.Checked = v == "1"; break;
                        case "dryrun": chkDryRun.Checked = v == "1"; break;
                        case "bigfile": chkBigFile.Checked = v == "1"; break;
                        case "xdirs": txtXD.Text = v; break;
                        case "xfiles": txtXF.Text = v; break;
                        case "threads": SetNum(numThreads, v); break;
                        case "src_hist": LoadHistory(txtSource, v); break;
                        case "dst_hist": LoadHistory(txtDest, v); break;
                    }
                }
            }
            catch { }
        }

        private static void LoadHistory(TextBox box, string v)
        {
            try
            {
                AutoCompleteStringCollection list = new AutoCompleteStringCollection();
                foreach (string item in v.Split(new char[] { '|' }, StringSplitOptions.RemoveEmptyEntries))
                    if (item.Trim().Length > 0) list.Add(item.Trim());
                if (list.Count > 0)
                {
                    box.AutoCompleteCustomSource = list;
                    box.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
                    box.AutoCompleteSource = AutoCompleteSource.CustomSource;
                }
            }
            catch { }
        }

        /// <summary>把刚用过的路径放到历史最前（去重，最多 10 条）。</summary>
        private void RememberHistory(TextBox box)
        {
            try
            {
                string cur = box.Text.Trim();
                if (cur.Length == 0) return;
                AutoCompleteStringCollection list = box.AutoCompleteCustomSource;
                if (list == null) list = new AutoCompleteStringCollection();
                List<string> items = new List<string>();
                foreach (string item in list)
                    if (item.Trim().Length > 0 && !item.Trim().Equals(cur, StringComparison.OrdinalIgnoreCase))
                        items.Add(item.Trim());
                items.Insert(0, cur);
                if (items.Count > 10) items.RemoveRange(10, items.Count - 10);
                AutoCompleteStringCollection nl = new AutoCompleteStringCollection();
                foreach (string it in items) nl.Add(it);
                box.AutoCompleteCustomSource = nl;
                box.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
                box.AutoCompleteSource = AutoCompleteSource.CustomSource;
            }
            catch { }
        }

        private static void SaveHistory(StringBuilder sb, string key, TextBox box)
        {
            try
            {
                AutoCompleteStringCollection list = box.AutoCompleteCustomSource;
                if (list == null || list.Count == 0) return;
                StringBuilder v = new StringBuilder();
                foreach (string item in list)
                {
                    if (item.IndexOf('|') >= 0) continue;   // 带分隔符的路径没法存
                    if (v.Length > 0) v.Append('|');
                    v.Append(item);
                }
                sb.AppendLine(key + "=" + v.ToString());
            }
            catch { }
        }

        private static void SetNum(NumericUpDown n, string v)
        {
            decimal d;
            if (decimal.TryParse(v, out d))
            {
                if (d < n.Minimum) d = n.Minimum;
                if (d > n.Maximum) d = n.Maximum;
                n.Value = d;
            }
        }

        private void SaveSettings()
        {
            try
            {
                string p = SettingsPath();
                Directory.CreateDirectory(Path.GetDirectoryName(p));
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("mode=" + (rbVerify.Checked ? "Verify" : rbFile.Checked ? "File" : "Folder"));
                sb.AppendLine("source=" + txtSource.Text.Trim());
                sb.AppendLine("dest=" + txtDest.Text.Trim());
                sb.AppendLine("threads=" + numThreads.Value);
                sb.AppendLine("verify=" + (chkVerify.Checked ? "1" : "0"));
                sb.AppendLine("mirror=" + (chkMirror.Checked ? "1" : "0"));
                sb.AppendLine("dryrun=" + (chkDryRun.Checked ? "1" : "0"));
                sb.AppendLine("bigfile=" + (chkBigFile.Checked ? "1" : "0"));
                sb.AppendLine("xdirs=" + txtXD.Text.Trim());
                sb.AppendLine("xfiles=" + txtXF.Text.Trim());
                SaveHistory(sb, "src_hist", txtSource);
                SaveHistory(sb, "dst_hist", txtDest);
                File.WriteAllText(p, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (running)
            {
                DialogResult r = MessageBox.Show(this,
                    "还在复制中，确定要退出吗？\r\n正在复制的文件会不完整。",
                    "确认退出", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                if (r != DialogResult.Yes) { e.Cancel = true; return; }
                cancelFlag = true;
                if (runner != null) runner.Cancel();
            }
            SaveSettings();
        }
    }
}
