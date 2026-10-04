using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace CopyTool
{
    /// <summary>
    /// 界面预览 + 布局自检 + 像素自检。
    /// 当前环境没有交互桌面，直接启动窗口会挂住，所以改成离屏渲染：
    /// 建好窗口后放到屏幕外，用 DrawToBitmap 出图，然后
    ///   1) 检查所有控件的边界、文字宽度、同级重叠；
    ///   2) 采样渲染结果的颜色，确认自绘的圆角/配色/进度条真的画出来了。
    /// 用法：界面预览.exe [输出图片路径]
    /// </summary>
    public static class UiPreview
    {
        private static int _fail;
        // DrawToBitmap 会把窗口的非客户区（标题栏 + 边框）也画进位图，
        // 所以客户区坐标要加上这个偏移才能对上位图里的像素。
        private static int _offX;
        private static int _offY;

        [STAThread]
        public static int Main(string[] args)
        {
            Dpi.Init();
            try { Console.OutputEncoding = Encoding.UTF8; }
            catch { }

            string outPath = args.Length > 0 && !args[0].StartsWith("--") ? args[0] : "界面预览.png";
            bool backdrop = Array.IndexOf(args, "--backdrop") >= 0;
            MainForm.BackdropMode = backdrop;
            bool veilSet = false;
            foreach (string a in args)
                if (a.StartsWith("--veil:"))
                {
                    int v;
                    if (int.TryParse(a.Substring("--veil:".Length), out v))
                    {
                        Art.BackdropVeilAlpha = v;
                        veilSet = true;
                    }
                }
            // 没显式给浓度时，按透明背景版的出厂值取景，保证预览和实际 exe 一致
            if (backdrop && !veilSet) Art.BackdropVeilAlpha = 178;

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            MainForm f = new MainForm();
            f.StartPosition = FormStartPosition.Manual;
            f.Location = new Point(-4000, -4000);   // 放到屏幕外，不打扰用户
            f.ShowInTaskbar = false;
            f.Show();
            Application.DoEvents();

            string[] demoLines = FillDemo(f);
            Application.DoEvents();
            // ---- 布局自检 ----
            Console.WriteLine("窗口客户区：" + f.ClientSize.Width + " x " + f.ClientSize.Height);
            Console.WriteLine();
            Console.WriteLine("── 布局自检");
            List<string> problems = new List<string>();
            CheckLayout(f, problems);
            if (problems.Count == 0)
                Console.WriteLine("   [通过] 控件未越界、文字未截断、同级控件无重叠");

            // ---- 日志着色自检 ----
            Console.WriteLine();
            Console.WriteLine("── 日志着色自检");
            LogColorChecks(problems);

            // ---- 渲染到离屏位图 ----
            Bitmap bmp = null;
            try
            {
                Rectangle scr = f.RectangleToScreen(f.ClientRectangle);
                _offX = scr.Left - f.Left;
                _offY = scr.Top - f.Top;
                bmp = new Bitmap(f.Width, f.Height);
                f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
            }
            catch (Exception ex)
            {
                Console.WriteLine("出图失败：" + ex.Message);
                _fail++;
            }

            // ---- 像素自检：确认自绘效果真的落到画面上了 ----
            Console.WriteLine();
            Console.WriteLine("── 像素自检");
            Console.WriteLine("   位图 " + (bmp != null ? bmp.Width + "x" + bmp.Height : "无")
                              + "，客户区偏移 (" + _offX + "," + _offY + ")");
            if (bmp != null)
            {
                RichTextBox rtb = Find(f, "txtLog") as RichTextBox;
                if (rtb != null) RenderLogFallback(f, bmp, rtb, demoLines);
                if (backdrop) BackdropChecks(f, bmp, problems);
                else PixelChecks(f, bmp, problems);
            }

            // ---- 布局清单（没有图形界面时用文字核对）----
            Console.WriteLine();
            Console.WriteLine("── 布局清单");
            Dump(f, 0);

            // ---- 保存 ----
            if (bmp != null)
            {
                try
                {
                    bmp.Save(outPath, ImageFormat.Png);
                    Console.WriteLine();
                    Console.WriteLine("预览图已保存：" + Path.GetFullPath(outPath)
                                      + "  (" + new FileInfo(outPath).Length + " 字节)");
                }
                catch (Exception ex)
                {
                    Console.WriteLine("保存图片失败：" + ex.Message);
                    _fail++;
                }
                bmp.Dispose();
            }

            f.Close();

            // ---- 安装向导自测：模拟点击走完"欢迎 -> 选项 -> 安装 -> 完成" ----
            Console.WriteLine();
            Console.WriteLine("── 安装向导自测");
            try
            {
                TestWizardFlow(problems);
            }
            catch (Exception ex)
            {
                problems.Add("安装向导自测异常：" + ex.Message);
                Console.WriteLine("   [失败] 异常：" + ex.Message);
                _fail++;
            }

            Console.WriteLine();
            if (problems.Count == 0)
            {
                Console.WriteLine("界面自检通过（布局 + 像素）");
            }
            else
            {
                Console.WriteLine("界面自检发现 " + problems.Count + " 个问题：");
                foreach (string s in problems) Console.WriteLine("   - " + s);
            }
            return problems.Count == 0 ? 0 : 1;
        }

        // =============================================================
        // 安装向导：模拟点击走完整安装流程（装到临时目录，不碰系统）
        // =============================================================
        private static void TestWizardFlow(List<string> problems)
        {
            string dst = Path.Combine(Path.GetTempPath(), "wiz_test_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            SetupWizard w = new SetupWizard();
            w.StartPosition = FormStartPosition.Manual;
            w.Location = new Point(-4000, -4000);
            w.ShowInTaskbar = false;
            w.Show();
            Application.DoEvents();

            P(w.TestOnWelcome, "初始在欢迎页", problems);
            w.TestClickNext();
            P(w.TestOnVariant, "点\"下一步\"能进入版式选择页", problems);
            w.TestSetVariant(InstallerEngine.InstallVariant.Both);
            w.TestClickNext();
            P(w.TestOnOptions, "选完版式能进入选项页", problems);
            w.TestOptions(false, false, false);
            w.TestInstallDir = dst;
            w.TestClickNext();          // 这次"下一步"就是"安装"按钮
            Application.DoEvents();
            P(w.TestOnDone, "安装完成能到达完成页", problems);
            P(Directory.Exists(dst)
              && File.Exists(Path.Combine(dst, InstallerEngine.AppExeName))
              && File.Exists(Path.Combine(dst, InstallerEngine.AppExeNameAlt))
              && File.Exists(Path.Combine(dst, InstallerEngine.UninstallExeName)),
              "向导真的把两个版式都装到了指定目录", problems);
            w.TestClickNext();          // "完成"（runAfter=false，不会启动主程序）
            Application.DoEvents();
            P(w.IsDisposed, "点\"完成\"后向导正常关闭", problems);

            try { if (Directory.Exists(dst)) Directory.Delete(dst, true); } catch { }
        }

        private static void P(bool ok, string name, List<string> problems)
        {
            Console.WriteLine("   [" + (ok ? "通过" : "失败") + "] " + name);
            if (!ok) { problems.Add(name); _fail++; }
        }

        // =============================================================
        // 示例内容
        // =============================================================
        private static string[] FillDemo(MainForm f)
        {
            TextBox src = Find(f, "txtSource") as TextBox;
            TextBox dst = Find(f, "txtDest") as TextBox;
            TextBox xd = Find(f, "txtXD") as TextBox;
            TextBox xf = Find(f, "txtXF") as TextBox;
            RichTextBox log = Find(f, "txtLog") as RichTextBox;
            FlatProgressBar bar = Find(f, "bar") as FlatProgressBar;
            Label status = Find(f, "lblStatus") as Label;
            Label count = Find(f, "lblCount") as Label;
            Label speed = Find(f, "lblSpeed") as Label;

            if (src != null) src.Text = @"D:\项目资料\2026 年度归档";
            if (dst != null) dst.Text = @"E:\备份\归档 2026";
            if (xd != null) xd.Text = "node_modules;.git";
            if (xf != null) xf.Text = "*.tmp;~$*";
            if (bar != null) bar.SetPercent(42);
            if (status != null) status.Text = "正在复制 42%";
            if (count != null) count.Text = "已处理 1,284 / 3,006 个文件　6.42 GB / 15.30 GB";
            if (speed != null) speed.Text = "速度 186.4 MB/s　剩余约 48 秒　当前：设计稿\\最终版\\主视觉 终稿.psd";

            if (log != null)
            {
                log.Clear();
                string[] demo = new string[]
                {
                    "========== 2026-10-01 18:52:03 开始 ==========",
                    "模式：复制文件夹",
                    "源　：D:\\项目资料\\2026 年度归档",
                    "目标：E:\\备份\\归档 2026",
                    "扫描完成：共 3,006 个文件，15.30 GB",
                    "[目录] 设计稿\\最终版",
                    "[文件] 设计稿\\最终版\\主视觉 终稿.psd   248.6 MB",
                    "[文件] 文档\\需求说明 v3.docx   1.2 MB",
                    "[文件] 数据\\清单 2026.csv   842.0 KB",
                    "========== 结果 ==========",
                    "退出码：1    （0=无变化，1=成功复制，2~7 也是成功，>=8 才是失败）",
                    "文件：总计 3,006，复制 3,004，跳过 2，不匹配 0，失败 0",
                    "校验：已校验 3,004，一致 3,004，不一致 0，缺失 0",
                    "耗时：2 分 18 秒",
                };
                for (int i = 0; i < demo.Length; i++)
                {
                    log.SelectionStart = log.TextLength;
                    log.SelectionLength = 0;
                    log.SelectionColor = MainForm.ClassifyLogLine(demo[i]);
                    log.AppendText(demo[i] + "\r\n");
                }
                log.SelectionStart = log.TextLength;
                log.SelectionLength = 0;
                log.ScrollToCaret();
                return demo;
            }
            return new string[0];
        }

        // =============================================================
        // 日志区域补画
        // RichEdit 不响应 WM_PRINT，DrawToBitmap 拍不到它的内容（会是一片灰）。
        // 真实窗口里它显示正常，这里只是为了让预览图和真实界面一致，
        // 用同样的字体和配色把日志重画到图上。
        // =============================================================
        private static void RenderLogFallback(Form f, Bitmap bmp, RichTextBox log, string[] lines)
        {
            if (log == null || lines == null || lines.Length == 0) return;
            Point p = AbsPos(f, log);
            Rectangle area = new Rectangle(p.X, p.Y, log.Width, log.Height);

            Graphics g = Graphics.FromImage(bmp);
            Region old = g.Clip;
            g.SetClip(area);
            using (SolidBrush bg = new SolidBrush(Theme.LogBg)) g.FillRectangle(bg, area);

            float lh = Theme.FontMono.GetHeight(g) + 1f;
            int visible = (int)(log.Height / lh);
            if (visible < 1) visible = 1;
            int first = lines.Length - visible;
            if (first < 0) first = 0;

            float y = area.Y + 2;
            for (int i = first; i < lines.Length; i++)
            {
                using (SolidBrush b = new SolidBrush(MainForm.ClassifyLogLine(lines[i])))
                    g.DrawString(lines[i], Theme.FontMono, b, area.X + 2, y);
                y += lh;
            }
            g.Clip = old;
            g.Dispose();
        }

        /// <summary>控件在窗口客户区里的绝对坐标。</summary>
        private static Point AbsPos(Form f, Control c)
        {
            Point p = new Point(0, 0);
            Control cur = c;
            while (cur != null && cur != f)
            {
                p.Offset(cur.Left, cur.Top);
                cur = cur.Parent;
            }
            return p;
        }

        // =============================================================
        // 日志着色自检
        // 每条用例都是实际踩过的坑：关键字后面是 0 的时候绝不能标红，
        // 否则每次复制成功都会刷出红色的"失败 0"汇总行，看起来像报错。
        // =============================================================
        private static void LogColorChecks(List<string> problems)
        {
            string[] lines = new string[]
            {
                // 成功复制的汇总行：失败是 0，必须保持中性，不能标红
                "文件：总计 3,006，复制 3,004，跳过 2，不匹配 0，失败 0",
                // 真的失败时（失败 4）必须标红
                "文件：总计 3,006，复制 3,000，跳过 2，不匹配 0，失败 4",
                // 校验全部通过：绿色
                "校验：已校验 3,004，一致 3,004，不一致 0，缺失 0，读取错误 0",
                // 校验发现问题的明细行：红色
                "  不一致：D:\\项目\\主视觉 终稿.psd",
                "  缺失　：D:\\项目\\数据\\清单.csv",
                // robocopy 的真错误行（英文版系统）：红色
                "2026/10/01 19:26:22 ERROR 32 (0x00000020) Copying File D:\\a\\b.txt",
                "   错误 5 (0x00000005) 正在复制文件 D:\\a\\c.txt",
                // robocopy 汇总表头里的 FAILED 只是列名，不能标红
                "                Total    Copied   Skipped  Mismatch    FAILED    Extras",
                "    Files :         2         1         0         0         1         0",
                // 退出码说明行：>=8 才红，否则绿
                "退出码：1    （0=无变化，1=成功复制，2~7 也是成功，>=8 才是失败）",
                "退出码：9    （0=无变化，1=成功复制，2~7 也是成功，>=8 才是失败）",
                // 叙述性文字
                "复制完成",
                "复制没有完成（提前退出）。",
                "状态：已手动停止。退出码 2",
                "扫描完成：共 3,006 个文件，15.30 GB",
                "========== 结果 ==========",
            };
            Color[] want = new Color[]
            {
                Theme.LogText, Theme.LogErr, Theme.LogOk, Theme.LogErr, Theme.LogErr,
                Theme.LogErr, Theme.LogErr,
                Theme.LogText, Theme.LogText,
                Theme.LogOk, Theme.LogErr,
                Theme.LogOk, Theme.LogErr, Theme.Warn, Theme.LogDim, Theme.LogHead,
            };

            int bad = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                Color got = MainForm.ClassifyLogLine(lines[i]);
                if (got != want[i])
                {
                    bad++;
                    problems.Add("日志着色错误：\"" + lines[i] + "\" 期望 " + ColorName(want[i])
                                 + "，实际 " + ColorName(got));
                }
            }
            if (bad == 0)
                Console.WriteLine("   [通过] 日志着色 " + lines.Length + " 例（含“失败 0”不标红、真错误码标红）");
        }

        private static string ColorName(Color c)
        {
            if (c == Theme.LogErr) return "红";
            if (c == Theme.LogOk) return "绿";
            if (c == Theme.LogHead) return "蓝";
            if (c == Theme.LogDim) return "灰";
            if (c == Theme.Warn) return "橙";
            return "中性";
        }

        // =============================================================
        // 像素自检
        // =============================================================
        private static void PixelChecks(Form f, Bitmap bmp, List<string> problems)
        {
            // 页面底色（两张卡片之间的空隙）
            Probe(f, bmp, "页面底色", null, new Point(8, 350), Theme.Page, 8, problems);
            // 页头白底与强调条（页头铺到左栏右侧 x=796，高 150）
            Probe(f, bmp, "页头白底", null, new Point(500, 30), Color.White, 8, problems);
            Probe(f, bmp, "页头标题强调条", null, new Point(25, 65), Theme.Accent, 60, problems);

            // 立绘卡片：整个区域必须是照片而不是一块纯色
            ArtVariance(f, bmp, Find(f, "art"), problems);

            Control cardPath = Find(f, "cardPath");
            Control bar = Find(f, "bar");
            Control log = Find(f, "cardLog");
            Control start = Find(f, "btnStart");
            Control save = Find(f, "btnSaveLog");
            Control chk = Find(f, "chkMirror");

            // 卡片：白底 + 圆角处应露出页面底色
            Probe(f, bmp, "卡片白底", cardPath, new Point(700, 150), Color.White, 8, problems);
            Probe(f, bmp, "卡片圆角(左上角应露出页面底色)", cardPath, new Point(1, 1), Theme.Page, 20, problems);

            // 深色日志面板：卡片本体 + 日志控件区域（后者由 RenderLogFallback 补画）
            Probe(f, bmp, "日志面板深色底", log, new Point(770, 24), Theme.LogBg, 10, problems);
            RichTextBox rtb = Find(f, "txtLog") as RichTextBox;
            if (rtb != null)
            {
                if (rtb.BackColor != Theme.LogBg || rtb.ForeColor != Theme.LogText)
                    problems.Add("日志控件配色不对：背景 " + Hex(rtb.BackColor) + "，前景 " + Hex(rtb.ForeColor));
                else
                    Console.WriteLine("   [通过] 日志控件配色 = 背景 " + Hex(rtb.BackColor) + " / 前景 " + Hex(rtb.ForeColor));
                if (rtb.TextLength == 0)
                    problems.Add("日志控件没有任何内容（AppendText 可能没生效）");
                // 运行时尺寸是物理像素，先换算回设计单位，Probe 内部会再统一缩放
                Probe(f, bmp, "日志内容区底色", rtb,
                    new Point((int)((rtb.Width - 4) / Dpi.Factor), 4), Theme.LogBg, 12, problems);
            }

            // 进度条：已填充为蓝色渐变，未填充为浅灰轨道（示例进度 42%）
            Probe(f, bmp, "进度条已填充(蓝)", bar, new Point(30, 10), Theme.Accent, 30, problems);
            Probe(f, bmp, "进度条未填充(浅灰轨道)", bar, new Point(740, 10), Theme.Track, 12, problems);

            // 按钮：主按钮实心强调色，次按钮白底
            Probe(f, bmp, "主按钮实心强调色", start, new Point(18, 6), Theme.Accent, 12, problems);
            Probe(f, bmp, "次按钮白底", save, new Point(18, 6), Color.White, 8, problems);

            // 卡片内的勾选框背景必须是白色（不能是系统灰）
            Probe(f, bmp, "卡片内控件背景为白", chk,
                new Point((int)((chk.Width - 3) / Dpi.Factor), 2), Color.White, 10, problems);
        }

        /// <summary>
        /// 立绘卡片里应该是照片：在一个 3x3 的网格上采样，颜色几乎完全相同
        /// 就说明背景图没有画出来（自绘代码出错时不报异常，只是"画不出来"）。
        /// </summary>
        private static void ArtVariance(Form f, Bitmap bmp, Control art, List<string> problems)
        {
            if (art == null)
            {
                problems.Add("像素自检失败：找不到立绘卡片 art");
                _fail++;
                return;
            }
            Point o = AbsPos(f, art);
            int minR = 255, maxR = 0, minG = 255, maxG = 0, minB = 255, maxB = 0;
            int seen = 0;
            for (int gy = 1; gy <= 3; gy++)
            {
                for (int gx = 1; gx <= 3; gx++)
                {
                    Point p = new Point(o.X + art.Width * gx / 4, o.Y + art.Height * gy / 4);
                    p.Offset(_offX, _offY);
                    if (!InBounds(bmp, p)) continue;
                    Color c = bmp.GetPixel(p.X, p.Y);
                    seen++;
                    if (c.R < minR) minR = c.R; if (c.R > maxR) maxR = c.R;
                    if (c.G < minG) minG = c.G; if (c.G > maxG) maxG = c.G;
                    if (c.B < minB) minB = c.B; if (c.B > maxB) maxB = c.B;
                }
            }
            if (seen < 9)
            {
                problems.Add("像素自检失败：立绘卡片采样越界（只采到 " + seen + " 点）");
                _fail++;
                return;
            }
            int spread = (maxR - minR) + (maxG - minG) + (maxB - minB);
            if (spread < 12)
            {
                problems.Add("像素自检失败：立绘卡片是纯色（色差幅度 " + spread + "），背景图可能没画出来");
                _fail++;
            }
            else
            {
                Console.WriteLine("   [通过] 立绘卡片显示照片（9 点采样色差幅度 " + spread + "）");
            }
        }

        /// <summary>
        /// 透明背景模式的像素检查：关键只有一条——背景图要真的"透出来"。
        /// 页面空隙和卡片圆角外既不该是页面底色，也不该是纯白，而是带白纱的壁纸。
        /// </summary>
        private static void BackdropChecks(Form f, Bitmap bmp, List<string> problems)
        {
            // 右侧页边在壁图里是深色涂鸦墙，适合当"透出来"的探针；
            // 左侧页边正好是白砖墙，采出来接近纯白属于正常，不能用它判断
            ProbeNotFlat(f, bmp, "页面空隙透出背景图", null, new Point(DesignGapX, 350), problems);
            Control cardPath = Find(f, "cardPath");
            if (cardPath != null)
                ProbeNotFlat(f, bmp, "卡片圆角外透出背景图", cardPath, new Point(1, 1), problems);

            // 日志面板仍然是深色实底
            Control log = Find(f, "cardLog");
            if (log != null)
                Probe(f, bmp, "日志面板深色底", log, new Point(770, 24), Theme.LogBg, 10, problems);
        }

        // 页面最右的页边空隙（透明背景变体里卡片占满全宽，右缘留 16 设计像素）
        private const int DesignGapX = 1092;

        /// <summary>断言该点透出的是"带白纱的壁纸"：不是页面底色、不是纯白。</summary>
        private static void ProbeNotFlat(Form f, Bitmap bmp, string name, Control c, Point pt, List<string> problems)
        {
            Point p = new Point(Dpi.S(pt.X), Dpi.S(pt.Y));
            if (c != null)
            {
                Control cur = c;
                while (cur != null && cur != f)
                {
                    p.Offset(cur.Left, cur.Top);
                    cur = cur.Parent;
                }
            }
            p.Offset(_offX, _offY);
            if (!InBounds(bmp, p))
            {
                problems.Add("像素自检失败：" + name + "，采样点 (" + p.X + "," + p.Y + ") 越界");
                _fail++;
                return;
            }
            Color c0 = bmp.GetPixel(p.X, p.Y);
            bool isPage = Math.Abs(c0.R - Theme.Page.R) <= 4 && Math.Abs(c0.G - Theme.Page.G) <= 4
                          && Math.Abs(c0.B - Theme.Page.B) <= 4;
            bool isWhite = c0.R >= 251 && c0.G >= 251 && c0.B >= 251;
            if (isPage || isWhite)
            {
                problems.Add("像素自检失败：" + name + "，该点是 " + Hex(c0)
                             + "（页面底色=" + Hex(Theme.Page) + "），背景图没有透出来");
                _fail++;
            }
            else
            {
                Console.WriteLine("   [通过] " + name + " = " + Hex(c0));
            }
        }

        /// <summary>控件内相对坐标取样（会换算成全窗客户区坐标，设计单位自动按 DPI 缩放）。</summary>
        private static void Probe(Form f, Bitmap bmp, string name, Control c, Point pt,
                                  Color expect, int tol, List<string> problems)
        {
            Point p = new Point(Dpi.S(pt.X), Dpi.S(pt.Y));
            if (c != null)
            {
                Control cur = c;
                while (cur != null && cur != f)
                {
                    p.Offset(cur.Left, cur.Top);
                    cur = cur.Parent;
                }
            }
            p.Offset(_offX, _offY);

            Color found;
            Point at;
            if (Sample(bmp, p, expect, tol, Dpi.S(5), out found, out at))
            {
                Console.WriteLine("   [通过] " + name + " = " + Hex(found)
                                  + (at == p ? "" : "（在 (" + p.X + "," + p.Y + ") 附近 (" + at.X + "," + at.Y + ") 找到）"));
                return;
            }
            Color actual = InBounds(bmp, p) ? bmp.GetPixel(p.X, p.Y) : Color.Empty;
            problems.Add("像素自检失败：" + name + "，在 (" + p.X + "," + p.Y + ") 及其 ±" + Dpi.S(5) + "px 内没找到 "
                         + Hex(expect) + "（容差 " + tol + "），该点实际是 " + Hex(actual));
        }

        private static bool Sample(Bitmap bmp, Point p, Color expect, int tol, int radius,
                                   out Color found, out Point at)
        {
            for (int r = 0; r <= radius; r++)
            {
                for (int dy = -r; dy <= r; dy++)
                {
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;
                        int x = p.X + dx, y = p.Y + dy;
                        if (!InBounds(bmp, new Point(x, y))) continue;
                        Color c = bmp.GetPixel(x, y);
                        if (Math.Abs(c.R - expect.R) <= tol &&
                            Math.Abs(c.G - expect.G) <= tol &&
                            Math.Abs(c.B - expect.B) <= tol)
                        {
                            found = c;
                            at = new Point(x, y);
                            return true;
                        }
                    }
                }
            }
            found = Color.Empty;
            at = p;
            return false;
        }

        private static bool InBounds(Bitmap b, Point p)
        {
            return p.X >= 0 && p.Y >= 0 && p.X < b.Width && p.Y < b.Height;
        }

        private static string Hex(Color c)
        {
            if (c == Color.Empty) return "(越界)";
            return "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");
        }

        // =============================================================
        // 布局自检
        // =============================================================
        private static Control Find(Control parent, string name)
        {
            Control[] c = parent.Controls.Find(name, true);
            return c.Length > 0 ? c[0] : null;
        }

        private static void CheckLayout(Control parent, List<string> problems)
        {
            Rectangle pc = parent.ClientRectangle;
            foreach (Control c in parent.Controls)
            {
                if (!c.Visible) continue;

                // 1) 不能超出父容器
                if (c.Right > pc.Width || c.Bottom > pc.Height || c.Left < 0 || c.Top < 0)
                {
                    Add(problems, c, "超出父容器 " + parent.GetType().Name
                        + "：位置 (" + c.Left + "," + c.Top + ") 尺寸 " + c.Width + "x" + c.Height
                        + "，父容器客户区 " + pc.Width + "x" + pc.Height);
                }

                // 2) 文字不能被截断
                if (c.Text != null && c.Text.Length > 0 && (c is Label || c is CheckBox || c is RadioButton || c is Button))
                {
                    Size need = TextRenderer.MeasureText(c.Text, c.Font);
                    int padX = (c is Button) ? 16 : (c is CheckBox || c is RadioButton) ? 22 : 2;
                    if (c.Width < need.Width + padX)
                    {
                        Add(problems, c, "文字可能被截断：需要约 " + (need.Width + padX)
                            + "px，实际 " + c.Width + "px —— \"" + c.Text + "\"");
                    }
                    if (c.Height < need.Height)
                    {
                        Add(problems, c, "高度不足：需要约 " + need.Height + "px，实际 " + c.Height + "px");
                    }
                }

                // 3) 数值控件必须留够宽度
                if (c is NumericUpDown && c.Width < 50)
                    Add(problems, c, "数字输入框过窄：" + c.Width + "px");

                if (c.HasChildren) CheckLayout(c, problems);
            }

            // 4) 同级控件不能互相重叠
            List<Control> sibs = new List<Control>();
            foreach (Control c in parent.Controls) if (c.Visible) sibs.Add(c);
            for (int i = 0; i < sibs.Count; i++)
            {
                for (int j = i + 1; j < sibs.Count; j++)
                {
                    if (sibs[i].Bounds.IntersectsWith(sibs[j].Bounds))
                    {
                        Add(problems, sibs[i], "与同级控件 [" + sibs[j].GetType().Name
                            + (string.IsNullOrEmpty(sibs[j].Name) ? "" : " " + sibs[j].Name)
                            + "] 重叠：" + sibs[i].Bounds + " ∩ " + sibs[j].Bounds);
                    }
                }
            }
        }

        /// <summary>打印一份布局清单，便于在没有图形界面的环境下核对位置。</summary>
        private static void Dump(Control parent, int depth)
        {
            foreach (Control c in parent.Controls)
            {
                if (!c.Visible) continue;
                string label = c.Text;
                if (label != null && label.Length > 28) label = label.Substring(0, 28) + "...";
                Console.WriteLine(new string(' ', depth * 2 + 3)
                    + string.Format("{0,-15} ({1,4},{2,4}) {3,4}x{4,-3}  {5}",
                        c.GetType().Name, c.Left, c.Top, c.Width, c.Height, label));
                if (c.HasChildren) Dump(c, depth + 1);
            }
        }

        private static void Add(List<string> list, Control c, string msg)
        {
            list.Add("[" + c.GetType().Name + (string.IsNullOrEmpty(c.Name) ? "" : " " + c.Name) + "] " + msg);
        }
    }
}
