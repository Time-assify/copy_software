using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace CopyTool
{
    /// <summary>
    /// 引擎自测（命令行）。GUI 不好自动化，所以把引擎单独编译成控制台程序跑真实复制测试。
    /// 用法：引擎测试.exe [测试目录]
    /// </summary>
    public static class SelfTest
    {
        private static int _pass;
        private static int _fail;
        private static string _root;

        public static int Main(string[] args)
        {
            try { Console.OutputEncoding = Encoding.UTF8; }
            catch { }

            _root = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "rcgui_selftest");
            if (Directory.Exists(_root)) TryDeleteDir(_root);
            Directory.CreateDirectory(_root);

            Console.WriteLine("测试目录：" + _root);
            Console.WriteLine();

            Section("解析器：robocopy 日志（中文版）");
            TestParserChinese();
            Section("解析器：robocopy 日志（英文版，验证与语言无关）");
            TestParserEnglish();
            Section("解析器：容量字符串");
            TestSizeParsing();
            Section("解析器：脏输入 / 边界");
            TestParserEdge();
            Section("路径工具");
            TestPathUtil();
            Section("真实复制：文件夹");
            TestFolderCopy();
            Section("真实复制：第二次运行应为“无变化”");
            TestSecondRun();
            Section("扫描取消：停止指令能让扫描立刻结束");
            TestScannerCancel();
            Section("真实复制：单个文件");
            TestSingleFile();
            Section("真实复制：排除项");
            TestExclude();
            Section("真实复制：仅演练 /L");
            TestDryRun();
            Section("真实复制：镜像 /MIR 会删多余文件");
            TestMirror();
            Section("校验：SHA-256 能发现被改动的文件");
            TestVerifier();
            Section("校验：多线程并行结果与单线程一致");
            TestVerifierParallel();
            Section("取消：停止能立刻结束");
            TestCancel();
            Section("日志目录：系统 TEMP 坏掉时也不能失败");
            TestLogPathPicker();
            Section("日志没生成时：报错必须带上 robocopy 自己的话");
            TestMissingLogDiagnosis();
            Section("目标探测：目标目录写不进去时要快速失败");
            TestDestProbe();
            Section("退出码纠正：退出码 0 但一个文件都没复制 => 必须判失败");
            TestCorrectSuccess();
            Section("安装引擎：装到临时目录再卸载");
            TestInstallerEngine();
            Section("回归：实际拒绝访问日志、部分失败和命令行路径");
            TestFailureRegression();

            Console.WriteLine();
            Console.WriteLine("=========================================");
            Console.WriteLine("通过 " + _pass + " 项，失败 " + _fail + " 项");
            Console.WriteLine("=========================================");

            try { TryDeleteDir(_root); } catch { }
            return _fail == 0 ? 0 : 1;
        }

        // =============================================================
        // 解析器测试
        // =============================================================

        // 完全照抄真实 robocopy 日志的形态：条目行用 TAB 分隔、行尾是 CR + 百分比 + CR LF
        private const string LogCN =
            "-------------------------------------------------------------------------------\r\n" +
            "   ROBOCOPY     ::     Windows 的可靠文件复制                              \r\n" +
            "-------------------------------------------------------------------------------\r\n" +
            "\r\n" +
            "  开始时间: 2026年10月1日 18:37:38\r\n" +
            "        源 : C:\\src\\\r\n" +
            "      目标 : C:\\dst\\\r\n" +
            "\r\n" +
            "      文件: *.*\r\n" +
            "\t    \r\n" +
            "      选项: *.* /S /E /DCOPY:DA /COPY:DAT /MT:8 /R:2 /W:2 \r\n" +
            "\r\n" +
            "------------------------------------------------------------------------------\r\n" +
            "\r\n" +
            "\t    新文件    \t\t       9\tC:\\src\\a.txt\r100%  \r\n" +
            "\t    新文件    \t\t   1.4 g\tC:\\src\\big.bin\r  0%  \r 34%  \r 57%  \r 80%  \r100%  \r\n" +
            "\t    新文件    \t\t      19\tC:\\src\\single.pdf\r100%  \r\n" +
            "\t    新文件    \t\t       9\tC:\\src\\sub\\b.txt\r100%  \r\n" +
            "------------------------------------------------------------------------------\r\n" +
            "\r\n" +
            "                  总数        复制        跳过       不匹配        失败        其他\r\n" +
            "       目录:         2         2         1         0         0         0\r\n" +
            "       文件:         4         3         1         0         1         0\r\n" +
            "       字节:   1.464 g   1.464 g         0         0         0         0\r\n" +
            "       时间:   0:00:01   0:00:00                       0:00:00   0:00:00\r\n" +
            "\r\n" +
            "       速度:           4,012,408,257 字节/秒。\r\n" +
            "   已结束: 2026年10月1日 18:37:39\r\n";

        private static void TestParserChinese()
        {
            RoboLogParser p = new RoboLogParser(null);
            string itemLines = "";
            int itemCount = 0;
            p.ItemLine += delegate(string s) { itemCount++; itemLines += s + "\n"; };
            p.FeedText(LogCN);
            p.Flush();

            Check("识别出 4 个文件条目（目录行不计入）", p.FilesSeen == 4, "FilesSeen=" + p.FilesSeen);
            Check("汇总行：文件复制数 = 3", p.FilesRow != null && p.FilesRow[1] == 3,
                  p.FilesRow == null ? "null" : string.Join(",", Array.ConvertAll(p.FilesRow, delegate(long x) { return x.ToString(); })));
            Check("汇总行：文件失败数 = 1", p.FilesRow != null && p.FilesRow[4] == 1, "FAILED 列");
            Check("汇总行：目录跳过数 = 1", p.FilesRow != null && p.FilesRow[2] == 1, "SKIPPED 列");
            Check("汇总行：字节行被识别（1.464g）", p.BytesRow != null && p.BytesRow[0] > 1500000000L,
                  p.BytesRow == null ? "null" : p.BytesRow[0].ToString());

            // 9 + 1.4g + 19 + 9，其中 1.4g = 1.4 * 1024^3 = 1503238553
            long expect = 9 + 1503238553L + 19 + 9;
            Check("累计字节数正确（含 1.4 g 换算）", p.DoneBytes == expect,
                  "得到 " + p.DoneBytes + "，期望 " + expect);

            Check("条目行格式为 FILE\\t大小\\t路径", itemLines.Contains("FILE\t9\tC:\\src\\a.txt"), itemLines);
            Check("进度百分比被消化，不当作日志行", itemCount == 4, "条目行数=" + itemCount);
        }

        private const string LogEN =
            "------------------------------------------------------------------------------\r\n" +
            "   ROBOCOPY     ::     Robust File Copy for Windows\r\n" +
            "------------------------------------------------------------------------------\r\n" +
            "\r\n" +
            "  Started : Thu Oct 01 18:37:38 2026\r\n" +
            "   Source : C:\\src\\\r\n" +
            "     Dest : C:\\dst\\\r\n" +
            "\r\n" +
            "    Files : *.*\r\n" +
            "\r\n" +
            "  Options : *.* /S /E /DCOPY:DA /COPY:DAT /MT:8 /R:2 /W:2\r\n" +
            "\r\n" +
            "------------------------------------------------------------------------------\r\n" +
            "\r\n" +
            "\t  New File  \t\t      12\tC:\\src\\one.txt\r100%  \r\n" +
            "\t  New File  \t\t  2.50 m\tC:\\src\\two.bin\r 50%  \r100%  \r\n" +
            "\t   New Dir  \t\t    \tC:\\src\\sub\\\r\n" +
            "\t  New File  \t\t      34\tC:\\src\\sub\\three.txt\r100%  \r\n" +
            "------------------------------------------------------------------------------\r\n" +
            "\r\n" +
            "               Total    Copied   Skipped  Mismatch    FAILED    Extras\r\n" +
            "    Dirs :         3         3         0         0         0         0\r\n" +
            "   Files :         3         2         1         0         0         0\r\n" +
            "   Bytes :   2.50 m   2.50 m         0         0         0         0\r\n" +
            "   Times :   0:00:00   0:00:00                       0:00:00   0:00:00\r\n";

        private static void TestParserEnglish()
        {
            RoboLogParser p = new RoboLogParser(null);
            int dirs = 0;
            p.ItemLine += delegate(string s) { if (s.StartsWith("DIR\t")) dirs++; };
            p.FeedText(LogEN);
            p.Flush();

            Check("英文界面同样识别 3 个文件", p.FilesSeen == 3, "FilesSeen=" + p.FilesSeen);
            Check("英文界面识别出 1 个目录行", dirs == 1, "目录行=" + dirs);
            Check("英文汇总行解析正确（Copied=2）", p.FilesRow != null && p.FilesRow[1] == 2,
                  p.FilesRow == null ? "null" : p.FilesRow[1].ToString());
            long expect = 12 + (long)(2.50 * 1024 * 1024) + 34;
            Check("英文界面字节累计正确（2.50 m）", p.DoneBytes == expect,
                  "得到 " + p.DoneBytes + "，期望 " + expect + "（2.50m 按二进制换算）");
        }

        private static void TestSizeParsing()
        {
            CheckSize("9", 9);
            CheckSize("4,012,408,257", 4012408257L);
            CheckSize("1.4 g", (long)(1.4 * 1024 * 1024 * 1024));
            CheckSize("1.464 g", (long)(1.464 * 1024 * 1024 * 1024));
            CheckSize("2.50 m", (long)(2.5 * 1024 * 1024));
            CheckSize("512 k", 512L * 1024);
            CheckSize("1.5 t", (long)(1.5 * 1024 * 1024 * 1024 * 1024));
            Check("非数字不误判：'abc'", !RoboLogParser.TryParseRoboSize("abc", out _dummy));
            Check("非数字不误判：空串", !RoboLogParser.TryParseRoboSize("", out _dummy));
            Check("非数字不误判：'C:\\src'", !RoboLogParser.TryParseRoboSize("C:\\src", out _dummy));
            Check("路径识别：'C:\\a\\b.txt'", RoboLogParser.LooksLikePath("C:\\a\\b.txt"));
            Check("路径识别：'\\\\server\\share\\x'", RoboLogParser.LooksLikePath("\\\\server\\share\\x"));
            Check("路径识别：'not a path'", !RoboLogParser.LooksLikePath("not a path"));
        }

        private static long _dummy;

        private static void CheckSize(string s, long expect)
        {
            long got;
            bool ok = RoboLogParser.TryParseRoboSize(s, out got) && got == expect;
            Check("容量解析 \"" + s + "\" = " + expect, ok, "得到 " + got);
        }

        private static void TestParserEdge()
        {
            // 分片喂入：真实运行时 UTF-16 日志是按字节增量读的，可能刚好落在半个字符上。
            // 这里每 6 字节喂一次，模拟最碎的读取边界。
            RoboLogParser p = new RoboLogParser(null);
            p.ItemLine += delegate(string s) { };
            byte[] bytes = Encoding.Unicode.GetBytes(LogEN);
            int pos = 0;
            int feeds = 0;
            while (pos < bytes.Length)
            {
                int n = Math.Min(6, bytes.Length - pos);
                p.FeedText(Encoding.Unicode.GetString(bytes, pos, n));
                pos += n;
                feeds++;
            }
            p.Flush();
            Check("分片喂入（每次 6 字节，" + feeds + " 次）结果一致", p.FilesSeen == 3, "FilesSeen=" + p.FilesSeen);
            Check("分片喂入后字节累计一致", p.DoneBytes == 12 + (long)(2.50 * 1024 * 1024) + 34,
                  "DoneBytes=" + p.DoneBytes);

            // 末尾没有换行符的半行：条目行本身以 CR 结尾，应能识别，但百分比还没到
            RoboLogParser p2 = new RoboLogParser(null);
            p2.FeedText("\t  New File  \t\t      77\tC:\\src\\tail.txt\r100%");
            Check("CR 结尾的条目行立即被识别", p2.FilesSeen == 1, "FilesSeen=" + p2.FilesSeen);
            Check("百分比未到达时进度为 0", p2.DoneBytes == 0, "DoneBytes=" + p2.DoneBytes);
            p2.Flush();
            Check("Flush 后百分比生效", p2.DoneBytes == 77, "DoneBytes=" + p2.DoneBytes);

            // 真正没有结束符的半行，应该攒着
            RoboLogParser p5 = new RoboLogParser(null);
            p5.FeedText("\t  New File  \t\t      88\tC:\\src\\half.txt");
            Check("无结束符的半行先不产生条目", p5.FilesSeen == 0, "FilesSeen=" + p5.FilesSeen);
            p5.FeedText("\r100%\r\n");
            Check("补上后半行后条目出现", p5.FilesSeen == 1 && p5.DoneBytes == 88, "DoneBytes=" + p5.DoneBytes);

            // BOM
            RoboLogParser p3 = new RoboLogParser(null);
            p3.ItemLine += delegate(string s) { };
            p3.FeedText("\uFEFF" + LogEN);
            p3.Flush();
            Check("带 BOM 的日志正常解析", p3.FilesSeen == 3, "FilesSeen=" + p3.FilesSeen);

            // 空目录/空输入不应抛异常
            RoboLogParser p4 = new RoboLogParser(null);
            p4.FeedText("");
            p4.FeedText(null);
            p4.Flush();
            Check("空输入不抛异常", p4.FilesSeen == 0, "ok");
        }

        private static void TestPathUtil()
        {
            Check("去掉结尾反斜杠", PathUtil.Clean("C:\\dst\\") == "C:\\dst", PathUtil.Clean("C:\\dst\\"));
            Check("去掉包裹引号", PathUtil.Clean("\"C:\\my dir\"") == "C:\\my dir", PathUtil.Clean("\"C:\\my dir\""));
            Check("去引号同时去尾斜杠", PathUtil.Clean("\"C:\\my dir\\\"") == "C:\\my dir", PathUtil.Clean("\"C:\\my dir\\\""));
            Check("保留根目录 C:\\", PathUtil.Clean("C:\\") == "C:\\", PathUtil.Clean("C:\\"));
            Check("去掉 \\\\?\\ 长路径前缀", PathUtil.Clean("\\\\?\\C:\\x") == "C:\\x", PathUtil.Clean("\\\\?\\C:\\x"));

            string d = PathUtil.MapToDest("C:\\src", "D:\\目标", "C:\\src\\子目录\\文件.txt");
            Check("源路径映射到目标（含中文）", d == "D:\\目标\\子目录\\文件.txt", d);

            Check("子目录判定：目标在源里", PathUtil.IsInside("C:\\src", "C:\\src\\dst"));
            Check("子目录判定：目标不在源里", !PathUtil.IsInside("C:\\src", "C:\\src2\\dst"));
            Check("子目录判定：完全相同也算在内", PathUtil.IsInside("C:\\src", "C:\\src"));

            string[] parts = PathUtil.SplitList("a;b,c|d  ;; ");
            Check("排除项拆分（分号/逗号/竖线）", parts.Length == 4, string.Join("|", parts));
        }

        // =============================================================
        // 真实复制测试
        // =============================================================

        private static string Src, Dst;

        private static void TestFolderCopy()
        {
            Src = Path.Combine(_root, "src");
            Dst = Path.Combine(_root, "dst");
            BuildTree(Src);

            CopyRequest req = new CopyRequest();
            req.Mode = CopyMode.Folder;
            req.Source = Src;
            req.Dest = Dst;
            req.Options.Threads = 8;

            SourceScan scan = Scanner.ScanFolder(Src, req.Options, null);
            Check("扫描文件数 = 5", scan.TotalFiles == 5, "得到 " + scan.TotalFiles);
            Check("扫描总字节 > 0", scan.TotalBytes > 0, scan.TotalBytes.ToString());

            List<long> samples = new List<long>();
            RunResult res = Run(req, scan, delegate(ProgressInfo pi) { samples.Add(pi.DoneBytes); });

            Check("退出码 < 8（成功）", res.Success, "ExitCode=" + res.ExitCode + " err=" + res.Error);
            Check("产生了进度样本", samples.Count > 0, "样本数=" + samples.Count);
            bool monotonic = true;
            for (int i = 1; i < samples.Count; i++) if (samples[i] < samples[i - 1]) monotonic = false;
            Check("进度单调不减", monotonic, string.Join(",", samples.ConvertAll(delegate(long x) { return x.ToString(); }).ToArray()));
            Check("解析出的文件数 = 5", res.ParserFilesSeen == 5, "得到 " + res.ParserFilesSeen);

            Check("目标里文件齐全且内容一致", AllFilesMatch(Src, Dst), DetailMismatch(Src, Dst));
            Check("空文件夹被复制过去", Directory.Exists(Path.Combine(Dst, "空目录")), "dst\\空目录");
            Check("深层中文路径存在", File.Exists(Path.Combine(Dst, "子目录", "深层", "数据 文件.bin")), "深层文件");
            Check("汇总行被解析", res.HasSummary, "HasSummary");
        }

        private static void TestSecondRun()
        {
            CopyRequest req = new CopyRequest();
            req.Mode = CopyMode.Folder;
            req.Source = Src;
            req.Dest = Dst;
            req.Options.Threads = 8;

            SourceScan scan = Scanner.ScanFolder(Src, req.Options, null);
            RunResult res = Run(req, scan, null);
            Check("第二次运行退出码 = 0（无变化）", res.ExitCode == 0, "ExitCode=" + res.ExitCode);
            Check("第二次运行没有重新复制文件", res.HasSummary && res.SumCopiedFiles == 0,
                  res.HasSummary ? "Copied=" + res.SumCopiedFiles : "无汇总");
        }

        /// <summary>
        /// 扫描阶段的取消：旧版点"停止"时扫描无人响应，扫完照样开始复制。
        /// 这里要求 isCancelled 一旦为真，扫描立刻带 Cancelled 标记返回。
        /// </summary>
        private static void TestScannerCancel()
        {
            SourceScan scan = Scanner.ScanFolder(Src, new CopyOptions(), null, delegate { return true; });
            Check("取消后扫描立即返回", scan.Cancelled, "Cancelled=" + scan.Cancelled);
            Check("取消后没有统计出全部文件", scan.TotalFiles < 5, "TotalFiles=" + scan.TotalFiles);

            // 不取消时不受影响
            SourceScan ok = Scanner.ScanFolder(Src, new CopyOptions(), null, delegate { return false; });
            Check("不取消时扫描完整", !ok.Cancelled && ok.TotalFiles == 5, "TotalFiles=" + ok.TotalFiles);
        }

        private static void TestSingleFile()
        {
            string file = Path.Combine(Src, "中文 文件.txt");
            string dst = Path.Combine(_root, "dst_single");
            Directory.CreateDirectory(dst);

            CopyRequest req = new CopyRequest();
            req.Mode = CopyMode.File;
            req.Source = file;
            req.Dest = dst;
            req.Options.Threads = 4;

            SourceScan scan = Scanner.ScanSingleFile(file);
            Check("单文件扫描：1 个文件", scan.TotalFiles == 1, "得到 " + scan.TotalFiles);

            RunResult res = Run(req, scan, null);
            Check("单文件复制成功", res.Success, "ExitCode=" + res.ExitCode + " err=" + res.Error);
            Check("只复制了那一个文件", File.Exists(Path.Combine(dst, "中文 文件.txt"))
                  && Directory.GetFiles(dst).Length == 1, "目标文件数=" + Directory.GetFiles(dst).Length);
            Check("单文件内容一致",
                  Hash(file) == Hash(Path.Combine(dst, "中文 文件.txt")), "哈希比对");
        }

        private static void TestExclude()
        {
            string dst = Path.Combine(_root, "dst_exclude");
            CopyRequest req = new CopyRequest();
            req.Mode = CopyMode.Folder;
            req.Source = Src;
            req.Dest = dst;
            req.Options.ExcludeDirs = "子目录";
            req.Options.ExcludeFiles = "skip.txt";

            SourceScan scan = Scanner.ScanFolder(Src, req.Options, null);
            RunResult res = Run(req, scan, null);
            Check("排除后复制成功", res.Success, "ExitCode=" + res.ExitCode);
            Check("被排除的文件夹没有复制", !Directory.Exists(Path.Combine(dst, "子目录")), "dst\\子目录");
            Check("被排除的文件没有复制", !File.Exists(Path.Combine(dst, "skip.txt")), "dst\\skip.txt");
            Check("未排除的文件正常复制", File.Exists(Path.Combine(dst, "a.txt")), "dst\\a.txt");
        }

        private static void TestDryRun()
        {
            string dst = Path.Combine(_root, "dst_dry");
            CopyRequest req = new CopyRequest();
            req.Mode = CopyMode.Folder;
            req.Source = Src;
            req.Dest = dst;
            req.Options.DryRun = true;

            SourceScan scan = Scanner.ScanFolder(Src, req.Options, null);
            RunResult res = Run(req, scan, null);
            Check("演练退出码 < 8", res.Success, "ExitCode=" + res.ExitCode);
            int n = Directory.Exists(dst) ? Directory.GetFiles(dst, "*", SearchOption.AllDirectories).Length : 0;
            Check("演练没有真的复制文件", n == 0, "目标文件数=" + n);
        }

        private static void TestMirror()
        {
            string dst = Path.Combine(_root, "dst_mirror");
            Directory.CreateDirectory(dst);
            File.WriteAllText(Path.Combine(dst, "多余的文件.txt"), "should be deleted");
            Directory.CreateDirectory(Path.Combine(dst, "多余目录"));
            File.WriteAllText(Path.Combine(dst, "多余目录", "x.txt"), "gone");

            CopyRequest req = new CopyRequest();
            req.Mode = CopyMode.Folder;
            req.Source = Src;
            req.Dest = dst;
            req.Options.Mirror = true;
            req.Options.SkipNewer = false;

            SourceScan scan = Scanner.ScanFolder(Src, req.Options, null);
            RunResult res = Run(req, scan, null);
            Check("镜像复制成功", res.Success, "ExitCode=" + res.ExitCode);
            Check("镜像删除了多余文件", !File.Exists(Path.Combine(dst, "多余的文件.txt")), "多余的文件.txt");
            Check("镜像删除了多余目录", !Directory.Exists(Path.Combine(dst, "多余目录")), "多余目录");
            Check("镜像后源文件都在", AllFilesMatch(Src, dst), DetailMismatch(Src, dst));
        }

        private static void TestVerifier()
        {
            string src = Path.Combine(_root, "vsrc");
            string dst = Path.Combine(_root, "vdst");
            Directory.CreateDirectory(src);
            Directory.CreateDirectory(dst);
            File.WriteAllText(Path.Combine(src, "same.txt"), "内容一致");
            File.WriteAllText(Path.Combine(dst, "same.txt"), "内容一致");
            File.WriteAllText(Path.Combine(src, "diff.txt"), "原始内容");
            File.WriteAllText(Path.Combine(dst, "diff.txt"), "被改过了");
            File.WriteAllText(Path.Combine(src, "missing.txt"), "源里有目标没有");

            CopyRequest req = new CopyRequest();
            req.Mode = CopyMode.Folder;
            req.Source = src;
            req.Dest = dst;
            SourceScan scan = Scanner.ScanFolder(src, req.Options, null);

            VerifyResult vr = Verifier.Run(req, scan, null, null);
            Check("校验了 3 个文件", vr.Checked == 3, "Checked=" + vr.Checked);
            Check("发现 1 个内容不一致", vr.Mismatched.Count == 1
                  && vr.Mismatched[0].EndsWith("diff.txt"), "Mismatched=" + vr.Mismatched.Count);
            Check("发现 1 个缺失文件", vr.Missing.Count == 1
                  && vr.Missing[0].EndsWith("missing.txt"), "Missing=" + vr.Missing.Count);
            Check("一致文件计数 = 1", vr.Matched == 1, "Matched=" + vr.Matched);
        }

        /// <summary>
        /// 并行校验（>=24 个文件才开多线程）：造 40 个文件，改坏 2 个、删掉 1 个，
        /// 结果必须与串行版一致：计数正确，且不一致/缺失清单保持源清单顺序。
        /// </summary>
        private static void TestVerifierParallel()
        {
            string src = Path.Combine(_root, "psrc");
            string dst = Path.Combine(_root, "pdst");
            Directory.CreateDirectory(src);
            Directory.CreateDirectory(dst);
            int n = 40;
            string[] corrupt = { "f07.txt", "f23.txt" };
            string missing = "f31.txt";
            for (int i = 0; i < n; i++)
            {
                string name = "f" + i.ToString("00") + ".txt";
                string content = "并行校验测试 " + i;
                File.WriteAllText(Path.Combine(src, name), content);
                if (name == missing) continue;
                bool bad = Array.IndexOf(corrupt, name) >= 0;
                File.WriteAllText(Path.Combine(dst, name), bad ? content + "（被改过）" : content);
            }

            CopyRequest req = new CopyRequest();
            req.Mode = CopyMode.Folder;
            req.Source = src;
            req.Dest = dst;
            SourceScan scan = Scanner.ScanFolder(src, req.Options, null);
            Check("并行校验扫描 40 个文件", scan.TotalFiles == n, "TotalFiles=" + scan.TotalFiles);

            VerifyResult vr = Verifier.Run(req, scan, null, null);
            Check("并行校验了 40 个文件", vr.Checked == n, "Checked=" + vr.Checked);
            Check("一致 37 个", vr.Matched == n - corrupt.Length - 1, "Matched=" + vr.Matched);
            Check("发现 2 个不一致", vr.Mismatched.Count == 2, "Mismatched=" + vr.Mismatched.Count);
            Check("发现 1 个缺失", vr.Missing.Count == 1 && vr.Missing[0].EndsWith(missing), "Missing=" + vr.Missing.Count);
            Check("不一致清单保持源顺序",
                  vr.Mismatched.Count == 2 && vr.Mismatched[0].EndsWith("f07.txt") && vr.Mismatched[1].EndsWith("f23.txt"),
                  string.Join("|", vr.Mismatched.ConvertAll(delegate(string s) { return Path.GetFileName(s); }).ToArray()));
        }

        private static void TestCancel()
        {
            string src = Path.Combine(_root, "cancelsrc");
            string dst = Path.Combine(_root, "canceldst");
            Directory.CreateDirectory(src);
            // 造一个足够大的文件，保证复制过程中来得及取消
            string big = Path.Combine(src, "big.bin");
            using (FileStream fs = new FileStream(big, FileMode.Create, FileAccess.Write))
            {
                byte[] buf = new byte[1024 * 1024];
                new Random(12345).NextBytes(buf);
                for (int i = 0; i < 400; i++) fs.Write(buf, 0, buf.Length);   // 400 MB
            }

            CopyRequest req = new CopyRequest();
            req.Mode = CopyMode.Folder;
            req.Source = src;
            req.Dest = dst;
            req.Options.Threads = 4;
            req.Options.Retries = 0;

            SourceScan scan = Scanner.ScanFolder(src, req.Options, null);

            RobocopyRunner runner = new RobocopyRunner();
            RunResult[] box = new RunResult[1];
            Thread t = new Thread(delegate() { box[0] = runner.Run(req, scan, Path.Combine(_root, "cancel.log")); });
            t.IsBackground = true;
            t.Start();

            Thread.Sleep(250);
            DateTime killAt = DateTime.Now;
            runner.Cancel();

            bool finished = t.Join(15000);
            double sec = (DateTime.Now - killAt).TotalSeconds;
            Check("取消后 15 秒内返回", finished, "耗时 " + sec.ToString("0.0") + "s");
            Check("返回结果被标记为已取消", box[0] != null && box[0].Cancelled,
                  box[0] == null ? "null" : "Cancelled=" + box[0].Cancelled);
            Check("取消后不算成功", box[0] == null || !box[0].Success, "Success 应为 false");
            Console.WriteLine("      （取消响应耗时 " + sec.ToString("0.00") + " 秒）");
        }

        // =============================================================
        // 辅助
        // =============================================================

        private static RunResult Run(CopyRequest req, SourceScan scan, Action<ProgressInfo> onProgress)
        {
            RobocopyRunner runner = new RobocopyRunner();
            StringBuilder log = new StringBuilder();
            runner.Log += delegate(string s) { log.AppendLine(s); };
            if (onProgress != null) runner.Progress += onProgress;
            string logPath = Path.Combine(_root, "rc_" + Guid.NewGuid().ToString("N") + ".log");
            RunResult res = runner.Run(req, scan, logPath);
            _lastLog = log.ToString();
            try { if (File.Exists(logPath)) File.Delete(logPath); } catch { }
            return res;
        }

        /// <summary>安装引擎演练：用临时目录完整走一遍"装 -> 文件齐全 -> 拒删源码目录 -> 卸 -> 目录消失"。</summary>
        private static void TestInstallerEngine()
        {
            // 准备安装源：优先用真实的编译产物，缺哪个就补占位文件，保证测试可重复
            string srcDir = Path.Combine(_root, "install_src");
            Directory.CreateDirectory(srcDir);
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            foreach (string f in new string[]
            {
                InstallerEngine.AppExeName, InstallerEngine.AppExeNameAlt,
                InstallerEngine.DocsName, InstallerEngine.UninstallExeName
            })
            {
                string real = Path.Combine(baseDir, f);
                if (File.Exists(real)) File.Copy(real, Path.Combine(srcDir, f), true);
                else File.WriteAllText(Path.Combine(srcDir, f), "placeholder");
            }

            string dst = Path.Combine(_root, "install_dst");
            string dst2 = Path.Combine(_root, "install_dst2");
            try
            {
                // 两个版式都装
                InstallerEngine.Install(srcDir, dst, InstallerEngine.InstallVariant.Both, false, false, false, null);
                Check("两个都装：程序文件齐全",
                      File.Exists(Path.Combine(dst, InstallerEngine.AppExeName)) &&
                      File.Exists(Path.Combine(dst, InstallerEngine.AppExeNameAlt)) &&
                      File.Exists(Path.Combine(dst, InstallerEngine.DocsName)) &&
                      File.Exists(Path.Combine(dst, InstallerEngine.UninstallExeName)), dst);

                // 只装透明背景版：立绘卡 exe 不能被带进去
                InstallerEngine.Install(srcDir, dst2, InstallerEngine.InstallVariant.Backdrop, false, false, false, null);
                Check("只装透明背景版：透明 exe 在",
                      File.Exists(Path.Combine(dst2, InstallerEngine.AppExeNameAlt)), dst2);
                Check("只装透明背景版：立绘卡 exe 没有被装进去",
                      !File.Exists(Path.Combine(dst2, InstallerEngine.AppExeName)), dst2);
                Check("只装透明背景版：说明和卸载程序在",
                      File.Exists(Path.Combine(dst2, InstallerEngine.DocsName)) &&
                      File.Exists(Path.Combine(dst2, InstallerEngine.UninstallExeName)), dst2);

                // 安全阀：源码目录绝不能被当成安装目录删掉
                bool refused = false;
                try { InstallerEngine.Uninstall(_root, false, false, false, false, null); }
                catch { refused = true; }
                Check("拒绝把源码目录当安装目录删除", refused && Directory.Exists(_root), "");

                InstallerEngine.Uninstall(dst, false, false, false, false, null);
                Check("卸载后目录被删除", !Directory.Exists(dst), dst);
                InstallerEngine.Uninstall(dst2, false, false, false, false, null);
                Check("只装透明背景版的目录也能卸载", !Directory.Exists(dst2), dst2);

                // delaySelfDelete=true：目录里没有正在运行的 exe（我们是测试进程），
                // 应走"整目录删除"分支，结果和普通卸载一致
                string dst3 = Path.Combine(_root, "install_dst3");
                InstallerEngine.Install(srcDir, dst3, InstallerEngine.InstallVariant.Classic, false, false, false, null);
                InstallerEngine.Uninstall(dst3, false, false, false, true, null);
                Check("自删演练（目录里没有运行中的 exe）也删除干净", !Directory.Exists(dst3), dst3);
            }
            finally
            {
                try { if (Directory.Exists(dst)) Directory.Delete(dst, true); } catch { }
                try { if (Directory.Exists(dst2)) Directory.Delete(dst2, true); } catch { }
                try { Directory.Delete(srcDir, true); } catch { }
            }
        }

        private static string _lastLog = "";

        /// <summary>
        /// 日志目录选择器：系统 TEMP 指向一个不存在/不可写的目录时，
        /// 也必须能找到一个真正可写的位置，否则 robocopy 连日志都建不出来。
        /// </summary>
        private static void TestLogPathPicker()
        {
            string dir = LogPathPicker.PickDirectory();
            Check("默认情况下能找到可写日志目录", !string.IsNullOrEmpty(dir), dir == null ? "(null)" : dir);

            string oldTemp = Environment.GetEnvironmentVariable("TEMP");
            string oldTmp = Environment.GetEnvironmentVariable("TMP");
            string broken = Path.Combine(_root, "no_such_" + Guid.NewGuid().ToString("N"), "deep", "deeper");
            try
            {
                Environment.SetEnvironmentVariable("TEMP", broken);
                Environment.SetEnvironmentVariable("TMP", broken);

                string dir2 = LogPathPicker.PickDirectory();
                Check("TEMP 坏掉时仍能返回一个目录", !string.IsNullOrEmpty(dir2), dir2 == null ? "(null)" : dir2);

                bool writes = false;
                if (!string.IsNullOrEmpty(dir2))
                {
                    try
                    {
                        string f = Path.Combine(dir2, "t_" + Guid.NewGuid().ToString("N") + ".tmp");
                        File.WriteAllText(f, "x");
                        writes = File.Exists(f);
                        File.Delete(f);
                    }
                    catch { }
                }
                Check("返回的目录确实可写", writes, dir2 == null ? "(null)" : dir2);

                // 端到端：就在 TEMP 坏掉的情况下，用选择器给的目录做一次真实复制，
                // 必须照样成功、照样有日志——这才是"修好了"的标准。
                if (!string.IsNullOrEmpty(dir2))
                {
                    CopyRequest req = new CopyRequest();
                    req.Mode = CopyMode.Folder;
                    req.Source = Src;
                    req.Dest = Dst;
                    req.Options.Threads = 4;
                    SourceScan scan = Scanner.ScanFolder(Src, req.Options, null);

                    string logPath = Path.Combine(dir2, "rc_" + Guid.NewGuid().ToString("N") + ".log");
                    RobocopyRunner r2 = new RobocopyRunner();
                    RunResult res2 = r2.Run(req, scan, logPath);

                    Check("TEMP 坏掉时真实复制仍然成功", res2.Success,
                          "ExitCode=" + res2.ExitCode + " err=" + res2.Error);
                    Check("日志真的写出来了", File.Exists(logPath),
                          res2.LogMissing ? "报了 LogMissing" : logPath);
                    try { if (File.Exists(logPath)) File.Delete(logPath); } catch { }
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable("TEMP", oldTemp);
                Environment.SetEnvironmentVariable("TMP", oldTmp);
            }
        }

        /// <summary>
        /// 复现"robocopy 没有生成日志文件"：日志路径的父级故意放一个同名文件，
        /// 这样日志无论如何都建不出来。要求报错里必须带上 robocopy 自己的输出，
        /// 而不是以前那句无从下手的"可能不支持 /UNILOG"。
        /// </summary>
        private static void TestMissingLogDiagnosis()
        {
            string blocker = Path.Combine(_root, "blocker_" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(blocker, "x");
            string badLog = Path.Combine(blocker, "rc.log");   // 父级是文件，不可能创建

            CopyRequest req = new CopyRequest();
            req.Mode = CopyMode.Folder;
            req.Source = Src;
            req.Dest = Dst;
            req.Options.Threads = 4;

            SourceScan scan = Scanner.ScanFolder(Src, req.Options, null);
            RobocopyRunner runner = new RobocopyRunner();
            RunResult res = runner.Run(req, scan, badLog);

            Check("识别出日志没生成", res.LogMissing, "LogMissing=" + res.LogMissing);
            Check("报错里带上了日志路径",
                  res.Error != null && res.Error.IndexOf(badLog, StringComparison.OrdinalIgnoreCase) >= 0,
                  res.Error == null ? "(null)" : res.Error);
            Check("报错里带上了 robocopy 自己的输出（不是那句没用的猜测）",
                  res.Error != null && res.Error.Length > 150,
                  res.Error == null ? "(null)" : "报错长度 " + res.Error.Length);
            bool hasReason = res.Error != null && res.Error.IndexOf("robocopy", StringComparison.OrdinalIgnoreCase) >= 0;
            Check("报错开头说明了是 robocopy 没生成日志", hasReason,
                  res.Error == null ? "(null)" : res.Error.Substring(0, Math.Min(40, res.Error.Length)));

            // robocopy 的控制台输出是 OEM 代码页（中文系统 936）。若按 UTF-8 解码，
            // 中文会变成 U+FFFD 替换字符——这个断言和系统语言无关，正好能守住这个 bug。
            bool mojibake = res.Error != null && res.Error.IndexOf('\uFFFD') >= 0;
            Check("捕获的控制台输出没有乱码", !mojibake, mojibake ? "出现了 U+FFFD 替换字符" : "ok");

            try { File.Delete(blocker); } catch { }
        }

        /// <summary>
        /// 目标目录建不出来时（父级是个文件），必须在启动 robocopy 之前就失败，
        /// 而不是让它重试 3 次、耗 6 秒后才冒出一堆红色报错。
        /// </summary>
        private static void TestDestProbe()
        {
            string blocker = Path.Combine(_root, "dest_blocker_" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(blocker, "x");

            CopyRequest req = new CopyRequest();
            req.Mode = CopyMode.Folder;
            req.Source = Src;
            req.Dest = Path.Combine(blocker, "dst");
            req.Options.Threads = 4;

            string logPath = Path.Combine(_root, "rc_probe_dest.log");
            SourceScan scan = Scanner.ScanFolder(Src, req.Options, null);
            RunResult res = new RobocopyRunner().Run(req, scan, logPath);

            Check("目标建不出来时提前失败", !res.Success, "Success=" + res.Success);
            Check("报错说明是目标目录的问题",
                  res.Error != null && res.Error.IndexOf("目标", StringComparison.Ordinal) >= 0,
                  res.Error == null ? "(null)" : res.Error);
            Check("报错里带上了系统原话", res.Error != null && res.Error.Length > 40,
                  res.Error == null ? "(null)" : "长度 " + (res.Error == null ? 0 : res.Error.Length));
            Check("robocopy 根本没跑（没留下日志文件）", !File.Exists(logPath), logPath);
            Check("演练模式不探测目标", RunDryRunProbePasses(), "");

            try { File.Delete(blocker); } catch { }
        }

        // 演练模式下即使目标明显不可写，也必须照常进入 robocopy（/L 不碰目标）
        private static bool RunDryRunProbePasses()
        {
            string blocker = Path.Combine(_root, "dry_blocker_" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(blocker, "x");
            CopyRequest req = new CopyRequest();
            req.Mode = CopyMode.Folder;
            req.Source = Src;
            req.Dest = Path.Combine(blocker, "dst");
            req.Options.DryRun = true;

            string logPath = Path.Combine(_root, "rc_probe_dry.log");
            SourceScan scan = Scanner.ScanFolder(Src, req.Options, null);
            RunResult res = new RobocopyRunner().Run(req, scan, logPath);
            try { File.Delete(blocker); } catch { }
            return res.LogMissing || res.ExitCode >= 0;   // 有日志或跑完了都算"没有提前拦下"
        }

        /// <summary>
        /// 复现 21:09 的真实场景：robocopy 退出码 0、汇总表"失败"= 0，
        /// 但目标目录整个打不开，14543 个文件一个都没复制。程序必须判为失败并给出原因。
        /// </summary>
        private static void TestCorrectSuccess()
        {
            SourceScan scan = new SourceScan();
            scan.TotalFiles = 14543;

            RoboLogParser bad = new RoboLogParser(null);
            bad.ErrorLines = 7;
            bad.FirstError = "2026/10/01 21:09:16 错误 5 (0x00000005) 正在访问目标目录 D:\\data\\比赛\\";
            bad.FilesRow = new long[] { 14543, 0, 0, 0, 0, 0 };
            bad.RetryLimitExceeded = true;
            bad.BytesRow = new long[6];

            RunResult r1 = new RunResult();
            r1.ExitCode = 0;
            r1.Success = true;
            RobocopyRunner.CorrectSuccess(r1, scan, bad);

            Check("退出码 0 + 复制数为 0 + 有报错 => 判失败", !r1.Success, "Success=" + r1.Success);
            Check("报错里引用了 robocopy 原话",
                  r1.Error != null && r1.Error.IndexOf("0x00000005", StringComparison.Ordinal) >= 0,
                  r1.Error == null ? "(null)" : r1.Error);
            Check("报错里带上了建议", r1.Error != null && r1.Error.IndexOf("拒绝访问", StringComparison.Ordinal) >= 0, "");
            Check("报错里说明了源文件数",
                  r1.Error != null && r1.Error.IndexOf("14543", StringComparison.Ordinal) >= 0, "");

            // 反例 1：复制了一部分（即便还有报错）=> 不误判为失败
            RoboLogParser partial = new RoboLogParser(null);
            partial.ErrorLines = 1;
            partial.FilesRow = new long[] { 14543, 100, 14443, 0, 0, 0 };
            partial.BytesRow = new long[6];
            RunResult r2 = new RunResult();
            r2.ExitCode = 3;
            r2.Success = true;
            RobocopyRunner.CorrectSuccess(r2, scan, partial);
            Check("复制了一部分时不误判", r2.Success, "");

            // 反例 2：一行报错都没有的"无变化"二次运行 => 不误判
            RoboLogParser clean = new RoboLogParser(null);
            clean.FilesRow = new long[] { 14543, 0, 14543, 0, 0, 0 };
            clean.BytesRow = new long[6];
            RunResult r3 = new RunResult();
            r3.ExitCode = 0;
            r3.Success = true;
            RobocopyRunner.CorrectSuccess(r3, scan, clean);
            Check("无报错的'无变化'不误判", r3.Success, "");
        }

        private static void TestFailureRegression()
        {
            SourceScan scan = new SourceScan();
            scan.TotalFiles = 14543;
            RoboLogParser parser = new RoboLogParser(null);
            parser.FeedText(
                "2026/10/01 21:09:16 错误 5 (0x00000005) 正在访问目标目录 D:\\data\\比赛\\\r\n" +
                "拒绝访问。\r\n错误: 超过重试限制。\r\n" +
                "目录: 1 1 1 0 0 0\r\n文件: 0 0 0 0 0 0\r\n字节: 0 0 0 0 0 0\r\n");
            RunResult result = new RunResult();
            result.ExitCode = 0;
            result.Success = true;
            RobocopyRunner.CorrectSuccess(result, scan, parser);
            Check("原始日志全零汇总仍判失败", !result.Success);
            Check("保留原始退出码 0", result.ExitCode == 0);
            Check("重试耗尽可识别", parser.RetryLimitExceeded);

            scan.TotalFiles = 0;
            result.Success = true;
            RobocopyRunner.CorrectSuccess(result, scan, parser);
            Check("空目录访问失败也不误报成功", !result.Success);

            parser = new RoboLogParser(null);
            parser.FeedText("ERROR: RETRY LIMIT EXCEEDED.\r\nDirs : 2 1 0 0 1 0\r\nFiles : 10 8 0 0 2 0\r\nBytes : 100 80 0 0 20 0\r\n");
            result.Success = true;
            result.ExitCode = 1;
            RobocopyRunner.CorrectSuccess(result, null, parser);
            Check("部分文件复制成功但仍有失败时判失败", !result.Success);

            parser = new RoboLogParser(null);
            parser.FeedText("Dirs : 2 1 0 0 1 0\r\nFiles : 0 0 0 0 0 0\r\nBytes : 0 0 0 0 0 0\r\n");
            result.Success = true;
            RobocopyRunner.CorrectSuccess(result, null, parser);
            Check("目录失败也进入最终状态判断", !result.Success);

            parser = new RoboLogParser(null);
            parser.FeedText("2026/10/01 21:09:16 ERROR 5 (0x00000005)\tCopying File\tC:\\src\\a.txt\r\n");
            Check("带 TAB 的错误不会被当成文件进度", parser.ErrorLines == 1 && parser.FilesSeen == 0);
            Check("文件名或路径包含 ERROR 不算错误",
                !RoboLogParser.IsErrorLine("Source : C:\\ERROR\\错误资料\\") &&
                !RoboLogParser.IsErrorLine("Files : ERROR.txt"));

            parser = new RoboLogParser(null);
            result.Success = true;
            RobocopyRunner.CorrectSuccess(result, null, parser);
            Check("日志没有完整汇总时不得宣布成功", !result.Success);

            long[] row;
            Check("字节汇总支持只跳过不复制的单位列",
                RoboLogParser.TryParseSummaryRow("Bytes : 3.877 g 0 3.877 g 0 0 0", out row) &&
                row[0] > 0 && row[1] == 0 && row[2] == row[0]);
            Check("字节汇总支持仅一列有单位",
                RoboLogParser.TryParseSummaryRow("Bytes : 1.0 g 1024 0 0 0 0", out row) && row[1] == 1024);

            CopyRequest req = new CopyRequest();
            req.Source = "C:\\";
            req.Dest = "D:\\";
            string args = RobocopyRunner.BuildArguments(req, "C:\\test.log");
            Check("盘符根目录的反斜杠正确引用", args.StartsWith("\"C:\\\\\" \"D:\\\\\" ", StringComparison.Ordinal));
            req.Options.IncludeEmptyDirs = false;
            Check("不含空目录时仍递归复制子目录", RobocopyRunner.BuildArguments(req, "C:\\test.log").IndexOf(" /S ", StringComparison.Ordinal) >= 0);

            req.Options.BigFileMode = true;
            Check("大文件模式带 /J 参数", RobocopyRunner.BuildArguments(req, "C:\\test.log").IndexOf(" /J", StringComparison.Ordinal) >= 0);
            req.Options.BigFileMode = false;

            req.Source = Src;
            req.Dest = Path.Combine(_root, "dry_missing_" + Guid.NewGuid().ToString("N"));
            req.Options.DryRun = true;
            SourceScan dryScan = Scanner.ScanFolder(Src, req.Options, null);
            RunResult dryResult = Run(req, dryScan, null);
            Check("演练成功且不创建目标目录", dryResult.Success && !Directory.Exists(req.Dest), dryResult.Error);
        }

        private static void BuildTree(string src)
        {
            if (Directory.Exists(src)) TryDeleteDir(src);
            Directory.CreateDirectory(src);
            File.WriteAllText(Path.Combine(src, "中文 文件.txt"), "中文内容测试 hello");
            File.WriteAllText(Path.Combine(src, "a.txt"), "aaa");
            File.WriteAllText(Path.Combine(src, "skip.txt"), "这个文件会被排除");
            Directory.CreateDirectory(Path.Combine(src, "空目录"));
            Directory.CreateDirectory(Path.Combine(src, "子目录", "深层"));
            File.WriteAllText(Path.Combine(src, "子目录", "b.txt"), "bbb");

            byte[] data = new byte[300 * 1024];
            new Random(7).NextBytes(data);
            File.WriteAllBytes(Path.Combine(src, "子目录", "深层", "数据 文件.bin"), data);
        }

        private static bool AllFilesMatch(string src, string dst)
        {
            foreach (string f in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
            {
                string rel = f.Substring(src.Length).TrimStart('\\');
                string d = Path.Combine(dst, rel);
                if (!File.Exists(d)) return false;
                if (Hash(f) != Hash(d)) return false;
            }
            return true;
        }

        private static string DetailMismatch(string src, string dst)
        {
            foreach (string f in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
            {
                string rel = f.Substring(src.Length).TrimStart('\\');
                string d = Path.Combine(dst, rel);
                if (!File.Exists(d)) return "缺少 " + rel;
                if (Hash(f) != Hash(d)) return "内容不同 " + rel;
            }
            return "ok";
        }

        private static string Hash(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream fs = File.OpenRead(path))
            {
                byte[] h = sha.ComputeHash(fs);
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < h.Length; i++) sb.Append(h[i].ToString("x2"));
                return sb.ToString();
            }
        }

        private static void Section(string name)
        {
            Console.WriteLine("── " + name);
        }

        private static void Check(string name, bool ok)
        {
            Check(name, ok, "");
        }

        private static void Check(string name, bool ok, string detail)
        {
            if (ok)
            {
                _pass++;
                Console.WriteLine("   [通过] " + name);
            }
            else
            {
                _fail++;
                Console.WriteLine("   [失败] " + name + "   -> " + detail);
                if (_lastLog.Length > 0)
                {
                    Console.WriteLine("      ---- 引擎日志 ----");
                    string[] lines = _lastLog.Split('\n');
                    for (int i = 0; i < lines.Length && i < 25; i++)
                        if (lines[i].Trim().Length > 0) Console.WriteLine("      " + lines[i].TrimEnd());
                    Console.WriteLine("      ------------------");
                }
            }
        }

        private static void TryDeleteDir(string dir)
        {
            try { Directory.Delete(dir, true); }
            catch { }
        }
    }
}
