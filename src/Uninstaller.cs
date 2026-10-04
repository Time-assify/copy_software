using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace CopyTool
{
    // ------------------------------------------------------------------
    // 卸载程序：确认 -> 移除快捷方式/系统登记/设置/程序目录。
    // 安装时被复制到安装目录，并登记到控制面板"程序和功能"。
    // ------------------------------------------------------------------
    public class UninstallForm : Form
    {
        private Label lblInfo;
        private FlatProgressBar bar;
        private Label lblStep;
        private FlatButton btnGo, btnCancel;
        private string dir;
        private bool uninstalled;

        public UninstallForm()
        {
            Text = "卸载 复制工具";
            ClientSize = new Size(Dpi.S(520), Dpi.S(300));
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = Theme.FontBase;
            BackColor = Theme.Page;

            dir = FindInstallDir();

            Label t1 = new Label();
            t1.Text = "卸载 复制工具";
            t1.SetBounds(26, 26, 300, 34);
            t1.Font = Theme.MakeFont("Microsoft YaHei UI", 14, FontStyle.Bold);
            t1.ForeColor = Theme.Text;
            Controls.Add(t1);

            lblInfo = new Label();
            lblInfo.SetBounds(26, 72, 470, 76);
            lblInfo.ForeColor = Theme.TextMuted;
            lblInfo.Text = "将从下面这个目录移除程序，并删除快捷方式、系统卸载登记和设置文件：\r\n" + dir +
                           "\r\n\r\n源码文件夹不会被碰。";
            Controls.Add(lblInfo);

            bar = new FlatProgressBar();
            bar.SetBounds(26, 168, 470, 20);
            Controls.Add(bar);

            lblStep = new Label();
            lblStep.SetBounds(26, 196, 470, 20);
            lblStep.ForeColor = Theme.TextMuted;
            Controls.Add(lblStep);

            btnGo = new FlatButton();
            btnGo.Text = "卸载";
            btnGo.Kind = BtnKind.Primary;
            btnGo.SetBounds(364, 250, 132, 34);
            btnGo.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            btnGo.Click += delegate { DoUninstall(); };
            Controls.Add(btnGo);

            btnCancel = new FlatButton();
            btnCancel.Text = "取消";
            btnCancel.SetBounds(222, 250, 132, 34);
            btnCancel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            btnCancel.Click += delegate { Close(); };
            Controls.Add(btnCancel);

            Dpi.ScaleTree(this);
        }

        private string FindInstallDir()
        {
            // 优先用注册表里登记的安装位置；没有就用自己所在的目录
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(InstallerEngine.RegKey))
                {
                    if (k != null)
                    {
                        string v = k.GetValue("InstallLocation") as string;
                        if (!string.IsNullOrEmpty(v) && Directory.Exists(v)) return v;
                    }
                }
            }
            catch { }
            try { return Path.GetDirectoryName(Application.ExecutablePath); }
            catch { return ""; }
        }

        private void DoUninstall()
        {
            if (uninstalled) { Close(); return; }
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                MessageBox.Show(this, "找不到安装目录。", "卸载 复制工具", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                Close();
                return;
            }
            bool selfDir = string.Equals(dir, Path.GetDirectoryName(Application.ExecutablePath),
                                         StringComparison.OrdinalIgnoreCase);
            // 任一版式的 exe 在就认这个安装目录（注意是 File.Exists——旧版误用 Directory.Exists）
            bool hasApp = File.Exists(Path.Combine(dir, InstallerEngine.AppExeName))
                       || File.Exists(Path.Combine(dir, InstallerEngine.AppExeNameAlt));
            if (!selfDir && !hasApp)
            {
                MessageBox.Show(this, "这个目录里没有 复制工具，可能已经卸载过了。", "卸载 复制工具",
                                MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            btnGo.Enabled = false; btnCancel.Enabled = false;
            try
            {
                InstallerEngine.Uninstall(dir, true, true, true, selfDir, delegate(int done, int total, string msg)
                {
                    lblStep.Text = msg == null ? "" : msg;
                    bar.SetPercent(Math.Min(100, done * 100 / Math.Max(1, total)));
                    Application.DoEvents();
                    if (msg != null) Thread.Sleep(150);
                });
                lblInfo.Text = "卸载完成。快捷方式、系统登记和设置文件都已删除。\r\n" +
                               "程序文件已清理；被系统占用的个别文件（卸载程序本身）\r\n将在下次重启时自动清除。";
                lblStep.Text = "";
                uninstalled = true;
                btnGo.Text = "关闭";
                btnGo.Enabled = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "卸载失败：\r\n" + ex.Message, "卸载 复制工具",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
                btnGo.Enabled = true; btnCancel.Enabled = true;
            }
        }

        [STAThread]
        public static void Main()
        {
            Dpi.Init();
            Application.EnableVisualStyles();
            Application.Run(new UninstallForm());
        }
    }
}