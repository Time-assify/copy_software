using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace CopyTool
{
    // ------------------------------------------------------------------
    // 安装/卸载的纯逻辑（不碰 UI）。安装向导、卸载程序共用，
    // 引擎自测也用它做"装到临时目录"的演练。
    // ------------------------------------------------------------------
    public static class InstallerEngine
    {
        public const string AppExeName = "复制工具.exe";
        public const string AppExeNameAlt = "复制工具-透明背景.exe";   // 壁纸铺满窗口的版式
        public const string DocsName = "使用说明.md";
        public const string UninstallExeName = "卸载复制工具.exe";
        public const string RegKey = "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\复制工具";
        public const string SettingsDirName = "RobocopyGuiTool";

        /// <summary>安装哪种界面版式。Both = 两个 exe 都装，桌面/开始菜单各建一个图标。</summary>
        public enum InstallVariant { Classic, Backdrop, Both }

        private const int MOVEFILE_DELAY_UNTIL_REBOOT = 0x4;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool MoveFileEx(string lpExistingFileName, string lpNewFileName, int dwFlags);

        /// <summary>版式对应的 exe 文件名。卸载时用 Both 枚举全部，保证都清理到。</summary>
        public static string[] ExeNames(InstallVariant variant)
        {
            if (variant == InstallVariant.Classic) return new string[] { AppExeName };
            if (variant == InstallVariant.Backdrop) return new string[] { AppExeNameAlt };
            return new string[] { AppExeName, AppExeNameAlt };
        }

        /// <summary>默认安装到 Program Files。</summary>
        public static string DefaultDir()
        {
            try
            {
                string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                if (string.IsNullOrEmpty(pf)) pf = @"C:\Program Files";
                return Path.Combine(pf, "复制工具");
            }
            catch { return @"C:\Program Files\复制工具"; }
        }

        /// <summary>
        /// 执行安装。progress(已完成步骤, 总步骤, 描述)。不建快捷方式/不写注册表时对应开关传 false。
        /// </summary>
        public static void Install(string srcDir, string dstDir, InstallVariant variant,
                                   bool desktopShortcut, bool startMenuShortcut,
                                   bool registerUninstall, Action<int, int, string> progress)
        {
            List<string> exes = new List<string>(ExeNames(variant));
            List<string> need = new List<string>();
            need.AddRange(exes);
            need.Add(DocsName);
            need.Add(UninstallExeName);
            foreach (string f in need)
                if (!File.Exists(Path.Combine(srcDir, f)))
                    throw new IOException("安装文件不齐全，缺少 " + f + "。请把安装向导和程序文件放在同一个文件夹里。");

            int step = 0, total = need.Count + 3;

            // 1. 建目录
            step++; if (progress != null) progress(step, total, "创建安装目录 " + dstDir);
            Directory.CreateDirectory(dstDir);

            // 2. 结束正在运行的程序（覆盖升级时文件会被占用）
            step++; if (progress != null) progress(step, total, "检查正在运行的 复制工具");
            KillApp();

            // 3~. 复制所选版式的程序文件 + 说明 + 卸载程序
            foreach (string f in exes)
            {
                step++; if (progress != null) progress(step, total, "复制 " + f);
                File.Copy(Path.Combine(srcDir, f), Path.Combine(dstDir, f), true);
            }
            step++; if (progress != null) progress(step, total, "复制 " + DocsName);
            File.Copy(Path.Combine(srcDir, DocsName), Path.Combine(dstDir, DocsName), true);
            step++; if (progress != null) progress(step, total, "复制 " + UninstallExeName);
            File.Copy(Path.Combine(srcDir, UninstallExeName), Path.Combine(dstDir, UninstallExeName), true);

            // 快捷方式 + 卸载登记
            step++; if (progress != null) progress(step, total, "创建快捷方式 / 写入卸载信息");
            if (desktopShortcut || startMenuShortcut) CreateShortcuts(dstDir, desktopShortcut, startMenuShortcut, exes);
            if (registerUninstall)
            {
                long bytes = 0;
                foreach (string f in need) bytes += new FileInfo(Path.Combine(dstDir, f)).Length;
                RegisterUninstall(dstDir, bytes);
            }
        }

        /// <summary>结束正在运行的主程序（两种版式的进程名都查，没有就跳过）。</summary>
        public static void KillApp()
        {
            foreach (string exe in ExeNames(InstallVariant.Both))
            {
                string name = Path.GetFileNameWithoutExtension(exe);
                try
                {
                    foreach (Process p in Process.GetProcessesByName(name))
                    {
                        try { p.Kill(); p.WaitForExit(3000); } catch { }
                    }
                }
                catch { }
            }
        }

        /// <summary>建桌面/开始菜单快捷方式（每种版式一个），并内置"以管理员身份运行"。</summary>
        public static void CreateShortcuts(string dstDir, bool desktop, bool startMenu, List<string> exes)
        {
            List<string> dirs = new List<string>();
            if (desktop)
                dirs.Add(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
            if (startMenu)
                dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                      "Microsoft", "Windows", "Start Menu", "Programs"));
            if (dirs.Count == 0) return;

            Type t = Type.GetTypeFromProgID("WScript.Shell");
            if (t == null) throw new IOException("系统缺少 WScript.Shell 组件，无法创建快捷方式。");
            object shell = Activator.CreateInstance(t);
            try
            {
                foreach (string dir in dirs)
                {
                    if (string.IsNullOrEmpty(dir)) continue;
                    foreach (string exe in exes)
                    {
                        string lnkPath = Path.Combine(dir, Path.GetFileNameWithoutExtension(exe) + ".lnk");
                        object lnk = t.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod,
                                                    null, shell, new object[] { lnkPath });
                        SetProp(lnk, "TargetPath", Path.Combine(dstDir, exe));
                        SetProp(lnk, "WorkingDirectory", dstDir);
                        SetProp(lnk, "IconLocation", Path.Combine(dstDir, exe) + ",0");
                        SetProp(lnk, "Description", "复制工具 - robocopy 图形界面（以管理员身份运行）");
                        t.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, lnk, null);

                        // SHLLINK 头第 21 字节置 RunAsUser 位 => 双击即请求管理员权限
                        byte[] b = File.ReadAllBytes(lnkPath);
                        if (b.Length > 21) b[21] = (byte)(b[21] | 0x20);
                        File.WriteAllBytes(lnkPath, b);
                    }
                }
            }
            finally { MarshalRelease(shell); }
        }

        private static void SetProp(object obj, string name, object value)
        {
            obj.GetType().InvokeMember(name, System.Reflection.BindingFlags.SetProperty, null, obj,
                                       new object[] { value });
        }

        private static void MarshalRelease(object com)
        {
            try { System.Runtime.InteropServices.Marshal.ReleaseComObject(com); } catch { }
        }

        /// <summary>注册到控制面板"程序和功能"，这样系统设置里也能卸载。</summary>
        public static void RegisterUninstall(string dstDir, long sizeBytes)
        {
            using (RegistryKey k = Registry.LocalMachine.CreateSubKey(RegKey))
            {
                k.SetValue("DisplayName", "复制工具 (robocopy 图形界面)");
                k.SetValue("DisplayVersion", "1.0.0");
                k.SetValue("Publisher", "本地编译");
                k.SetValue("InstallLocation", dstDir);
                k.SetValue("DisplayIcon", Path.Combine(dstDir, AppExeName));
                k.SetValue("UninstallString", "\"" + Path.Combine(dstDir, UninstallExeName) + "\"");
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                k.SetValue("EstimatedSize", (int)(sizeBytes / 1024), RegistryValueKind.DWord);
            }
        }

        /// <summary>
        /// 卸载。delaySelfDelete=true 用于卸载程序删除自己所在的目录：先清空内容，
        /// 自己的 exe 和空目录登记为"重启时删除"（MoveFileEx）。
        /// </summary>
        public static void Uninstall(string installDir, bool removeShortcuts, bool removeRegistry,
                                     bool removeSettings, bool delaySelfDelete, Action<int, int, string> progress)
        {
            if (string.IsNullOrEmpty(installDir) || !Directory.Exists(installDir))
                throw new IOException("找不到安装目录。");

            // 安全阀：绝不能删源码目录（里面有 src 和 build.ps1 的不是安装目录）
            if (Directory.Exists(Path.Combine(installDir, "src")) || File.Exists(Path.Combine(installDir, "build.ps1")))
                throw new IOException("这个目录看起来是源码目录而不是安装目录，拒绝删除：" + installDir);

            int step = 0, total = 5;
            if (progress != null) progress(++step, total, "关闭正在运行的 复制工具");
            KillApp();

            if (progress != null) progress(++step, total, "删除快捷方式");
            if (removeShortcuts)
            {
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                string startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                                "Microsoft", "Windows", "Start Menu", "Programs");
                foreach (string dir2 in new string[] { desktop, startMenu })
                {
                    // 每种版式一个快捷方式，全部枚举着删（卸载时不知道当初装了哪种）
                    foreach (string exe in ExeNames(InstallVariant.Both))
                    {
                        try
                        {
                            string lnk = Path.Combine(dir2, Path.GetFileNameWithoutExtension(exe) + ".lnk");
                            if (File.Exists(lnk)) File.Delete(lnk);
                        }
                        catch { }
                    }
                }
            }

            if (progress != null) progress(++step, total, "删除系统里的卸载登记");
            if (removeRegistry)
            {
                try { Registry.LocalMachine.DeleteSubKeyTree(RegKey, false); } catch { }
            }

            if (progress != null) progress(++step, total, "删除设置文件");
            if (removeSettings)
            {
                try
                {
                    string st = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                             SettingsDirName);
                    if (Directory.Exists(st)) Directory.Delete(st, true);
                }
                catch { }
            }

            if (progress != null) progress(++step, total, "删除程序目录 " + installDir);
            try { Environment.CurrentDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System); }
            catch { }
            if (delaySelfDelete)
            {
                // 卸载程序自己也在这个目录里，运行中删不掉自己。
                // 旧写法把目录拼进 "cmd /c timeout & rd ..." 命令行——目录里的引号/元字符会被 cmd
                // 二次解释，属于命令注入模式，已弃用。现在完全不启动任何进程：先删掉目录里除
                // 自己以外的所有内容，再用 MoveFileEx(MOVEFILE_DELAY_UNTIL_REBOOT)（Windows 安装器
                // 的标准做法）把自己的 exe 和随后的空目录登记为"重启时删除"。
                // 纯文件 API 只接受路径本身，没有命令行解释面。
                string self = null;
                try { self = Process.GetCurrentProcess().MainModule.FileName; } catch { }
                bool selfInside = self != null && string.Equals(
                    Path.GetDirectoryName(self), installDir, StringComparison.OrdinalIgnoreCase);

                foreach (string f in Directory.GetFiles(installDir))
                {
                    try
                    {
                        if (self == null || !string.Equals(f, self, StringComparison.OrdinalIgnoreCase))
                            File.Delete(f);
                    }
                    catch { }
                }
                foreach (string d in Directory.GetDirectories(installDir))
                {
                    try { Directory.Delete(d, true); } catch { }
                }

                if (selfInside)
                {
                    // 先登记 exe、后登记目录：重启时按登记顺序先删文件再删空目录
                    try { MoveFileEx(self, null, MOVEFILE_DELAY_UNTIL_REBOOT); } catch { }
                    try { MoveFileEx(installDir, null, MOVEFILE_DELAY_UNTIL_REBOOT); } catch { }
                }
                else
                {
                    // 目录里没有正在运行的 exe（测试演练等场景），直接整目录删除
                    try { Directory.Delete(installDir, true); } catch { }
                }
            }
            else
            {
                Directory.Delete(installDir, true);
            }
        }
    }
}