using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace CopyTool
{
    // ------------------------------------------------------------------
    // 安装向导：欢迎 -> 选择位置和组件 -> 安装进度 -> 完成
    // 以 requireAdministrator 清单启动，双击即弹 UAC。
    // ------------------------------------------------------------------
    public class SetupWizard : Form
    {
        private Panel pageWelcome, pageVariant, pageOptions, pageInstalling, pageDone;
        private RadioButton rbVariantClassic, rbVariantBackdrop, rbVariantBoth;
        private TextBox txtDir;
        private Label lblDirWarn;
        private CheckBox chkDesktop, chkStartMenu, chkRegister;
        private Label lblStep;
        private FlatProgressBar bar;
        private FlatButton btnBack, btnNext, btnCancel;
        private string srcDir;

        public SetupWizard()
        {
            Text = "复制工具 安装向导";
            ClientSize = new Size(Dpi.S(560), Dpi.S(400));
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = Theme.FontBase;
            BackColor = Theme.Page;

            srcDir = Path.GetDirectoryName(Application.ExecutablePath);
            BuildPages();
            Dpi.ScaleTree(this);
            ShowPage(pageWelcome);
        }

        private FlatButton MakeBtn(string text, int x)
        {
            FlatButton b = new FlatButton();
            b.Text = text;
            b.SetBounds(x, 356, 96, 32);
            b.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            Controls.Add(b);
            return b;
        }

        private void BuildPages()
        {
            btnCancel = MakeBtn("取消", 238);
            btnCancel.Click += delegate { Close(); };
            btnBack = MakeBtn("< 上一步", 344);
            btnBack.Click += delegate
            {
                // 返回上一页：选项页回版式页，版式页回欢迎页
                if (pageOptions.Visible) ShowPage(pageVariant);
                else ShowPage(pageWelcome);
            };
            btnNext = MakeBtn("下一步 >", 454);
            btnNext.Kind = BtnKind.Primary;
            btnNext.Click += delegate { OnNext(); };
            AcceptButton = btnNext;

            // ---------- 欢迎页 ----------
            pageWelcome = new Panel();
            pageWelcome.SetBounds(0, 0, 560, 344);
            pageWelcome.BackColor = Theme.Page;
            Controls.Add(pageWelcome);

            Label t1 = new Label();
            t1.Text = "欢迎安装 复制工具";
            t1.SetBounds(28, 40, 500, 40);
            t1.Font = Theme.MakeFont("Microsoft YaHei UI", 17, FontStyle.Bold);
            t1.ForeColor = Theme.Text;
            pageWelcome.Controls.Add(t1);

            Label t2 = new Label();
            t2.Text = "这个向导会把 Windows 自带的 robocopy 包装成图形界面的小工具，\r\n" +
                      "用来复制文件和文件夹：有真实进度、断点重试和完整日志。\r\n\r\n" +
                      "将安装：你选择的版式程序、使用说明、卸载程序\r\n\r\n" +
                      "提示：程序复制文件到其它盘需要管理员权限，\r\n安装和运行时会各弹一次授权窗口，点\"是\"即可。";
            t2.SetBounds(28, 96, 500, 170);
            t2.ForeColor = Theme.TextMuted;
            pageWelcome.Controls.Add(t2);

            // ---------- 版式选择页 ----------
            pageVariant = new Panel();
            pageVariant.SetBounds(0, 0, 560, 344);
            pageVariant.BackColor = Theme.Page;
            Controls.Add(pageVariant);

            Label v1 = new Label();
            v1.Text = "选择界面版式";
            v1.SetBounds(28, 26, 300, 24);
            v1.Font = Theme.MakeFont("Microsoft YaHei UI", 11, FontStyle.Bold);
            v1.ForeColor = Theme.Text;
            pageVariant.Controls.Add(v1);

            Label v2 = new Label();
            v2.Text = "两种版式的功能完全相同、设置通用，随时可以换着用；\r\n区别只在背景图怎么展示。";
            v2.SetBounds(28, 58, 504, 36);
            v2.ForeColor = Theme.TextMuted;
            pageVariant.Controls.Add(v2);

            rbVariantBoth = MakeVariantRadio(pageVariant, "两个版式都装（推荐）\r\n    桌面和开始菜单各建两个图标，随时切换", 28, 110, true);
            rbVariantClassic = MakeVariantRadio(pageVariant, "只装 立绘卡版\r\n    窗口右侧展示完整人物，背景干净清爽", 28, 168, false);
            rbVariantBackdrop = MakeVariantRadio(pageVariant, "只装 透明背景版\r\n    壁纸铺满整个窗口，卡片半透明，图片做整体氛围", 28, 226, false);

            Label v3 = new Label();
            v3.Text = "以后想加装另一个版式，把对应的 exe 和本向导放同一文件夹再跑一遍即可。";
            v3.SetBounds(28, 292, 504, 20);
            v3.ForeColor = Theme.TextFaint;
            v3.Font = Theme.FontSmall;
            pageVariant.Controls.Add(v3);

            // ---------- 选项页 ----------
            pageOptions = new Panel();
            pageOptions.SetBounds(0, 0, 560, 344);
            pageOptions.BackColor = Theme.Page;
            Controls.Add(pageOptions);

            Label l1 = new Label();
            l1.Text = "选择安装位置";
            l1.SetBounds(28, 26, 300, 24);
            l1.Font = Theme.MakeFont("Microsoft YaHei UI", 11, FontStyle.Bold);
            l1.ForeColor = Theme.Text;
            pageOptions.Controls.Add(l1);

            txtDir = new TextBox();
            txtDir.SetBounds(28, 60, 404, 26);
            txtDir.BorderStyle = BorderStyle.FixedSingle;
            txtDir.Text = InstallerEngine.DefaultDir();
            txtDir.TextChanged += delegate { UpdateDirWarn(); };
            pageOptions.Controls.Add(txtDir);

            FlatButton btnBrowse = new FlatButton();
            btnBrowse.Text = "浏览...";
            btnBrowse.Kind = BtnKind.Secondary;
            btnBrowse.SetBounds(444, 58, 88, 30);
            btnBrowse.Click += delegate { BrowseDir(); };
            pageOptions.Controls.Add(btnBrowse);

            lblDirWarn = new Label();
            lblDirWarn.SetBounds(28, 92, 504, 20);
            lblDirWarn.ForeColor = Theme.TextFaint;
            lblDirWarn.Font = Theme.FontSmall;
            pageOptions.Controls.Add(lblDirWarn);

            chkDesktop = MakeOpt(pageOptions, "创建桌面快捷方式（以管理员身份运行）", 28, 140, true);
            chkStartMenu = MakeOpt(pageOptions, "创建开始菜单快捷方式", 28, 172, true);
            chkRegister = MakeOpt(pageOptions, "添加到控制面板的\"程序和功能\"（这样才能在系统里卸载）", 28, 204, true);

            Label l2 = new Label();
            l2.Text = "点击\"安装\"开始。已装过的话会直接覆盖升级，不影响你填过的路径设置。";
            l2.SetBounds(28, 250, 500, 20);
            l2.ForeColor = Theme.TextFaint;
            l2.Font = Theme.FontSmall;
            pageOptions.Controls.Add(l2);

            // ---------- 安装中页 ----------
            pageInstalling = new Panel();
            pageInstalling.SetBounds(0, 0, 560, 344);
            pageInstalling.BackColor = Theme.Page;
            Controls.Add(pageInstalling);

            Label l3 = new Label();
            l3.Text = "正在安装...";
            l3.SetBounds(28, 120, 300, 30);
            l3.Font = Theme.MakeFont("Microsoft YaHei UI", 12, FontStyle.Bold);
            l3.ForeColor = Theme.Text;
            pageInstalling.Controls.Add(l3);

            bar = new FlatProgressBar();
            bar.SetBounds(28, 164, 504, 20);
            pageInstalling.Controls.Add(bar);

            lblStep = new Label();
            lblStep.SetBounds(28, 196, 504, 20);
            lblStep.ForeColor = Theme.TextMuted;
            pageInstalling.Controls.Add(lblStep);

            // ---------- 完成页 ----------
            pageDone = new Panel();
            pageDone.SetBounds(0, 0, 560, 344);
            pageDone.BackColor = Theme.Page;
            Controls.Add(pageDone);

            Label t3 = new Label();
            t3.Text = "安装完成";
            t3.SetBounds(28, 60, 300, 40);
            t3.Font = Theme.MakeFont("Microsoft YaHei UI", 16, FontStyle.Bold);
            t3.ForeColor = Theme.Text;
            pageDone.Controls.Add(t3);

            Label t4 = new Label();
            t4.Text = "桌面和开始菜单里都有\"复制工具\"图标，双击即可使用。\r\n" +
                      "以后想卸载：开始菜单里的\"卸载 复制工具\"，或控制面板的\"程序和功能\"。";
            t4.SetBounds(28, 112, 500, 48);
            t4.ForeColor = Theme.TextMuted;
            pageDone.Controls.Add(t4);
        }

        private CheckBox MakeOpt(Panel p, string text, int x, int y, bool @checked)
        {
            CheckBox c = new CheckBox();
            c.Text = text;
            c.SetBounds(x, y, 504, 24);
            c.Checked = @checked;
            c.BackColor = Theme.Page;
            c.ForeColor = Theme.Text;
            p.Controls.Add(c);
            return c;
        }

        /// <summary>版式选择页的单选项（两行文字：标题 + 缩进说明）。</summary>
        private RadioButton MakeVariantRadio(Panel p, string text, int x, int y, bool @checked)
        {
            RadioButton r = new RadioButton();
            r.Text = text;
            r.SetBounds(x, y, 504, 48);
            r.Checked = @checked;
            r.BackColor = Theme.Page;
            r.ForeColor = Theme.Text;
            p.Controls.Add(r);
            return r;
        }

        /// <summary>当前选中的版式（默认两个都装）。</summary>
        private InstallerEngine.InstallVariant SelectedVariant
        {
            get
            {
                if (rbVariantBackdrop.Checked) return InstallerEngine.InstallVariant.Backdrop;
                if (rbVariantClassic.Checked) return InstallerEngine.InstallVariant.Classic;
                return InstallerEngine.InstallVariant.Both;
            }
        }

        private void ShowPage(Panel page)
        {
            pageWelcome.Visible = page == pageWelcome;
            pageVariant.Visible = page == pageVariant;
            pageOptions.Visible = page == pageOptions;
            pageInstalling.Visible = page == pageInstalling;
            pageDone.Visible = page == pageDone;
            btnCancel.Visible = page == pageWelcome || page == pageVariant || page == pageOptions;
            btnBack.Visible = page == pageVariant || page == pageOptions;
            btnNext.Visible = page != pageInstalling;
            if (page == pageWelcome || page == pageVariant) { btnNext.Text = "下一步 >"; }
            else if (page == pageOptions) { btnNext.Text = "安装"; }
            else if (page == pageDone) { btnNext.Text = "完成"; }
        }

        private void BrowseDir()
        {
            FolderBrowserDialog d = new FolderBrowserDialog();
            d.Description = "选择安装位置（不存在的目录会自动创建）";
            d.ShowNewFolderButton = true;
            if (d.ShowDialog(this) == DialogResult.OK) txtDir.Text = d.SelectedPath;
        }

        private void UpdateDirWarn()
        {
            try
            {
                string d = txtDir.Text.Trim();
                if (d.Length == 0) { lblDirWarn.Text = ""; return; }
                if (File.Exists(Path.Combine(d, InstallerEngine.AppExeName)))
                    lblDirWarn.Text = "检测到这里已经安装过，将继续覆盖升级。";
                else
                    lblDirWarn.Text = "";
            }
            catch { lblDirWarn.Text = ""; }
        }

        private void OnNext()
        {
            if (pageWelcome.Visible) { ShowPage(pageVariant); return; }

            if (pageVariant.Visible) { ShowPage(pageOptions); return; }

            if (pageOptions.Visible)
            {
                string dst = txtDir.Text.Trim().Trim('"').Trim();
                if (dst.Length < 3)
                {
                    Warn("请填写安装位置（例如 C:\\Program Files\\复制工具）。");
                    return;
                }
                if (dst.EndsWith(":\\"))
                {
                    Warn("建议装到一个专门的文件夹里，而不是盘符根目录。");
                    return;
                }
                btnBack.Enabled = false; btnNext.Enabled = false; btnCancel.Enabled = false;
                ShowPage(pageInstalling);
                try
                {
                    InstallerEngine.Install(srcDir, dst, SelectedVariant, chkDesktop.Checked, chkStartMenu.Checked,
                                            chkRegister.Checked, delegate(int done, int total, string msg)
                    {
                        lblStep.Text = msg == null ? "" : msg;
                        bar.SetPercent(Math.Min(100, done * 100 / Math.Max(1, total)));
                        Application.DoEvents();
                        if (msg != null) Thread.Sleep(180);
                    });
                    lblStep.Text = "全部完成";
                    bar.SetPercent(100);
                    ShowPage(pageDone);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "安装失败：\r\n" + ex.Message + "\r\n\r\n" +
                                    "常见原因：目标目录需要权限、程序正在运行。", "安装失败",
                                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                    ShowPage(pageOptions);
                    btnBack.Enabled = true; btnNext.Enabled = true; btnCancel.Enabled = true;
                    return;
                }
                btnBack.Enabled = true; btnNext.Enabled = true; btnCancel.Enabled = true;
                return;
            }

            if (pageDone.Visible)
            {
                // 装完了。桌面/开始菜单图标立即可用，不需要再代用户启动程序
                Close();
            }
        }

        private void Warn(string msg)
        {
            MessageBox.Show(this, msg, "安装向导", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        // ---- 供界面预览自测驱动（internal，正常使用不经过这些） ----
        internal void TestClickNext() { OnNext(); }
        internal bool TestOnWelcome { get { return pageWelcome.Visible; } }
        internal bool TestOnVariant { get { return pageVariant.Visible; } }
        internal bool TestOnOptions { get { return pageOptions.Visible; } }
        internal bool TestOnDone { get { return pageDone.Visible; } }
        internal string TestInstallDir { set { txtDir.Text = value; } }
        internal void TestSetVariant(InstallerEngine.InstallVariant v)
        {
            rbVariantClassic.Checked = v == InstallerEngine.InstallVariant.Classic;
            rbVariantBackdrop.Checked = v == InstallerEngine.InstallVariant.Backdrop;
            rbVariantBoth.Checked = v == InstallerEngine.InstallVariant.Both;
        }
        internal void TestOptions(bool desktop, bool startMenu, bool register)
        {
            chkDesktop.Checked = desktop;
            chkStartMenu.Checked = startMenu;
            chkRegister.Checked = register;
        }

        [STAThread]
        public static void Main()
        {
            Dpi.Init();
            Application.EnableVisualStyles();
            Application.Run(new SetupWizard());
        }
    }
}