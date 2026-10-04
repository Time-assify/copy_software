using System;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Windows.Forms;

namespace CopyTool
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Dpi.Init();   // 必须在创建任何窗口之前声明 DPI 感知
            // 实验开关：壁纸铺满窗口背景、卡片半透明（区别于默认的右侧立绘卡版式）
            foreach (string a in args)
                if (a == "--backdrop") MainForm.BackdropMode = true;
            // 用主程序自身做无界面的写入诊断，避免命令行进程可写却 GUI 被拦截的假象。
            if (args.Length == 3 && args[0] == "--diagnose-dest")
            {
                CopyRequest req = new CopyRequest();
                req.Dest = args[1];
                string error = RobocopyRunner.ProbeDestWritable(req);
                StringBuilder report = new StringBuilder();
                report.AppendLine("时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                report.AppendLine("程序：" + Application.ExecutablePath);
                report.AppendLine("账号：" + WindowsIdentity.GetCurrent().Name);
                report.AppendLine("管理员权限：" + new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator));
                report.AppendLine("目标：" + req.Dest);
                report.AppendLine("创建并删除测试文件：" + (error == null ? "成功" : "失败"));
                if (error != null) report.AppendLine(error);
                File.WriteAllText(args[2], report.ToString(), Encoding.UTF8);
                Environment.ExitCode = error == null ? 0 : 1;
                return;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                Application.Run(new MainForm());
            }
            catch (Exception ex)
            {
                MessageBox.Show("程序发生异常：\r\n\r\n" + ex.ToString(), "复制工具",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
