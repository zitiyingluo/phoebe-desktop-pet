// activity.cs —— 活动感知（「菲比能看到你在干嘛」）
//
// 设计原则（隐私优先）
//   1. 默认【一般模式】：只取前台窗口的**标题**和**进程名**，不截屏。
//   2. 深度模式才截屏，且**默认关闭**，只截前台窗口、缩到 512 长边、
//      转 JPEG 压体积，再交给视觉模型。图片有 token 上限（每张最多 1024），
//      所以再大的屏幕也不会爆成本。
//   3. 黑名单按**进程名**和**标题关键词**双向匹配。命中后：
//        一般模式 → 连标题都不上报，进程名也不上报；
//        深度模式 → 跳过截屏。
//   4. 采集结果只留在内存里，**不落盘**。日志里也不写窗口标题。
//   5. 隐私模式开着时，本模块整体停摆（调用方先判断）。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PhoebePet
{
    /// <summary>一次活动采样：用户正在用哪个程序、窗口标题是什么。</summary>
    public sealed class ActivityInfo
    {
        public string ProcessName = "";
        public string WindowTitle = "";
        /// <summary>命中黑名单时，标题和进程名都已被清空。</summary>
        public bool Blacklisted;

        /// <summary>给 AI 看的一句话描述。</summary>
        public string Describe()
        {
            string p = string.IsNullOrWhiteSpace(ProcessName) ? "未知程序" : ProcessName;
            if (string.IsNullOrWhiteSpace(WindowTitle)) return p;
            return $"{p}（{WindowTitle}）";
        }
    }

    /// <summary>
    /// 活动采集与黑名单。
    ///
    /// 类名不叫 Activity：那会和 System.Diagnostics.Activity 撞名
    /// （pet.cs 里 using 了 System.Diagnostics），编译器会报 CS0104 二义性。
    /// </summary>
    public static class ActivityWatch
    {
        // ---------- Win32 ----------
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int count);

        [DllImport("user32.dll")]
        private static extern int GetWindowThreadProcessId(IntPtr hWnd, out int pid);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT r);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        // ---------- 配置 ----------
        /// <summary>深度模式：截屏给视觉模型看。默认关闭（隐私）。</summary>
        public static bool DeepMode;

        /// <summary>截图长边缩放目标。512 会走视觉模型的 low 档，最省。</summary>
        public static int ShotMaxEdge = 512;

        public static int ShotQuality = 60;

        // ---------- 黑名单 ----------
        //
        // 出厂默认值。用户可以在菜单里改，改完后整份清单存进 pet-config.json；
        // 这里保留一份原始副本，用于「恢复默认」。
        private static readonly string[] DefaultProcessRules =
        {
            // 密码 / 密钥
            "keepass", "keepassxc", "1password", "bitwarden", "lastpass",
            "dashlane", "enpass", "roboform", "nordpass",
            // 银行 / 支付（按进程名粗筛，覆盖不到的靠标题关键词）
            "alipay", "bank", "cmb", "icbc", "ccb", "abchina", "boc",
            // 系统安全界面
            "credentialui", "logonui", "consent",
        };

        private static readonly string[] DefaultTitleRules =
        {
            "密码", "password", "passwd", "私钥", "助记词", "密钥",
            "无痕", "incognito", "inprivate", "隐私浏览",
            "网银", "支付", "转账", "银行卡", "信用卡",
            "身份证", "验证码",
            // 注意：不要放「登录」「login」这类词。
            // 它们太宽 —— 大量正常网页标题里都有「登录」（「登录 - 某某网站」），
            // 会把普通浏览当成敏感页面拦掉，用户会觉得「菲比怎么突然瞎了」。
            // 真正要拦的是登录**表单**，那由密码管理器进程名和「密码」关键词覆盖。
            "恢复代码", "recovery code", "seed phrase",
        };

        /// <summary>
        /// 当前生效的清单（内置 + 用户自定义已经合并）。
        /// 用户改过之后这里就是完整的一份，不再是「内置 + 额外」两层。
        /// </summary>
        private static readonly List<string> ProcessRules = new List<string>(DefaultProcessRules);
        private static readonly List<string> TitleRules = new List<string>(DefaultTitleRules);

        /// <summary>
        /// 设置完整清单（来自 pet-config.json）。
        ///
        /// 每个参数**独立**判定：null = 该项用户从未自定义过 → 用出厂默认；
        /// 非 null（哪怕是空列表）= 用户改过 → 完全按他给的来。
        /// 两者分别判定很重要：用户可能只清空了进程名但没动标题。
        ///
        /// 注意：这里不做「默认 + 额外」的合并。早期是两层结构，
        /// 结果用户无法删除出厂规则（只能加不能减）。现在是一份完整清单，
        /// 可以随便增删，清空了就是空。
        /// </summary>
        public static void SetBlacklist(IEnumerable<string>? processes, IEnumerable<string>? titles)
        {
            ProcessRules.Clear();
            TitleRules.Clear();

            if (processes == null)
                ProcessRules.AddRange(DefaultProcessRules);
            else
                foreach (var s in processes)
                    if (!string.IsNullOrWhiteSpace(s)) ProcessRules.Add(s.Trim().ToLowerInvariant());

            if (titles == null)
                TitleRules.AddRange(DefaultTitleRules);
            else
                foreach (var s in titles)
                    if (!string.IsNullOrWhiteSpace(s)) TitleRules.Add(s.Trim().ToLowerInvariant());
        }

        /// <summary>恢复出厂默认清单。</summary>
        public static void ResetBlacklistToDefault()
        {
            ProcessRules.Clear();
            ProcessRules.AddRange(DefaultProcessRules);
            TitleRules.Clear();
            TitleRules.AddRange(DefaultTitleRules);
        }

        public static IReadOnlyList<string> CurrentProcessRules => ProcessRules;
        public static IReadOnlyList<string> CurrentTitleRules => TitleRules;
        public static IReadOnlyList<string> DefaultProcessRulesList => DefaultProcessRules;
        public static IReadOnlyList<string> DefaultTitleRulesList => DefaultTitleRules;

        /// <summary>导出当前清单的副本（存配置用）。</summary>
        public static List<string> ExportProcessRules() => new List<string>(ProcessRules);
        public static List<string> ExportTitleRules() => new List<string>(TitleRules);

        /// <summary>某个进程名 / 标题是否命中黑名单。</summary>
        public static bool IsBlacklisted(string processName, string windowTitle)
        {
            return MatchRule(processName, windowTitle) != null;
        }

        /// <summary>
        /// 返回命中的那条规则（没命中返回 null）。
        /// 单独抽出来是为了让「测试当前窗口」能告诉用户**是哪条规则**拦的 ——
        /// 不然用户只会觉得「菲比怎么看不见了」，无从排查。
        /// </summary>
        public static string? MatchRule(string processName, string windowTitle)
        {
            string p = (processName ?? "").ToLowerInvariant();
            string t = (windowTitle ?? "").ToLowerInvariant();

            foreach (var rule in ProcessRules)
                if (rule.Length > 0 && p.Contains(rule)) return $"进程名包含「{rule}」";

            foreach (var rule in TitleRules)
                if (rule.Length > 0 && t.Contains(rule)) return $"标题包含「{rule}」";

            return null;
        }

        // ---------- 采样 ----------

        /// <summary>
        /// 本进程 PID。用来识别「前台窗口是我们自己」。
        /// 由 PetForm 启动时填，默认取当前进程（构造时就是对的）。
        /// </summary>
        private static int _ownPid = Environment.ProcessId;

        /// <summary>自检用：覆盖「自己」的判定。</summary>
        public static void SetOwnPidForTest(int pid) => _ownPid = pid;

        private static int _currentPid;

        /// <summary>
        /// 前台窗口是否属于桌宠自己。
        ///
        /// 这很常见：右键菜单、设置对话框、以及**输入框**都会把桌宠顶成前台窗口。
        /// 那种时候「用户在干嘛」的答案不该是「在用 dotnet」。
        /// </summary>
        public static bool ForegroundIsSelf()
        {
            try
            {
                IntPtr hwnd = GetForegroundWindow();
                if (hwnd == IntPtr.Zero) return false;
                int pid;
                GetWindowThreadProcessId(hwnd, out pid);
                return pid > 0 && pid == _ownPid;
            }
            catch { return false; }
        }

        /// <summary>
        /// 上一次「不是自己」的前台窗口信息。
        ///
        /// 为什么需要：你在输入框里打「这个好看吗」然后回车 —— 此刻前台窗口
        /// 就是输入框本身（属于桌宠）。但你想让菲比看的是**输入框背后那个**
        /// 窗口。所以这里记住最近一次非自己的前台窗口，截屏时用它。
        /// </summary>
        private static ActivityInfo? _lastForeign;
        private static IntPtr _lastForeignHwnd = IntPtr.Zero;

        /// <summary>每帧调用：刷新「最近一次非自己的前台窗口」。</summary>
        public static void TickRemember()
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero || !IsWindowVisible(hwnd)) return;

            int pid;
            GetWindowThreadProcessId(hwnd, out pid);
            if (pid > 0 && pid == _ownPid) return;   // 是自己的窗口，跳过

            var cur = Current();
            if (cur != null && !cur.Blacklisted)
            {
                _lastForeign = cur;
                _lastForeignHwnd = hwnd;
            }
        }

        /// <summary>
        /// 忘掉已经记住的东西。
        /// 进入隐私模式时调用 —— 只停止采集还不够，之前记下的那次
        /// 「用户在用什么」也得一起丢掉，否则它还会被用于下一次搭话。
        /// </summary>
        public static void Forget()
        {
            _lastForeign = null;
            _lastForeignHwnd = IntPtr.Zero;
        }

        /// <summary>是否有记住的历史（自检用）。</summary>
        public static bool HasRemembered => _lastForeign != null;

        /// <summary>
        /// 取「用户实际在看的东西」。
        /// 前台是自己的窗口（菜单/对话框/输入框）时，回退到最近一次非自己的窗口。
        /// </summary>
        public static ActivityInfo? CurrentOrLastForeign()
        {
            var cur = Current();
            if (cur != null) return cur;
            return _lastForeign;   // 前台是自己 or 取不到 → 用上一次的
        }

        /// <summary>
        /// 取当前前台窗口信息。取不到（或命中黑名单）时返回 null 或清空后的对象。
        /// 注意：命中黑名单时**不返回进程名**，避免"只知道程序名也算泄露"。
        /// </summary>
        public static ActivityInfo? Current()
        {
            try
            {
                IntPtr hwnd = GetForegroundWindow();
                if (hwnd == IntPtr.Zero || !IsWindowVisible(hwnd)) return null;

                var sb = new StringBuilder(512);
                GetWindowTextW(hwnd, sb, sb.Capacity);
                string title = sb.ToString().Trim();

                int pid;
                GetWindowThreadProcessId(hwnd, out pid);

                // 自己不算「用户在干嘛」。
                //
                // 为什么按 PID 比而不是按进程名：
                //   桌宠是用 `dotnet PhoebePet.dll` 启动的，前台窗口的进程名是
                //   **dotnet**，不是 PhoebePet。早期按名字比对，永远匹配不上，
                //   于是右键菜单或对话框一弹出来（它们会把桌宠顶成前台窗口），
                //   菲比就以为「用户在用一个叫 dotnet 的程序」，
                //   甚至对着自己的窗口截图、自言自语。
                // 按 PID 比能一劳永逸地覆盖各种宿主。
                if (pid > 0 && pid == _ownPid) return null;

                string proc = "";
                try
                {
                    if (pid > 0) proc = Process.GetProcessById(pid).ProcessName;
                }
                catch { /* 进程可能已退出，拿不到就算了 */ }

                var info = new ActivityInfo { ProcessName = proc, WindowTitle = title };

                if (IsBlacklisted(proc, title))
                {
                    info.Blacklisted = true;
                    info.ProcessName = "";
                    info.WindowTitle = "";
                }

                return info;
            }
            catch
            {
                return null;   // 采集失败一律当作「不知道」，绝不因此崩掉
            }
        }

        /// <summary>
        /// 截取「用户实际在看的那一扇窗」，返回 JPEG 字节（用于视觉模型）。
        ///
        /// 为什么不能直接截前台窗口：
        ///   你按回车发消息时，前台窗口是**输入框自己**（属于桌宠）——
        ///   直接截就只会拍到菲比的输入框，而不是你想给她看的那个页面。
        ///   所以优先用「最近一次非自己的前台窗口」的句柄。
        ///
        /// 几个刻意的选择：
        ///   - 只截那一扇窗，不截整个屏幕 —— 别的窗口和桌面不进画面；
        ///   - 缩到 ShotMaxEdge 长边，走视觉模型的 low 档，最省 token；
        ///   - 转 JPEG 而不是 PNG —— 截图内容通常色彩连续，JPEG 体积小得多；
        ///   - 用 CopyFromScreen：PrintWindow 对部分 GPU 加速窗口会画出空白。
        ///     代价是窗口被遮挡时会拍到上层内容，所以只能拍当时确实在前台的窗口。
        /// </summary>
        public static byte[]? CaptureForeground()
        {
            try
            {
                IntPtr hwnd;
                if (ForegroundIsSelf() && _lastForeignHwnd != IntPtr.Zero && IsWindowVisible(_lastForeignHwnd))
                    hwnd = _lastForeignHwnd;          // 前台是自己 → 拍上一次那个窗口
                else
                    hwnd = GetForegroundWindow();

                if (hwnd == IntPtr.Zero || !IsWindowVisible(hwnd)) return null;

                RECT r;
                if (!GetWindowRect(hwnd, out r)) return null;

                int w = r.Right - r.Left, h = r.Bottom - r.Top;
                if (w <= 0 || h <= 0 || w > 20000 || h > 20000) return null;

                // 缩放到目标长边（只缩不放）
                int maxEdge = Math.Max(64, ShotMaxEdge);
                double scale = Math.Min(1.0, maxEdge / (double)Math.Max(w, h));
                int dw = Math.Max(1, (int)Math.Round(w * scale));
                int dh = Math.Max(1, (int)Math.Round(h * scale));

                using var full = new Bitmap(w, h, PixelFormat.Format24bppRgb);
                using (var g = Graphics.FromImage(full))
                    g.CopyFromScreen(r.Left, r.Top, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);

                using var small = new Bitmap(dw, dh, PixelFormat.Format24bppRgb);
                using (var g = Graphics.FromImage(small))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.DrawImage(full, 0, 0, dw, dh);
                }

                using var ms = new MemoryStream();
                var codec = GetJpegCodec();
                if (codec != null)
                {
                    // 注意：Encoder 要写全名。System.Text.Encoder 也在这个文件的作用域里
                    // （因为 using 了 System.Text），不限定会报 CS0104 二义性。
                    var ps = new EncoderParameters(1);
                    ps.Param[0] = new EncoderParameter(
                        System.Drawing.Imaging.Encoder.Quality,
                        (long)Math.Max(10, Math.Min(100, ShotQuality)));
                    small.Save(ms, codec, ps);
                }
                else
                {
                    small.Save(ms, ImageFormat.Jpeg);   // 找不到编码器就走默认质量
                }
                return ms.ToArray();
            }
            catch (Exception e)
            {
                AppPaths.Log($"  [活动] 截屏失败: {e.Message}");
                return null;
            }
        }

        private static ImageCodecInfo? GetJpegCodec()
        {
            try
            {
                return ImageCodecInfo.GetImageEncoders()
                    .FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);
            }
            catch { return null; }
        }
    }
}
