using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace CopyTool
{
    public enum CopyMode { File, Folder, VerifyOnly }

    // ------------------------------------------------------------------
    // 数据结构
    // ------------------------------------------------------------------

    public class CopyOptions
    {
        public int Threads = 32;        // /MT
        public int Retries = 2;         // /R
        public int WaitSeconds = 2;     // /W
        public bool SkipNewer = true;   // /XO
        public bool IncludeEmptyDirs = true; // /E
        public bool BigFileMode = false;// /J
        public bool Mirror = false;     // /MIR
        public bool DryRun = false;     // /L
        public bool Verify = false;     // 复制后 SHA-256 校验
        public string ExcludeDirs = "";
        public string ExcludeFiles = "";
    }

    public class CopyRequest
    {
        public CopyMode Mode = CopyMode.Folder;
        public string Source = "";
        public string Dest = "";
        public CopyOptions Options = new CopyOptions();
    }

    public class ProgressInfo
    {
        public string Phase = "";
        public long DoneBytes;
        public long TotalBytes;
        public int DoneFiles;
        public int TotalFiles;
        public double BytesPerSecond;
        public long EtaSeconds = -1;
        public string CurrentFile = "";
    }

    public class RunResult
    {
        public int ExitCode = -1;
        public bool Success;
        public bool Cancelled;
        public bool LogMissing;
        public string CommandLine = "";
        public string Error;

        // 来自 robocopy 汇总表（第二行 = 文件，第三行 = 字节）
        public bool HasSummary;
        public long SumTotalFiles, SumCopiedFiles, SumSkippedFiles, SumMismatch, SumFailedFiles, SumExtras;
        public long SumTotalBytes, SumCopiedBytes;

        public long ParserDoneBytes;
        public int ParserFilesSeen;
        public int ErrorLines;
    }

    public class VerifyResult
    {
        public int Checked;
        public int Matched;
        public List<string> Mismatched = new List<string>();
        public List<string> Missing = new List<string>();
        public List<string> Errors = new List<string>();
        public bool Cancelled;
        public string Fatal;
    }

    public class SourceScan
    {
        public long TotalBytes;
        public int TotalFiles;
        public List<string> Files = new List<string>();
        public Dictionary<string, long> Sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        public string Root = "";
        public string Error;
        public bool Truncated;   // 文件数过多，只统计总量不保留清单
        public bool Cancelled;   // 扫描中途被用户取消
    }

    // ------------------------------------------------------------------
    // 工具
    // ------------------------------------------------------------------

    public static class Fmt
    {
        public static string Size(long bytes)
        {
            if (bytes < 0) return "-";
            double b = bytes;
            if (b < 1024) return b.ToString("0") + " B";
            if (b < 1024 * 1024) return (b / 1024).ToString("0.0") + " KB";
            if (b < 1024L * 1024 * 1024) return (b / 1024 / 1024).ToString("0.0") + " MB";
            return (b / 1024 / 1024 / 1024).ToString("0.00") + " GB";
        }

        public static string Speed(double bps)
        {
            if (bps <= 0) return "-";
            return Size((long)bps) + "/s";
        }

        public static string Duration(long seconds)
        {
            if (seconds < 0) return "--:--";
            if (seconds < 60) return seconds + " 秒";
            if (seconds < 3600)
                return (seconds / 60) + " 分 " + (seconds % 60) + " 秒";
            return (seconds / 3600) + " 时 " + ((seconds % 3600) / 60) + " 分";
        }

        public static string Count(long n)
        {
            return n.ToString("#,0", CultureInfo.InvariantCulture);
        }
    }

    public static class PathUtil
    {
        /// <summary>去掉结尾反斜杠（引号包裹时 "C:\dst\" 会把引号转义掉，必须去掉）。</summary>
        public static string Clean(string p)
        {
            if (string.IsNullOrEmpty(p)) return "";
            string s = p.Trim();
            if (s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"')
                s = s.Substring(1, s.Length - 2).Trim();
            if (s.StartsWith("\\\\?\\")) s = s.Substring(4);
            while (s.Length > 3 && (s.EndsWith("\\") || s.EndsWith("/")))
                s = s.Substring(0, s.Length - 1);
            return s;
        }

        /// <summary>源文件路径 -> 目标文件路径。</summary>
        public static string MapToDest(string sourceRoot, string destRoot, string file)
        {
            string root = sourceRoot;
            if (!root.EndsWith("\\")) root += "\\";
            string rel;
            if (file.Length >= root.Length &&
                file.Substring(0, root.Length).Equals(root, StringComparison.OrdinalIgnoreCase))
                rel = file.Substring(root.Length);
            else if (file.Length >= sourceRoot.Length &&
                     file.Substring(0, sourceRoot.Length).Equals(sourceRoot, StringComparison.OrdinalIgnoreCase))
                rel = file.Substring(sourceRoot.Length).TrimStart('\\', '/');
            else
                rel = Path.GetFileName(file);
            return Path.Combine(destRoot, rel);
        }

        public static bool IsInside(string parent, string child)
        {
            if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(child)) return false;
            string a = Clean(parent);
            string b = Clean(child);
            if (!a.EndsWith("\\")) a += "\\";
            if (!b.EndsWith("\\")) b += "\\";
            return b.StartsWith(a, StringComparison.OrdinalIgnoreCase);
        }

        public static string[] SplitList(string s)
        {
            if (string.IsNullOrEmpty(s)) return new string[0];
            string[] raw = s.Split(new char[] { ';', ',', '|', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            List<string> list = new List<string>();
            foreach (string r in raw)
            {
                string t = r.Trim().Trim('"');
                if (t.Length > 0) list.Add(t);
            }
            return list.ToArray();
        }
    }

    // ------------------------------------------------------------------
    // 扫描源（用于算总字节数 -> 真进度）
    // ------------------------------------------------------------------

    public static class Scanner
    {
        public const int MaxTrackedFiles = 400000;

        public static SourceScan ScanFolder(string root, CopyOptions opt, Action<int, long> tick)
        {
            return ScanFolder(root, opt, tick, null);
        }

        /// <summary>
        /// isCancelled 返回 true 时立刻返回（scan.Cancelled = true），让"停止"在扫描大目录时也能生效。
        /// </summary>
        public static SourceScan ScanFolder(string root, CopyOptions opt, Action<int, long> tick, Func<bool> isCancelled)
        {
            SourceScan scan = new SourceScan();
            scan.Root = PathUtil.Clean(root);
            string[] xDirs = PathUtil.SplitList(opt.ExcludeDirs);
            string[] xFiles = PathUtil.SplitList(opt.ExcludeFiles);
            Stack<string> stack = new Stack<string>();
            stack.Push(scan.Root);
            DateTime last = DateTime.UtcNow;
            int dirCount = 0;

            while (stack.Count > 0)
            {
                if (isCancelled != null && isCancelled())
                {
                    scan.Cancelled = true;
                    return scan;
                }
                string dir = stack.Pop();
                dirCount++;

                string[] subs;
                try { subs = Directory.GetDirectories(dir); }
                catch { subs = new string[0]; }
                for (int i = 0; i < subs.Length; i++)
                {
                    string name = Path.GetFileName(subs[i]);
                    if (MatchName(name, xDirs)) continue;
                    // 跳过联接点/符号链接，避免死循环
                    try
                    {
                        FileAttributes at = File.GetAttributes(subs[i]);
                        if ((at & FileAttributes.ReparsePoint) != 0) continue;
                    }
                    catch { continue; }
                    stack.Push(subs[i]);
                }

                string[] files;
                try { files = Directory.GetFiles(dir); }
                catch { files = new string[0]; }
                for (int i = 0; i < files.Length; i++)
                {
                    string name = Path.GetFileName(files[i]);
                    if (MatchName(name, xFiles)) continue;
                    long len = 0;
                    try { len = new FileInfo(files[i]).Length; } catch { }
                    scan.TotalFiles++;
                    scan.TotalBytes += len;
                    if (scan.TotalFiles <= MaxTrackedFiles)
                    {
                        scan.Files.Add(files[i]);
                        scan.Sizes[files[i]] = len;
                    }
                    else
                    {
                        scan.Truncated = true;
                    }
                }

                if (tick != null && (DateTime.UtcNow - last).TotalMilliseconds > 300)
                {
                    last = DateTime.UtcNow;
                    tick(scan.TotalFiles, scan.TotalBytes);
                }
            }
            return scan;
        }

        public static SourceScan ScanSingleFile(string file)
        {
            SourceScan scan = new SourceScan();
            string f = PathUtil.Clean(file);
            scan.Root = Path.GetDirectoryName(f);
            FileInfo fi = new FileInfo(f);
            if (!fi.Exists) { scan.Error = "源文件不存在：" + f; return scan; }
            scan.TotalFiles = 1;
            scan.TotalBytes = fi.Length;
            scan.Files.Add(fi.FullName);
            scan.Sizes[fi.FullName] = fi.Length;
            return scan;
        }

        private static bool MatchName(string name, string[] patterns)
        {
            for (int i = 0; i < patterns.Length; i++)
                if (string.Equals(name, patterns[i], StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }

    // ------------------------------------------------------------------
    // robocopy 日志解析器
    //
    // 设计要点：完全**不依赖界面语言**。
    // robocopy 的本地化文案（"新文件"/"New File"/"新目录"）会随系统语言变化，
    // 所以只依赖两件稳定的事实：
    //   1. 每个条目的行内用 TAB 分隔，最后一个非空字段是完整路径；
    //   2. 路径字段前一个字段要么是字节数、要么是 "1.4 g" 这样的容量。
    // 百分比行（0% 34% 100%）是纯数字，同样与语言无关。
    // ------------------------------------------------------------------

    public class RoboLogParser
    {
        private readonly Dictionary<string, long> _sizes;
        private readonly StringBuilder _pending = new StringBuilder();
        private readonly List<Entry> _inFlight = new List<Entry>();
        private long _doneBytes;
        private int _filesSeen;
        private bool _bomTrimmed;
        private readonly List<long[]> _summaryRows = new List<long[]>();

        private class Entry
        {
            public long Size;
            public int Percent;
        }

        public string CurrentFile = "";
        public int ErrorLines;
        public string FirstError = "";   // 第一条报错的原文，直接拿给用户看原因
        public bool RetryLimitExceeded;
        public long[] DirsRow;
        public long[] FilesRow;   // 汇总表第 2 行
        public long[] BytesRow;   // 汇总表第 3 行

        public event Action<string> ItemLine;   // 复制了某个文件/目录
        public event Action<string> InfoLine;   // 其它原样输出

        public RoboLogParser(Dictionary<string, long> sizeLookup)
        {
            _sizes = sizeLookup != null ? sizeLookup : new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        }

        public long DoneBytes
        {
            get
            {
                long sum = _doneBytes;
                for (int i = 0; i < _inFlight.Count; i++)
                    sum += _inFlight[i].Size * _inFlight[i].Percent / 100;
                return sum;
            }
        }

        public int FilesSeen { get { return _filesSeen; } }
        public int InFlightCount { get { return _inFlight.Count; } }

        public void FeedText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (!_bomTrimmed)
            {
                _bomTrimmed = true;
                if (text.Length > 0 && text[0] == '\uFEFF') text = text.Substring(1);
            }
            _pending.Append(text);
            string all = _pending.ToString();
            int cut = -1;
            for (int i = all.Length - 1; i >= 0; i--)
            {
                if (all[i] == '\n' || all[i] == '\r') { cut = i; break; }
            }
            if (cut < 0) return;    // 还没有完整的行
            string head = all.Substring(0, cut + 1);
            _pending.Length = 0;
            _pending.Append(all.Substring(cut + 1));

            StringBuilder tok = new StringBuilder();
            for (int i = 0; i < head.Length; i++)
            {
                char c = head[i];
                if (c == '\r' || c == '\n')
                {
                    if (tok.Length > 0) { HandleToken(tok.ToString()); tok.Length = 0; }
                }
                else tok.Append(c);
            }
            if (tok.Length > 0) HandleToken(tok.ToString());
        }

        public void Flush()
        {
            if (_pending.Length > 0)
            {
                string rest = _pending.ToString();
                _pending.Length = 0;
                if (rest.Trim().Length > 0) HandleToken(rest);
            }
        }

        private void HandleToken(string raw)
        {
            string t = raw.Trim();
            if (t.Length == 0) return;

            // 1) 进度百分比：纯数字 + %
            if (t[t.Length - 1] == '%')
            {
                int pct;
                if (int.TryParse(t.Substring(0, t.Length - 1).Trim(), out pct))
                {
                    SetPercent(pct);
                    return;
                }
            }

            // 错误也可能包含 TAB 和路径，必须先识别，不能把它当成文件条目。
            if (IsErrorLine(t))
            {
                ErrorLines++;
                if (FirstError.Length == 0) FirstError = t;
                if (t.IndexOf("超过重试限制", StringComparison.Ordinal) >= 0 ||
                    t.IndexOf("RETRY LIMIT EXCEEDED", StringComparison.OrdinalIgnoreCase) >= 0)
                    RetryLimitExceeded = true;
                Raise(InfoLine, t);
                return;
            }

            // 2) 条目行：含 TAB，且最后一个非空字段是路径
            if (raw.IndexOf('\t') >= 0 && HandleItemLine(raw)) return;

            // 3) 汇总表：标签 + 6 个数字
            long[] row;
            if (TryParseSummaryRow(t, out row))
            {
                _summaryRows.Add(row);
                if (_summaryRows.Count == 1) DirsRow = row;
                else if (_summaryRows.Count == 2) FilesRow = row;
                else if (_summaryRows.Count == 3) BytesRow = row;
                Raise(InfoLine, t);
                return;
            }

            Raise(InfoLine, t);
        }

        private bool HandleItemLine(string raw)
        {
            string[] parts = raw.Split('\t');
            int last = -1;
            for (int i = parts.Length - 1; i >= 0; i--)
                if (parts[i].Trim().Length > 0) { last = i; break; }
            if (last < 0) return false;

            string pathField = parts[last].Trim();
            if (!LooksLikePath(pathField)) return false;

            long shown = 0;
            bool hasSize = false;
            if (last - 1 >= 0) hasSize = TryParseRoboSize(parts[last - 1].Trim(), out shown);

            // 判断是文件还是目录：
            //   扫描阶段已经枚举过源文件，命中字典 => 一定是文件（最可靠，且能拿到精确字节数）；
            //   否则看它前面有没有容量字段：没有容量字段就是目录。
            long exact;
            bool knownFile = _sizes.TryGetValue(pathField, out exact);
            if (!knownFile && !hasSize)
            {
                Raise(ItemLine, "DIR\t" + pathField);
                return true;
            }

            long size = knownFile ? exact : shown;

            _filesSeen++;
            Entry e = new Entry();
            e.Size = size;
            e.Percent = 0;
            _inFlight.Add(e);
            if (_inFlight.Count > 64) ForceFlushOldest();
            CurrentFile = pathField;

            Raise(ItemLine, "FILE\t" + size.ToString(CultureInfo.InvariantCulture) + "\t" + pathField);
            return true;
        }

        private void SetPercent(int pct)
        {
            if (pct < 0) pct = 0;
            if (pct > 100) pct = 100;
            if (_inFlight.Count == 0) return;
            Entry e = _inFlight[_inFlight.Count - 1];
            if (pct > e.Percent) e.Percent = pct;
            while (_inFlight.Count > 0 && _inFlight[_inFlight.Count - 1].Percent >= 100)
            {
                _doneBytes += _inFlight[_inFlight.Count - 1].Size;
                _inFlight.RemoveAt(_inFlight.Count - 1);
            }
        }

        private void ForceFlushOldest()
        {
            _doneBytes += _inFlight[0].Size;
            _inFlight.RemoveAt(0);
        }

        public static bool LooksLikePath(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            if (s.Length >= 3 && s[1] == ':' && (s[2] == '\\' || s[2] == '/')) return true;
            if (s.StartsWith("\\\\")) return true;
            return false;
        }

        /// <summary>解析 robocopy 显示的容量："12345" / "1.4 g" / "4,012,408,257" / "1.464 g"。</summary>
        public static bool TryParseRoboSize(string s, out long bytes)
        {
            bytes = 0;
            if (string.IsNullOrEmpty(s)) return false;
            string t = s.Trim().Replace(",", "").Replace(" ", "");
            if (t.Length == 0) return false;

            int unit = 0;
            char tail = t[t.Length - 1];
            long mul = 1;
            if (tail == 'k' || tail == 'K') { mul = 1024L; unit = 1; }
            else if (tail == 'm' || tail == 'M') { mul = 1024L * 1024; unit = 1; }
            else if (tail == 'g' || tail == 'G') { mul = 1024L * 1024 * 1024; unit = 1; }
            else if (tail == 't' || tail == 'T') { mul = 1024L * 1024 * 1024 * 1024; unit = 1; }
            string num = unit == 1 ? t.Substring(0, t.Length - 1) : t;
            if (num.Length == 0) return false;

            if (unit == 0)
            {
                long v;
                if (!long.TryParse(num, NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) return false;
                bytes = v;
                return true;
            }
            double d;
            if (!double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return false;
            bytes = (long)(d * mul);
            return true;
        }

        /// <summary>
        /// 汇总表行。标签随语言变化，所以只看冒号后面的数字：
        ///   目录/文件行："4         3         1         0         1         0"   -> 6 个纯整数
        ///   字节行：    "300.0 k   300.0 k   0         0         0         0"   -> 前两列带单位
        /// 时间行（0:00:01 …）和速度行都因为不是纯数字而不会误判。
        /// </summary>
        public static bool TryParseSummaryRow(string line, out long[] vals)
        {
            vals = null;
            if (string.IsNullOrEmpty(line)) return false;
            int idx = line.IndexOf(':');
            int idx2 = line.IndexOf('：');   // 全角冒号
            if (idx2 >= 0 && (idx < 0 || idx2 < idx)) idx = idx2;
            if (idx < 0) return false;
            string rest = line.Substring(idx + 1).Trim();
            if (rest.Length == 0) return false;
            string[] toks = rest.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

            // 六列可各自带容量单位，例如第二次运行时 "3.877 g 0 3.877 g 0 0 0"。
            long[] parsed = new long[6];
            int pos = 0;
            for (int col = 0; col < parsed.Length; col++)
            {
                if (pos >= toks.Length) return false;
                string number = toks[pos++];
                if (pos < toks.Length && toks[pos].Length == 1 &&
                    "kmgt".IndexOf(toks[pos].ToLowerInvariant(), StringComparison.Ordinal) >= 0)
                    number += toks[pos++];
                if (!TryParseRoboSize(number, out parsed[col])) return false;
            }
            if (pos != toks.Length) return false;
            vals = parsed;
            return true;
        }

        public static bool IsErrorLine(string t)
        {
            if (string.IsNullOrEmpty(t)) return false;
            string line = t.Trim();
            return ErrorRecord.IsMatch(line) ||
                   line == "拒绝访问。" || line == "拒绝访问" ||
                   line.Equals("Access is denied.", StringComparison.OrdinalIgnoreCase) ||
                   line.StartsWith("错误: 超过重试限制", StringComparison.Ordinal) ||
                   line.StartsWith("错误：超过重试限制", StringComparison.Ordinal) ||
                   line.StartsWith("ERROR: RETRY LIMIT EXCEEDED", StringComparison.OrdinalIgnoreCase);
        }

        private static readonly Regex ErrorRecord = new Regex(
            @"(?:\bERROR|错误)\s+\d+\s+\(0x[0-9a-f]+\)", RegexOptions.IgnoreCase);

        private void Raise(Action<string> h, string s)
        {
            if (h != null) h(s);
        }
    }

    // ------------------------------------------------------------------
    // 增量读取 UTF-16 日志文件（robocopy 会边写边刷）
    // ------------------------------------------------------------------

    // ------------------------------------------------------------------
    // 日志目录选择器
    //
    // 不能直接拿 Path.GetTempPath() 拼日志路径：系统 TEMP 有可能指向一个
    // 不存在（被清理工具删掉）或不可写的目录。那种情况下 robocopy 会直接
    //   ERROR 3 (0x00000003) Opening Log File ... The system cannot find the path specified.
    // 并且**一个日志都不生成**，界面上只会看到"可能不支持 /UNILOG"这种误导性提示。
    // 这里按顺序找第一个"能建目录 + 能真正写进文件"的位置。
    // ------------------------------------------------------------------
    public static class LogPathPicker
    {
        public static string PickDirectory()
        {
            List<string> cands = new List<string>();
            Add(cands, SafeTemp(), "RobocopyGuiTool");
            Add(cands, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RobocopyGuiTool\\logs");
            Add(cands, Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "RobocopyGuiTool\\logs");
            Add(cands, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "RobocopyGuiTool\\logs");
            try
            {
                string exe = Process.GetCurrentProcess().MainModule.FileName;
                if (!string.IsNullOrEmpty(exe)) Add(cands, Path.GetDirectoryName(exe), "logs");
            }
            catch { }

            for (int i = 0; i < cands.Count; i++)
            {
                string dir = cands[i];
                if (string.IsNullOrEmpty(dir)) continue;
                try
                {
                    Directory.CreateDirectory(dir);       // TEMP 的子目录不存在就建出来
                    // 真的写一次才算数：只判断目录存在是不够的
                    string probe = Path.Combine(dir, "w_" + Guid.NewGuid().ToString("N") + ".tmp");
                    File.WriteAllText(probe, "ok");
                    File.Delete(probe);
                    return dir;
                }
                catch { }
            }
            return null;
        }

        private static void Add(List<string> list, string root, string leaf)
        {
            if (string.IsNullOrEmpty(root)) return;
            try { list.Add(Path.Combine(root, leaf)); }
            catch { }
        }

        public static string SafeTemp()
        {
            try { return Path.GetTempPath(); }
            catch { return null; }
        }
    }

    internal class LogTailer
    {
        private readonly string _path;
        private long _offset;
        private bool _started;

        public LogTailer(string path) { _path = path; }

        public bool Exists { get { return File.Exists(_path); } }

        public string ReadNew()
        {
            if (!File.Exists(_path)) return null;
            try
            {
                using (FileStream fs = new FileStream(_path, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete))
                {
                    if (!_started)
                    {
                        _started = true;
                        _offset = 0;
                    }
                    if (fs.Length <= _offset) return null;
                    fs.Seek(_offset, SeekOrigin.Begin);
                    long remain = fs.Length - _offset;
                    if (remain > 8 * 1024 * 1024) remain = 8 * 1024 * 1024;
                    byte[] buf = new byte[remain];
                    int read = fs.Read(buf, 0, (int)remain);
                    if (read <= 0) return null;

                    int start = 0;
                    // 跳过 BOM
                    if (_offset == 0 && read >= 2 && buf[0] == 0xFF && buf[1] == 0xFE) start = 2;
                    int usable = read - start;
                    if (usable % 2 != 0) usable--;   // 半个字符留到下次
                    _offset += start + usable;
                    if (usable <= 0) return null;
                    return Encoding.Unicode.GetString(buf, start, usable);
                }
            }
            catch
            {
                return null;
            }
        }
    }

    // ------------------------------------------------------------------
    // 复制引擎
    // ------------------------------------------------------------------

    public class RobocopyRunner
    {
        public event Action<string> Log;          // 给人看的日志行
        public event Action<ProgressInfo> Progress;

        private volatile bool _cancel;
        private Process _proc;

        public void Cancel()
        {
            _cancel = true;
            try
            {
                Process p = _proc;
                if (p != null && !p.HasExited) p.Kill();
            }
            catch { }
        }

        public static string BuildArguments(CopyRequest req, string logPath)
        {
            StringBuilder sb = new StringBuilder();
            string src;
            string filter = null;
            if (req.Mode == CopyMode.File)
            {
                src = Path.GetDirectoryName(PathUtil.Clean(req.Source));
                filter = Path.GetFileName(PathUtil.Clean(req.Source));
            }
            else
            {
                src = PathUtil.Clean(req.Source);
            }

            sb.Append(Quote(src));
            sb.Append(' ').Append(Quote(PathUtil.Clean(req.Dest)));
            if (filter != null) sb.Append(' ').Append(Quote(filter));

            CopyOptions o = req.Options;
            if (o.Mirror) sb.Append(" /MIR");
            else if (o.IncludeEmptyDirs) sb.Append(" /E");
            else if (req.Mode == CopyMode.Folder) sb.Append(" /S");

            sb.Append(" /COPY:DAT /DCOPY:DA");
            sb.Append(" /R:").Append(o.Retries);
            sb.Append(" /W:").Append(o.WaitSeconds);
            if (o.Threads > 1) sb.Append(" /MT:").Append(o.Threads);
            if (o.SkipNewer) sb.Append(" /XO");
            if (o.BigFileMode) sb.Append(" /J");
            if (o.DryRun) sb.Append(" /L");

            string[] xd = PathUtil.SplitList(o.ExcludeDirs);
            if (xd.Length > 0)
            {
                sb.Append(" /XD");
                for (int i = 0; i < xd.Length; i++) sb.Append(' ').Append(Quote(xd[i]));
            }
            string[] xf = PathUtil.SplitList(o.ExcludeFiles);
            if (xf.Length > 0)
            {
                sb.Append(" /XF");
                for (int i = 0; i < xf.Length; i++) sb.Append(' ').Append(Quote(xf[i]));
            }

            // 关键：必须用 /UNILOG，UTF-16 日志才能同时正确保存中文路径和中文提示
            // 绝不能用 /NP，否则连日志里的百分比进度一起没了
            sb.Append(" /UNILOG:").Append(Quote(logPath));
            return sb.ToString();
        }

        private static string Quote(string s)
        {
            // Windows 命令行在闭引号前把反斜杠当转义；盘符根目录也必须正确引用。
            int trailing = 0;
            for (int i = s.Length - 1; i >= 0 && s[i] == '\\'; i--) trailing++;
            return "\"" + s + new string('\\', trailing) + "\"";
        }

        public RunResult Run(CopyRequest req, SourceScan scan, string logPath)
        {
            _cancel = false;
            RunResult res = new RunResult();

            // 参数边界校验（纵深防御）：本方法用 UseShellExecute=false 直接启动 robocopy，
            // 没有 shell 参与解释，路径由 Quote() 包裹成单个参数；NTFS 文件名本身不允许
            // 包含引号和控制字符，这里再显式校验一次，确保任何输入都不可能改变参数边界。
            string unsafeArg = CheckArgumentSafety(req);
            if (unsafeArg != null)
            {
                res.Error = unsafeArg;
                res.ExitCode = -1;
                return res;
            }

            // 开跑前先确认目标目录真的能写。写不进去就别让 robocopy 白跑一趟（它还会
            // 按重试设置耗上几秒），直接给出人话报错——BitLocker 锁定、写保护、权限、
            // 安全软件拦截，表现全都是"拒绝访问"。
            string destErr = ProbeDestWritable(req);
            if (destErr != null)
            {
                res.Error = destErr;
                res.ExitCode = -1;
                return res;
            }

            string args = BuildArguments(req, logPath);
            res.CommandLine = "robocopy.exe " + args;

            try { if (File.Exists(logPath)) File.Delete(logPath); }
            catch { }

            RoboLogParser parser = new RoboLogParser(scan != null ? scan.Sizes : null);
            parser.ItemLine += delegate(string s) { Raise(FormatItem(s, scan)); };
            parser.InfoLine += delegate(string s) { Raise(s); };

            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = "robocopy.exe";
            psi.Arguments = args;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;

            Process p;
            try
            {
                p = Process.Start(psi);
            }
            catch (Exception ex)
            {
                res.Error = "无法启动 robocopy：" + ex.Message;
                return res;
            }
            _proc = p;

            // 抽干 stdout/stderr，防止管道写满把 robocopy 卡死。
            // 内容平时用不上（日志走 /UNILOG），但**日志没能创建时它是唯一的线索**，
            // 所以留一份（有上限），不能再像以前那样读完就丢。
            //
            // 注意：robocopy 是控制台程序，这些输出用的是 OEM 代码页（中文系统是 936），
            // 不是 UTF-8。用错编码，报错里的中文提示就全是乱码。
            // 这里直接读原始字节流，自己用 OEM 编码解码（还能正确处理跨读取边界的汉字）。
            StringBuilder console = new StringBuilder();
            Encoding consoleEnc = ConsoleEncoding();
            Thread drainOut = new Thread(delegate() { TryDrain(p.StandardOutput.BaseStream, consoleEnc, console); });
            Thread drainErr = new Thread(delegate() { TryDrain(p.StandardError.BaseStream, consoleEnc, console); });
            drainOut.IsBackground = true;
            drainErr.IsBackground = true;
            drainOut.Start();
            drainErr.Start();

            LogTailer tailer = new LogTailer(logPath);
            long totalBytes = scan != null ? scan.TotalBytes : 0;
            long lastBytes = 0;
            DateTime start = DateTime.UtcNow;
            DateTime lastSample = start;
            double speed = 0;
            int stalled = 0;
            bool sawLog = false;

            while (!p.HasExited)
            {
                if (_cancel)
                {
                    try { p.Kill(); } catch { }
                    break;
                }
                Thread.Sleep(150);

                string chunk = tailer.ReadNew();
                if (chunk != null)
                {
                    sawLog = true;
                    stalled = 0;
                    parser.FeedText(chunk);
                }
                else if (!sawLog)
                {
                    stalled++;
                }

                DateTime now = DateTime.UtcNow;
                double dt = (now - lastSample).TotalSeconds;
                if (dt >= 0.5)
                {
                    long done = parser.DoneBytes;
                    double inst = (done - lastBytes) / dt;
                    speed = speed <= 0 ? inst : (speed * 0.6 + inst * 0.4);
                    lastBytes = done;
                    lastSample = now;
                    RaiseProgress(parser, scan, totalBytes, speed, "正在复制");
                }
            }

            try { p.WaitForExit(5000); } catch { }
            drainOut.Join(1000);
            drainErr.Join(1000);

            // 收尾：把日志尾巴读完
            for (int i = 0; i < 6; i++)
            {
                string chunk = tailer.ReadNew();
                if (chunk == null) break;
                parser.FeedText(chunk);
            }
            parser.Flush();

            res.ExitCode = -1;
            try { res.ExitCode = p.ExitCode; } catch { }

            if (!tailer.Exists)
            {
                res.LogMissing = true;
                res.Error = DescribeMissingLog(logPath, console);
            }

            res.Cancelled = _cancel;
            res.ParserDoneBytes = parser.DoneBytes;
            res.ParserFilesSeen = parser.FilesSeen;
            res.ErrorLines = parser.ErrorLines;
            if (parser.FilesRow != null)
            {
                res.HasSummary = true;
                res.SumTotalFiles = parser.FilesRow[0];
                res.SumCopiedFiles = parser.FilesRow[1];
                res.SumSkippedFiles = parser.FilesRow[2];
                res.SumMismatch = parser.FilesRow[3];
                res.SumFailedFiles = parser.FilesRow[4];
                res.SumExtras = parser.FilesRow[5];
            }
            if (parser.BytesRow != null)
            {
                res.SumTotalBytes = parser.BytesRow[0];
                res.SumCopiedBytes = parser.BytesRow[1];
            }

            res.Success = !res.Cancelled && !res.LogMissing && res.ExitCode >= 0 && res.ExitCode < 8;
            CorrectSuccess(res, scan, parser);
            RaiseProgress(parser, scan, totalBytes, speed, res.Cancelled ? "已停止" : "正在收尾");
            return res;
        }

        /// <summary>
        /// 检查将要拼进 robocopy 参数的各段文本。返回 null 表示安全，否则返回给用户的报错。
        /// </summary>
        internal static string CheckArgumentSafety(CopyRequest req)
        {
            if (req == null) return null;
            string bad = FirstUnsafe(req.Source, "源路径");
            if (bad == null) bad = FirstUnsafe(req.Dest, "目标路径");
            if (bad == null)
            {
                foreach (string s in PathUtil.SplitList(req.Options.ExcludeDirs))
                {
                    bad = FirstUnsafe(s, "排除目录");
                    if (bad != null) break;
                }
            }
            if (bad == null)
            {
                foreach (string s in PathUtil.SplitList(req.Options.ExcludeFiles))
                {
                    bad = FirstUnsafe(s, "排除文件");
                    if (bad != null) break;
                }
            }
            return bad;
        }

        private static string FirstUnsafe(string value, string what)
        {
            if (string.IsNullOrEmpty(value)) return null;
            foreach (char c in value)
            {
                if (c == '"' || c < 0x20)
                    return what + "含有非法字符（引号或控制字符），无法继续：" + value;
            }
            return null;
        }

        /// <summary>
        /// 开跑前探测目标目录可写：真实地建目录 + 写入并删除一个探针文件。
        /// 仅演练（/L）模式不碰目标，跳过探测。返回 null 表示一切正常。
        /// </summary>
        internal static string ProbeDestWritable(CopyRequest req)
        {
            if (req == null || req.Options == null || req.Options.DryRun) return null;

            string dest;
            try { dest = PathUtil.Clean(req.Dest); }
            catch (Exception ex) { return "目标路径无效：" + ex.Message; }
            if (string.IsNullOrEmpty(dest)) return "目标路径为空。";

            try
            {
                Directory.CreateDirectory(dest);
            }
            catch (Exception ex)
            {
                return "无法创建目标目录：" + dest + "\r\n"
                     + "系统报错：" + ex.Message + "\r\n"
                     + "常见原因：盘符不存在、目标盘被 BitLocker 锁定、写保护，或当前账号没有写权限。";
            }

            try
            {
                string probe = Path.Combine(dest, "~rc_probe_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp");
                try { File.WriteAllText(probe, "ok"); }
                finally { try { File.Delete(probe); } catch { } }
            }
            catch (Exception ex)
            {
                return "目标目录无法写入：" + dest + "\r\n"
                     + "系统报错：" + ex.Message + "\r\n"
                     + "当前程序：" + Process.GetCurrentProcess().MainModule.FileName + "\r\n"
                     + "如果资源管理器或命令行能写入同一目录，请检查安全软件的防护记录是否拦截了本程序；"
                     + "找到对应记录后，允许该程序访问此目录。\r\n"
                     + "如果其他程序也不能写入，请检查目录写权限和磁盘写保护。";
            }
            return null;
        }

        /// <summary>
        /// 退出码保留进程原值；最终状态还必须检查完整日志、失败汇总和重试耗尽。
        /// 成功重试允许有历史错误记录，但不允许漏掉失败目录或不完整的结果。
        /// </summary>
        internal static void CorrectSuccess(RunResult res, SourceScan scan, RoboLogParser parser)
        {
            if (!res.Success) return;
            bool missingSummary = parser == null || parser.FilesRow == null || parser.BytesRow == null;
            bool failedRows = parser != null &&
                ((parser.DirsRow != null && parser.DirsRow[4] > 0) ||
                 (parser.FilesRow != null && parser.FilesRow[4] > 0));
            bool incompleteWithErrors = parser != null && parser.ErrorLines > 0 &&
                (parser.FilesRow == null ||
                 (scan != null && parser.FilesRow[0] < scan.TotalFiles));
            if (!missingSummary && !failedRows && !incompleteWithErrors && !parser.RetryLimitExceeded) return;

            res.Success = false;
            StringBuilder m = new StringBuilder();
            m.Append("复制没有完成：退出码为 ").Append(res.ExitCode)
             .Append("，但日志显示失败或结果不完整。\r\n");
            if (scan != null) m.Append("源扫描文件数：").Append(scan.TotalFiles).Append("。\r\n");
            if (missingSummary) m.Append("未取得完整的文件和字节汇总，无法确认复制成功。\r\n");
            if (parser != null && !string.IsNullOrEmpty(parser.FirstError))
                m.Append("robocopy 报错：").Append(parser.FirstError).Append("\r\n");
            if (parser != null && (parser.FirstError.IndexOf("0x00000005", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                   parser.FirstError.IndexOf("拒绝访问", StringComparison.Ordinal) >= 0))
                m.Append("拒绝访问：请检查该目录的写权限、只读状态以及安全软件的拦截记录；必要时以管理员身份运行。\r\n");
            res.Error = m.ToString();
        }

        private const int ConsoleCap = 64 * 1024;

        /// <summary>robocopy 控制台输出用的编码（OEM 代码页，中文系统是 936）。</summary>
        private static Encoding ConsoleEncoding()
        {
            try
            {
                int cp = CultureInfo.CurrentCulture.TextInfo.OEMCodePage;
                if (cp <= 0) cp = 936;
                return Encoding.GetEncoding(cp);
            }
            catch
            {
                try { return Encoding.Default; }
                catch { return Encoding.UTF8; }
            }
        }

        private static void TryDrain(Stream stream, Encoding enc, StringBuilder sink)
        {
            try
            {
                byte[] buf = new byte[4096];
                char[] chars = new char[8192];
                Decoder dec = enc.GetDecoder();       // 有状态，能正确处理被切断的多字节字符
                int n;
                while ((n = stream.Read(buf, 0, buf.Length)) > 0)
                {
                    int c = dec.GetChars(buf, 0, n, chars, 0);
                    if (c <= 0 || sink == null) continue;
                    lock (sink)
                    {
                        if (sink.Length < ConsoleCap) sink.Append(chars, 0, c);
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// 日志没能生成时，把 robocopy 自己说的话摆出来——原因通常就写在那儿。
        /// </summary>
        private static string DescribeMissingLog(string logPath, StringBuilder console)
        {
            string text = "";
            if (console != null) { lock (console) text = console.ToString(); }
            text = (text == null ? "" : text).Trim();
            if (text.Length > 3000) text = text.Substring(0, 3000) + "…";

            StringBuilder m = new StringBuilder();
            m.Append("robocopy 没有生成日志文件。\r\n");
            m.Append("日志路径：").Append(logPath).Append("\r\n");
            m.Append("系统 TEMP：").Append(LogPathPicker.SafeTemp()).Append("\r\n");
            if (text.Length > 0)
            {
                m.Append("robocopy 自己的输出（原因通常就在这几行里）：\r\n");
                m.Append(text).Append("\r\n");
            }
            else
            {
                m.Append("robocopy 在控制台上没有任何输出，可能根本没跑起来（被安全软件拦了？）。\r\n");
            }
            m.Append("常见原因：\r\n");
            m.Append("  1. 日志所在目录不存在或没有写权限（通常是系统 TEMP 目录异常）——"
                     + "本工具已会自动改用一个可写目录，若仍失败请把上面几行发我；\r\n");
            m.Append("  2. robocopy 版本过旧（Windows 7 以下）不支持 /UNILOG——"
                     + "可在命令行执行 robocopy /? 查看是否列出 /UNILOG。");
            return m.ToString();
        }

        private string FormatItem(string raw, SourceScan scan)
        {
            string[] f = raw.Split('\t');
            if (f.Length < 2) return raw;
            string root = scan != null ? scan.Root : null;
            if (f[0] == "DIR")
            {
                return "[目录] " + Shorten(f[1], root);
            }
            long size;
            long.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out size);
            return "[文件] " + Shorten(f[2], root) + "   " + Fmt.Size(size);
        }

        private static string Shorten(string path, string root)
        {
            if (string.IsNullOrEmpty(root)) return path;
            string r = root;
            if (!r.EndsWith("\\")) r += "\\";
            if (path.Length > r.Length && path.Substring(0, r.Length).Equals(r, StringComparison.OrdinalIgnoreCase))
                return path.Substring(r.Length);
            return path;
        }

        private void RaiseProgress(RoboLogParser parser, SourceScan scan, long totalBytes, double speed, string phase)
        {
            if (Progress == null) return;
            ProgressInfo pi = new ProgressInfo();
            pi.Phase = phase;
            pi.DoneBytes = parser.DoneBytes;
            pi.TotalBytes = totalBytes;
            pi.TotalFiles = scan != null ? scan.TotalFiles : 0;
            pi.DoneFiles = parser.FilesSeen;
            pi.BytesPerSecond = speed;
            pi.CurrentFile = parser.CurrentFile;
            if (speed > 1 && totalBytes > pi.DoneBytes)
            {
                double remain = (totalBytes - pi.DoneBytes) / speed;
                if (remain < 86400 * 30) pi.EtaSeconds = (long)remain;
            }
            Progress(pi);
        }

        private void Raise(string line)
        {
            if (Log != null) Log(line);
        }
    }

    // ------------------------------------------------------------------
    // 校验：逐个文件比对 SHA-256。
    // 单线程时读盘速度只有复制速度的一半左右，所以按 CPU 核数开几个线程
    // 并行哈希；结果统一按源清单顺序汇总，行为和旧的串行版完全一致。
    // ------------------------------------------------------------------

    public static class Verifier
    {
        private const byte StMatch = 1, StMismatch = 2, StMissing = 3, StError = 4;

        public static VerifyResult Run(CopyRequest req, SourceScan scan, Func<bool> isCancelled,
                                       Action<int, int, string, long> onProgress)
        {
            VerifyResult vr = new VerifyResult();
            if (scan == null || scan.Files.Count == 0) return vr;

            string destRoot = PathUtil.Clean(req.Dest);
            int total = scan.Files.Count;

            int threads = Math.Min(8, Math.Max(2, Environment.ProcessorCount));
            if (total < 24) threads = 1;   // 文件太少，开多线程纯属开销

            // 每个文件的结果槽。工作线程只写自己的下标，最后按顺序汇总，
            // 保证 Mismatched/Missing/Errors 的顺序与单线程版一致。
            byte[] status = new byte[total];
            string[] errMsg = new string[total];
            long[] sizes = new long[total];
            for (int i = 0; i < total; i++)
            {
                long v;
                if (scan.Sizes.TryGetValue(scan.Files[i], out v)) sizes[i] = v;
            }

            int done = 0;
            long doneBytes = 0;
            bool cancelled = false;
            int per = (total + threads - 1) / threads;
            Thread[] pool = new Thread[threads];

            for (int t = 0; t < threads; t++)
            {
                int from = t * per;
                int to = Math.Min(total, from + per);
                if (from >= to) { pool[t] = null; continue; }
                pool[t] = new Thread(delegate()
                {
                    SHA256 sha = SHA256.Create();
                    byte[] buf = new byte[1024 * 1024];
                for (int i = from; i < to; i++)
                {
                    if (isCancelled != null && isCancelled()) { cancelled = true; return; }
                        string srcFile = scan.Files[i];
                        string dstFile = PathUtil.MapToDest(scan.Root, destRoot, srcFile);
                        try
                        {
                            if (!File.Exists(dstFile))
                            {
                                status[i] = StMissing;
                            }
                            else
                            {
                                byte[] ha = HashFile(sha, srcFile, buf);
                                byte[] hb = HashFile(sha, dstFile, buf);
                                status[i] = Same(ha, hb) ? StMatch : StMismatch;
                            }
                        }
                        catch (Exception ex)
                        {
                            status[i] = StError;
                            errMsg[i] = srcFile + "  ->  " + ex.Message;
                        }
                        Interlocked.Increment(ref done);
                        Interlocked.Add(ref doneBytes, sizes[i]);
                        if (onProgress != null)
                            onProgress(done, total, Path.GetFileName(srcFile), Interlocked.Read(ref doneBytes));
                    }
                });
                pool[t].IsBackground = true;
                pool[t].Start();
            }
            for (int t = 0; t < threads; t++)
                if (pool[t] != null) pool[t].Join();

            // 与旧版一致：取消时只统计已处理的部分（遇到第一个未处理的槽就停）
            for (int i = 0; i < total; i++)
            {
                if (status[i] == 0) break;
                vr.Checked++;
                if (status[i] == StMatch) vr.Matched++;
                else if (status[i] == StMismatch) vr.Mismatched.Add(scan.Files[i]);
                else if (status[i] == StMissing) vr.Missing.Add(scan.Files[i]);
                else if (status[i] == StError) vr.Errors.Add(errMsg[i]);
            }
            vr.Cancelled = cancelled;
            return vr;
        }

        private static byte[] HashFile(SHA256 sha, string path, byte[] buf)
        {
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                sha.Initialize();
                int n;
                while ((n = fs.Read(buf, 0, buf.Length)) > 0)
                    sha.TransformBlock(buf, 0, n, null, 0);
                sha.TransformFinalBlock(new byte[0], 0, 0);
                return sha.Hash;
            }
        }

        private static bool Same(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }
    }
}
