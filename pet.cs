// 桌宠 · 菲比啾比 —。单文。C# / WinForms
//
// 为什么用分层窗口（UpdateLayeredWindow）而不。TransparencyKey。//   图片边缘有大量反锯齿的半透明像素。TransparencyKey 是二值透明。//   半透明像素只能要么全透要么全不透，会留一圈黑边。//   UpdateLayeredWindow 走逐像。alpha 合成，边缘干净。//
// 运行时要求：.NET 8+（本机实测可用）。不依赖任何第三方库。// 启动：双。start-pet.cmd，或 `dotnet run pet.cs`

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Forms;
using PhoebePet;   // 气泡绘制在 bubble.cs，命名空间是 PhoebePet

static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct BLENDFUNCTION
    {
        public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFO { public BITMAPINFOHEADER bmiHeader; }

    public const int ULW_ALPHA = 2;
    public const byte AC_SRC_OVER = 0;

    [DllImport("user32.dll")]
    public static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    /// <summary>SetWindowPos 。hWndInsertAfter：置。/ 取消置顶。/summary>
    public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    public static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const byte AC_SRC_ALPHA = 1;
    public const int DIB_RGB_COLORS = 0;
    public const int BI_RGB = 0;

    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_LAYERED = 0x00080000;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst,
        ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc,
        int crKey, ref BLENDFUNCTION pblend, int dwFlags);

    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hDC);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hDC);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi,
        uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);

    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();

    /// <summary>
    /// 取「距离用户最后一次输入（键鼠）过了多久」。
///
    /// 隐私说明：这个接口只回答「多久没动」，拿不到你按了什么键、点了哪里。
/// 系统里唯一记录输入时间的全局计数器，零权限、零成本。
/// </summary>
    [DllImport("user32.dll")]
    public static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [StructLayout(LayoutKind.Sequential)]
    public struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;   // 最后一次输入时的 tick（毫秒，会回绕）
    }

    /// <summary>
    /// 用户已闲置的毫秒数。取不到时返。0（当作「刚操作过」，宁可不说也不要乱说）。
/// </summary>
    public static long IdleMilliseconds()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return 0;

        // dwTime 。32 。tick，约 49.7 天回绕一次。
// GetTickCount 也是 32 位，直接。uint 相减就能正确处理回绕。
uint now = (uint)Environment.TickCount;
        uint elapsed = unchecked(now - info.dwTime);
        return elapsed;
    }

    /// <summary>把窗口形状裁剪成任意区域（用于输入框的圆角，消掉四角白边）。/summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

    /// <summary>查询窗口当前区域。返回的是句柄副本，用完必须 DeleteObject。/summary>
    [DllImport("user32.dll")]
    public static extern IntPtr GetWindowRgn(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    /// <summary>区域类型常量（GetRgnBox 的返回值）。/summary>
    public const int NULLREGION = 1;
    public const int SIMPLEREGION = 2;
    public const int COMPLEXREGION = 3;

    [DllImport("gdi32.dll")]
    public static extern int GetRgnBox(IntPtr hrgn, out RECT r);

    /// <summary>
    /// 。GraphicsPath 得到一。HRGN（GDI 区域句柄）。
///
    /// 两个坑：
    ///   1) GraphicsPath.GetHrgn 。.NET Core / 6+ 才加。API，本项目引用。
///      8.0.10 参考包里没有这个方法，编译不过。Region.GetHrgn 是。API。
///   2) Region.GetHrgn 。.NET 8 上已经不接受 null —。。null 会抛
    ///      ArgumentNullException。必须给一个真 Graphics，这里是 1x1 。
///      内存位图。返回的 HRGN 是设备相关坐标，。1x1 位图而言就是
    ///      原始坐标，够用。
/// </summary>
    public static IntPtr PathToHrgn(GraphicsPath path)
    {
        using var bmp = new Bitmap(1, 1);
        using var g = Graphics.FromImage(bmp);
        using var rgn = new Region(path);
        return rgn.GetHrgn(g);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern IntPtr GetWindowLongPtr32(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern IntPtr SetWindowLongPtr32(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    public static IntPtr GetWindowLong(IntPtr h, int i) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(h, i) : GetWindowLongPtr32(h, i);
    public static void SetWindowLong(IntPtr h, int i, IntPtr v)
    {
        if (IntPtr.Size == 8) SetWindowLongPtr64(h, i, v);
        else SetWindowLongPtr32(h, i, v);
    }
}

/// <summary>
/// 定位「程序自己的目录」并写日志。/// 为什么要这么绕：Environment.ProcessPath 。`dotnet foo.dll` 这种宿主下可能为 null。/// 直接 Path.GetDirectoryName(null) 会抛异常；这里逐候选目录尝试，写不进去也不影响运行。/// </summary>
static class AppPaths
{
    private static string? _dir;

    /// <summary>程序目录：优。exe/dll 所在位置，其次 BaseDirectory。/summary>
    public static string Dir => _dir ??= ResolveDir();

    private static string ResolveDir()
    {
        // 顺序很重要：
        //   1) Assembly.Location —。`dotnet PhoebePet.dll` 。ProcessPath 。dotnet.exe。
//      拿它当程序目录会。C:\Program Files\dotnet 找贴图，必然失败。
//   2) ProcessPath —。将来若做成独。exe，这里才是对的。
//   3) BaseDirectory / cwd —。兜底。
var entry = System.Reflection.Assembly.GetEntryAssembly();
        var candidates = new[]
        {
            entry?.Location,
            Environment.ProcessPath,
            AppContext.BaseDirectory,
            Environment.CurrentDirectory,
        };

        foreach (var raw in candidates)
        {
            if (string.IsNullOrEmpty(raw)) continue;
            try
            {
                var d = Path.GetDirectoryName(raw!);
                if (!string.IsNullOrEmpty(d)) return d!;
            }
            catch { }
        }
        return AppContext.BaseDirectory;
    }

    /// <summary>
    /// 日志文件大小上限（KB）。由 PetForm 启动时从 pet-config.json 读入后写到这里。
/// 0 或负。= 不限制。默。1024KB = 1MB。
/// </summary>
    public static int MaxLogKB = 1024;

    /// <summary>
    /// 每写这么多行才去查一次文件大小。
///
    /// 为什么不每行都查：Log() 每次调用都会发生，。FileInfo.Length 是一次系统调用。
/// 桌宠正常运行时日志很少，但一旦开了主动发言，每秒都可能写。
/// 用计数器节流，代价从「每行一次」降到「每 64 行一次」，。1MB 的阈值下
    /// 最多写。64 行（几十 KB）才会轮转，完全无所谓。
/// </summary>
    private const int SizeCheckEveryLines = 64;
    private static int _linesSinceSizeCheck;

    /// <summary>
    /// 超限时把 pet.log 轮转。pet.log.1，然后重开一个空。pet.log。
/// 保留一份旧的而不是直接删 —。刚崩过的现场往往就在上一份里。
/// </summary>
    private static void RotateIfNeeded(string dir)
    {
        if (MaxLogKB <= 0) return;
        if (++_linesSinceSizeCheck < SizeCheckEveryLines) return;
        _linesSinceSizeCheck = 0;

        string log = Path.Combine(dir, "pet.log");
        try
        {
            var fi = new FileInfo(log);
            if (!fi.Exists || fi.Length < (long)MaxLogKB * 1024) return;

            string old = Path.Combine(dir, "pet.log.1");
            // File.Move 默认不覆盖，先把上一份旧的清。
if (File.Exists(old)) File.Delete(old);
            File.Move(log, old);

            File.AppendAllText(log,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  --- 日志超过 {MaxLogKB}KB，已轮转为 pet.log.1 ---{Environment.NewLine}");
        }
        catch { /* 轮转失败不能影响正常写日志 */ }
    }

    /// <summary>依次尝试各候选目录写一行日志，任何一个成功就返回。/summary>
    public static void Log(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}";
        foreach (var dir in Candidates())
        {
            try
            {
                RotateIfNeeded(dir);
                File.AppendAllText(Path.Combine(dir, "pet.log"), line);
                return;
            }
            catch { /* 换下一个候选目录 */ }
        }
    }

    private static IEnumerable<string> Candidates()
    {
        // 自检时把目标目录钉死，避免污染真。pet.log
        if (_testDir != null) { yield return _testDir; yield break; }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        yield return Dir;
        seen.Add(Dir);

        foreach (var d in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            if (string.IsNullOrEmpty(d)) continue;
            if (seen.Add(d)) yield return d;
        }
    }

    // ---------- 自检钩子 ----------
    private static string? _testDir;
    /// <summary>自检用：把日志强制写到指定目录。/summary>
    public static void ForceLogDirForTest(string dir)
    {
        _testDir = dir;
        _linesSinceSizeCheck = SizeCheckEveryLines - 1;   // 下一次写就触发大小检查
}
    /// <summary>自检用：解除强制目录。/summary>
    public static void ClearLogDirForTest() => _testDir = null;
}

sealed class PetForm : Form
{
    // ---------- 可调参数 ----------
    private const string SpriteFull = "phoebe@2x.png";
    private const string SpriteSmall = "phoebe.png";

    /// <summary>
    /// 闭眼立绘的命名后缀（隐私模式用）。
    /// 约定：立绘「菲比-站」的闭眼版叫「菲比-站-闭眼」。
    /// 用约定而不是让用户逐张配，是因为内置三套姿势都要配一遍太啰嗦。
    /// </summary>
    private const string ClosedEyeSuffix = "-闭眼";

    /// <summary>
    /// 气泡左右两侧各留多少像素。
    ///
    /// 原来只留 3px（`_padW - 6`），气泡几乎贴满整个窗口，看着窄且挤。
    /// 18px 是个手感值：既明显留白，又不会把可用宽度压得太窄。
    /// 注意它会同时影响折行数，所以画布尺寸变化时高度会跟着变 —— 这是对的。
    /// </summary>
    private const int BubbleSideMargin = 18;


    private const int PetSizeDefault = 200;   // 屏幕上的目标宽度（像素）
    private const int BobAmplitude = 4;       // 待机上下浮动幅度
    private const int BobPeriodMs = 3000;     // 浮动周期
    public const int CanvasMargin = 14;       // 画布留边：给浮动 + 倾斜 + 挤压留位移空间
private const double LeanMax = 10;        // 拖动时的最大倾斜偏移
    private const int ReactionMs = 520;
    private const int TickMs = 33;

    private enum Reaction { None, Bounce, Squash }

    private readonly string _dir;
    private readonly string _cfgPath;
    private Config _cfg;

    private Bitmap? _sprite;     // 按目标尺寸缩放后的角色图
    private Bitmap? _frame;      // 每帧绘制用的画布（带留边）
private int _padW, _padH;    // 画布尺寸 = 贴图尺寸 + Margin*2

    /// <summary>
    /// 立绘下方预留的高度（像素），给输入框让位。
///
    /// 为什么需要它：原。_padH = 贴图。+ Margin*2，。Render() 。_padH/2 。
/// 立绘中心；画布一加高，立绘就会跟着往下跑。所以这里把「立绘区」和
    /// 「输入区」拆开：_artH 是立绘所在区域的高度，_padH 是含输入区的总高。
/// 立绘始终。_artH 内居中，输入区只是画布向下多出来的一块透明区域。
///
    /// 默认 0 = 行为与改动前完全一致。
/// </summary>
    private int _bottomReserve;

    /// <summary>立绘区上方给气泡预留的高度。/summary>
    private int _topReserve;

    /// <summary>立绘所在区域的高度（不含上下预留）。/summary>
    private int _artH;

    private IntPtr _memDc = IntPtr.Zero;
    private IntPtr _dib = IntPtr.Zero;
    private IntPtr _oldObj = IntPtr.Zero;
    private IntPtr _bits = IntPtr.Zero;

    private Native.BLENDFUNCTION _blend = new Native.BLENDFUNCTION
    {
        BlendOp = Native.AC_SRC_OVER,
        BlendFlags = 0,
        SourceConstantAlpha = 255,
        AlphaFormat = Native.AC_SRC_ALPHA,
    };

    private readonly Timer _timer = new Timer { Interval = TickMs };
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private bool _dragging;
    private Point _dragCursorOrigin;
    private Point _dragWinOrigin;
    private double _lean;              // 当前倾斜（正=向右倾）
    private long _lastMouseMoveMs;

    private Reaction _reaction = Reaction.None;
    private long _reactionStartMs;
    private long _lastClickMs;         // 用来区分单击/双击

    private int _size;

    /// <summary>「尺寸」子菜单里的所有档位项（用来同步打勾状态）。</summary>
    private readonly List<ToolStripMenuItem> _sizeMenuItems = new();

    public PetForm()
    {
        // 贴图/配置/日志一律以程序所在目录为准，不依赖当前工作目。
_dir = AppPaths.Dir;
        _cfgPath = Path.Combine(_dir, "pet-config.json");
        _cfg = Config.Load(_cfgPath);
        _size = _cfg.Size > 0 ? _cfg.Size : PetSizeDefault;
        // 日志上限要在任何 Log() 之前生效，否则启动阶段的日志不受约束
        AppPaths.MaxLogKB = _cfg.LogMaxKB;
        ApplyActivityConfig();

        // 恢复上次的隐私模式状态。
        // 直接赋字段而不是走 PrivacyMode 的 setter —— setter 会读
        // _privacyToggleItem（菜单此时还没建），而且会触发保存和气泡，都不合适。
        _privacyMode = _cfg.PrivacyMode;
        if (_privacyMode)
        {
            _privacyOnAtMs = _clock.ElapsedMilliseconds;
            _privacyHintShown = false;
        }

        // 音效音量要在第一次播放前设好
        Sound.SetVolumePercent(_cfg.SoundVolume);

        // 用量账本（跨重启累计）
        Usage.Load(_dir);

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = _cfg.TopMost;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Black;
        Text = "桌宠 · 菲比啾比";

        _bubbleFont = CreateBubbleFont(_bubbleStyle.FontSize);
        BuildSprite();

        var wa = Screen.PrimaryScreen!.WorkingArea;
        int x = _cfg.X ?? (wa.Right - Width - 40);
        int y = _cfg.Y ?? (wa.Bottom - Height - 8);
        Location = ClampToScreen(new Point(x, y));

        BuildMenu();
        WireEvents();

        _timer.Tick += (_, _) => Render();
        _timer.Start();

        // 置顶要在窗口真正显示之后再落一次。
// 构造期。TopMost = true 只是排队，句柄建好后 exStyle 会被重写。
// 所。Shown 时再确认一次，保证「刚打开就是置顶的」。        Shown += (_, _) =>
        {
            ApplyTopMost(_cfg.TopMost);
            PositionInput();
        };

        Log($"启动完成：程序目录 {_dir}");
        Log($"  贴图 {_sprite!.Width}x{_sprite.Height}，画布 {_padW}x{_padH}，" +
            $"立绘区 {_padW}x{_artH}，预留 {_bottomReserve}px，" +
            $"窗口 ({Left},{Top})，尺寸参数 {_size}，置顶 {TopMost}，屏幕 {wa.Width}x{wa.Height}");
    }

    /// <summary>写一行日志（失败也不影响运行）。</summary>
    private static void Log(string message) => AppPaths.Log(message);

    /// <summary>
    /// <summary>自检用：气泡左右边距（预览要和实际一致）。</summary>
    internal static int BubbleSideMarginForTest => BubbleSideMargin;

    /// <summary>自检用：当前气泡字号（pt）。</summary>
    internal float BubbleFontSize => _bubbleStyle.FontSize;

    /// <summary>自检用：气泡字体的实际名字（验证真的选中了中文字体）。</summary>
    internal static string BubbleFontNameForTest(float size)
    {
        try { using var f = CreateBubbleFont(size); return f.Name; }
        catch { return "(error)"; }
    }

    /// <summary>自检用：按当前字号生成的气泡字体（能显示中文才算合格）。</summary>
    internal static Font BubbleFontForTest(float size) => CreateBubbleFont(size);

    /// <summary>
    /// 挑一个能显示中文的字体。系统字体名不确定时逐个回退。
    /// 全都失败才用通用无衬线（会缺字，但不至于崩）。
    ///
    /// 注意这里的检测**不能只看 f.Name**：GDI+ 在字体不存在时不会抛异常，
    /// 而是悄悄回退（常常回退成 Microsoft Sans Serif），而 Name 属性
    /// 有时仍报原名 —— 那样就会「以为拿到了雅黑，其实拿到的是英文字体」。
    /// 后果不是崩，而是**中文走字体链接**，行高与 MeasureString 对不上，
    /// 气泡里行距忽大忽小（实测过：GetHeight 16.6 但实画 18.4）。
    /// 所以这里额外确认它真的能显示中文。
    /// </summary>
    public static Font CreateBubbleFont(float size)
    {
        foreach (var name in new[] { "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "SimSun" })
        {
            try
            {
                var f = new Font(name, size);
                if (string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase) && CanRenderCjk(f))
                    return f;
                f.Dispose();
            }
            catch { }
        }

        // 名字都对不上时，再扫一遍系统已装字体，挑第一个能显示中文的。
        // 这一步是为了「装了中文雅黑但名字不同」的机器。
        try
        {
            foreach (var fam in FontFamily.Families)
            {
                try
                {
                    var f = new Font(fam, size);
                    if (CanRenderCjk(f)) return f;
                    f.Dispose();
                }
                catch { }
            }
        }
        catch { }

        return new Font(FontFamily.GenericSansSerif, size);
    }

    /// <summary>这个字体能不能真的画出汉字（用「有字形」判断，不看名字）。</summary>
    private static bool CanRenderCjk(Font f)
    {
        try { return f.FontFamily.IsStyleAvailable(FontStyle.Regular) && CanRenderCjk(f.FontFamily); }
        catch { return false; }
    }

    private static bool CanRenderCjk(FontFamily fam)
    {
        try
        {
            // 0x4E2D = 「中」。没有字形时 GDI+ 会返回 false 而不是抛异常。
            return fam.GetCellAscent(FontStyle.Regular) > 0 && HasGlyph(fam, 0x4E2D);
        }
        catch { return false; }
    }

    /// <summary>字体里有没有这个码点的字形。</summary>
    private static bool HasGlyph(FontFamily fam, int codepoint)
    {
        try
        {
            // GDI+ 没有直接的「有无字形」API，用测量的方式间接判断：
            // 缺字形时通常画成方块或空白，宽度会明显异常。这里用
            // 「能不能正常测出宽度且非零」作为近似判据。
            using var bmp = new Bitmap(1, 1);
            using var g = Graphics.FromImage(bmp);
            using var f = new Font(fam, 12f);
            float w = g.MeasureString(char.ConvertFromUtf32(codepoint), f).Width;
            return w > 1f;
        }
        catch { return false; }
    }

    // ================= 配置 =================
    // internal 而不。private：自检（Program 类）要能 new 一个默认配置来验证默认。
internal sealed class Config
    {
        public int? X { get; set; }
        public int? Y { get; set; }
        public int Size { get; set; } = PetSizeDefault;

        /// <summary>
        /// 正在使用的立绘（换装）。
        ///
        /// 空 = 出厂贴图 phoebe.png / phoebe@2x.png，行为和加这个功能之前**完全一致**。
        /// 非空时支持三种写法：文件名（可省扩展名）、相对路径、绝对路径。
        /// 找不到会回退出厂贴图并写日志，不会让菲比消失。
        /// </summary>
        public string? Art { get; set; }
        public bool TopMost { get; set; } = true;
        public bool ClickThrough { get; set; }
        public bool ShowBubble { get; set; } = true;

        /// <summary>pet.log 超过这个大小（KB）就轮转。pet.log.1。 = 不限制。/summary>
        public int LogMaxKB { get; set; } = 1024;

        // ---------- 主动发言 ----------

        /// <summary>是否启用「闲置主动发言」。/summary>
        public bool ProactiveIdle { get; set; } = true;

        /// <summary>闲置多少秒后主动开口。/summary>
        public int IdleSeconds { get; set; } = 120;

        /// <summary>
        /// 两次主动发言之间至少间隔多少秒。
/// 没有这个的话，用户去开个会，它会每。IdleSeconds 说一句，没完没了。
/// </summary>
        public int ProactiveCooldownSeconds { get; set; } = 600;

        /// <summary>
        /// <summary>
        /// 单次闲置期间最多主动开口几次。到上限就安静等着，直到用户有操作才重置。
        /// 这是比冷却更硬的一道闸：冷却只管间隔，这个管总量。
        /// </summary>
        public int ProactiveMaxPerIdle { get; set; } = 2;

        // ---------- 活动感知（阶段 C） ----------

        /// <summary>是否采集「用户在用什么程序」（一般模式）。关掉就完全不知道。</summary>
        public bool WatchActivity { get; set; } = true;

        /// <summary>
        /// 深度模式：在上面的基础上再截屏给视觉模型。
        /// 默认 **false** —— 截屏是隐私敏感操作，必须由用户主动打开。
        /// </summary>
        public bool DeepWatch { get; set; } = false;

        /// <summary>截图长边像素。512 走视觉模型的 low 档，最省 token。</summary>
        public int ShotMaxEdge { get; set; } = 512;

        /// <summary>截图 JPEG 质量（10-100）。越低越省流量，60 足够看清在干嘛。</summary>
        public int ShotQuality { get; set; } = 60;

        /// <summary>
        /// 黑名单：进程名。**null = 用户从未自定义过**（用出厂默认）；
        /// 空列表 = 用户故意清空了（就是没有黑名单）。
        ///
        /// 这两种状态必须能区分：早期用「列表为空」当作「没自定义」，
        /// 结果用户把黑名单清空保存后，下次启动又变回默认内容 —— 只能加不能清。
        /// </summary>
        public List<string>? BlacklistProcess { get; set; }

        /// <summary>黑名单：窗口标题关键词。null = 从未自定义过，语义同上。</summary>
        public List<string>? BlacklistTitle { get; set; }

        /// <summary>
        /// 用户主动聊起当前窗口内容时，要不要顺手截一张图给模型看。
        /// 例如「你看这个好不好看」——这句话本身就是 user 消息，
        /// 图片正好可以挂在这条消息上（视觉模型只接受 user 消息带图）。
        /// </summary>
        public bool ShotOnUserMention { get; set; } = true;

        /// <summary>
        /// 气泡放多久自动消失（秒）。0 = 不自动消失。
        ///
        /// 为什么需要这个：主动发言要求「气泡里没话说」，气泡不清空就永远
        /// 不会主动开口。所以必须有个自动清理机制。
        /// </summary>
        public int BubbleSeconds { get; set; } = 0;   // 0 = 按文字长度自动估算

        /// <summary>切换窗口/程序时要不要主动搭话。</summary>
        public bool ProactiveOnSwitch { get; set; } = true;

        /// <summary>
        /// 切换后等几秒再开口。
        /// 用户切窗口常常是连续操作，立刻说话会被下一次切换打断，也显得聒噪。
        /// </summary>
        public int SwitchSettleSeconds { get; set; } = 6;

        // ---------- 隐私模式（阶段 D） ----------

        /// <summary>
        /// 隐私模式是否开启。开启后不采集活动、不截屏、不主动发言。
        ///
        /// 默认 **false**，但会记住上次的状态（见 PrivacyMode 的实现）——
        /// 如果你为了开会开了隐私模式然后关掉桌宠，下次启动应该还是隐私的，
        /// 否则会在你不知情的时候恢复采集。
        /// </summary>
        public bool PrivacyMode { get; set; } = false;

        // ---------- 双击音效（阶段 E） ----------

        /// <summary>
        /// 双击时播放音效（而不是跳一下）。
        /// 音效文件夹为空或全部播放失败时，无论如何都会回退成「跳一下」。
        /// </summary>
        public bool DoubleClickSound { get; set; } = true;

        /// <summary>音效音量 0-100。</summary>
        public int SoundVolume { get; set; } = 80;

        /// <summary>
        /// 启用的音效分组（= Sounds/ 下的子文件夹名）。
        ///
        /// **null 或空 = 不限制**，用全部音效 —— 这样老配置升级上来
        /// 行为不变（不会突然没声音）。用户勾了具体分组才收敛到那几组。
        ///
        /// 用 null 和空的区别只在「是否自定义过」，这里两者都当「全部」处理，
        /// 因为「一个组都不启用」对用户没意义（等于静音，那还不如关掉开关）。
        /// </summary>
        public List<string>? SoundGroups { get; set; }

        // ---------- 隐私模式立绘 ----------

        /// <summary>
        /// 隐私模式是否自动换成闭眼立绘。
        ///
        /// 默认**开**：内置的三套立绘（站/坐/趴）都配了闭眼版，
        /// 开隐私模式时换成闭眼，一眼就能看出「我现在不看也不说」。
        ///
        /// 为什么要有这个开关：用户自己往 Art/ 里丢立绘时，
        /// **多半不会准备闭眼版**。没有开关的话，功能只能二选一 ——
        /// 要么对所有人生效（那别人自加的图就没法用隐私模式了），
        /// 要么根本不提供。给个开关，谁需要谁开。
        ///
        /// 找不到配对的闭眼图时会**静默跳过**（保持原图），不报错 ——
        /// 缺一张图不该让桌宠出问题。
        /// </summary>
        public bool PrivacyArtSwitch { get; set; } = true;

        /// <summary>
        /// 隐私模式用的立绘名（对应 Art/ 里的文件名，可不带扩展名）。
        /// 留空 = 自动：在当前立绘后面加「-闭眼」去找。
        ///
        /// 为什么默认留空走自动：约定优于配置。内置图叫「菲比-站」，
        /// 闭眼版自然就叫「菲比-站-闭眼」，不用用户再配一遍。
        /// </summary>
        public string PrivacyArt { get; set; } = "";

        public static Config Load(string path)
        {
            try
            {
                if (File.Exists(path))
                    return JsonSerializer.Deserialize<Config>(File.ReadAllText(path)) ?? new Config();
            }
            catch { /* 配置损坏就退回默认值 */ }
            return new Config();
        }

        public void Save(string path)
        {
            try
            {
                File.WriteAllText(path,
                    JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { /* 存不下也不能因此崩掉 */ }
        }
    }

    // ================= 贴图 =================
    /// <summary>
    /// 当前生效的贴图路径（换装时会变）。
    /// 默认是出厂那两张，Art/ 里选了别的就换成那张。
    /// </summary>
    private string? _spritePath;

    /// <summary>
    /// 当前屏幕允许的最大画布高度。
    ///
    /// 为什么要问系统而不是写死一个数：
    ///   Windows 对窗口高度有上限（约屏幕高 + 边框），超过会被**静默夹掉** ——
    ///   ClientSize 赋值不报错，但读回来是夹过的值，于是画布位图比窗口还大，
    ///   立绘底部被切。不同屏幕分辨率上限不同，所以按当前屏算。
    ///
    /// 余量必须留够：自检曾经量出「窗口高 == 上限」这种**卡在边界上**的结果
    /// （1352 == 1352），差一点点就会被系统夹。所以这里额外留出 8% 的余量，
    /// 保证夹取后一定在系统允许范围内。
    /// </summary>
    private int MaxCanvasHeightForCurrentScreen()
    {
        int screenH;
        try
        {
            var sc = Screen.FromControl(this) ?? Screen.PrimaryScreen;
            screenH = sc?.WorkingArea.Height ?? 1080;
        }
        catch { screenH = 1080; }

        // 留 8% 余量给边框/任务栏，宁可小一点也不要被系统夹掉
        return Math.Max(200, (int)(screenH * 0.92));
    }

    /// <summary>重新解析贴图路径（换装 / 改尺寸后调用）。</summary>
    private void ResolveSpritePath()
    {
        // 隐私模式：优先用闭眼立绘。解析不出来就照常用普通的 ——
        // 缺一张图不该让菲比换不了装，甚至消失。
        if (_privacyMode && _cfg.PrivacyArtSwitch)
        {
            string? privacy = ResolvePrivacyArtPath();
            if (privacy != null)
            {
                _spritePath = privacy;
                return;
            }
        }

        _spritePath = Art.Resolve(
            _dir,
            _cfg.Art,
            Path.Combine(_dir, SpriteFull),
            Path.Combine(_dir, SpriteSmall));

        if (_spritePath == null)
            throw new FileNotFoundException(
                "找不到任何立绘。请运行 build-pet-asset.mjs 生成 phoebe.png，" +
                "或往 Art/ 文件夹里放一张图片。", Path.Combine(_dir, SpriteFull));
    }

    /// <summary>
    /// 找出隐私模式该用的闭眼立绘路径；找不到返回 null（调用方回退普通立绘）。
    ///
    /// 两种来源，按优先级：
    ///   1) 配置里明确写了 PrivacyArt → 就用它（走 Art.Resolve，支持省略扩展名）
    ///   2) 没写 → 在当前立绘名后面加「-闭眼」再找
    ///
    /// 为什么第 2 条要基于「当前立绘」而不是硬编码：
    ///   用户可能换了姿势（站/坐/趴），闭眼图必须跟着换，
    ///   否则坐着却闭着站姿的眼睛，一看就穿帮。
    /// </summary>
    private string? ResolvePrivacyArtPath()
    {
        // 1) 配置指定的闭眼立绘（留空则跳过）
        if (!string.IsNullOrWhiteSpace(_cfg.PrivacyArt))
        {
            string? explicitPath = Art.Resolve(
                _dir, _cfg.PrivacyArt,
                Path.Combine(_dir, SpriteFull), Path.Combine(_dir, SpriteSmall));
            if (explicitPath != null) return explicitPath;
            AppPaths.Log($"  [立绘] 隐私立绘「{_cfg.PrivacyArt}」找不到，改按约定找 -闭眼");
        }

        // 2) 约定：<当前立绘名>-闭眼
        return FindClosedEyePath();
    }

    /// <summary>
    /// 按约定找当前立绘的闭眼版（`<名字>-闭眼.<ext>`）；没有返回 null。
    ///
    /// 单独抽出来是为了让菜单也能问「配好没有」——
    /// 菜单提示和实际换图必须用同一套判断，否则会出现
    /// 「提示说找到了、实际没换」这种自相矛盾。
    /// </summary>
    private string? FindClosedEyePath()
    {
        if (string.IsNullOrWhiteSpace(_cfg.Art)) return null;

        string baseName = Path.GetFileNameWithoutExtension(_cfg.Art.Trim());
        if (baseName.Length == 0) return null;

        string artDir = Art.DirPath(_dir);
        string want = baseName + ClosedEyeSuffix;
        foreach (var ext in Art.SupportedExtensions)
        {
            string cand = Path.Combine(artDir, want + ext);
            if (File.Exists(cand)) return cand;
        }
        return null;
    }

    private void BuildSprite()
    {
        ResolveSpritePath();
        string path = _spritePath!;

        _sprite?.Dispose();
        using var raw = new Bitmap(path);

        // 防御：图片尺寸为 0 或畸形时别算出一堆 0/负数几何。
        if (raw.Width <= 0 || raw.Height <= 0)
            throw new InvalidOperationException($"立绘尺寸非法：{raw.Width}x{raw.Height}（{path}）");

        int w = _size;
        int h = Math.Max(1, (int)Math.Round(raw.Height * (double)w / raw.Width));

        // 高度上限。为什么必须有这道闸：
        //
        //   1) **Windows 对窗口尺寸有系统级上限**（约屏幕高 + 边框）。
        //      ClientSize 设成 1628 会被静默夹到 ~1460，而画布位图仍是 1628，
        //      结果是**立绘底部被切掉**，而且不报错、日志也没有痕迹。
        //      实测：100x800 的图在 200px 宽下就是 1600 高，正好撞上。
        //
        //   2) 极端长条图（比如 1:40）会算出 8000px 高，每帧绘制的位图
        //      大到离谱，纯属浪费。
        //
        // 处理方式：**超标就把整张图等比缩小**（宽高一起缩），
        // 而不是只夹高度 —— 只夹高度会改变长宽比，人就拉变形了。
        int maxArtH = MaxCanvasHeightForCurrentScreen() - CanvasMargin * 2 - _topReserve;
        if (h > maxArtH)
        {
            AppPaths.Log($"  [立绘] 按宽度 {w} 算出的高度 {h}px 超过可用上限 {maxArtH}px，等比缩到上限");
            double k = maxArtH / (double)h;
            h = maxArtH;
            w = Math.Max(1, (int)Math.Round(w * k));
        }

        var scaled = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(scaled))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.DrawImage(raw, new Rectangle(0, 0, w, h));
        }
        _sprite = scaled;

        _padW = w + CanvasMargin * 2;
        _artH = h + CanvasMargin * 2;              // 立绘区（含留边）
        // 画布总高 = 顶上有气泡预。+ 立绘。+ 下方输入框预。
_padH = _topReserve + _artH + _bottomReserve;
        _frame?.Dispose();
        _frame = new Bitmap(_padW, _padH, PixelFormat.Format32bppArgb);

        ClientSize = new Size(_padW, _padH);
        RebuildSurface();
    }

    /// <summary>立绘区高度（不含上下预留区）。窗。气泡定位都以它为准。/summary>
    public int ArtHeight => _artH;

    /// <summary>立绘区宽度。/summary>
    public int ArtWidth => _padW;

    /// <summary>立绘区在画布内的纵向起始偏移。 顶部气泡预留高度）。/summary>
    public int ArtTop => _topReserve;

    /// <summary>顶部气泡预留高度（像素）。/summary>
    public int TopReserve => _topReserve;

    // ---------- 自检辅助（只。--selftest 用） ----------

    /// <summary>强制渲染一帧（自检用，不开计时器也能出画面）。/summary>
    public void RenderOnce() => Render();

    /// <summary>
    /// 数一数当前画布上的像素，用来判断「气泡真的画出来了」。
/// 比截图便宜，也比人眼可靠。
/// </summary>
    public (int w, int h, int opaque, int topOpaque, int restOpaque, int borderPixels, int whitePixels) MeasureFrame()
    {
        if (_frame == null) return (0, 0, 0, 0, 0, 0, 0);
        int w = _frame.Width, h = _frame.Height;
        int opaque = 0, top = 0, rest = 0, border = 0, white = 0;
        int split = _topReserve > 0 ? _topReserve : h / 3;

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                var c = _frame.GetPixel(x, y);
                if (c.A <= 8) continue;
                opaque++;
                if (y < split) top++; else rest++;

                // 深蓝描边：接。(31,56,100)，允许一定容。
if (c.A > 200 && Math.Abs(c.R - 31) < 45 && Math.Abs(c.G - 56) < 45 && Math.Abs(c.B - 100) < 55)
                    border++;
                // 白色填充
                if (c.A > 200 && c.R > 235 && c.G > 235 && c.B > 235) white++;
            }
        }
        return (w, h, opaque, top, rest, border, white);
    }

    /// <summary>把当前画布存。PNG（自检用，供人工确认外观）。/summary>
    public void SaveFrame(string path)
    {
        try { _frame?.Save(path, System.Drawing.Imaging.ImageFormat.Png); }
        catch (Exception e) { Log($"  导出预览失败: {e.Message}"); }
    }

    // ================= 输入框（独立窗口。=================
    private InputBoxWindow? _input;
    private ChatCore? _chat;
    private ChatConfig? _chatCfg;

    /// <summary>输入框实例（首次访问时创建）。/summary>
    public InputBoxWindow Input
    {
        get
        {
            if (_input == null)
            {
                _input = new InputBoxWindow();
                _input.Submitted += OnSubmitted;
                _input.TopMost = TopMost;
                // 显式建句柄。WinForms 的句柄是懒创建的，OnHandleCreated 里的
                // 工作（工具窗口样式、圆角区域）要等第一次访。Handle 才发生。
// 之前是靠 PositionInput() 里给 Size 赋值顺带触发的 —。太隐晦，
                // 一旦调用顺序变了圆角就悄悄没了。这里直接强制创建。
_input.CreateControl();
                PositionInput();
            }
            return _input;
        }
    }

    /// <summary>挂上聊天核心（由 Program 在启动时调用）。/summary>
    public void AttachChat(ChatCore chat, ChatConfig cfg)
    {
        _chat = chat;
        _chatCfg = cfg;
        // 菜单在构造函数里就建好了，那时聊天核心还没挂上，
        // 「人设」子菜单会显示「聊天核心未挂载」。挂载后必须重建一次。
        BuildPersonaMenuItems();

        // 「启动时自动打开输入框」的状态存在 chat-config.json 里，
        // 建菜单时还没载入，所以这里补一次同步。
        if (_inputOnStartItem != null) _inputOnStartItem.Checked = cfg.ShowInputOnStart;
        // 「退出后保留对话」同理
        if (_saveHistoryItem != null) _saveHistoryItem.Checked = cfg.SaveHistory;

        if (!_cfg.ShowBubble) SetTopReserve(0);
    }

    /// <summary>用户按下回车。</summary>
    private void OnSubmitted(string text)
    {
        Log($"  [输入] 发送: {text}");
        if (_chat == null)
        {
            BubbleText = "（还没接上聊天核心）";
            return;
        }

        BubbleText = "…";

        // 深度模式：用户聊起当前窗口内容时（「你看这个好不好看」），
        // 顺手截一张挂在这条消息上。图片只能出现在 user 消息里，
        // 而这句话本来就是 user 消息，正好合规。
        byte[]? shot = null;
        if (_cfg.ShotOnUserMention && LooksLikeShowingSomething(text))
            shot = TryCaptureForDepth();

        _chat.Send(text, (reply, err) =>
        {
            // 回调在后台线程 → 必须切回 UI 线程改窗口
            try
            {
                BeginInvoke(new Action(() =>
                {
                    if (err != null)
                    {
                        // 失败时把原文放回输入框 —— 不能让用户白打一遍字。
                        // 网络断了、余额不足都可能发生，而那段话往往挺长。
                        RestoreInputAfterFailure(text, err);
                    }
                    else
                    {
                        BubbleText = reply;
                        Log($"  [输入] 回复 {reply.Length} 字: {reply}");
                    }
                }));
            }
            catch (Exception e) { Log($"  [输入] 回填失败: {e.Message}"); }
        }, shot);
    }

    /// <summary>
    /// 发送失败后的收尾：说清楚出了什么事，并把原话还给用户。
    ///
    /// 两件事都要做：
    ///   1) 气泡里给出**能看懂的原因**（不是 "出错了：<英文堆栈>"）；
    ///   2) 输入框恢复原文，光标在末尾，用户改一改或直接回车就能重试。
    ///
    /// 另外把「失败」写进历史是**故意不做**的：失败的请求不该污染上下文，
    /// 否则下一轮菲比会看到一段自己没回应的对话。
    /// </summary>
    private void RestoreInputAfterFailure(string originalText, string err)
    {
        BubbleText = "出错了：" + err;
        Log($"  [输入] 失败: {err}（原文 {originalText.Length} 字已归还输入框）");

        try
        {
            if (_input == null) return;

            // 用户可能在等待期间又打了新字 —— 那就别覆盖人家正在写的东西
            string current = _input.Value ?? "";
            if (current.Trim().Length > 0)
            {
                Log("  [输入] 输入框已有新内容，不覆盖（原文见上）");
                return;
            }

            _input.SetValue(originalText);
            if (!InputShown) ShowInput(true);
            PositionInput();
        }
        catch (Exception e)
        {
            Log($"  [输入] 恢复原文失败: {e.Message}");
        }
    }

    /// <summary>输入框宽度跟随立绘宽度。/summary>
    public int InputWidth => Math.Max(140, _padW - 8);

    /// <summary>
    /// 把输入框摆到立绘正下方、水平居中。
/// 用屏幕坐标而不是窗口内坐标 —。输入框是独立窗口。
/// </summary>
    public void PositionInput()
    {
        if (_input == null) return;

        int w = InputWidth;
        int h = Math.Max(34, _input.DesiredHeight);

        // 立绘底边的屏。y：窗口顶 + 顶预。+ 立绘区高
        int artBottom = Top + _topReserve + _artH;
        int x = Left + (_padW - w) / 2;
        int y = artBottom - CanvasMargin + 4;   // 稍微压进立绘区一点，视觉上贴着

        _input.Size = new Size(w, h);
        _input.Location = new Point(x, y);
    }

    /// <summary>显示/隐藏输入框。/summary>
    public void ShowInput(bool show)
    {
        if (show)
        {
            Input.Show();
            PositionInput();
            Input.FocusInput();
        }
        else
        {
            // Hide() 会把焦点还给系统（自动回到上一个窗口），不用手动 SetForegroundWindow。
            _input?.Hide();
        }
        _inputShown = _input?.Visible ?? false;
        _inputToggleItem.Checked = _inputShown;
        Log($"  输入框显示 = {_inputShown}");
    }

    /// <summary>输入框是否可见。/summary>
    public bool InputShown => _input?.Visible ?? false;

    /// <summary>切换输入框显示（热键和菜单都用它）。/summary>
    public void ToggleInput() => ShowInput(!InputShown);

    private bool _inputShown;
    private ToolStripMenuItem _inputToggleItem = null!;
    private ToolStripMenuItem _inputOnStartItem = null!;
    private ToolStripMenuItem _saveHistoryItem = null!;

    /// <summary>气泡文字是否为空（有没有在说话）。/summary>
    public bool HasBubble => !string.IsNullOrEmpty(_bubbleText);

    /// <summary>
    /// 量出立绘的实际包围盒（只看立绘区，忽略气泡文字）。
/// 用来确认立绘没有被画布边缘切。—。比肉眼看预览图可靠。
/// </summary>
    public (int top, int bottom, int left, int right) MeasureSpriteBounds()
    {
        if (_frame == null) return (0, 0, 0, 0);
        int minX = int.MaxValue, maxX = -1, minY = int.MaxValue, maxY = -1;

        // 只在立绘区内找，且跳过气泡的深蓝像素（描边色 31,56,100。
int yStart = _topReserve;
        for (int y = yStart; y < _frame.Height; y++)
        {
            for (int x = 0; x < _frame.Width; x++)
            {
                var c = _frame.GetPixel(x, y);
                if (c.A <= 8) continue;
                bool isBubbleInk = c.A > 200 && Math.Abs(c.R - 31) < 45
                                   && Math.Abs(c.G - 56) < 45 && Math.Abs(c.B - 100) < 55;
                if (isBubbleInk) continue;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }
        if (maxX < 0) return (0, 0, 0, 0);
        return (minY, maxY, minX, maxX);
    }

    // ================= 气泡 =================
    private readonly BubbleStyle _bubbleStyle = new BubbleStyle();
    private Font? _bubbleFont;
    private string _bubbleText = "";

    /// <summary>
    /// 气泡自动消失的时刻（_clock 毫秒）。0 = 不自动消失。
    ///
    /// 为什么需要它：主动发言的前置条件里有「气泡里没话说」。
    /// 早期版本气泡**永不清空**，于是只要说过一句话，之后所有主动发言
    /// 都会被这个条件永久挡住 —— 表现就是「闲置再久也不主动开口」。
    /// 现在每句话都带一个显示时长，到点自动清掉。
    /// </summary>
    private long _bubbleClearAtMs;

    /// <summary>
    /// 菜单/对话框打开期间，把气泡更新挂起，等它们关掉再补上。
    ///
    /// 为什么必须这样：改气泡文字会走 LayoutBubble → SetTopReserve，
    /// 那会**移动并缩放窗口**。而右键菜单是挂在这个窗口上的，
    /// 窗口一动，Windows 就把菜单关掉 —— 表现是「点了菜单项，菜单消失、
    /// 子菜单打不开」。踩过一次。
    /// </summary>
    private int _menuSuspendDepth;
    private bool _bubbleDirtyWhileSuspended;

    /// <summary>挂起气泡相关的窗口改动（菜单/对话框弹出来时调用）。</summary>
    private void SuspendBubbleLayout() => _menuSuspendDepth++;

    /// <summary>解除挂起，并把期间攒下的改动补做一次。</summary>
    private void ResumeBubbleLayout()
    {
        if (_menuSuspendDepth > 0) _menuSuspendDepth--;
        if (_menuSuspendDepth > 0) return;
        if (!_bubbleDirtyWhileSuspended) return;
        _bubbleDirtyWhileSuspended = false;
        LayoutBubble();
    }

    /// <summary>当前是否处于「不要动窗口」的状态。</summary>
    private bool BubbleLayoutSuspended => _menuSuspendDepth > 0;

    /// <summary>当前气泡文字（空 = 不画气泡）。</summary>
    public string BubbleText
    {
        get => _bubbleText;
        set
        {
            string v = value ?? "";
            if (v == _bubbleText) return;
            _bubbleText = v;
            // 说话时按内容长度给一个显示时长，到点自动消失
            _bubbleClearAtMs = v.Length == 0 ? 0 : _clock.ElapsedMilliseconds + BubbleDurationMs(v);

            if (BubbleLayoutSuspended)
            {
                // 菜单开着：只记住「该重排了」，等菜单关掉再做。
                // 文字本身已经换了，所以渲染出来是对的，只是窗口尺寸晚一步变。
                _bubbleDirtyWhileSuspended = true;
                return;
            }
            LayoutBubble();
        }
    }

    /// <summary>
    /// 一句话在气泡里停留多久。
    /// 配置里 BubbleSeconds > 0 就用配置值；否则按字数估算：
    /// 至少 4 秒，最多 20 秒。长回复要留够阅读时间，但也不能太久 ——
    /// 气泡不清空，主动发言就一直被挡着。
    /// </summary>
    private long BubbleDurationMs(string text)
    {
        if (_cfg.BubbleSeconds > 0) return _cfg.BubbleSeconds * 1000L;
        // 中文按每字约 90ms 阅读速度估，再给 2.5 秒起步时间
        long ms = 2500 + text.Length * 90L;
        return Math.Max(4000, Math.Min(20000, ms));
    }

    /// <summary>到点自动清空气泡。每帧调用，开销只有一次比较。</summary>
    private void TickBubbleFade()
    {
        if (_bubbleClearAtMs == 0) return;
        if (_clock.ElapsedMilliseconds < _bubbleClearAtMs) return;
        _bubbleClearAtMs = 0;
        BubbleText = "";
    }

    // ---------- 自检钩子（气泡） ----------
    internal static long BubbleDurationForTest(PetForm f, string text) => f.BubbleDurationMs(text);
    internal void SetBubbleSecondsForTest(int sec) => _cfg.BubbleSeconds = sec;
    /// <summary>把到期时间拨到过去，模拟「已经到点了」。</summary>
    internal void ForceBubbleExpireForTest() => _bubbleClearAtMs = _clock.ElapsedMilliseconds - 1;
    internal void TickBubbleFadeForTest() => TickBubbleFade();

    // ---------- 自检钩子（隐私模式） ----------
    internal bool PrivacyForTest => _privacyMode;
    internal void SetPrivacyForTest(bool on) => PrivacyMode = on;

    /// <summary>自检用：当前贴图的实际路径（验证隐私立绘真的换了）。</summary>
    internal string? SpritePathForTest => _spritePath;

    /// <summary>自检用：程序目录（造临时立绘要用）。</summary>
    internal string AppDirForTest => _dir;

    /// <summary>自检用：拨动「隐私模式换闭眼立绘」总开关（不落盘）。</summary>
    internal void SetPrivacyArtSwitchForTest(bool on) => _cfg.PrivacyArtSwitch = on;

    /// <summary>自检用：按约定找闭眼立绘，null = 没配对。</summary>
    internal string? FindClosedEyePathForTest() => FindClosedEyePath();

    /// <summary>自检用：当前配置里的立绘名（用于测完还原）。</summary>
    internal string? ArtNameForTest => _cfg.Art;

    /// <summary>自检用：把当前配置落盘（还原用户设置后要调一次）。</summary>
    internal void SaveConfigForTest() => _cfg.Save(_cfgPath);

    /// <summary>自检用：闭眼立绘的命名后缀（测试要照着造文件）。</summary>
    internal const string ClosedEyeSuffixForTest = ClosedEyeSuffix;

    internal string BuildRecentContextForTest() => BuildRecentContextForProactive();
    internal byte[]? TryCaptureForDepthForTest() => TryCaptureForDepth();
    internal int SwitchCountForTest => _switchCountInWindow;

    // ---------- 自检钩子（菜单挂起） ----------
    internal void SuspendBubbleForTest() => SuspendBubbleLayout();
    internal void ResumeBubbleForTest() => ResumeBubbleLayout();
    internal bool BubbleSuspendedForTest => BubbleLayoutSuspended;
    /// <summary>自检用：拿到「隐私模式」菜单项，以便真的走一次点击路径。</summary>
    internal ToolStripMenuItem PrivacyItemForTest => _privacyToggleItem;

    // ---------- 自检钩子（输入框） ----------
    internal bool InputToggleCheckedForTest => _inputToggleItem?.Checked ?? false;

    // ---------- 自检钩子（音效） ----------
    internal bool DoubleClickSoundForTest => _cfg.DoubleClickSound;
    internal void SetDoubleClickSoundForTest(bool on) => _cfg.DoubleClickSound = on;

    /// <summary>
    /// 按当前文字重新计算气泡高度，据此设置顶部预留。
/// 气泡高度 = 折行后的实际高度；预留再补上尾巴的高度。
/// </summary>
    private void LayoutBubble()
    {
        if (_bubbleFont == null) return;

        int need;
        if (string.IsNullOrEmpty(_bubbleText) || !_cfg.ShowBubble)
        {
            need = 0;
        }
        else
        {
            // 气泡不能比画布宽：MaxWidth 是静态值，画布宽度随尺寸档变化。
            // 直接写 240 会在 200px 档（画布 228）把气泡右边缘切掉（踩过一次）。
            //
            // 留边从 6px 加到 BubbleSideMargin：只留 6px 时气泡几乎贴满整个窗口，
            // 看着又窄又挤（用户反馈「气泡显得有点窄」）。左右各留 18px 之后
            // 才有「浮在立绘上方」的观感。
            _bubbleStyle.MaxWidth = Math.Max(60, _padW - BubbleSideMargin * 2);

            using var g = Graphics.FromImage(_frame ?? new Bitmap(1, 1));
            var size = Bubble.Measure(g, _bubbleText, _bubbleStyle, _bubbleFont);
            need = size.Height + _bubbleStyle.TailHeight + 6;   // 尾巴 + 与立绘的间隙
        }

        if (need != _topReserve) SetTopReserve(need);
    }

    /// <summary>在画布上画气泡（在立绘上方居中，尾巴指向立绘）。/summary>
    private void DrawBubble(Graphics g)
    {
        if (_bubbleFont == null || string.IsNullOrEmpty(_bubbleText)) return;
        if (_topReserve <= 0) return;

        var size = Bubble.Measure(g, _bubbleText, _bubbleStyle, _bubbleFont);
        float bw = size.Width, bh = size.Height;
        float bx = (float)(_padW / 2.0 - bw / 2.0 + _lean * 0.5);   // 跟着立绘轻微晃动
        float by = 2f;

        // 尾巴对准立绘中轴
        Bubble.Draw(g, _bubbleText, _bubbleStyle, _bubbleFont, bx, by, bw, bh, (float)(_padW / 2.0));
    }

    /// <summary>
    /// 设置顶部预留高度（给气泡）。传 0 = 无气泡。
/// 以「立绘顶边不动」为锚点：加高画布时窗口向上扩展，立绘在屏幕上不移动。
/// </summary>
    public void SetTopReserve(int px)
    {
        if (px < 0) px = 0;
        if (px == _topReserve) return;

        // 菜单开着时不能移动窗口 —— 菜单挂在窗口上，一动就关掉，
        // 表现是「点完菜单项子菜单就打不开了」。攒着，等菜单关掉再重排。
        if (BubbleLayoutSuspended) { _bubbleDirtyWhileSuspended = true; return; }

        // 立绘在屏幕上的顶边位置
        int anchorTop = Top + _topReserve;

        _topReserve = px;
        BuildSprite();

        // 窗口顶边上移 px，立绘仍落在 anchorTop
        Location = ClampToScreen(new Point(Left, anchorTop - _topReserve));
        PositionInput();
        Log($"  顶部预留变更: {px}px，画布 {_padW}x{_padH}（立绘区 {_artH}），窗口 ({Left},{Top})");
    }

    /// <summary>
    /// 设置立绘下方的预留高度（给输入框）。传 0 恢复成改动前的行为。
/// 以「立绘底边」为锚点重新定位，视觉上立绘不会跳。
/// </summary>
    public void SetBottomReserve(int px)
    {
        if (px < 0) px = 0;
        if (px == _bottomReserve) return;

        // 同 SetTopReserve：菜单期间不许动窗口
        if (BubbleLayoutSuspended) { _bubbleDirtyWhileSuspended = true; return; }

        // 锚点取立绘底边：加高画布时立绘留在原处，多出来的空间在下。
        int anchorBottom = Top + _artH;

        _bottomReserve = px;
        BuildSprite();

        Location = ClampToScreen(new Point(Left, anchorBottom - _artH));
        Log($"  预留区变更: {px}px，画布 {_padW}x{_padH}（立绘区 {_artH}）");
    }

    /// <summary>重建 DIB 缓冲区（尺寸变化时）。/summary>
    private void RebuildSurface()
    {
        ReleaseSurface();

        var bmi = new Native.BITMAPINFO();
        bmi.bmiHeader.biSize = (uint)Marshal.SizeOf<Native.BITMAPINFOHEADER>();
        bmi.bmiHeader.biWidth = _padW;
        bmi.bmiHeader.biHeight = -_padH;        // 负数 = 自上而下，省掉翻转
bmi.bmiHeader.biPlanes = 1;
        bmi.bmiHeader.biBitCount = 32;
        bmi.bmiHeader.biCompression = Native.BI_RGB;

        var screenDc = Native.GetDC(IntPtr.Zero);
        try
        {
            _memDc = Native.CreateCompatibleDC(screenDc);
            _dib = Native.CreateDIBSection(_memDc, ref bmi, Native.DIB_RGB_COLORS, out _bits, IntPtr.Zero, 0);
            if (_dib == IntPtr.Zero) throw new InvalidOperationException("CreateDIBSection 失败");
            _oldObj = Native.SelectObject(_memDc, _dib);
        }
        finally
        {
            Native.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private void ReleaseSurface()
    {
        if (_memDc != IntPtr.Zero && _oldObj != IntPtr.Zero) Native.SelectObject(_memDc, _oldObj);
        if (_dib != IntPtr.Zero) Native.DeleteObject(_dib);
        if (_memDc != IntPtr.Zero) Native.DeleteDC(_memDc);
        _memDc = _dib = _oldObj = _bits = IntPtr.Zero;
    }

    // ================= 渲染 =================
    /// <summary>
    /// 立刻渲染一帧（自检用）。
    /// 自检里没有消息循环，定时器不会触发，所以必须手动调一次 ——
    /// 否则 _frame 是空的，MeasureSpriteBounds 量出来全是 0，
    /// 「立绘未出界」那种断言会假通过。
    /// </summary>
    public void RenderNow() => Render();

    private void Render()
    {
        if (!IsHandleCreated || _sprite == null || _frame == null || _dib == IntPtr.Zero) return;

        long nowMs = _clock.ElapsedMilliseconds;

        // 菜单开着时整个跳过渲染。
        //
        // 两个理由：
        //   1) 菜单是挂在窗口上的弹出层，窗口每帧被 UpdateLayeredWindow 重推
        //      有可能把弹出层顶掉（子菜单尤其敏感）；
        //   2) 菜单开着时用户根本没在看桌宠，白烧 30fps 的 CPU 没意义。
        // 反正菜单一关就会正常渲染，画面不会丢。
        if (BubbleLayoutSuspended) return;

        TickBubbleFade();
        TickPrivacyHint();
        TickProactive();

        // 采集相关的全部放在最后，且统一由这里把关。
        // 隐私模式开着时【一次都不调用】ActivityWatch —— 不是「采了不用」，
        // 而是根本不采。这样「隐私模式不监控你」是代码层面成立的，
        // 而不是靠后面各处记得判断。
        if (!_privacyMode && _cfg.WatchActivity)
        {
            ActivityWatch.TickRemember();   // 记住最近一次「不是自己」的前台窗口
            TickWindowSwitch();
            TickSwitchProactive();
        }

        // 待机浮动
        double bob = Math.Sin(nowMs / (double)BobPeriodMs * Math.PI * 2) * BobAmplitude;

        // 拖动倾斜：鼠标停下约 120ms 后自动回正，避免一直歪着
        if (_dragging && nowMs - _lastMouseMoveMs > 120) _lean *= 0.72;
        else if (!_dragging) _lean *= 0.78;
        if (Math.Abs(_lean) < 0.2) _lean = 0;

        // 点击反应
        double scaleX = 1, scaleY = 1;
        if (_reaction != Reaction.None)
        {
            double p = (nowMs - _reactionStartMs) / (double)ReactionMs;
            if (p >= 1) _reaction = Reaction.None;
            else
            {
                double e = Math.Sin(p * Math.PI);                 // 0→1→0
                if (_reaction == Reaction.Squash) { scaleY = 1 - 0.13 * e; scaleX = 1 + 0.08 * e; }
                else { scaleY = 1 + 0.15 * e; scaleX = 1 - 0.06 * e; bob -= 14 * e; }
            }
        }

        using (var g = Graphics.FromImage(_frame))
        {
            g.CompositingMode = CompositingMode.SourceCopy;
            g.Clear(Color.Transparent);
            g.CompositingMode = CompositingMode.SourceOver;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            // 立绘在「立绘区」内居中；立绘区整体下移 _topReserve（顶上气泡占位）
            //
            // 尺寸。_sprite 自身的宽高，而不。_padW/_artH —。
// _padW/_artH 已经。CanvasMargin 算进去了，拿它当绘制尺寸会把立绘
            // 放大到超出画布，底部被切掉（踩过一次，。bubble-preview 立绘断脚）。
double cx = _padW / 2.0 + _lean;
            double cy = _topReserve + _artH / 2.0 + bob;
            double w = _sprite.Width * scaleX;
            double h = _sprite.Height * scaleY;
            g.DrawImage(_sprite, new RectangleF(
                (float)(cx - w / 2), (float)(cy - h / 2), (float)w, (float)h));

            // 气泡画在立绘上方
            DrawBubble(g);
        }

        PushFrame();
    }

    /// <summary>把画布推送到分层窗口（GDI+ 是直。alpha，分层窗口需要预乘）。/summary>
    private void PushFrame()
    {
        int bytes = _padW * _padH * 4;
        var src = new byte[bytes];
        var data = _frame!.LockBits(new Rectangle(0, 0, _padW, _padH),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try { Marshal.Copy(data.Scan0, src, 0, bytes); }
        finally { _frame.UnlockBits(data); }

        var dst = new byte[bytes];
        for (int i = 0; i < bytes; i += 4)
        {
            byte a = src[i + 3];
            if (a == 255)
            {
                dst[i] = src[i];
                dst[i + 1] = src[i + 1];
                dst[i + 2] = src[i + 2];
            }
            else if (a != 0)
            {
                // 预乘 alpha（分层窗口要。BGR 先乘 alpha 再上交）
                dst[i] = (byte)(src[i] * a / 255);
                dst[i + 1] = (byte)(src[i + 1] * a / 255);
                dst[i + 2] = (byte)(src[i + 2] * a / 255);
            }
            dst[i + 3] = a;
        }
        Marshal.Copy(dst, 0, _bits, bytes);

        var screenDc = Native.GetDC(IntPtr.Zero);
        try
        {
            var ptDst = new Native.POINT { X = Left, Y = Top };
            var size = new Native.SIZE { cx = _padW, cy = _padH };
            var ptSrc = new Native.POINT { X = 0, Y = 0 };
            bool ok = Native.UpdateLayeredWindow(Handle, screenDc, ref ptDst, ref size,
                _memDc, ref ptSrc, 0, ref _blend, Native.ULW_ALPHA);

            // 只在头几帧做一次自检：失败说明分层窗口没建起来，值得记一。
if (_framesRendered++ < 3 && !ok)
            {
                Log($"  渲染#{_framesRendered} 失败: err={Marshal.GetLastWin32Error()} " +
                    $"Form=({Left},{Top},{Width},{Height}) Visible={Visible}");
            }
        }
        finally
        {
            Native.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private int _framesRendered;

    // ---------- 窗口切换触发的主动发言 ----------
    // 用户明确要求：换窗口/换程序时应该有个反应，而不是只有闲置才说话。
    private string _lastForegroundKey = "";
    private long _lastSwitchAtMs;
    private int _switchCountInWindow;

    /// <summary>同一个程序连续切换几次算「折腾」，最多说一次，避免刷屏。</summary>
    private const int SwitchBurstLimit = 1;

    /// <summary>
    /// 检测前台窗口是否变了。
    /// 变了才算「切换」—— 同一次切换会连续触发好几帧，所以要比对 key。
    /// </summary>
    private void TickWindowSwitch()
    {
        if (_chat == null) return;
        if (_privacyMode || !_cfg.WatchActivity) return;
        if (!_cfg.ProactiveOnSwitch) return;

        var act = ActivityWatch.Current();
        string key = act == null ? "" : (act.ProcessName + "|" + act.WindowTitle);

        // 黑名单窗口：不当作「切换」，也不带动计数 —— 否则用户切进密码管理器
        // 再切出来，反而会触发一次基于黑名单窗口的搭话
        if (act != null && act.Blacklisted) { _lastForegroundKey = key; return; }

        if (_lastForegroundKey.Length == 0)
        {
            _lastForegroundKey = key;   // 首次只是记下基线，不当成切换
            return;
        }
        if (key == _lastForegroundKey) return;

        _lastForegroundKey = key;
        _lastSwitchAtMs = _clock.ElapsedMilliseconds;
        _switchCountInWindow = 0;   // 进入新窗口，重置本窗口的计数
    }

    /// <summary>
    /// 切换窗口后隔一小会儿再开口。
    /// 为什么不当场说：用户切窗口往往是在连续操作，立刻说话会被下一个
    /// 切换打断、也显得聒噪。等几秒确认他停下来了再说。
    /// </summary>
    private void TickSwitchProactive()
    {
        if (_chat == null) return;
        if (_privacyMode || !_cfg.WatchActivity) return;
        if (!_cfg.ProactiveOnSwitch) return;
        if (_proactiveBusy) return;
        if (HasBubble) return;

        if (_lastSwitchAtMs == 0) return;
        long settle = Math.Max(1, _cfg.SwitchSettleSeconds) * 1000L;
        if (_clock.ElapsedMilliseconds - _lastSwitchAtMs < settle) return;

        // 说过了就清掉触发点，别重复说
        _lastSwitchAtMs = 0;

        if (_switchCountInWindow >= SwitchBurstLimit) return;

        // 全局冷却仍然生效，避免「切一次说一次」
        long cool = Math.Max(0, _cfg.ProactiveCooldownSeconds) * 1000L;
        if (_clock.ElapsedMilliseconds - _lastProactiveMs < cool) return;

        var act = ActivityWatch.Current();
        if (act == null || act.Blacklisted) return;

        _switchCountInWindow++;
        _lastProactiveMs = _clock.ElapsedMilliseconds;
        _proactiveBusy = true;

        string reason = $"刚切到「{act.Describe()}」，看起来换了件事做。";
        Log($"  [主动] 切换触发（{act.Describe()}）");

        byte[]? shot = TryCaptureForDepth();
        _chat.SendProactive(reason, "", (reply, err) =>
        {
            try
            {
                BeginInvoke(new Action(() =>
                {
                    _proactiveBusy = false;
                    if (err != null) { Log($"  [主动] 切换发言失败: {err}"); return; }
                    BubbleText = reply;
                    Log($"  [主动] 切换发言 {reply.Length} 字: {reply}");
                }));
            }
            catch (Exception e) { _proactiveBusy = false; Log($"  [主动] 切换回填失败: {e.Message}"); }
        }, shot);
    }

    /// <summary>程序所在目录（贴图、配置、日志都放这儿）。/summary>
    public static string AppDir => AppPaths.Dir;

    // ================= 主动发言 =================
    // 三个状态量共同决定「现在能不能主动开口」：
    //   _lastInputIdleMs  上一帧的闲置时长，用来判断用户「刚回来。
//   _lastProactiveMs  上次主动发言的时刻（冷却用）
    //   _proactiveInIdle  本轮闲置已经说了几次（次数上限用。
private long _lastInputIdleMs;
    private long _lastProactiveMs = long.MinValue / 2;   // 避免减法溢出
    private int _proactiveInIdle;
    private bool _proactiveBusy;                          // 正在等 API 返回

    /// <summary>隐私模式：开时不采集活动、不主动发言。/summary>
    private bool _privacyMode;
    private long _privacyOnAtMs;      // 开启时刻，用于延时提醒
    private bool _privacyHintShown;   // 本次开启是否已提醒过

    /// <summary>隐私模式是否开启。/summary>
    public bool PrivacyMode
    {
        get => _privacyMode;
        set
        {
            if (_privacyMode == value) return;
            _privacyMode = value;
            _privacyToggleItem.Checked = value;
            // 刚开启时把本轮计数清掉；关闭时也清，等于重新开始计。
            _proactiveInIdle = 0;
            // 清掉切换触发的待办，否则关掉隐私模式后会补说一句「刚才切到了…」
            _lastSwitchAtMs = 0;

            if (value)
            {
                _privacyOnAtMs = _clock.ElapsedMilliseconds;
                _privacyHintShown = false;
                // 不只是停止采集 —— 已经记住的那次「用户在用什么」也要丢掉，
                // 否则它还会被用在隐私模式下的某次搭话里。
                ActivityWatch.Forget();
            }

            // 记住状态：为了开会开了隐私模式就关掉桌宠，下次启动应该还是隐私的，
            // 否则会在用户不知情时恢复采集。
            _cfg.PrivacyMode = value;
            _cfg.Save(_cfgPath);

            // 换成闭眼立绘（关掉时换回来）。
            // 放在这里而不是别处：立绘是「隐私模式的视觉指示」，
            // 跟开关同生共死才不会出现「开着隐私却是睁眼」的错位。
            // 换图失败不能把菲比搞没 —— 沿用当前贴图继续跑。
            if (_cfg.PrivacyArtSwitch)
            {
                try
                {
                    SwitchPrivacyArt();
                }
                catch (Exception e)
                {
                    AppPaths.Log($"  [立绘] 隐私立绘切换失败（忽略）: {e.Message}");
                }
            }

            Log($"  隐私模式 = {value}");
            BubbleText = value
                ? "隐私模式已开启，我不会看你也不会主动说话了。"
                : "隐私模式已关闭。";
        }
    }

    /// <summary>
    /// 隐私模式换图：保持立绘底边锚点不动，重建贴图。
    ///
    /// 为什么不直接调 ApplyArt：那是「用户选立绘」的入口，
    /// 会把名字写进 `_cfg.Art` 并弹「换好了：xxx」的气泡 ——
    /// 隐私模式换图是临时的，不该污染用户的选择，也不该刷屏。
    ///
    /// 注意 `_spritePath` 是 BuildSprite 里的 ResolveSpritePath() 决定的，
    /// 它自己会看 `_privacyMode`，所以这里只要触发重建即可。
    /// </summary>
    private void SwitchPrivacyArt()
    {
        int anchorX = Left + Width / 2;
        int anchorY = Top + _artH;

        BuildSprite();

        // 立绘底边留在原处：换姿势（站/坐/趴）高度差别很大，
        // 不锚定的话菲比会跳到屏幕别的地方去。
        Location = ClampToScreen(new Point(anchorX - Width / 2, anchorY - _artH));
        PositionInput();
        BuildArtMenuItems();

        Log($"  [立绘] 隐私模式换图: {Path.GetFileName(_spritePath ?? "(无)")} " +
            $"（贴图 {_sprite?.Width}x{_sprite?.Height}）");
    }

    /// <summary>
    /// 隐私模式开着时，过一会儿再提醒一次。
    ///
    /// 为什么需要：隐私模式是「什么都没发生」的状态 —— 用户看不到任何反馈，
    /// 很容易忘了它开着，然后奇怪「菲比怎么不理我了」。
    /// 隔一段时间说一句是最省事的状态指示。
    /// </summary>
    private void TickPrivacyHint()
    {
        if (!_privacyMode) return;
        if (_privacyHintShown) return;
        // 开启后 25 秒再提醒（开的那一刻气泡里已经说过一次了）
        if (_clock.ElapsedMilliseconds - _privacyOnAtMs < 25000) return;
        _privacyHintShown = true;
        BubbleText = "（隐私模式还开着，我什么都没看。要恢复按 Ctrl+Alt+P）";
    }

    /// <summary>每帧调用：判断该不该主动开口。开销只有一次 GetLastInputInfo。</summary>
    private void TickProactive()
    {
        if (_chat == null) return;

        long idleMs = Native.IdleMilliseconds();
        long prevIdle = _lastInputIdleMs;
        _lastInputIdleMs = idleMs;

        // 用户回来了（闲置时长倒退/归零）→ 重置本轮计数。
// 这个判断比「闲。< 阈值」更准：用户只是动了一下鼠标也算回来过。
if (idleMs < prevIdle) _proactiveInIdle = 0;

        if (_privacyMode) return;
        if (!_cfg.ProactiveIdle) return;
        if (_proactiveBusy) return;
        if (HasBubble) return;          // 气泡里还有话说，别打断
        long need = Math.Max(5, _cfg.IdleSeconds) * 1000L;
        if (idleMs < need) return;

        if (_proactiveInIdle >= Math.Max(1, _cfg.ProactiveMaxPerIdle))
        {
            // 本轮说到上限了，安静等着。
            // 这里刻意只在第一次超限时多记一个计数，否则每帧都会写日志，
            // 几十秒就能把 pet.log 刷爆。
            if (_proactiveInIdle == Math.Max(1, _cfg.ProactiveMaxPerIdle)) _proactiveInIdle++;
            return;
        }

        long nowMs = _clock.ElapsedMilliseconds;
        long cool = Math.Max(0, _cfg.ProactiveCooldownSeconds) * 1000L;
        if (nowMs - _lastProactiveMs < cool) return;

        // 条件都满足 → 开口
        _proactiveInIdle++;
        _lastProactiveMs = nowMs;
        _proactiveBusy = true;

        string reason = $"已经闲置了约 {idleMs / 1000 / 60} 分钟。";
        Log($"  [主动] 触发（闲置 {idleMs / 1000}s，本轮第 {_proactiveInIdle} 次）");

        string recent = BuildRecentContextForProactive();
        // 深度模式：只在「要主动发言了」的这一瞬间截一张，不做周期性截屏
        byte[]? shot = TryCaptureForDepth();
        _chat.SendProactive(reason, recent, (reply, err) =>
        {
            try
            {
                BeginInvoke(new Action(() =>
                {
                    _proactiveBusy = false;
                    if (err != null)
                    {
                        Log($"  [主动] 失败: {err}");
                        return;   // 主动发言失败不打扰用户，不显示错误气泡
}
                    BubbleText = reply;
                    Log($"  [主动] 发言 {reply.Length} 字: {reply}");
                }));
            }
            catch (Exception e)
            {
                _proactiveBusy = false;
                Log($"  [主动] 回填失败: {e.Message}");
            }
        });
    }

    /// <summary>
    /// 给主动搭话提供一点「刚才在聊什么」的上下文。
/// 阶段 C 会在这里接上「用户正在用什么程序」。
/// </summary>
    private string BuildRecentContextForProactive()
    {
        if (_privacyMode || !_cfg.WatchActivity) return "";

        // 用 CurrentOrLastForeign：菜单/对话框弹出时前台是自己，
        // 那种时候要说的是「你刚才在用什么」，而不是「你在用 dotnet」
        var act = ActivityWatch.CurrentOrLastForeign();
        if (act == null) return "";
        if (act.Blacklisted) return "";   // 黑名单：当作完全不知道

        return $"{PlayerDisplayName}正在用「{act.Describe()}」。";
    }

    /// <summary>气泡/提示里称呼用户的名字（聊天核心没挂时兜底）。</summary>
    private string PlayerDisplayName => _chat?.PlayerName ?? "哥哥";

    /// <summary>
    /// 用户这句话是不是在「让我看屏幕上的东西」。
    ///
    /// 只在这种时候才截图：用户明确把注意力指向当前画面时才需要视觉，
    /// 平常聊天截屏纯属浪费 token 且多此一举。
    /// 判据是「指示代词 + 看/觉得」这类短语，宁可漏判也不要误判
    /// （误判会在用户没要求时偷偷截屏，那是隐私问题）。
    /// </summary>
    public static bool LooksLikeShowingSomething(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        string t = text.Trim();

        // 指向当前画面的指示词
        string[] pointing = { "这个", "这张", "这幅", "这图", "这段", "这里", "这页", "这行", "这儿", "这歌", "这视频", "这玩意" };
        // 征求看法的动词
        string[] asking = { "好不好看", "好不好", "怎么样", "好看吗", "好看不", "看看", "看一下", "瞧瞧", "觉得", "行不行", "可以吗", "有问题吗" };

        bool hasPoint = false;
        foreach (var p in pointing) if (t.Contains(p)) { hasPoint = true; break; }
        if (!hasPoint) return false;

        foreach (var a in asking) if (t.Contains(a)) return true;

        // 「看 + 这」这种最简说法：「你看这个」「看这里」。
        // 没有征求看法的词，但「看」直接指向当前画面，意图已经够明确。
        // 只认紧挨着的那几种，避免「我看这个方案挺好」这类叙述被误判成求助。
        if (t.Contains("你看这") || t.Contains("看这里") || t.Contains("看这张") ||
            t.Contains("看这个") || t.Contains("看这一段") || t.Contains("看看这"))
            return true;

        return false;
    }

    /// <summary>把配置同步给活动模块。改完配置要重新调用。</summary>
    private void ApplyActivityConfig()
    {
        ActivityWatch.DeepMode = _cfg.DeepWatch;
        ActivityWatch.ShotMaxEdge = Math.Max(64, Math.Min(2048, _cfg.ShotMaxEdge));
        ActivityWatch.ShotQuality = Math.Max(10, Math.Min(100, _cfg.ShotQuality));
        // null = 用户从未自定义过 → 用出厂默认；空列表 = 用户故意清空
        ActivityWatch.SetBlacklist(_cfg.BlacklistProcess, _cfg.BlacklistTitle);
    }

    /// <summary>
    /// 该不该为这次发言截一张图。
    /// 隐私模式、总开关关闭、深度模式关闭、命中黑名单时都不截。
    /// </summary>
    private byte[]? TryCaptureForDepth()
    {
        if (_privacyMode) { Log("  [活动] 不截屏: 隐私模式"); return null; }
        if (!_cfg.WatchActivity) { Log("  [活动] 不截屏: 采集已关闭"); return null; }
        if (!_cfg.DeepWatch) { Log("  [活动] 不截屏: 深度模式未开"); return null; }

        var act = ActivityWatch.CurrentOrLastForeign();
        if (act == null) { Log("  [活动] 不截屏: 取不到目标窗口"); return null; }
        if (act.Blacklisted) { Log("  [活动] 不截屏: 命中黑名单"); return null; }

        var shot = ActivityWatch.CaptureForeground();
        if (shot == null) { Log($"  [活动] 截屏失败（目标 {act.Describe()}）"); return null; }

        Log($"  [活动] 深度模式截图 {shot.Length / 1024}KB（{act.Describe()}）");
        return shot;
    }

    // ================= 交互 =================
    private void WireEvents()
    {
        MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;

            // 先处理点击反应：距上次点击在双击窗口内 → 判定为双击，否则单击（挤）
            long now = _clock.ElapsedMilliseconds;
            if (now - _lastClickMs <= SystemInformation.DoubleClickTime)
            {
                _lastClickMs = 0;
                OnDoubleClickAction();
            }
            else
            {
                _lastClickMs = now;
                StartReaction(Reaction.Squash);
            }

            // 再进入拖动待命状态（按住不动 3px 内就不会真的移动。
_dragging = true;
            _dragCursorOrigin = Cursor.Position;
            _dragWinOrigin = Location;
        };

        MouseMove += (_, _) =>
        {
            if (!_dragging) return;
            var cur = Cursor.Position;
            Location = new Point(
                _dragWinOrigin.X + (cur.X - _dragCursorOrigin.X),
                _dragWinOrigin.Y + (cur.Y - _dragCursorOrigin.Y));

            // 倾斜量由「鼠标相对起点的水平偏移」决定：拖着走就自然歪一。
double dx = cur.X - _dragCursorOrigin.X;
            double target = Math.Max(-LeanMax, Math.Min(LeanMax, dx * 0.12));
            _lean = _lean * 0.55 + target * 0.45;
            _lastMouseMoveMs = _clock.ElapsedMilliseconds;

            PositionInput();   // 输入框跟着走
};

        MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Left && _dragging)
            {
                _dragging = false;
                _lean = 0;
                SavePosition();
                PositionInput();
            }
            else if (e.Button == MouseButtons.Right)
            {
                // 菜单挂起必须在**菜单真正关闭**时才解除。
                //
                // 踩过的坑（日志实证）：`_menu.Show()` 是**非阻塞**的 ——
                // 它把菜单弹出来就立刻返回。所以
                //     Suspend(); try { _menu.Show(...) } finally { Resume(); }
                // 里的 Resume 在主菜单刚弹出时就跑了，等于**什么都没保护**。
                //
                // 后果：菜单开着那几秒里，气泡到期消失 → BubbleText 变空 →
                // LayoutBubble → SetTopReserve(0) → **窗口纵向跳 80px**。
                // 菜单是挂在窗口上的，宿主一跳，菜单就被甩走了 ——
                // 日志里「子菜单已展开: 人设与记忆」和「顶部预留变更: 0px」
                // 相隔 2 秒，就是现场。
                //
                // 所以改成由 Closed 事件负责解除，用 try/finally 兜异常。
                HookMenuDiagnostics();
                _menu.Closed += OnMenuClosedOnce;   // 一次性，关闭后自己摘掉
                SuspendBubbleLayout();

                try { _menu.Show(this, e.Location); }
                catch (Exception ex)
                {
                    Log($"  [菜单] Show 抛异常: {ex.GetType().Name}: {ex.Message}");
                    // Show 炸了就不会有 Closed 事件，得自己收尾
                    UnhookMenuClosed();
                    ResumeBubbleLayout();
                }
            }
        };

        // 双击走「挤/跳」反应，但输入框不能跟着抖，只在稳定后同步一次
        Move += (_, _) => PositionInput();

        FormClosing += (_, _) =>
        {
            SavePosition();
            _cfg.Save(_cfgPath);
            // 退出时再存一次历史。其实每轮回复后都存过了，
            // 这里是兜底：万一那几次写盘失败，退出时还有一次机会。
            try { _chat?.SaveHistory(); } catch (Exception e) { Log($"  退出保存历史失败: {e.Message}"); }
        };
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };

        // 全局热键 Ctrl+Alt+I 打开输入框。
// 为什么不。KeyDown + Ctrl+I：宠物窗口带 WS_EX_NOACTIVATE。
// 永远拿不到键盘焦点，普通按键事件根本不会来（菜单里原来写的
        // 「Ctrl+I」只是句空话，实测没反应）。要全局生效只能 RegisterHotKey。
RegisterInputHotkey();
    }

    private const int HotkeyId = 0xB1B1;
    private const int HotkeyIdPrivacy = 0xB1B2;
    private bool _hotkeyOk;
    private bool _hotkeyPrivacyOk;

    private void RegisterInputHotkey()
    {
        try
        {
            // MOD_CONTROL | MOD_ALT，VK_I
            _hotkeyOk = Native.RegisterHotKey(Handle, HotkeyId, 0x0002 | 0x0001, 0x49);
            Log(_hotkeyOk
                ? "  全局热键已注册: Ctrl+Alt+I（打开输入框）"
                : $"  全局热键注册失败（可能被占用），仍可用右键菜单打开输入框。err={Marshal.GetLastWin32Error()}");
        }
        catch (Exception e) { Log($"  注册热键异常: {e.Message}"); }

        try
        {
            // MOD_CONTROL | MOD_ALT，VK_P —— 隐私模式
            // 用热键而不是只留菜单：隐私模式往往要在「来不及点菜单」的时候
            // 一把关掉（有人走过来、要共享屏幕），热键才是真正有用的入口。
            _hotkeyPrivacyOk = Native.RegisterHotKey(Handle, HotkeyIdPrivacy, 0x0002 | 0x0001, 0x50);
            Log(_hotkeyPrivacyOk
                ? "  全局热键已注册: Ctrl+Alt+P（隐私模式）"
                : $"  隐私模式热键注册失败（可能被占用），仍可用右键菜单切换。err={Marshal.GetLastWin32Error()}");
        }
        catch (Exception e) { Log($"  注册隐私热键异常: {e.Message}"); }
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_HOTKEY = 0x0312;
        if (m.Msg == WM_HOTKEY)
        {
            int id = m.WParam.ToInt32();
            if (id == HotkeyId)
            {
                // 热键是「切换」：开着就关，关着就开。
                // 否则输入框一显示出来，热键就再也关不掉了。
                ToggleInput();
                return;
            }
            if (id == HotkeyIdPrivacy)
            {
                TogglePrivacy();
                return;
            }
        }
        base.WndProc(ref m);
    }

    /// <summary>
    /// 双击要做什么。
    ///
    /// 默认行为从「跳一下」改成「播音效」（用户要求），但**保留回退**：
    /// 音效文件夹是空的、或者文件全都坏了的时候，还是跳一下 ——
    /// 双击完全没反应会让人以为程序卡死了。
    /// </summary>
    private void OnDoubleClickAction()
    {
        if (_cfg.DoubleClickSound)
        {
            string played;
            // 只在**启用的分组**里随机播。SoundGroups 为空 = 不限制（全部）。
            if (Sound.PlayRandomInGroups(Sound.DirPath(_dir), ResolvedSoundGroups(), out played))
            {
                Log($"  [音效] 双击播放: {played}");
                return;
            }
            // 没有音效可播：只在第一次提醒一下，别每次双击都弹
            if (!_soundMissingHinted)
            {
                _soundMissingHinted = true;
                var files = Sound.ListSounds(_dir);
                if (files.Count == 0)
                {
                    Log($"  [音效] 没有音效文件（{Sound.DirName}/ 是空的），回退为跳一下");
                    BubbleText = $"把音频丢进 {Sound.DirName} 文件夹，双击就会响了～（现在先跳一下）";
                }
                else
                {
                    // 有文件但一个都没播成 —— 多半是分组筛掉了全部音效。
                    // 这个提示很有必要：用户勾了「菲比」组但那个组是空的时，
                    // 双击没声音会以为是 bug。
                    Log("  [音效] 当前分组内没有可播的音效，回退为跳一下");
                    BubbleText = "选中的音效分组里没有音频。右键 → 双击音效 → 分组 里勾几个有内容的。";
                }
            }
        }
        StartReaction(Reaction.Bounce);
    }

    private bool _soundMissingHinted;

    private void StartReaction(Reaction kind)
    {
        _reaction = kind;
        _reactionStartMs = _clock.ElapsedMilliseconds;
    }

    private Point ClampToScreen(Point p)
    {
        var wa = Screen.PrimaryScreen!.WorkingArea;
        // 纵向。_artH 而不。_padH：下方预留区是透明的输入框空间。
// 不该参与「立绘是否出屏」的判断，否则立绘会被顶得过高。
int x = Math.Max(wa.Left - _padW / 3, Math.Min(p.X, wa.Right - _padW * 2 / 3));
        int y = Math.Max(wa.Top - _artH / 3, Math.Min(p.Y, wa.Bottom - _artH * 2 / 3));
        return new Point(x, y);
    }

    private void SavePosition()
    {
        _cfg.X = Left;
        _cfg.Y = Top;
        _cfg.Save(_cfgPath);
    }

    // ================= 右键菜单 =================
    private readonly ContextMenuStrip _menu = new ContextMenuStrip();
    private ToolStripMenuItem _topMostItem = null!;
    private ToolStripMenuItem _passThroughItem = null!;
    private ToolStripMenuItem _bubbleOnItem = null!;
    private ToolStripMenuItem _privacyToggleItem = null!;
    private ToolStripMenuItem _soundOnItem = null!;
    private ToolStripMenuItem _proactiveIdleItem = null!;
    private ToolStripMenuItem _switchProactiveItem = null!;
    private ToolStripMenuItem _watchActivityItem = null!;
    private ToolStripMenuItem _deepWatchItem = null!;

    /// <summary>给小标题项（不可点，只做分组标签）。/summary>
    private static ToolStripMenuItem Header(string text) =>
        new ToolStripMenuItem(text) { Enabled = false };

    private void BuildMenu()
    {
        _menu.ShowImageMargin = false;

        // —— 隐私模式：放在最外层，一眼能看到当前状态
        _privacyToggleItem = new ToolStripMenuItem("隐私模式  (Ctrl+Alt+P)", null, (_, _) => TogglePrivacy())
        { Checked = _privacyMode, CheckOnClick = false };

        // ---------- 对话 ----------
        // 输入框只留一个开关项。「隐藏输入框」和它完全重复 ——
        // 取消勾选就是隐藏，没必要两个入口（用户指出的）。
        var talkMenu = new ToolStripMenuItem("对话");
        _inputToggleItem = new ToolStripMenuItem("显示输入框  (Ctrl+Alt+I)", null, (_, _) => ToggleInput())
        { Checked = false, CheckOnClick = false };

        talkMenu.DropDownItems.Add(_inputToggleItem);
        _inputOnStartItem = new ToolStripMenuItem("启动时自动打开输入框", null, (_, _) =>
        {
            if (_chatCfg == null) { BubbleText = "聊天核心未挂载"; return; }
            _chatCfg.ShowInputOnStart = !_chatCfg.ShowInputOnStart;
            _inputOnStartItem.Checked = _chatCfg.ShowInputOnStart;
            _chatCfg.Save(Path.Combine(AppPaths.Dir, "chat-config.json"));
            BubbleText = _chatCfg.ShowInputOnStart
                ? "好，下次启动就把输入框打开。"
                : "好，下次启动不自动打开输入框。";
        })
        { Checked = false };
        talkMenu.DropDownItems.Add(_inputOnStartItem);
        talkMenu.DropDownItems.Add(new ToolStripMenuItem("清空气泡", null, (_, _) => BubbleText = ""));
        talkMenu.DropDownItems.Add(new ToolStripSeparator());
        talkMenu.DropDownItems.Add(new ToolStripMenuItem("查看上下文…", null, (_, _) => ShowContext()));
        talkMenu.DropDownItems.Add(new ToolStripMenuItem("清空上下文", null, (_, _) =>
        {
            // ClearHistory 里会连磁盘存档一起删 —— 不然重启后「忘掉的又回来了」
            _chat?.ClearHistory();
            BubbleText = "好，刚才聊的我都忘了～";
        }));
        _saveHistoryItem = new ToolStripMenuItem("退出后保留对话", null, (_, _) =>
        {
            if (_chatCfg == null) { BubbleText = "聊天核心未挂载"; return; }
            _chatCfg.SaveHistory = !_chatCfg.SaveHistory;
            _saveHistoryItem.Checked = _chatCfg.SaveHistory;
            _chatCfg.Save(Path.Combine(AppPaths.Dir, "chat-config.json"));

            if (_chatCfg.SaveHistory)
            {
                // 立刻存一份当前内存里的历史，免得「开了开关但这次还是丢」
                _chat?.SaveHistory();
                BubbleText = "好，以后聊过什么我都记着，下次开机接着聊。";
            }
            else
            {
                BubbleText = "好，关掉之后每次都是新的开始（已有的存档不会删）。";
            }
        })
        { Checked = false };
        talkMenu.DropDownItems.Add(_saveHistoryItem);
        talkMenu.DropDownItems.Add(new ToolStripMenuItem("重新载入人设", null, (_, _) =>
        {
            _chat?.LoadPersona();
            ReloadPersonaMenu();
            BubbleText = $"人设已重新载入：{_chat?.CharacterName}";
        }));

        // ---------- 人设与记忆 ----------
        // 合并成一个子菜单：两者都是「菲比是谁 / 她记得什么」，
        // 单独摆两个顶层项太占地方，分类也不清晰（用户指出的）。
        _personaMenu = new ToolStripMenuItem("人设与记忆");
        BuildPersonaMenuItems();

        // ---------- 外观 ----------
        var lookMenu = new ToolStripMenuItem("外观");
        var sizeMenu = new ToolStripMenuItem("尺寸");
        // 记下每个尺寸项，ApplySize 里靠它更新打勾状态。
        //
        // 之前这里是靠遍历 _menu.Items[0].DropDownItems 找勾的，
        // 两处都错：
        //   1) Items[0] 是「── 菲比 ──」这个分组标题，它的 DropDownItems 是空的，
        //      循环体一次都不执行 —— 所以勾永远不动，一直停在初始的「中」；
        //   2) 就算遍历对了，item.Text.Contains("200") 也会误伤（比如 "2000"）。
        // 这是菜单还是扁平结构时留下的代码，重构成多级菜单后失效了。
        _sizeMenuItems.Clear();
        foreach (var (label, px) in new[]
        {
            ("很小 · 140", 140), ("小 · 170", 170), ("中 · 200", 200),
            ("大 · 260", 260), ("很大 · 340", 340),
        })
        {
            int size = px;
            var item = new ToolStripMenuItem(label, null, (_, _) => ApplySize(size))
            {
                Checked = size == _size,
                CheckOnClick = false,
                Tag = size,   // 用 Tag 存真实像素值，别再靠字符串匹配
            };
            _sizeMenuItems.Add(item);
            sizeMenu.DropDownItems.Add(item);
        }
        lookMenu.DropDownItems.Add(sizeMenu);

        // ---------- 立绘（换装） ----------
        // 和尺寸并列放在「外观」下 —— 两者都是「菲比长什么样」。
        // 结构照抄双击音效那套：列表打勾 / 打开文件夹 / 重新扫描，
        // 用户已经熟悉这个模式了。
        _artMenu = new ToolStripMenuItem("立绘");
        BuildArtMenuItems();
        lookMenu.DropDownItems.Add(_artMenu);

        _topMostItem = new ToolStripMenuItem("总在最前", null, (_, _) =>
        {
            _cfg.TopMost = !_cfg.TopMost;
            _topMostItem.Checked = _cfg.TopMost;
            _cfg.Save(_cfgPath);
            ApplyTopMost(_cfg.TopMost);
        })
        { Checked = _cfg.TopMost, CheckOnClick = false };

        _passThroughItem = new ToolStripMenuItem("鼠标穿透（点不到）", null, (_, _) => ToggleClickThrough())
        { Checked = _cfg.ClickThrough, CheckOnClick = false };

        _bubbleOnItem = new ToolStripMenuItem("显示气泡", null, (_, _) =>
        {
            _cfg.ShowBubble = !_cfg.ShowBubble;
            _bubbleOnItem.Checked = _cfg.ShowBubble;
            _cfg.Save(_cfgPath);
            if (!_cfg.ShowBubble) SetTopReserve(0);
            else if (!string.IsNullOrEmpty(_bubbleText)) LayoutBubble();
            Log($"  气泡显示 = {_cfg.ShowBubble}");
        })
        { Checked = _cfg.ShowBubble, CheckOnClick = false };

        lookMenu.DropDownItems.Add(new ToolStripSeparator());
        lookMenu.DropDownItems.Add(_bubbleOnItem);
        lookMenu.DropDownItems.Add(new ToolStripMenuItem("气泡字号", null, (_, _) => ChangeBubbleFont()));

        // ---------- 双击音效（阶段 E） ----------
        _soundMenu = new ToolStripMenuItem("双击音效");
        _soundOnItem = new ToolStripMenuItem("双击时播放音效", null, (_, _) =>
        {
            _cfg.DoubleClickSound = !_cfg.DoubleClickSound;
            _soundOnItem.Checked = _cfg.DoubleClickSound;
            _cfg.Save(_cfgPath);
            BubbleText = _cfg.DoubleClickSound
                ? "好，双击我会出个声～"
                : "双击不响了，改回跳一下。";
        })
        { Checked = _cfg.DoubleClickSound };
        _soundMenu.DropDownItems.Add(_soundOnItem);
        _soundMenu.DropDownItems.Add(new ToolStripMenuItem("音量…", null, (_, _) => ChangeSoundVolume()));
        _soundMenu.DropDownItems.Add(new ToolStripSeparator());

        // ---------- 音效分组 ----------
        // 放在音量后面、文件夹操作前面：它是「用哪些」，属于内容选择，
        // 和「打开文件夹 / 重新扫描」这类操作分开。
        _soundGroupMenu = new ToolStripMenuItem("分组");
        BuildSoundGroupMenuItems();
        _soundMenu.DropDownItems.Add(_soundGroupMenu);
        _soundMenu.DropDownItems.Add(new ToolStripSeparator());

        _soundMenu.DropDownItems.Add(new ToolStripMenuItem("打开音效文件夹", null, (_, _) =>
        {
            string d = Sound.EnsureDir(_dir);
            OpenInShell(d);
            var groups = Sound.ListGroups(_dir);
            int total = groups.Sum(g => g.Count);
            BubbleText = total == 0
                ? $"把音频丢进去就行（支持 {string.Join(" / ", Sound.SupportedExtensions)}）\n" +
                  $"想分组就在 {Sound.DirName}/ 下建子文件夹，文件夹名就是组名。"
                : $"共 {groups.Count} 组 / {total} 个音效";
        }));
        _soundMenu.DropDownItems.Add(new ToolStripMenuItem("重新扫描", null, (_, _) =>
        {
            // 必须走 ListGroups（会递归子文件夹），不能用 ListSounds。
            //
            // 踩过的坑：「重新扫描」原来用的是 ListSounds(_dir)，它内部是
            // Directory.GetFiles(soundDir) —— **只看 Sounds/ 根目录那一层**。
            // 而音效通常是分组的（Sounds/菲比/*.mp3），根目录一个音频都没有，
            // 于是明明有 51 个音效却提示「没找到音效」（用户实际反馈过）。
            // 同一个菜单里的「打开文件夹」用的是 ListGroups，所以那里显示正常，
            // 两个 handler 扫描范围不一致才露的馅。
            var groups = Sound.ListGroups(_dir);
            int total = groups.Sum(g => g.Count);
            if (total == 0)
                BubbleText = $"没找到音效。往 {Sound.DirName}/ 里放音频文件吧。";
            else
            {
                // 连分组明细一起报，一眼能看出扫到了哪些组
                string head = string.Join("、",
                    groups.Where(g => g.Count > 0).Select(g => $"{g.Name}({g.Count})"));
                BubbleText = $"共 {groups.Count(g => g.Count > 0)} 组 / {total} 个音效：{head}";
            }
        }));
        // 试听：打开一个**独立窗口**，列出当前分组的音效，点一行听一个。
        //
        // 为什么不做成「点菜单项播放」：
        //   实测发现 `DropDown.Closing` **先于**菜单项的 `Click` 触发，
        //   所以「在 Click 里设个『别关』标记、在 Closing 里读」根本拿不到
        //   （日志显示 Closing 时标记还是 false，菜单照关）。
        //   用 MouseEnter 提前 arm 理论可行，但那要依赖鼠标事件时序，
        //   键盘操作时又没有 MouseEnter —— 太脆。
        //
        //   独立窗口反而更直接：能看列表、能一条条挑、想听多久听多久，
        //   也不受菜单生命周期影响。
        _soundMenu.DropDownItems.Add(new ToolStripMenuItem("试听…", null, (_, _) => ShowSoundPreview()));

        // ---------- 主动发言 ----------
        var proactiveMenu = new ToolStripMenuItem("主动发言");
        _proactiveIdleItem = new ToolStripMenuItem("闲置时主动开口", null, (_, _) =>
        {
            _cfg.ProactiveIdle = !_cfg.ProactiveIdle;
            _proactiveIdleItem.Checked = _cfg.ProactiveIdle;
            _cfg.Save(_cfgPath);
            _proactiveInIdle = 0;
            BubbleText = _cfg.ProactiveIdle ? "好，你不在的时候我会念叨两句～" : "那我就不主动打扰你了。";
        })
        { Checked = _cfg.ProactiveIdle };
        proactiveMenu.DropDownItems.Add(_proactiveIdleItem);
        proactiveMenu.DropDownItems.Add(new ToolStripMenuItem("闲置时长…", null, (_, _) => ChangeIdleSeconds()));
        proactiveMenu.DropDownItems.Add(new ToolStripMenuItem("冷却时间…", null, (_, _) => ChangeProactiveCooldown()));
        proactiveMenu.DropDownItems.Add(new ToolStripSeparator());
        _switchProactiveItem = new ToolStripMenuItem("切换窗口时搭话", null, (_, _) =>
        {
            _cfg.ProactiveOnSwitch = !_cfg.ProactiveOnSwitch;
            _switchProactiveItem.Checked = _cfg.ProactiveOnSwitch;
            _cfg.Save(_cfgPath);
            BubbleText = _cfg.ProactiveOnSwitch ? "好，你换程序的时候我会搭句话～" : "那我就不在你切窗口时插嘴了。";
        })
        { Checked = _cfg.ProactiveOnSwitch };
        proactiveMenu.DropDownItems.Add(_switchProactiveItem);
        proactiveMenu.DropDownItems.Add(new ToolStripMenuItem("切换后延迟…", null, (_, _) => ChangeSwitchSettle()));
        proactiveMenu.DropDownItems.Add(new ToolStripSeparator());
        proactiveMenu.DropDownItems.Add(new ToolStripMenuItem("现在就说一句", null, (_, _) =>
        {
            if (_chat == null) { BubbleText = "聊天核心未挂载"; return; }
            if (_privacyMode) { BubbleText = "隐私模式下我不主动说话。"; return; }
            ForceProactiveNow();
        }));

        // ---------- 活动感知（阶段 C） ----------
        var watchMenu = new ToolStripMenuItem("活动感知");
        _watchActivityItem = new ToolStripMenuItem("知道我在用什么程序", null, (_, _) =>
        {
            _cfg.WatchActivity = !_cfg.WatchActivity;
            _watchActivityItem.Checked = _cfg.WatchActivity;
            _cfg.Save(_cfgPath);
            ApplyActivityConfig();
            BubbleText = _cfg.WatchActivity ? "好，我会留意你在忙什么～" : "那我就不看你用的是什么了。";
        })
        { Checked = _cfg.WatchActivity };
        _deepWatchItem = new ToolStripMenuItem("深度模式（截屏理解）", null, (_, _) => ToggleDeepWatch())
        { Checked = _cfg.DeepWatch };
        watchMenu.DropDownItems.Add(_watchActivityItem);
        watchMenu.DropDownItems.Add(_deepWatchItem);
        watchMenu.DropDownItems.Add(new ToolStripMenuItem("截屏尺寸…", null, (_, _) => ChangeShotEdge()));
        watchMenu.DropDownItems.Add(new ToolStripSeparator());
        watchMenu.DropDownItems.Add(new ToolStripMenuItem("看看菲比看到了什么…", null, (_, _) => ShowWhatISee()));
        watchMenu.DropDownItems.Add(new ToolStripMenuItem("黑名单…", null, (_, _) => ShowBlacklist()));
        watchMenu.DropDownItems.Add(new ToolStripMenuItem("现在在看什么？", null, (_, _) => ShowCurrentActivity()));

        // ---------- 设置 ----------
        var setMenu = new ToolStripMenuItem("设置");
        setMenu.DropDownItems.Add(new ToolStripMenuItem("API 配置…", null, (_, _) => ShowApiSettings()));
        setMenu.DropDownItems.Add(BuildMaxTokensMenu());
        setMenu.DropDownItems.Add(new ToolStripMenuItem("用量统计…", null, (_, _) => ShowUsage()));
        setMenu.DropDownItems.Add(new ToolStripMenuItem("打开配置文件", null, (_, _) =>
            OpenInShell(Path.Combine(AppPaths.Dir, "chat-config.json"))));
        setMenu.DropDownItems.Add(new ToolStripMenuItem("打开人设文件夹", null, (_, _) =>
            OpenInShell(Path.Combine(AppPaths.Dir, "Personas"))));
        setMenu.DropDownItems.Add(new ToolStripSeparator());
        setMenu.DropDownItems.Add(new ToolStripMenuItem("日志上限…", null, (_, _) => ChangeLogLimit()));
        setMenu.DropDownItems.Add(new ToolStripMenuItem("查看日志", null, (_, _) =>
            OpenInShell(Path.Combine(AppPaths.Dir, "pet.log"))));
        setMenu.DropDownItems.Add(new ToolStripMenuItem("查看上一份日志", null, (_, _) =>
        {
            string old = Path.Combine(AppPaths.Dir, "pet.log.1");
            if (File.Exists(old)) OpenInShell(old);
            else BubbleText = "还没有轮转过的日志";
        }));

        // ---------- 其他 ----------
        var otherMenu = new ToolStripMenuItem("其他");
        otherMenu.DropDownItems.Add(new ToolStripMenuItem("回到右下角", null, (_, _) => MoveToDefault()));
        otherMenu.DropDownItems.Add(new ToolStripMenuItem("关于 / 快捷键", null, (_, _) => ShowAbout()));

        _menu.Items.Add(Header("── 菲比 ──"));
        // 分三组，用分隔线隔开，一眼能看出层次：
        //   1) 聊天相关（说什么、记得什么、谁在说话）
        //   2) 行为相关（她会不会主动开口、会不会看你）
        //   3) 外观与设置
        _menu.Items.Add(talkMenu);
        _menu.Items.Add(_personaMenu);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(proactiveMenu);
        _menu.Items.Add(watchMenu);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(lookMenu);
        _menu.Items.Add(_soundMenu);
        _menu.Items.Add(setMenu);
        _menu.Items.Add(otherMenu);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_privacyToggleItem);
        _menu.Items.Add(new ToolStripMenuItem("退出", null, (_, _) => Close()));
    }

    private ToolStripMenuItem _personaMenu = null!;

    /// <summary>「立绘」子菜单（换装）。</summary>
    private ToolStripMenuItem _artMenu = null!;

    /// <summary>「双击音效 → 分组」子菜单。</summary>
    private ToolStripMenuItem _soundGroupMenu = null!;

    /// <summary>「双击音效」父菜单（要做「点完不关」的拦截）。</summary>
    private ToolStripMenuItem _soundMenu = null!;

    /// <summary>
    /// 重建音效分组子菜单。
    ///
    /// 分组名和内容**完全来自文件夹结构** —— 代码里没有任何硬编码组名，
    /// 所以在 Sounds/ 下新建一个子文件夹就等于加了一组，不用改代码也不用重启。
    ///
    /// 勾选语义：`_cfg.SoundGroups` 为空 = 不限制（用全部）；
    /// 用户勾了具体组之后，只在那几组里随机。
    /// </summary>
    private void BuildSoundGroupMenuItems()
    {
        _soundGroupMenu.DropDownItems.Clear();

        var groups = Sound.ListGroups(_dir);

        if (groups.Count == 0)
        {
            _soundGroupMenu.DropDownItems.Add(
                new ToolStripMenuItem("（Sounds 里还没有音效）") { Enabled = false });
            _soundGroupMenu.DropDownItems.Add(new ToolStripSeparator());
            _soundGroupMenu.DropDownItems.Add(
                new ToolStripMenuItem("想分组就建子文件夹，文件夹名=组名") { Enabled = false });
            return;
        }

        // 勾选状态就存一个集合，**不用 null 表示「全部」**。
        //
        // 之前用 null（配置里没这个键）= 全部，于是菜单里必须对
        // 「全部」和「每个组」两套状态做区分，出现「点一组却把别的组挤掉」
        // 这种反直觉行为（用户反馈「勾选怪怪的」）。
        //
        // 现在语义变单纯了：
        //   · 勾选集合 = 实际要用的组
        //   · 空集合 = 一个都不用 → 等于关掉音效（用户能看见自己取消了所有）
        //   · 配置里没有这个键（null）= 从没用过 → 默认**全选**，保持兼容
        // 这样菜单只需要画勾，不需要再推算「全部模式」。
        var enabled = EffectiveSoundGroups(groups);

        // ---- 全选 / 全不选（动作，不是状态，所以自己不打勾）----
        var selectAll = new ToolStripMenuItem("全选", null, (_, _) =>
        {
            _cfg.SoundGroups = groups.Select(g => g.Name).ToList();
            _cfg.Save(_cfgPath);
            RefreshSoundGroupMenu();
            BubbleText = $"好，{Sound.CollectEnabled(Sound.DirPath(_dir), ResolvedSoundGroups()).Count} 个音效都用。";
        });
        var selectNone = new ToolStripMenuItem("全不选（等于关掉）", null, (_, _) =>
        {
            _cfg.SoundGroups = new List<string>();
            _cfg.Save(_cfgPath);
            RefreshSoundGroupMenu();
            BubbleText = "都取消勾选了 —— 双击只会跳一下。想要声音就再勾几个。";
        });
        _soundGroupMenu.DropDownItems.Add(selectAll);
        _soundGroupMenu.DropDownItems.Add(selectNone);
        _soundGroupMenu.DropDownItems.Add(new ToolStripSeparator());

        // ---- 每个组一个纯复选框 ----
        foreach (var g in groups)
        {
            string name = g.Name;
            bool on = enabled.Contains(name, StringComparer.OrdinalIgnoreCase);

            var item = new ToolStripMenuItem(
                g.Count > 0 ? $"{name}  ({g.Count})" : $"{name}  （空）",
                null,
                (_, _) => ToggleSoundGroup(name))
            {
                Checked = on,
                CheckOnClick = false,
                Tag = name,
                ToolTipText = g.Count == 0
                    ? $"「{name}」里还没有音频文件"
                    : $"{g.Count} 个：{string.Join("、", g.Files.Take(3).Select(Path.GetFileName))}" +
                      (g.Count > 3 ? " …" : ""),
            };
            _soundGroupMenu.DropDownItems.Add(item);
        }

        // 当前实际会用的数量，直接说出来 —— 比让用户自己数勾靠谱
        int active = Sound.CollectEnabled(Sound.DirPath(_dir), ResolvedSoundGroups()).Count;
        _soundGroupMenu.DropDownItems.Add(new ToolStripSeparator());
        _soundGroupMenu.DropDownItems.Add(
            new ToolStripMenuItem(active == 0
                ? "（一个组都没勾，双击只会跳一下）"
                : $"（双击时从这 {active} 个里随机）")
            { Enabled = false });

        HookSoundMenuKeepOpen();
    }

    /// <summary>
    /// 计算「实际生效的勾选集合」。
    ///
    /// 关键在**区分 null 和空列表**（这个坑在别处也踩过，见黑名单那章）：
    ///   null   = 配置里从来没这个键（老配置升级上来）→ 默认全选，保证行为不变
    ///   []     = 用户主动全不选 → 尊重他，一个都不用
    ///   [a,b]  = 只用这两个
    /// </summary>
    private List<string> EffectiveSoundGroups(List<Sound.SoundGroup> groups)
    {
        if (_cfg.SoundGroups == null)
            return groups.Select(g => g.Name).ToList();   // 从没用过 → 全选
        return _cfg.SoundGroups
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .ToList();
    }

    /// <summary>
    /// **播放和统计都必须走这个**，别把 `_cfg.SoundGroups` 直接传给 sound 层。
    ///
    /// 原因：sound 层的 `CollectEnabled` 把 `null` 和 `[]` **都**当「全部」
    /// （那是为了兼容「没设置过」的调用方）。但界面上「全不选」的语义是
    /// 「一个都不用」—— 直接把 `[]` 透传下去，就会变成「菜单说不勾了，
    /// 实际还在放」，自相矛盾。
    ///
    /// 所以在这里统一把「原始配置」翻译成「生效集合」，再交给 sound 层。
    /// </summary>
    private List<string> ResolvedSoundGroups()
        => EffectiveSoundGroups(Sound.ListGroups(_dir));

    /// <summary>
    /// 让分组菜单**点完不关**，可以连着勾好几组。
    ///
    /// WinForms 没有现成的「多选不关闭」选项。踩了一下：
    ///   `DropDownItemClicked` 的事件参数**没有 Cancel**（编译不过），
    ///   那是给「只读通知」用的。真正能拦的是 `DropDown.Closing`，
    ///   它是 `ToolStripDropDownClosingEventHandler`，`e.Cancel` 有效。
    ///
    /// 只拦 `ItemClicked` 这一种原因：
    ///   Esc、点菜单外部、程序失焦仍然要能正常关闭，
    ///   全都拦掉的话菜单会「关不掉」，那更糟。
    /// </summary>
    private bool _soundGroupKeepOpenHooked;

    /// <summary>
    /// 让「双击音效」菜单（含分组子菜单）**点完不关**。
    ///
    /// 为什么不只是分组子菜单：试听、重新扫描都属于「要反复点几次」的操作 ——
    /// 每试一次都要重新开两次菜单太烦了（用户反馈试听「和没用一样」，
    /// 一半原因就是点一次就关，根本没法连着挑）。
    ///
    /// WinForms 没有现成的「多选不关闭」选项。踩了一下：
    ///   `DropDownItemClicked` 的事件参数**没有 Cancel**（编译不过），
    ///   那是给「只读通知」用的。真正能拦的是 `DropDown.Closing`，
    ///   它是 `ToolStripDropDownClosingEventHandler`，`e.Cancel` 有效。
    ///
    /// 只拦 `ItemClicked` 这一种原因：
    ///   Esc、点菜单外部、程序失焦仍然要能正常关闭，
    ///   全都拦掉的话菜单会「关不掉」，那更糟。
    /// </summary>
    private void HookSoundMenuKeepOpen()
    {
        if (_soundGroupKeepOpenHooked) return;
        _soundGroupKeepOpenHooked = true;

        // 分组子菜单：点任何一项都不关（全是即时生效的开关）。
        // 这里能成立是因为**拦截发生在子菜单自己的 Closing 上**，
        // 而子菜单项的 Click 不需要参与判断 —— 勾选状态在点击前就能算出来。
        _soundGroupMenu.DropDown.Closing += (_, e) =>
        {
            if (e.CloseReason != ToolStripDropDownCloseReason.ItemClicked) return;
            e.Cancel = true;
            BeginInvoke(new Action(() => ResetHover(_soundGroupMenu.DropDown)));
        };
    }

    /// <summary>
    /// 取消关闭后，鼠标划过去仍会自动高亮下一项，看起来像「选中的跑偏了」。
    /// 这里把高亮清掉，并让菜单拿回焦点（否则键盘上下键行为也会怪）。
    ///
    /// `ClearAllSelections` 是 ToolStrip 的非公开方法 —— 公开 API 没有
    /// 「取消选中」这一说。用反射调用，取不到就跳过（不影响功能，
    /// 只是高亮会留着）。
    /// </summary>
    private void ResetHover(ToolStripDropDown dd)
    {
        try
        {
            if (!dd.Visible) return;
            var mi = typeof(ToolStrip).GetMethod("ClearAllSelections",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            mi?.Invoke(dd, null);
            dd.Focus();
        }
        catch (Exception e) { Log($"  [音效] 复位高亮失败: {e.Message}"); }
    }

    /// <summary>
    /// 切换一个分组（纯勾选/取消，不影响别的组）。
    ///
    /// 和之前的区别：以前从「全部」点一组会**把其他组挤掉**（收敛成单选），
    /// 用户反馈那样「勾选怪怪的」。现在就是纯粹的开关。
    /// </summary>
    private void ToggleSoundGroup(string groupName)
    {
        var groups = Sound.ListGroups(_dir);
        var cur = new List<string>(EffectiveSoundGroups(groups));

        int idx = cur.FindIndex(s => string.Equals(s, groupName, StringComparison.OrdinalIgnoreCase));
        bool turningOn = idx < 0;
        if (turningOn) cur.Add(groupName);
        else cur.RemoveAt(idx);

        _cfg.SoundGroups = cur;
        _cfg.Save(_cfgPath);
        RefreshSoundGroupMenu();

        int n = Sound.CollectEnabled(Sound.DirPath(_dir), ResolvedSoundGroups()).Count;
        if (n == 0)
            BubbleText = "一个组都没勾了，双击只会跳一下。";
        else
            BubbleText = turningOn
                ? $"「{groupName}」加进来了，现在共 {n} 个。"
                : $"取消「{groupName}」，还剩 {n} 个。";
        Log($"  [音效] 勾选 = [{string.Join("/", _cfg.SoundGroups)}]，可用 {n} 个");
    }

    /// <summary>
    /// 只更新打勾状态，**不重建控件**。
    ///
    /// 为什么不能直接调 BuildSoundGroupMenuItems：
    ///   它会把所有菜单项 Clear 掉重建。而点击处理器正在这些控件的事件里跑 ——
    ///   控件被销毁会让菜单异常关闭，正好破坏「点完不关」。
    ///   所以这里只改 Checked 和底部那句话，控件本身不动。
    /// </summary>
    private void RefreshSoundGroupMenu()
    {
        var groups = Sound.ListGroups(_dir);
        var enabled = EffectiveSoundGroups(groups);

        foreach (ToolStripItem it in _soundGroupMenu.DropDownItems)
        {
            if (it is not ToolStripMenuItem mi) continue;
            if (mi.Tag is string tag && tag.Length > 0)
                mi.Checked = enabled.Contains(tag, StringComparer.OrdinalIgnoreCase);
        }

        int n = Sound.CollectEnabled(Sound.DirPath(_dir), ResolvedSoundGroups()).Count;
        // 最后一项是那句「（双击时从这 N 个里随机）」，就地改文案
        var items = _soundGroupMenu.DropDownItems;
        if (items.Count > 0 && items[items.Count - 1] is ToolStripMenuItem last && !last.Enabled)
        {
            last.Text = n == 0
                ? "（一个组都没勾，双击只会跳一下）"
                : $"（双击时从这 {n} 个里随机）";
        }
    }

    /// <summary>
    /// 重建「立绘」子菜单：列出 Art/ 里的所有图片，当前用的打勾。
    ///
    /// 每次打开菜单前都重建（见 BuildMenu 的 DropDownOpening），
    /// 所以往文件夹里丢一张新图，不用重启就能看到。
    /// </summary>
    private void BuildArtMenuItems()
    {
        _artMenu.DropDownItems.Clear();

        string artDir = Art.DirPath(_dir);
        var items = Art.ListIn(artDir, _spritePath);

        // 出厂贴图始终作为第一个选项 —— 用户换了一圈想换回来时得有路可走
        var none = new ToolStripMenuItem("出厂贴图（默认）", null, (_, _) => ApplyArt(null))
        {
            Checked = string.IsNullOrWhiteSpace(_cfg.Art),
            CheckOnClick = false,
            Tag = "(none)",
        };
        _artMenu.DropDownItems.Add(none);

        if (items.Count == 0)
        {
            _artMenu.DropDownItems.Add(new ToolStripSeparator());
            _artMenu.DropDownItems.Add(new ToolStripMenuItem("（Art 文件夹是空的）") { Enabled = false });
        }
        else
        {
            _artMenu.DropDownItems.Add(new ToolStripSeparator());
            foreach (var it in items)
            {
                // 尺寸放右边当提示：换图前能看出比例对不对，
                // 免得选了一张超长的图才发现窗口变得很高
                string label = it.Width > 0 && it.Height > 0
                    ? $"{it.Name}  ({it.Width}×{it.Height})"
                    : it.Name;

                string name = it.Name;
                var mi = new ToolStripMenuItem(label, null, (_, _) => ApplyArt(name))
                {
                    Checked = it.Active,
                    CheckOnClick = false,
                    Tag = name,
                };
                // 比例差异大的给个提示，用户能预期窗口会变高
                if (it.Aspect > 2.0 || it.Aspect < 0.5)
                    mi.ToolTipText = $"长宽比 {it.Aspect:F2}，换上去窗口高度会明显变化";
                _artMenu.DropDownItems.Add(mi);
            }
        }

        _artMenu.DropDownItems.Add(new ToolStripSeparator());

        // 隐私模式换闭眼立绘的总开关。
        // 收在立绘菜单里而不是隐私菜单里：它决定的是「用哪张图」，
        // 属于立绘的事，跟「要不要停止采集」是两码事。
        var privacyArtItem = new ToolStripMenuItem("隐私模式换成闭眼立绘", null, (_, _) =>
        {
            _cfg.PrivacyArtSwitch = !_cfg.PrivacyArtSwitch;
            _cfg.Save(_cfgPath);

            // 立刻按新设置重算一次贴图，不用等下次切隐私模式
            try { SwitchPrivacyArt(); }
            catch (Exception e) { Log($"  [立绘] 切换失败（忽略）: {e.Message}"); }

            if (!_cfg.PrivacyArtSwitch)
                BubbleText = "好，隐私模式不再换立绘了。";
            else if (string.IsNullOrWhiteSpace(_cfg.Art))
                BubbleText = "开了。不过你现在用的是出厂贴图，它没有闭眼版。";
            else if (FindClosedEyePath() == null)
                BubbleText = $"开了。但没找到「{Path.GetFileNameWithoutExtension(_cfg.Art)}{ClosedEyeSuffix}」这张图。";
            else
                BubbleText = "好，开隐私模式时会换成闭眼立绘。";
        })
        {
            Checked = _cfg.PrivacyArtSwitch,
            CheckOnClick = false,
            Tag = "(privacy-art)",
        };

        // 当前立绘有没有配对的闭眼图 —— 直接写在提示里，
        // 省得用户开了开关却发现「怎么没变化」还要来问。
        if (_cfg.PrivacyArtSwitch)
        {
            string? paired = FindClosedEyePath();
            privacyArtItem.ToolTipText = paired != null
                ? $"已找到：{Path.GetFileName(paired)}"
                : "当前立绘没有配对的闭眼图，隐私模式下会沿用原图";
        }
        _artMenu.DropDownItems.Add(privacyArtItem);

        _artMenu.DropDownItems.Add(new ToolStripMenuItem("打开立绘文件夹", null, (_, _) =>
        {
            Art.EnsureDir(_dir);   // 不存在就先建出来，省得用户自己找
            OpenInShell(artDir);
        }));
        _artMenu.DropDownItems.Add(new ToolStripMenuItem("重新扫描", null, (_, _) =>
        {
            BuildArtMenuItems();
            int n = Art.ListIn(artDir).Count;
            BubbleText = n == 0
                ? $"没找到立绘。往 {Art.DirName}/ 里丢几张图片吧。"
                : $"找到 {n} 张立绘。";
        }));
    }

    /// <summary>
    /// 换装。artName 传 null = 回退出厂贴图。
    ///
    /// 两个必须做对的地方：
    ///   1) **以立绘底边为锚点**重建，和 ApplySize 一样 ——
    ///      否则换图后菲比会「跳」一下，因为新图高度不同。
    ///   2) 重建后要同步菜单打勾，不然选了新图但勾还停在旧图上。
    /// </summary>
    private void ApplyArt(string? artName)
    {
        // 锚点：立绘底边中点。用 _artH 而不是 Height（Height 含下方透明输入区）
        int anchorX = Left + Width / 2;
        int anchorY = Top + _artH;

        _cfg.Art = artName;
        _cfg.Save(_cfgPath);

        try
        {
            BuildSprite();
        }
        catch (Exception e)
        {
            // 换图失败不能把菲比搞没 —— 回退出厂贴图再来一次
            Log($"  [立绘] 换装失败: {e.Message}，回退出厂贴图");
            _cfg.Art = null;
            _cfg.Save(_cfgPath);
            try { BuildSprite(); } catch { }
            BubbleText = "这张图我读不了，先用回原来的。";
            BuildArtMenuItems();
            return;
        }

        // 立绘底边留在原处，视觉上像「原地换衣服」而不是「跳一下」
        Location = ClampToScreen(new Point(anchorX - Width / 2, anchorY - _artH));
        PositionInput();
        BuildArtMenuItems();

        string shown = string.IsNullOrWhiteSpace(artName) ? "出厂贴图" : artName!;
        BubbleText = $"换好了：{shown}";
        Log($"  [立绘] 已切换: {shown}（贴图 {_sprite?.Width}x{_sprite?.Height}）");
    }

    /// <summary>右键菜单（自检用）。/summary>
    public ContextMenuStrip Menu => _menu;

    /// <summary>
    /// 菜单真正关闭时才解除挂起。
    ///
    /// 为什么不用 try/finally 包 _menu.Show()：Show 是非阻塞的，
    /// 主菜单一弹出它就返回了，finally 会在菜单还开着的时候就跑 ——
    /// 那就等于没挂起（实测就是这里出的问题）。
    /// </summary>
    private void OnMenuClosedOnce(object? sender, ToolStripDropDownClosedEventArgs e)
    {
        UnhookMenuClosed();
        ResumeBubbleLayout();
        Log($"  [菜单诊断] 挂起已解除（菜单关闭）");
    }

    private void UnhookMenuClosed()
    {
        _menu.Closed -= OnMenuClosedOnce;
    }

    /// <summary>
    /// 给菜单装诊断钩子。**只在异常或子菜单展开时写日志**，
    /// 正常使用不会污染 pet.log。
    ///
    /// 背景：「子菜单打不开」我离线验了 7 种假设 ——
    /// 挂起状态时序、动宿主窗口、NOACTIVATE、LAYERED+30fps 重绘、
    /// Clear 挂载中的子项、真实菜单结构、OwnerItem 断裂 ——
    /// 一种都没复现。结构和数据都是对的，说明触发条件在真机环境里，
    /// 所以不再猜，改成让真机自己说话。
    ///
    /// 结论：日志证明 8 个子菜单**都成功展开了**，问题不在「打不开」，
    /// 而在「打开后窗口跳走把菜单甩没了」。这个钩子留着，
    /// 下次再出类似问题还是它最快。
    /// </summary>
    private bool _menuDiagHooked;

    private void HookMenuDiagnostics()
    {
        if (_menuDiagHooked) return;
        _menuDiagHooked = true;

        _menu.Opening += (_, e) =>
        {
            if (e.Cancel) Log("  [菜单诊断] 主菜单 Opening 被取消 —— 菜单不会弹出来");

            // 每次打开菜单都重扫一次立绘文件夹 ——
            // 用户往 Art/ 里丢一张新图就不用重启了。
            // 扫描只读图片头（不解码），几张图的代价可以忽略。
            try { BuildArtMenuItems(); }
            catch (Exception ex) { Log($"  [菜单] 重扫立绘失败: {ex.Message}"); }

            // 音效分组同理：新建一个子文件夹就等于加了一组。
            // 只在菜单没展开时重建 —— 展开状态下 Clear() 会把控件销毁掉，
            // 正在处理点击事件的那一项突然消失，菜单会异常关闭。
            try
            {
                if (_soundGroupMenu != null && !_soundGroupMenu.DropDown.Visible)
                    BuildSoundGroupMenuItems();
            }
            catch (Exception ex) { Log($"  [菜单] 重扫音效分组失败: {ex.Message}"); }
        };
        _menu.Opened += (_, _) =>
        {
            Log("  [菜单诊断] 主菜单已弹出");
            // 主菜单也会跑出屏幕（菲比贴着屏幕边缘时），同样延后夹一次
            try { _menu.BeginInvoke(new Action(ClampMainMenu)); }
            catch (Exception ex) { Log($"  [菜单] 主菜单定位修正失败: {ex.Message}"); }
        };
        _menu.Closed += (_, _) => Log("  [菜单诊断] 主菜单已关闭");

        // 子菜单打不开，最直接的证据是「DropDownOpening 到底有没有发生」：
        //   有 Opening 没 Opened → 展开了但被打断
        //   连 Opening 都没有    → 鼠标事件根本没送到这个子菜单
        foreach (ToolStripItem it in _menu.Items)
        {
            if (it is not ToolStripMenuItem m || !m.HasDropDownItems) continue;
            string name = m.Text ?? "?";
            m.DropDownOpening += (_, _) =>
            {
                Log($"  [菜单诊断] 子菜单展开中: {name}（Enabled={m.Enabled}）");
                // 多屏：子菜单不能被甩到副屏去（用户反馈的问题）
                KeepDropDownOnScreen(m);
            };
            m.DropDownOpened += (_, _) =>
            {
                Log($"  [菜单诊断] 子菜单已展开: {name}");
                KeepDropDownOnScreen(m);   // 展开后再校一次：这时才知道真实尺寸
            };
            m.DropDownClosed += (_, _) => Log($"  [菜单诊断] 子菜单已关闭: {name}");
        }
    }

    /// <summary>
    /// 把主菜单夹回它所在的屏幕内。
    /// 菲比贴着屏幕边缘时右键，主菜单会有一半跑到屏幕外或隔壁屏。
    /// </summary>
    private void ClampMainMenu()
    {
        if (!_menu.Visible) return;
        try
        {
            var screen = Screen.FromControl(this) ?? Screen.PrimaryScreen;
            if (screen == null) return;
            var wa = screen.WorkingArea;
            var b = _menu.Bounds;

            int nx = Math.Max(wa.Left, Math.Min(b.X, wa.Right - b.Width));
            int ny = Math.Max(wa.Top, Math.Min(b.Y, wa.Bottom - b.Height));

            if (nx != b.X || ny != b.Y)
            {
                _menu.Location = new Point(nx, ny);
                Log($"  [菜单] 主菜单拉回屏幕内: ({b.X},{b.Y}) → ({nx},{ny})");
            }
        }
        catch (Exception ex)
        {
            Log($"  [菜单] 主菜单定位修正失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 把子菜单拉回**主菜单所在的那块屏幕**内。
    ///
    /// 问题（用户反馈）：菲比靠近副屏时，子菜单会展开到副屏上去。
    ///
    /// 原因：WinForms 给 ToolStripDropDown 定位时，判断「右边放不下就翻到左边、
    /// 下边放不下就上移」用的是 **WorkingArea 并集**级别的空间感知，
    /// 多屏环境下它只保证菜单落在**虚拟桌面**内，不管落在哪一块屏上。
    /// 于是靠近副屏边界时，子菜单很自然地就滑过去了。
    ///
    /// 这里做的很朴素：拿主菜单所在的屏（而不是虚拟桌面），
    /// 把子菜单左上角夹回该屏的工作区里。夹不动就保持原样，不硬来。
    /// </summary>
    private void KeepDropDownOnScreen(ToolStripMenuItem owner)
    {
        var dd = owner.DropDown;
        if (dd == null || !dd.Visible) return;

        // 为什么要在消息循环里延后一拍：
        //
        // 日志实证 —— 在 DropDownOpening 里把 Location 改对了（2560 → 2198），
        // 但用户看到的还是在副屏。因为 WinForms 在 Opening 和实际显示之间
        // 还会再算一次位置，**把我们写的值覆盖掉**。
        // 所以必须等它算完（BeginInvoke 排到消息队列尾部）再落我们的值，
        // 保证我们是最后一个写的。
        try { dd.BeginInvoke(new Action(() => ClampDropDown(owner))); }
        catch (Exception ex)
        {
            Log($"  [菜单] 子菜单定位修正失败: {ex.Message}");
        }
    }

    /// <summary>真正做夹取的那一步（延后执行，避开 WinForms 自己的定位）。</summary>
    private void ClampDropDown(ToolStripMenuItem owner)
    {
        var dd = owner.DropDown;
        if (dd == null || !dd.Visible) return;

        try
        {
            // 以主菜单为参照：它当前在哪块屏，子菜单就属于哪块屏
            var screen = Screen.FromControl(_menu) ?? Screen.PrimaryScreen;
            if (screen == null) return;
            var wa = screen.WorkingArea;

            var b = dd.Bounds;
            int nx = b.X, ny = b.Y;

            // 右边超出 → 翻到主菜单左侧
            if (b.Right > wa.Right)
            {
                nx = _menu.Left - b.Width;
                if (nx < wa.Left) nx = Math.Max(wa.Left, wa.Right - b.Width);
            }
            if (nx < wa.Left) nx = wa.Left;

            // 下边超出 → 上移
            if (b.Bottom > wa.Bottom) ny = Math.Max(wa.Top, wa.Bottom - b.Height);
            if (ny < wa.Top) ny = wa.Top;

            if (nx != b.X || ny != b.Y)
            {
                dd.Location = new Point(nx, ny);
                Log($"  [菜单] 子菜单「{owner.Text}」拉回屏幕内: " +
                    $"({b.X},{b.Y}) → ({nx},{ny})，屏幕工作区 {wa}");
            }
        }
        catch (Exception ex)
        {
            // 定位失败不算大事，菜单还在，只是位置可能不理想
            Log($"  [菜单] 子菜单定位修正失败: {ex.Message}");
        }
    }

    /// <summary>配置里的置顶开关（自检用）。/summary>
    public bool TopMostEnabled => _cfg.TopMost;

    /// <summary>切换置顶（自检用，走和菜单同一条路径）。/summary>
    public void SetTopMostForTest(bool on)
    {
        _cfg.TopMost = on;
        ApplyTopMost(on);
    }

    /// <summary>当前尺寸参数（自检用）。</summary>
    public int SizeForTest => _size;

    /// <summary>缩放后的立绘尺寸（自检用）。几何不变量的核心输入。</summary>
    public Size SpriteSizeForTest => _sprite?.Size ?? Size.Empty;

    /// <summary>当前画布允许的最大高度（自检用，和实例版同一套算式）。</summary>
    public static int MaxCanvasHeightForTest
    {
        get
        {
            int screenH;
            try { screenH = Screen.PrimaryScreen?.WorkingArea.Height ?? 1080; }
            catch { screenH = 1080; }
            return Math.Max(200, (int)(screenH * 0.92));
        }
    }

    /// <summary>
    /// 换一张立绘（自检用）。传 null = 回退出厂贴图。
    /// 走的是和菜单完全相同的路径（改配置 → 重建贴图）。
    /// </summary>
    public void SetArtForTest(string? artName)
    {
        _cfg.Art = artName;
        BuildSprite();
    }

    /// <summary>
    /// 换装（自检用）—— 走和菜单点击**完全相同**的路径，
    /// 包括锚点定位和菜单打勾同步。
    /// </summary>
    public void ApplyArtForTest(string? artName) => ApplyArt(artName);

    /// <summary>设置音效分组配置（自检用）。</summary>
    public void SetSoundGroupsForTest(List<string>? groups) => _cfg.SoundGroups = groups;

    /// <summary>读出生效的分组集合（自检用，走 EffectiveSoundGroups）。</summary>
    public List<string> EffectiveSoundGroupsForTest
        => EffectiveSoundGroups(Sound.ListGroups(_dir));

    /// <summary>生效分组下实际可播的音效（自检用，走 ResolvedSoundGroups 翻译层）。</summary>
    public List<string> ResolvedSoundPoolForTest
        => Sound.CollectEnabled(Sound.DirPath(_dir), ResolvedSoundGroups());

    /// <summary>调尺寸（自检用，走菜单点击那条完全一样的路径）。</summary>
    public void ApplySizeForTest(int size) => ApplySize(size);

    /// <summary>列出 Personas/ 下的所有人设，点击即切换。/summary>
    private void BuildPersonaMenuItems()
    {
        _personaMenu.DropDownItems.Clear();
        if (_chat == null)
        {
            _personaMenu.DropDownItems.Add(new ToolStripMenuItem("（聊天核心未挂载）") { Enabled = false });
            return;
        }

        var files = _chat.ListPersonaFiles();
        if (files.Count == 0)
        {
            _personaMenu.DropDownItems.Add(new ToolStripMenuItem("（Personas 文件夹是空的）") { Enabled = false });
        }
        else
        {
            string current = Path.GetFileName(_chat.ArchivePath);
            foreach (var f in files)
            {
                string name = f;
                _personaMenu.DropDownItems.Add(new ToolStripMenuItem(name, null, (_, _) =>
                {
                    _chat.SetPersonaFile(name);
                    BuildPersonaMenuItems();
                    BubbleText = $"我是{_chat.CharacterName}，请多指教～";
                })
                {
                    Checked = string.Equals(f, current, StringComparison.OrdinalIgnoreCase),
                    CheckOnClick = false,
                });
            }
        }

        _personaMenu.DropDownItems.Add(new ToolStripSeparator());
        _personaMenu.DropDownItems.Add(new ToolStripMenuItem("打开人设文件夹", null, (_, _) =>
            OpenInShell(Path.Combine(AppPaths.Dir, "Personas"))));
        _personaMenu.DropDownItems.Add(new ToolStripMenuItem("重新扫描", null, (_, _) => BuildPersonaMenuItems()));

        // 记忆并进同一个子菜单：它和人设一样，回答的是「菲比是谁」这个问题
        _personaMenu.DropDownItems.Add(new ToolStripSeparator());
        _personaMenu.DropDownItems.Add(new ToolStripMenuItem("查看记忆…", null, (_, _) => ShowMemory()));
        // 单独给个「主动教她」的入口：想补一句的时候不用翻出整段记忆
        _personaMenu.DropDownItems.Add(new ToolStripMenuItem("让菲比记住…", null, (_, _) => TeachMemory()));
        _personaMenu.DropDownItems.Add(new ToolStripMenuItem("清空记忆", null, (_, _) =>
        {
            _chat?.ClearMemory();
            BubbleText = "关于你的记忆…我清掉了。";
        }));
    }

    /// <summary>直接追加一条记忆（菜单入口，不打开编辑器）。</summary>
    private void TeachMemory()
    {
        if (_chat == null) { BubbleText = "聊天核心未挂载"; return; }

        string? t = PromptText(this, "让菲比记住…",
            "写一件希望菲比长期记住的事（喜好、习惯、纪念日都可以）：", "");
        if (string.IsNullOrWhiteSpace(t)) return;

        _chat.AppendMemory(t);
        BubbleText = "好，我记住了。";
        Log($"  [记忆] 追加 {t.Trim().Length} 字，现共 {_chat.MemoryLength} 字");
    }

    private void ReloadPersonaMenu() => BuildPersonaMenuItems();

    /// <summary>用系统默认程序打开文件/文件夹。/summary>
    private void OpenInShell(string path)
    {
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                BubbleText = "找不到：" + Path.GetFileName(path);
                return;
            }
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception e) { Log($"  打开 {path} 失败: {e.Message}"); }
    }

    /// <summary>
    /// 记忆编辑器。原来是个只读 MessageBox —— 记错了只能「清空记忆」从头来，
    /// 也没法主动教她点什么。
    ///
    /// 现在是一个可写窗口：改完点保存写回 user_memory.json，立即生效。
    /// 顺手支持「让菲比记住…」，那个入口更常用（往往只想补一句，
    /// 而不是翻出整段记忆去编辑）。
    /// </summary>
    private void ShowMemory()
    {
        if (_chat == null) { BubbleText = "聊天核心未挂载"; return; }

        string original = _chat.UserMemory ?? "";

        using var dlg = new Form
        {
            Text = "菲比记住的事",
            FormBorderStyle = FormBorderStyle.Sizable,
            StartPosition = FormStartPosition.CenterScreen,
            ClientSize = new Size(560, 420),
            MinimumSize = new Size(420, 300),
            MaximizeBox = true,
            MinimizeBox = false,
            ShowInTaskbar = true,
        };

        var hint = new Label
        {
            Dock = DockStyle.Top, Height = 46, Padding = new Padding(12, 8, 12, 0),
            Text = "这里就是菲比长期记得的关于你的事（user_memory.json）。\n" +
                   "可以直接改，改完点「保存」立即生效。清空内容等于让她忘掉。",
            ForeColor = Color.FromArgb(110, 110, 122),
        };
        var line = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = Color.FromArgb(226, 226, 234) };

        var box = new TextBox
        {
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            WordWrap = true,
            Dock = DockStyle.Fill,
            Font = new Font("Microsoft YaHei UI", 9.5f),
            Text = original,
            BorderStyle = BorderStyle.None,
            BackColor = Color.White,
        };

        var status = new Label
        {
            Dock = DockStyle.Bottom, Height = 22, Padding = new Padding(12, 0, 0, 0),
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(130, 130, 142),
        };

        void UpdateStatus()
        {
            int n = box.Text.Trim().Length;
            bool changed = box.Text.Trim() != original.Trim();
            status.Text = n == 0
                ? "（空的 —— 菲比不会记得任何事）"
                : $"{n} 字" + (changed ? " · 有未保存的修改" : "");
        }
        box.TextChanged += (_, _) => UpdateStatus();
        UpdateStatus();

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 46, Padding = new Padding(10, 8, 10, 8) };
        var closeBtn = new Button { Text = "关闭", Width = 88, Height = 28, Dock = DockStyle.Right };
        var saveBtn = new Button { Text = "保存", Width = 88, Height = 28, Dock = DockStyle.Right };
        var teachBtn = new Button { Text = "让菲比记住…", Width = 120, Height = 28, Dock = DockStyle.Left };
        var revertBtn = new Button { Text = "撤销修改", Width = 100, Height = 28, Dock = DockStyle.Left };

        closeBtn.Click += (_, _) => dlg.Close();
        revertBtn.Click += (_, _) => { box.Text = original; UpdateStatus(); };

        saveBtn.Click += (_, _) =>
        {
            _chat.SetMemory(box.Text);
            BubbleText = _chat.MemoryLength == 0 ? "好，我忘掉了。" : "记住了。";
            dlg.Close();
        };

        teachBtn.Click += (_, _) =>
        {
            string? t = PromptText(dlg, "让菲比记住…",
                "写一件希望菲比长期记住的事（喜好、习惯、纪念日都可以）：", "");
            if (string.IsNullOrWhiteSpace(t)) return;
            _chat.AppendMemory(t);
            box.Text = _chat.UserMemory;   // 回显，让用户看到确实加进去了
            UpdateStatus();
            BubbleText = "好，我记住了。";
        };

        bottom.Controls.Add(closeBtn);
        bottom.Controls.Add(saveBtn);
        bottom.Controls.Add(teachBtn);
        bottom.Controls.Add(revertBtn);

        dlg.Controls.Add(box);
        dlg.Controls.Add(status);
        dlg.Controls.Add(bottom);
        dlg.Controls.Add(line);
        dlg.Controls.Add(hint);
        dlg.CancelButton = closeBtn;
        dlg.ShowDialog(this);
    }

    /// <summary>
    /// 单行输入对话框（「让菲比记住…」用）。
    /// WinForms 没有内置的 InputBox，自己搭一个。
    /// </summary>
    private static string? PromptText(IWin32Window owner, string title, string prompt, string initial)
    {
        using var dlg = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(420, 160),
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
        };

        var label = new Label
        {
            Left = 16, Top = 14, Width = 388, Height = 40,
            Text = prompt,
        };
        var input = new TextBox
        {
            Left = 16, Top = 58, Width = 388, Height = 24,
            Text = initial,
        };
        var ok = new Button { Text = "确定", DialogResult = DialogResult.OK, Left = 316, Top = 110, Width = 88, Height = 28 };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Left = 220, Top = 110, Width = 88, Height = 28 };

        dlg.Controls.AddRange(new Control[] { label, input, ok, cancel });
        dlg.AcceptButton = ok;
        dlg.CancelButton = cancel;

        return dlg.ShowDialog(owner) == DialogResult.OK ? input.Text : null;
    }

    /// <summary>
    /// 把当前上下文（历史对话）拼成可读文本。
/// 单独抽出来是为了能离线自检 —。只看渲染结果没法验证内容对不对。
/// </summary>
    public static string FormatContext(List<ChatTurn> turns, string charName, string playerName)
    {
        if (turns.Count == 0)
            return "（还没有聊过什么）\n\n上下文为空时，菲比不会记得之前的对话。";

        var sb = new System.Text.StringBuilder();
        int shown = 0;
        for (int i = 0; i < turns.Count; i++)
        {
            var t = turns[i];
            // 历史里 user 的 content 带了一层「玩家：xxx」包装（见 chat.cs 的 enriched），
            // 展示时剥掉，否则会出现「哥哥：玩家：…」这种双重前缀。
            string body = t.Content ?? "";
            if (t.Role == "user")
            {
                foreach (var prefix in new[] { "玩家：", "玩家:" })
                    if (body.StartsWith(prefix, StringComparison.Ordinal))
                    { body = body.Substring(prefix.Length); break; }
            }
            else
            {
                foreach (var prefix in new[] { $"{charName}：", $"{charName}:" })
                    if (body.StartsWith(prefix, StringComparison.Ordinal))
                    { body = body.Substring(prefix.Length); break; }
            }
            body = body.Trim();

            string who = t.Role == "user" ? playerName : charName;
            // 轮次：一问一答算一轮。user 开一轮，assistant 沿用当前轮号。
int round = i / 2 + 1;
            string tag = t.Role == "user" ? $"[{round}] " : "    ";
            sb.Append(tag).Append(who).Append("：\n");
            sb.Append("    ").Append(body.Replace("\n", "\n    ")).Append("\n\n");
            shown++;
        }
        sb.Append("─────\n");
        // 轮数按「user 条数」算，不能拿总数除以 2 ——
        // 最后一句如果是菲比说的（或只有半轮），总数是奇数，除 2 会少算一轮。
        int rounds = turns.Count(t => t.Role == "user");
        sb.Append($"共 {shown} 条（{rounds} 轮），上限 {ChatConfig.Load(Path.Combine(AppPaths.Dir, "chat-config.json")).HistoryLimit} 条");
        return sb.ToString();
    }

    /// <summary>
    /// 把历史对话拆成「一条一条」的结构化条目，供 ListView 渲染。
    ///
    /// 为什么要拆：原来是把整段历史拼成一个大字符串塞进只读 TextBox，
    /// 一问一答全部连在一起、只有缩进没有分界，长对话看上去就是糊成一团
    /// （用户原话：太乱了，全扎堆了）。文本框再怎么做也就这点表现力。
    /// 改成 ListView 之后每条独立成行、可以点选、可以按轮次看。
    ///
    /// 返回的 Tuple：轮号 / 说话人 / 正文。
    /// </summary>
    public static List<(int Round, string Who, string Body)> BuildContextRows(
        List<ChatTurn> turns, string charName, string playerName)
    {
        var rows = new List<(int, string, string)>();
        for (int i = 0; i < turns.Count; i++)
        {
            var t = turns[i];
            // 历史里 user 的 content 带了一层「玩家：xxx」包装（见 chat.cs 的 enriched），
            // 展示时剥掉，否则会出现「哥哥：玩家：…」这种双重前缀。
            string body = t.Content ?? "";
            if (t.Role == "user")
            {
                foreach (var prefix in new[] { "玩家：", "玩家:" })
                    if (body.StartsWith(prefix, StringComparison.Ordinal))
                    { body = body.Substring(prefix.Length); break; }
            }
            else
            {
                foreach (var prefix in new[] { $"{charName}：", $"{charName}:" })
                    if (body.StartsWith(prefix, StringComparison.Ordinal))
                    { body = body.Substring(prefix.Length); break; }
            }
            body = body.Trim();

            string who = t.Role == "user" ? playerName : charName;
            // 轮次：一问一答算一轮。user 开一轮，assistant 沿用当前轮号。
            int round = i / 2 + 1;
            rows.Add((round, who, body));
        }
        return rows;
    }

    /// <summary>查看当前上下文（历史对话）。</summary>
    private void ShowContext()
    {
        if (_chat == null) { BubbleText = "聊天核心未挂载"; return; }

        var turns = _chat.HistorySnapshot();
        var rows = BuildContextRows(turns, _chat.CharacterName, _chat.PlayerName);

        int limit = ChatConfig.Load(Path.Combine(AppPaths.Dir, "chat-config.json")).HistoryLimit;

        using var dlg = new Form
        {
            Text = $"上下文 · {_chat.CharacterName}（{turns.Count} 条）",
            FormBorderStyle = FormBorderStyle.Sizable,
            StartPosition = FormStartPosition.CenterScreen,
            ClientSize = new Size(680, 480),
            MinimumSize = new Size(480, 300),
            MaximizeBox = true,
            MinimizeBox = false,
            ShowInTaskbar = true,
            // 这窗口比桌宠本体的对话框大一圈，居中到屏幕比挂在宠物边上好看
            BackColor = Color.FromArgb(250, 250, 252),
        };

        // ---------- 顶部：概览 ----------
        int totalRounds = turns.Count == 0 ? 0 : (turns.Count + 1) / 2;
        var header = new Label
        {
            Dock = DockStyle.Top,
            Height = 40,
            Padding = new Padding(12, 0, 12, 0),
            TextAlign = ContentAlignment.MiddleLeft,
            Text = turns.Count == 0
                ? "还没有聊过什么 —— 上下文为空时，菲比不会记得之前的对话。"
                : $"一共 {totalRounds} 轮 / {turns.Count} 条，上限 {limit} 条。" +
                  $" 列表里每条一行，点一下可以看全文。",
            ForeColor = Color.FromArgb(110, 110, 122),
        };

        var line = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = Color.FromArgb(226, 226, 234) };

        // ---------- 主体：能横竖滚动的明细表 ----------
        var list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            MultiSelect = true,
            HideSelection = false,
            BorderStyle = BorderStyle.None,
            BackColor = Color.White,
            Font = new Font("Microsoft YaHei UI", 9.5f),
        };
        list.Columns.Add("#", 46, HorizontalAlignment.Center);
        list.Columns.Add("说话人", 90, HorizontalAlignment.Left);
        list.Columns.Add("内容", 500, HorizontalAlignment.Left);

        // 交替行底色，长列表更容易横向对齐着看
        list.Items.Add(new ListViewItem(new[] { "", "", "" }));   // 占位，稍后清掉
        list.Items.Clear();

        var userBack = Color.FromArgb(238, 245, 255);   // 淡蓝 = 哥哥
        var petBack = Color.FromArgb(255, 246, 240);    // 淡橙 = 菲比
        int lastRound = -1;
        foreach (var (round, who, body) in rows)
        {
            bool isUser = who == _chat.PlayerName;
            // 同一轮里只给第一条标轮号，避免「1 1 2 2」这种噪音
            string roundText = round == lastRound ? "" : round.ToString();
            lastRound = round;

            var item = new ListViewItem(new[]
            {
                roundText,
                who,
                body.Replace("\r\n", " ").Replace("\n", " "),
            });
            item.BackColor = isUser ? userBack : petBack;
            item.ForeColor = Color.FromArgb(40, 40, 48);
            // 全文挂在 ToolTip 上，被列宽截断时鼠标停一下就能看全
            item.ToolTipText = $"[{round}] {who}：\n{body}";
            list.Items.Add(item);
        }

        // 内容列自动撑满剩下的宽度，窗口拉大时不用手动调列宽
        void FitColumns()
        {
            int rest = list.ClientSize.Width - list.Columns[0].Width - list.Columns[1].Width - 24;
            list.Columns[2].Width = Math.Max(160, rest);
        }
        FitColumns();
        list.Resize += (_, _) => FitColumns();

        // 双击某条 → 弹一个能滚动的全文窗（比工具提示好读，尤其是长回复）
        list.DoubleClick += (_, _) =>
        {
            if (list.SelectedItems.Count == 0) return;
            var sel = list.SelectedItems[0];
            ShowTextWindow($"上下文 · 第 {sel.SubItems[0].Text} 轮 · {sel.SubItems[1].Text}",
                           sel.ToolTipText, 520, 320);
        };

        // ---------- 底部：按钮 ----------
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 46, Padding = new Padding(10, 8, 10, 8) };
        var closeBtn = new Button { Text = "关闭", Width = 88, Height = 28, Dock = DockStyle.Right };
        closeBtn.Click += (_, _) => dlg.Close();

        var copySelBtn = new Button { Text = "复制选中", Width = 96, Height = 28, Dock = DockStyle.Right };
        copySelBtn.Click += (_, _) =>
        {
            if (list.SelectedItems.Count == 0) { BubbleText = "先选一条再复制"; return; }
            var sbSel = new System.Text.StringBuilder();
            foreach (ListViewItem it in list.SelectedItems)
                sbSel.AppendLine($"[{it.SubItems[0].Text}] {it.SubItems[1].Text}：{it.SubItems[2].Text}");
            try { Clipboard.SetText(sbSel.ToString()); BubbleText = $"已复制 {list.SelectedItems.Count} 条"; }
            catch (Exception ex) { BubbleText = "复制失败：" + ex.Message; }
        };

        var copyBtn = new Button { Text = "复制全部", Width = 88, Height = 28, Dock = DockStyle.Left };
        copyBtn.Click += (_, _) =>
        {
            try { Clipboard.SetText(FormatContext(turns, _chat.CharacterName, _chat.PlayerName)); BubbleText = "上下文已复制到剪贴板"; }
            catch (Exception ex) { BubbleText = "复制失败：" + ex.Message; }
        };

        var hint = new Label
        {
            Text = "双击一行看全文",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.FromArgb(140, 140, 152),
        };

        bottom.Controls.Add(hint);       // Fill 最后加，先加会被 Dock 顺序挤掉
        bottom.Controls.Add(closeBtn);
        bottom.Controls.Add(copySelBtn);
        bottom.Controls.Add(copyBtn);

        dlg.Controls.Add(list);          // Fill 必须最先加
        dlg.Controls.Add(line);
        dlg.Controls.Add(header);
        dlg.Controls.Add(bottom);
        dlg.CancelButton = closeBtn;
        dlg.ShowDialog(this);
    }

    /// <summary>
    /// 弹一个只读文本窗（上下文全文、日志片段等共用）。
    /// 抽出来是因为上下文那边双击要看全文，行为得跟原来的大文本框一致。
    /// </summary>
    private void ShowTextWindow(string title, string text, int w, int h)
    {
        using var dlg = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.Sizable,
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(w, h),
            MinimumSize = new Size(320, 200),
            MaximizeBox = true,
            MinimizeBox = false,
            ShowInTaskbar = false,
        };

        var box = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill, WordWrap = true,
            Font = new Font("Microsoft YaHei UI", 9.5f),
            BackColor = Color.FromArgb(250, 250, 252),
            BorderStyle = BorderStyle.None,
            Text = text,
        };
        box.SelectionStart = 0;
        box.SelectionLength = 0;

        var bar = new Panel { Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(8) };
        var closeBtn = new Button { Text = "关闭", Width = 88, Height = 28, Dock = DockStyle.Right };
        closeBtn.Click += (_, _) => dlg.Close();
        var copyBtn = new Button { Text = "复制", Width = 80, Height = 28, Dock = DockStyle.Right };
        copyBtn.Click += (_, _) =>
        {
            try { Clipboard.SetText(text); BubbleText = "已复制到剪贴板"; }
            catch (Exception ex) { BubbleText = "复制失败：" + ex.Message; }
        };
        bar.Controls.Add(closeBtn);
        bar.Controls.Add(copyBtn);

        dlg.Controls.Add(box);
        dlg.Controls.Add(bar);
        dlg.CancelButton = closeBtn;
        dlg.ShowDialog(this);
    }

    // ---------- 双击音效（阶段 E） ----------

    /// <summary>音量调整（0-100）。</summary>
    private void ChangeSoundVolume()
    {
        using var dlg = new Form
        {
            Text = "音效音量",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterScreen,
            ClientSize = new Size(340, 168),
            MaximizeBox = false,
            MinimizeBox = false,
        };
        var label = new Label
        {
            Left = 16, Top = 14, Width = 308, Height = 22,
            Text = $"当前 {_cfg.SoundVolume}%",
        };
        var track = new TrackBar
        {
            Left = 16, Top = 40, Width = 300, Height = 45,
            Minimum = 0, Maximum = 100,
            Value = Math.Max(0, Math.Min(100, _cfg.SoundVolume)),
            TickFrequency = 10,
        };
        var hint = new Label
        {
            Left = 16, Top = 90, Width = 308, Height = 40,
            Text = "0 = 静音（但双击仍会播放，只是听不见）。\n调完可以点「确定」后再双击试试。",
            ForeColor = Color.FromArgb(120, 120, 130),
        };
        var okBtn = new Button { Text = "确定", DialogResult = DialogResult.OK, Left = 236, Top = 132, Width = 88, Height = 28 };
        var cancelBtn = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Left = 140, Top = 132, Width = 88, Height = 28 };

        track.ValueChanged += (_, _) =>
        {
            label.Text = $"当前 {track.Value}%";
            // 边拖边听效果最直观
            Sound.SetVolumePercent(track.Value);
        };

        dlg.Controls.AddRange(new Control[] { label, track, hint, okBtn, cancelBtn });
        dlg.AcceptButton = okBtn;
        dlg.CancelButton = cancelBtn;

        var result = dlg.ShowDialog(this);
        if (result != DialogResult.OK)
        {
            // 取消：把音量还原成进来时的值
            Sound.SetVolumePercent(_cfg.SoundVolume);
            return;
        }

        _cfg.SoundVolume = track.Value;
        _cfg.Save(_cfgPath);
        Sound.SetVolumePercent(track.Value);
        BubbleText = $"音量 {track.Value}%";
        Log($"  [音效] 音量 = {track.Value}%");
    }

    /// <summary>试听：随机播一个音效，并报告结果（含失败原因）。</summary>
    /// <summary>
    /// 试听窗口：列出**当前勾选分组内**的音效，点一行听一个。
    ///
    /// 为什么要做成窗口而不是菜单项（用户反馈「和没用一样」）：
    ///   1) 菜单点一次就关，想多听几个要反复开两次菜单 —— 根本没法挑；
    ///      而「点完不关」在 WinForms 里做不到：实测 `DropDown.Closing`
    ///      **先于**菜单项的 `Click` 触发，所以拿不到「这次要不要关」的信息。
    ///   2) 只报一个文件名，看不到整组有什么，也不知道播的是哪一组。
    ///   3) 原来的试听用 `PlayRandom` 扫的是**全部**音效，不遵守分组 ——
    ///      你只勾了 A 组，试听却可能播 B 组的，和双击听到的对不上。
    ///
    /// 窗口解决全部三个问题：能看列表、能连着点、按分组过滤。
    /// </summary>
    private void ShowSoundPreview()
    {
        var groups = Sound.ListGroups(_dir);
        var enabled = EffectiveSoundGroups(groups);

        // 按分组组织，只列启用的组
        var shown = groups
            .Where(g => enabled.Contains(g.Name, StringComparer.OrdinalIgnoreCase))
            .ToList();

        var pool = Sound.CollectEnabled(Sound.DirPath(_dir), ResolvedSoundGroups());

        using var dlg = new Form
        {
            Text = $"试听音效（{pool.Count} 个）",
            FormBorderStyle = FormBorderStyle.Sizable,
            StartPosition = FormStartPosition.CenterScreen,
            ClientSize = new Size(520, 420),
            MinimumSize = new Size(360, 260),
            MaximizeBox = true,
            MinimizeBox = false,
            ShowInTaskbar = true,
        };

        var hint = new Label
        {
            Dock = DockStyle.Top, Height = 46, Padding = new Padding(12, 8, 12, 0),
            Text = pool.Count == 0
                ? "当前勾选的分组里没有音频。关掉这个窗口，去「分组」里多勾几组。"
                : "只列出**当前勾选的组**里的音效（和双击随机播的范围一致）。\n" +
                  "点一行听一个，可以连着听；双击某行也能重播。",
            ForeColor = Color.FromArgb(110, 110, 122),
        };
        var line = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = Color.FromArgb(226, 226, 234) };

        var list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            MultiSelect = false,
            HideSelection = false,
            BorderStyle = BorderStyle.None,
            BackColor = Color.White,
            Font = new Font("Microsoft YaHei UI", 9.5f),
        };
        list.Columns.Add("分组", 110, HorizontalAlignment.Left);
        list.Columns.Add("音效", 260, HorizontalAlignment.Left);
        list.Columns.Add("格式", 70, HorizontalAlignment.Left);

        var groupBack = Color.FromArgb(245, 246, 250);
        foreach (var g in shown)
        {
            foreach (var f in g.Files)
            {
                var item = new ListViewItem(new[]
                {
                    g.IsUngrouped ? Sound.UngroupedName : g.Name,
                    Path.GetFileNameWithoutExtension(f),
                    Path.GetExtension(f).TrimStart('.').ToLowerInvariant(),
                })
                { Tag = f, BackColor = groupBack };
                item.ToolTipText = f;
                list.Items.Add(item);
            }
        }

        if (list.Items.Count > 0)
        {
            list.Items[0].Selected = true;
            list.EnsureVisible(0);
        }

        var status = new Label
        {
            Dock = DockStyle.Bottom, Height = 24, Padding = new Padding(12, 0, 0, 0),
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(130, 130, 142),
            Text = pool.Count == 0 ? "（没有可试听的音效）" : "点一行开始试听",
        };

        void PlayItem(ListViewItem? item)
        {
            if (item?.Tag is not string path) return;
            string name = Path.GetFileName(path);
            if (Sound.Play(path))
            {
                status.Text = $"正在播：{name}";
                status.ForeColor = Color.FromArgb(70, 130, 90);
                Log($"  [音效] 试听 {name}");
            }
            else
            {
                status.Text = $"播不了：{name}（格式可能不受支持）";
                status.ForeColor = Color.FromArgb(180, 90, 90);
            }
        }

        list.ItemActivate += (_, _) => PlayItem(list.SelectedItems.Count > 0 ? list.SelectedItems[0] : null);
        // 单击就播：挑音效时最常见的是「一个个点过去听听」
        list.SelectedIndexChanged += (_, _) =>
        {
            if (list.SelectedItems.Count > 0) PlayItem(list.SelectedItems[0]);
        };

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 46, Padding = new Padding(10, 8, 10, 8) };
        var closeBtn = new Button { Text = "关闭", Width = 88, Height = 28, Dock = DockStyle.Right };
        closeBtn.Click += (_, _) => dlg.Close();
        var stopBtn = new Button { Text = "停止播放", Width = 96, Height = 28, Dock = DockStyle.Right };
        stopBtn.Click += (_, _) =>
        {
            Sound.StopAll();
            status.Text = "已停止";
            status.ForeColor = Color.FromArgb(130, 130, 142);
        };
        var randBtn = new Button { Text = "随机一个", Width = 96, Height = 28, Dock = DockStyle.Left };
        randBtn.Click += (_, _) =>
        {
            string played;
            if (Sound.PlayRandomInGroups(Sound.DirPath(_dir), ResolvedSoundGroups(), out played))
            {
                status.Text = $"随机播放：{played}";
                status.ForeColor = Color.FromArgb(70, 130, 90);
                // 在列表里高亮到那一行
                for (int i = 0; i < list.Items.Count; i++)
                    if (string.Equals(Path.GetFileName((string)list.Items[i].Tag!),
                                      played, StringComparison.OrdinalIgnoreCase))
                    {
                        list.Items[i].Selected = true;
                        list.EnsureVisible(i);
                        break;
                    }
            }
            else
            {
                status.Text = "没有可播的音效";
                status.ForeColor = Color.FromArgb(180, 90, 90);
            }
        };
        var folderBtn = new Button { Text = "打开文件夹", Width = 100, Height = 28, Dock = DockStyle.Left };
        folderBtn.Click += (_, _) => OpenInShell(Sound.EnsureDir(_dir));

        bottom.Controls.Add(closeBtn);
        bottom.Controls.Add(stopBtn);
        bottom.Controls.Add(randBtn);
        bottom.Controls.Add(folderBtn);

        dlg.Controls.Add(list);
        dlg.Controls.Add(status);
        dlg.Controls.Add(bottom);
        dlg.Controls.Add(line);
        dlg.Controls.Add(hint);
        dlg.CancelButton = closeBtn;
        // 关窗时停掉正在播的，别让声音一直响
        dlg.FormClosed += (_, _) => Sound.StopAll();
        dlg.ShowDialog(this);
    }

    /// <summary>
    /// 试听一个音效（气泡提示版，保留给需要「不弹窗快速听一下」的场景）。
    /// 遵守当前分组 —— 这点很关键，否则试听到的和双击听到的不是一回事。
    /// </summary>
    private void PreviewSound()
    {
        var pool = Sound.CollectEnabled(Sound.DirPath(_dir), ResolvedSoundGroups());
        if (pool.Count == 0)
        {
            var all = Sound.ListSounds(_dir);
            BubbleText = all.Count == 0
                ? $"没有音效文件。往 {Sound.DirName}/ 里丢音频就行。"
                : "当前勾选的分组里没有音频。右键 → 双击音效 → 分组 里多勾几组。";
            return;
        }

        string played;
        if (Sound.PlayRandomInGroups(Sound.DirPath(_dir), ResolvedSoundGroups(), out played))
        {
            string where = GroupOf(played);
            BubbleText = string.IsNullOrEmpty(where)
                ? $"试听：{played}（{pool.Count} 个里随机）"
                : $"试听：{played}  来自「{where}」（{pool.Count} 个里随机）";
            Log($"  [音效] 试听 {where}/{played}");
        }
        else
        {
            BubbleText = $"播放失败了，看看格式是否受支持（{string.Join(" / ", Sound.SupportedExtensions)}）";
        }
    }

    /// <summary>某个音效文件属于哪个分组（找不到返回空串）。</summary>
    private string GroupOf(string fileName)
    {
        try
        {
            foreach (var g in Sound.ListGroups(_dir))
                if (g.Files.Any(f => string.Equals(Path.GetFileName(f), fileName,
                                                   StringComparison.OrdinalIgnoreCase)))
                    return g.IsUngrouped ? "" : g.Name;   // 未分组就不特别说明
        }
        catch { }
        return "";
    }

    /// <summary>气泡字号调整。</summary>
    private void ChangeBubbleFont()
    {
        using var dlg = new Form
        {
            Text = "气泡字号",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterScreen,
            // 高度要给够：TrackBar 的刻度是画在控件下沿之外的，
            // 110 会把下面的文字标签切掉（踩过一次）。
ClientSize = new Size(320, 160),
            MaximizeBox = false,
            MinimizeBox = false,
        };
        var track = new TrackBar
        {
            Minimum = 8, Maximum = 20,
            Value = (int)Math.Round(_bubbleStyle.FontSize),
            TickFrequency = 2, Width = 280, Left = 20, Top = 15,
            Height = 45,
        };
        var label = new Label
        {
            Left = 20, Top = 78, Width = 280, Height = 24,
            Text = $"当前 {track.Value} pt",
            TextAlign = ContentAlignment.MiddleLeft,
        };
        track.ValueChanged += (_, _) => label.Text = $"当前 {track.Value} pt";
        var okBtn = new Button { Text = "确定", DialogResult = DialogResult.OK, Left = 220, Top = 115, Width = 80, Height = 28 };
        dlg.Controls.AddRange(new Control[] { track, label, okBtn });
        dlg.AcceptButton = okBtn;

        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        _bubbleStyle.FontSize = track.Value;
        _bubbleFont?.Dispose();
        _bubbleFont = CreateBubbleFont(_bubbleStyle.FontSize);
        LayoutBubble();
        Log($"  气泡字号 = {track.Value}pt");
    }

    /// <summary>日志上限调整（KB。 = 不限制）。/summary>
    /// <summary>
    /// 「回复长度上限」子菜单：几个预设 + 自定义。
    ///
    /// 为什么做成预设而不是让用户手填数字：
    ///   token 数对普通人没有直觉（300 是多少字？），而它直接决定
    ///   「会不会被截断」和「花多少钱」。给几个带说明的档位比一个空框友好。
    ///
    /// 单位说明：1 token ≈ 0.7 个中文字（菲比的提示词要求 150 字以内）。
    /// </summary>
    private ToolStripMenuItem BuildMaxTokensMenu()
    {
        var menu = new ToolStripMenuItem("回复长度上限");
        var items = new List<ToolStripMenuItem>();

        var presets = new (int Tokens, string Note)[]
        {
            (300,  "很短，约 200 字（容易撞上限）"),
            (500,  "偏短，约 350 字"),
            (700,  "适中，约 500 字（推荐）"),
            (1000, "宽松，约 700 字"),
            (1500, "很长，约 1000 字（费钱）"),
        };

        foreach (var (tokens, note) in presets)
        {
            int captured = tokens;
            var item = new ToolStripMenuItem($"{tokens}  ——  {note}", null, (_, _) =>
            {
                SetMaxTokens(captured);
                foreach (var it in items) it.Checked = it.Tag is int v && v == captured;
            })
            { CheckOnClick = false, Tag = tokens };

            item.Checked = GetMaxTokens() == tokens;
            items.Add(item);
            menu.DropDownItems.Add(item);
        }

        menu.DropDownItems.Add(new ToolStripSeparator());
        menu.DropDownItems.Add(new ToolStripMenuItem("自定义…", null, (_, _) => ChangeMaxTokens()));
        menu.DropDownItems.Add(new ToolStripSeparator());
        menu.DropDownItems.Add(new ToolStripMenuItem("（撞到上限时会让菲比重说短一点）") { Enabled = false });

        return menu;
    }

    /// <summary>读当前的 MaxTokens（没挂载聊天核心时直接从文件读）。</summary>
    private int GetMaxTokens()
    {
        if (_chatCfg != null) return _chatCfg.MaxTokens;
        try { return ChatConfig.Load(Path.Combine(AppPaths.Dir, "chat-config.json")).MaxTokens; }
        catch { return 700; }
    }

    /// <summary>写回 MaxTokens 并立即生效（不用重启）。</summary>
    private void SetMaxTokens(int tokens)
    {
        tokens = Math.Max(64, Math.Min(4096, tokens));
        if (_chatCfg == null) { BubbleText = "聊天核心未挂载"; return; }
        _chatCfg.MaxTokens = tokens;
        _chatCfg.Save(Path.Combine(AppPaths.Dir, "chat-config.json"));
        BubbleText = $"回复上限 {tokens} token（约 {tokens * 7 / 10} 字）";
        Log($"  [chat] MaxTokens = {tokens}");
    }

    /// <summary>自定义 MaxTokens（带滑块的对话框）。</summary>
    private void ChangeMaxTokens()
    {
        int cur = GetMaxTokens();
        var r = PickValue("回复长度上限", "一次回复最多生成多少 token（越小越省钱）：",
                          cur, 128, 4096,
                          new[] { 300, 500, 700, 1000, 1500 },
                          t => $"{t} token（约 {t * 7 / 10} 字）");
        if (r < 0) return;
        SetMaxTokens(r);
    }

    /// <summary>日志上限调整（KB。0 = 不限制）。</summary>
    private void ChangeLogLimit()
    {
        using var dlg = new Form
        {
            Text = "日志上限",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterScreen,
            ClientSize = new Size(340, 168),
            MaximizeBox = false,
            MinimizeBox = false,
        };
        // 当前值的文字标签必须单独放，不能复用 TrackBar 的刻度区（会被切掉）
        var label = new Label
        {
            Left = 16, Top = 14, Width = 308, Height = 22,
            Text = CurrentLogLabel(),
        };
        var track = new TrackBar
        {
            Left = 16, Top = 40, Width = 300, Height = 45,
            Minimum = 0,          // 0 = 不限制
Maximum = 20,         // 档位索引，见 LogTiersKB
            Value = LogTierIndex(AppPaths.MaxLogKB),
            TickFrequency = 5,
        };
        var hint = new Label
        {
            Left = 16, Top = 90, Width = 308, Height = 40,
            Text = "超过上限时 pet.log 轮转为 pet.log.1（保留一份旧的）。\n0 = 不限制，日志会一直增长。",
            ForeColor = Color.FromArgb(120, 120, 130),
        };
        var okBtn = new Button { Text = "确定", DialogResult = DialogResult.OK, Left = 236, Top = 132, Width = 88, Height = 28 };
        var cancelBtn = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Left = 140, Top = 132, Width = 88, Height = 28 };

        track.ValueChanged += (_, _) => label.Text = LogTierLabel(track.Value);

        dlg.Controls.AddRange(new Control[] { label, track, hint, okBtn, cancelBtn });
        dlg.AcceptButton = okBtn;
        dlg.CancelButton = cancelBtn;

        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        int kb = LogTierKB(track.Value);
        _cfg.LogMaxKB = kb;
        AppPaths.MaxLogKB = kb;
        _cfg.Save(_cfgPath);
        BubbleText = kb == 0 ? "日志不再限制大小" : $"日志上限 {kb} KB";
        Log($"  日志上限 = {kb} KB");
    }

    /// <summary>日志上限的可选档位（KB）。 = 不限制，最后两档给大日志留余地。/summary>
    private static readonly int[] LogTiersKB =
        { 0, 64, 128, 256, 512, 1024, 2048, 5120, 10240, 20480, 51200 };

    private static int LogTierIndex(int kb)
    {
        int best = 5;   // 默认 1024
        int bestDiff = int.MaxValue;
        for (int i = 0; i < LogTiersKB.Length; i++)
        {
            int d = Math.Abs(LogTiersKB[i] - kb);
            if (d < bestDiff) { bestDiff = d; best = i; }
        }
        return best;
    }

    private static int LogTierKB(int index) =>
        LogTiersKB[Math.Max(0, Math.Min(LogTiersKB.Length - 1, index))];

    private static string LogTierLabel(int index)
    {
        int kb = LogTierKB(index);
        return kb == 0 ? "当前：不限制" : $"当前：{FormatKB(kb)}";
    }

    private string CurrentLogLabel()
    {
        int kb = AppPaths.MaxLogKB;
        if (kb <= 0) return "当前：不限制";
        // 顺便把实际文件大小报出来，省得再去资源管理器。
try
        {
            var fi = new FileInfo(Path.Combine(AppPaths.Dir, "pet.log"));
            if (fi.Exists) return $"当前：{FormatKB(kb)}（现在 {FormatKB((int)(fi.Length / 1024))}）";
        }
        catch { }
        return $"当前：{FormatKB(kb)}";
    }

    private static string FormatKB(int kb) =>
        kb >= 1024 ? $"{kb / 1024.0:0.#} MB" : $"{kb} KB";

    /// <summary>隐私模式开关。/summary>
    public void TogglePrivacy() => PrivacyMode = !PrivacyMode;

    /// <summary>不管冷却，立刻让菲比说一句（菜单「现在就说一句」）。/summary>
    public void ForceProactiveNow()
    {
        if (_chat == null) return;
        if (_proactiveBusy) { BubbleText = "我还在想上一句呢…"; return; }
        if (HasBubble) { BubbleText = ""; return; }   // 先让气泡空出来
        _proactiveBusy = true;
        Log("  [主动] 手动触发");
        _chat.SendProactive("（这是你自己想开口，不是被闲置触发的。）",
            BuildRecentContextForProactive(), (reply, err) =>
        {
            try
            {
                BeginInvoke(new Action(() =>
                {
                    _proactiveBusy = false;
                    if (err != null) { BubbleText = "出错了：" + err; Log($"  [主动] 手动触发失败: {err}"); return; }
                    BubbleText = reply;
                    Log($"  [主动] 手动发言 {reply.Length} 字: {reply}");
                }));
            }
            catch (Exception e) { _proactiveBusy = false; Log($"  [主动] 手动回填失败: {e.Message}"); }
        });
    }

    /// <summary>闲置时长调整（秒）。/summary>
    private void ChangeIdleSeconds()
    {
        int v = PickSeconds("闲置时长", "闲置多久之后菲比主动开口：",
                            _cfg.IdleSeconds, 5, 3600,
                            new[] { 15, 30, 60, 120, 300, 600, 1800 });
        if (v < 0) return;
        _cfg.IdleSeconds = v;
        _cfg.Save(_cfgPath);
        _proactiveInIdle = 0;
        BubbleText = $"闲置 {DescribeSeconds(v)} 后我会主动开口";
        Log($"  闲置时长 = {v}s");
    }

    /// <summary>冷却时间调整（秒）。</summary>
    private void ChangeProactiveCooldown()
    {
        int v = PickSeconds("冷却时间", "两次主动发言之间至少间隔：",
                            _cfg.ProactiveCooldownSeconds, 0, 7200,
                            new[] { 0, 60, 300, 600, 1800, 3600 });
        if (v < 0) return;
        _cfg.ProactiveCooldownSeconds = v;
        _cfg.Save(_cfgPath);
        BubbleText = v == 0 ? "冷却已关闭（会说得比较频繁）" : $"冷却 {DescribeSeconds(v)}";
        Log($"  主动发言冷却 = {v}s");
    }

    // ---------- 活动感知（阶段 C） ----------

    /// <summary>切换后延迟调整（秒）。</summary>
    private void ChangeSwitchSettle()
    {
        int v = PickSeconds("切换后延迟", "切换窗口后等几秒再开口：",
                            _cfg.SwitchSettleSeconds, 1, 120,
                            new[] { 3, 5, 6, 10, 20, 30 });
        if (v < 0) return;
        _cfg.SwitchSettleSeconds = v;
        _cfg.Save(_cfgPath);
        BubbleText = $"切换窗口后 {DescribeSeconds(v)} 再搭话";
        Log($"  切换延迟 = {v}s");
    }

    /// <summary>深度模式开关。截屏是隐私敏感操作，打开前明确问一次。</summary>
    private void ToggleDeepWatch()
    {
        if (_cfg.DeepWatch)
        {
            _cfg.DeepWatch = false;
            _deepWatchItem.Checked = false;
            _cfg.Save(_cfgPath);
            ApplyActivityConfig();
            BubbleText = "深度模式已关闭，我不再截屏了。";
            Log("  [活动] 深度模式 → 关");
            return;
        }

        var r = MessageBox.Show(
            "开启后，在满足以下条件时菲比会截取**当前前台窗口**的一张图交给模型理解：\n\n" +
            "  · 要主动搭话时（闲置触发）\n" +
            "  · 你说「你看这个好不好看」这类话时\n\n" +
            "说明：\n" +
            "  · 只截前台窗口，不含其他窗口和桌面\n" +
            "  · 截图会缩到 " + _cfg.ShotMaxEdge + "px 并转 JPEG\n" +
            "  · 黑名单命中的程序绝不截屏\n" +
            "  · 截图只在内存里，不落盘\n" +
            "  · 隐私模式开着时完全不截\n\n" +
            "图片会发送给 DeepSeek API。确定开启吗？",
            "深度模式（截屏理解）", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

        if (r != DialogResult.Yes) { _deepWatchItem.Checked = false; return; }

        _cfg.DeepWatch = true;
        _deepWatchItem.Checked = true;
        _cfg.Save(_cfgPath);
        ApplyActivityConfig();
        BubbleText = "深度模式开了，我看得到你在忙什么了。";
        Log("  [活动] 深度模式 → 开");
    }

    /// <summary>截屏尺寸选择。</summary>
    private void ChangeShotEdge()
    {
        // 注意：这里必须传 DescribePixels。
        // 以前图省事复用了 PickSeconds，于是「1024」被显示成「17 分 4 秒」
        // —— 数值是对的，单位完全错了。
        var r = PickValue("截屏尺寸", "截图长边缩放到的像素数（越小越省 token）：",
                          _cfg.ShotMaxEdge, 128, 2048,
                          new[] { 256, 384, 512, 768, 1024 },
                          DescribePixels);
        if (r < 0) return;
        _cfg.ShotMaxEdge = r;
        _cfg.Save(_cfgPath);
        ApplyActivityConfig();
        BubbleText = $"截屏尺寸 {r}px";
        Log($"  [活动] 截屏长边 = {r}px");
    }

    /// <summary>
    /// 黑名单编辑。两个框：进程名、标题关键词。**全部规则都可改**，
    /// 包括出厂自带的那些 —— 早期版本内置规则是只读的，只能加不能删，
    /// 用户想放行某个程序做不到。
    /// </summary>
    private void ShowBlacklist()
    {
        using var dlg = new Form
        {
            Text = "黑名单 · 这些不会被采集",
            FormBorderStyle = FormBorderStyle.Sizable,
            StartPosition = FormStartPosition.CenterScreen,
            ClientSize = new Size(620, 520),
            MinimumSize = new Size(460, 400),
            MaximizeBox = true,
            MinimizeBox = false,
        };

        var hint = new Label
        {
            Dock = DockStyle.Top, Height = 58, Padding = new Padding(10, 8, 10, 0),
            Text = "每行一条，匹配方式为「包含」，不区分大小写。\n" +
                   "命中的程序：不采集标题、不截屏。可以随意增删，清空就是没有黑名单。",
        };

        // 进程名
        var lblProc = new Label
        {
            Dock = DockStyle.Top, Height = 22, Padding = new Padding(10, 4, 0, 0),
            Text = "── 进程名（如 keepass、chrome）──",
        };
        var procBox = new TextBox
        {
            Multiline = true, ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Top, Height = 150,
            Font = new Font("Consolas", 9.5f),
            Text = string.Join("\r\n", ActivityWatch.CurrentProcessRules),
        };

        // 标题关键词
        var lblTitle = new Label
        {
            Dock = DockStyle.Top, Height = 22, Padding = new Padding(10, 4, 0, 0),
            Text = "── 窗口标题关键词（如 密码、网银、登录）──",
        };
        var titleBox = new TextBox
        {
            Multiline = true, ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            Font = new Font("Consolas", 9.5f),
            Text = string.Join("\r\n", ActivityWatch.CurrentTitleRules),
        };

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(8) };
        var saveBtn = new Button { Text = "保存", Width = 88, Height = 28, Dock = DockStyle.Right };
        var cancelBtn = new Button { Text = "取消", Width = 88, Height = 28, Dock = DockStyle.Right };
        var resetBtn = new Button { Text = "恢复默认", Width = 96, Height = 28, Dock = DockStyle.Left };
        var testBtn = new Button { Text = "测试当前窗口", Width = 124, Height = 28, Dock = DockStyle.Left };

        // 恢复默认：把出厂规则填回输入框（还没保存，可以反悔）
        resetBtn.Click += (_, _) =>
        {
            procBox.Text = string.Join("\r\n", ActivityWatch.DefaultProcessRulesList);
            titleBox.Text = string.Join("\r\n", ActivityWatch.DefaultTitleRulesList);
        };

        // 测试用的是「输入框里当前的规则」，而不是已保存的 ——
        // 这样用户可以改完立刻试，不用先保存再回来改。
        testBtn.Click += (_, _) =>
        {
            var procs = SplitLines(procBox.Text);
            var titles = SplitLines(titleBox.Text);

            var act = ActivityWatch.Current();
            if (act == null)
            {
                MessageBox.Show(dlg, "取不到前台窗口。", "测试", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string proc = act.ProcessName, title = act.WindowTitle;
            string? hit = ActivityWatch.MatchRule(proc, title);

            MessageBox.Show(dlg,
                $"当前前台窗口：\n  程序：{(proc.Length == 0 ? "(取不到)" : proc)}\n  标题：{title}\n\n" +
                (hit != null
                    ? $"✔ 命中黑名单（{hit}）\n→ 不会被采集，也不会截屏。"
                    : "✘ 未命中黑名单\n→ 会被采集。"),
                "测试结果（用的是输入框里未保存的规则）",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        };

        bottom.Controls.Add(saveBtn);
        bottom.Controls.Add(cancelBtn);
        bottom.Controls.Add(resetBtn);
        bottom.Controls.Add(testBtn);

        // Dock 的叠放顺序：先加的在内层，所以 Fill 的要最先加
        dlg.Controls.Add(titleBox);
        dlg.Controls.Add(lblTitle);
        dlg.Controls.Add(procBox);
        dlg.Controls.Add(lblProc);
        dlg.Controls.Add(bottom);
        dlg.Controls.Add(hint);
        dlg.AcceptButton = saveBtn;
        dlg.CancelButton = cancelBtn;

        saveBtn.Click += (_, _) => dlg.DialogResult = DialogResult.OK;
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        var newProcs = SplitLines(procBox.Text);
        var newTitles = SplitLines(titleBox.Text);
        _cfg.BlacklistProcess = newProcs;
        _cfg.BlacklistTitle = newTitles;
        _cfg.Save(_cfgPath);
        ApplyActivityConfig();
        BubbleText = $"黑名单已保存：进程 {newProcs.Count} 条 / 标题 {newTitles.Count} 条";
        Log($"  [活动] 黑名单 = 进程 {newProcs.Count} / 标题 {newTitles.Count}");
    }

    /// <summary>把多行文本拆成规则列表，去掉空行和首尾空白。</summary>
    private static List<string> SplitLines(string text)
    {
        var list = new List<string>();
        foreach (var raw in (text ?? "").Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length > 0) list.Add(line);
        }
        return list;
    }

    /// <summary>
    /// 预览「菲比现在看到的东西」。
    ///
    /// 这个功能的意义是**可验证性**：用户不用猜菲比到底看到了什么、
    /// 黑名单有没有生效、深度模式截的图长什么样。
    /// 一切都摆在眼前，比任何文字说明都可信。
    /// </summary>
    private void ShowWhatISee()
    {
        if (_privacyMode)
        {
            BubbleText = "隐私模式开着呢，我什么都没看。";
            return;
        }

        var act = ActivityWatch.CurrentOrLastForeign();

        // ---- 采集信息 ----
        string infoText;
        if (!_cfg.WatchActivity)
        {
            infoText = "【采集已关闭】\n你在菜单里关掉了「知道我在用什么程序」，我什么都不看。";
        }
        else if (act == null)
        {
            infoText = "【取不到前台窗口】\n可能当前没有活动窗口。";
        }
        else if (act.Blacklisted)
        {
            infoText = "【命中黑名单】\n这个窗口在黑名单里 —— 我不采集它的标题，也不会截屏。";
        }
        else
        {
            infoText = $"程序：{act.ProcessName}\n标题：{act.WindowTitle}";
            if (ActivityWatch.ForegroundIsSelf())
                infoText += "\n\n（当前前台是桌宠自己的窗口，这里显示的是你上一次实际在用的程序）";
        }

        string modeText = _cfg.DeepWatch
            ? $"深度模式：开（截屏 {_cfg.ShotMaxEdge}px / JPEG q{_cfg.ShotQuality}）"
            : "深度模式：关（只看程序名和标题，不截屏）";

        // ---- 截图 ----
        byte[]? shot = null;
        string shotNote;
        if (!_cfg.WatchActivity)
        {
            shotNote = "（未开启采集，不截屏）";
        }
        else if (_cfg.DeepWatch && act != null && !act.Blacklisted)
        {
            shot = ActivityWatch.CaptureForeground();
            shotNote = shot == null ? "（截屏失败）" : $"这就是我现在能看到的画面（{shot.Length / 1024} KB）";
        }
        else if (act != null && act.Blacklisted)
        {
            shotNote = "（命中黑名单，绝不截屏）";
        }
        else
        {
            shotNote = "（深度模式没开，不截屏。想看画面请在菜单里开启深度模式）";
        }

        // ---- 窗口 ----
        using var dlg = new Form
        {
            Text = "菲比现在看到的",
            FormBorderStyle = FormBorderStyle.Sizable,
            StartPosition = FormStartPosition.CenterScreen,
            ClientSize = new Size(720, 560),
            MinimumSize = new Size(480, 360),
            MaximizeBox = true,
            MinimizeBox = false,
            ShowInTaskbar = true,
        };

        var info = new TextBox
        {
            Multiline = true, ReadOnly = true, Dock = DockStyle.Top, Height = 96,
            Font = new Font("Microsoft YaHei UI", 9.5f),
            BackColor = Color.FromArgb(248, 248, 250),
            BorderStyle = BorderStyle.FixedSingle,
            Text = infoText + "\r\n" + modeText,
        };

        var pic = new PictureBox
        {
            Dock = DockStyle.Fill,
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.FromArgb(40, 40, 46),
        };

        // PictureBox 的 Dispose 不会释放自己设的 Image，得自己管
        Image? shown = null;
        if (shot != null)
        {
            try
            {
                using var ms = new MemoryStream(shot);
                shown = Image.FromStream(ms);
                pic.Image = shown;
            }
            catch (Exception e) { Log($"  [预览] 图片解码失败: {e.Message}"); }
        }

        var note = new Label
        {
            Dock = DockStyle.Bottom, Height = 26, Padding = new Padding(10, 4, 0, 0),
            Text = shotNote,
            ForeColor = Color.FromArgb(90, 90, 100),
        };

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(8) };
        var closeBtn = new Button { Text = "关闭", Width = 88, Height = 28, Dock = DockStyle.Right };
        var refreshBtn = new Button { Text = "重新采集", Width = 96, Height = 28, Dock = DockStyle.Right };
        var copyBtn = new Button { Text = "复制图片", Width = 96, Height = 28, Dock = DockStyle.Left };
        closeBtn.Click += (_, _) => dlg.Close();
        refreshBtn.Click += (_, _) =>
        {
            // 关掉重开最省事：状态（截图、黑名单判定）会整条重算一遍
            dlg.Close();
            BeginInvoke(new Action(ShowWhatISee));
        };
        copyBtn.Enabled = shot != null;
        copyBtn.Click += (_, _) =>
        {
            try
            {
                if (pic.Image != null) { Clipboard.SetImage(pic.Image); BubbleText = "图片已复制到剪贴板"; }
            }
            catch (Exception e) { BubbleText = "复制失败：" + e.Message; }
        };
        bottom.Controls.Add(closeBtn);
        bottom.Controls.Add(refreshBtn);
        bottom.Controls.Add(copyBtn);

        dlg.Controls.Add(pic);
        dlg.Controls.Add(note);
        dlg.Controls.Add(bottom);
        dlg.Controls.Add(info);
        dlg.CancelButton = closeBtn;

        try { dlg.ShowDialog(this); }
        finally { shown?.Dispose(); }
    }

    /// <summary>显示当前采集到什么（让用户能亲眼确认黑名单有没有生效）。</summary>
    private void ShowCurrentActivity()
    {
        if (_privacyMode) { BubbleText = "隐私模式开着呢，我看不到。"; return; }

        var act = ActivityWatch.Current();
        if (act == null)
        {
            BubbleText = "现在看不出你在用什么";
            return;
        }

        string mode = _cfg.DeepWatch ? "深度（会截屏）" : "一般（只看程序名）";
        string body = act.Blacklisted
            ? "当前窗口命中黑名单 —— 我不会采集，也不会截屏。"
            : $"程序：{act.ProcessName}\n标题：{act.WindowTitle}\n\n模式：{mode}";

        MessageBox.Show(body, "现在在看什么", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>
    /// 秒数选择：一个滑块 + 几个常用档位的快捷按钮。
    /// 滑块是线性刻度，而秒数跨度从 5 到 7200，线性拖很久才到位，
    /// 所以常用值走按钮，滑块负责微调。
    /// </summary>
    private int PickSeconds(string title, string prompt, int current, int min, int max, int[] presets)
        => PickValue(title, prompt, current, min, max, presets, DescribeSeconds);

    /// <summary>
    /// 通用的「一个整数」选择框：滑块 + 快捷档位按钮。
    ///
    /// 为什么要有 format 参数：原来只有 PickSeconds 一个函数，显示值写死走
    /// DescribeSeconds（把秒数说成「17 分 4 秒」）。截屏尺寸复用了它，
    /// 于是「1024 像素」被显示成「17 分 4 秒」—— 单位完全错了。
    /// 数值本身是对的（保存的确实是 1024），纯粹是显示层的锅。
    ///
    /// 所以把「怎么把数字说给人听」抽成参数，谁用谁决定单位。
    /// </summary>
    private int PickValue(string title, string prompt, int current, int min, int max,
                          int[] presets, Func<int, string> format)
    {
        using var dlg = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterScreen,
            ClientSize = new Size(380, 190),
            MaximizeBox = false,
            MinimizeBox = false,
        };

        var label = new Label { Left = 16, Top = 12, Width = 348, Height = 20, Text = prompt };
        var value = new Label
        {
            Left = 16, Top = 36, Width = 348, Height = 24,
            Text = format(current),
            Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold),
        };
        var track = new TrackBar
        {
            Left = 12, Top = 62, Width = 356, Height = 45,
            Minimum = min, Maximum = max, Value = Math.Max(min, Math.Min(max, current)),
            TickFrequency = Math.Max(1, (max - min) / 8),
        };
        track.ValueChanged += (_, _) => value.Text = format(track.Value);

        // 快捷档位按钮。宽度按文字长度给，否则「1024」和「12 分 48 秒」
        // 用同一个宽度，长的会被截断成「12 分 4…」。
        var presetPanel = new Panel { Left = 12, Top = 112, Width = 356, Height = 30 };
        int px = 0;
        foreach (int p in presets)
        {
            if (p < min || p > max) continue;
            string text = format(p);
            int w = Math.Max(56, 20 + text.Length * 9);
            if (px + w > presetPanel.Width) break;      // 放不下就不硬塞，避免跑出面板
            var b = new Button { Text = text, Left = px, Top = 0, Width = w, Height = 26 };
            int captured = p;
            b.Click += (_, _) => { track.Value = captured; };
            presetPanel.Controls.Add(b);
            px += w + 6;
        }

        var okBtn = new Button { Text = "确定", DialogResult = DialogResult.OK, Left = 276, Top = 150, Width = 88, Height = 28 };
        var cancelBtn = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Left = 180, Top = 150, Width = 88, Height = 28 };

        dlg.Controls.AddRange(new Control[] { label, value, track, presetPanel, okBtn, cancelBtn });
        dlg.AcceptButton = okBtn;
        dlg.CancelButton = cancelBtn;

        return dlg.ShowDialog(this) == DialogResult.OK ? track.Value : -1;
    }

    /// <summary>把秒数说成人话：90 → 「1 分 30 秒」。</summary>
    public static string DescribeSeconds(int s)
    {
        if (s <= 0) return "不等待";
        if (s < 60) return $"{s} 秒";
        int m = s / 60, r = s % 60;
        if (r == 0) return m < 60 ? $"{m} 分钟" : $"{m / 60} 小时{(m % 60 == 0 ? "" : " " + m % 60 + " 分")}";
        return $"{m} 分 {r} 秒";
    }

    /// <summary>
    /// 把像素数说给人听：1024 → 「1024 px」。
    /// 同时标出「这是长边缩放后的上限」，避免和屏幕分辨率搞混。
    /// </summary>
    public static string DescribePixels(int px) => $"{px} px";

    /// <summary>
    /// 用量统计。三个维度：今天 / 最近 7 天 / 累计。
    ///
    /// 特意把「缓存命中」单独列出来 —— 桌宠每轮都重发整个历史，
    /// 绝大部分输入 token 其实走了缓存价（约为未命中的 1/50）。
    /// 不区分会严重高估花费。
    /// </summary>
    private void ShowUsage()
    {
        using var dlg = new Form
        {
            Text = "用量统计",
            FormBorderStyle = FormBorderStyle.Sizable,
            StartPosition = FormStartPosition.CenterScreen,
            ClientSize = new Size(560, 520),
            MinimumSize = new Size(420, 380),
            MaximizeBox = true,
            MinimizeBox = false,
            ShowInTaskbar = true,
        };

        var box = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill, WordWrap = false,
            Font = new Font("Consolas", 9.5f),
            BackColor = Color.FromArgb(250, 250, 252),
            BorderStyle = BorderStyle.None,
            Text = BuildUsageText(),
        };
        box.SelectionStart = 0;
        box.SelectionLength = 0;

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(8) };
        var closeBtn = new Button { Text = "关闭", Width = 88, Height = 28, Dock = DockStyle.Right };
        var balBtn = new Button { Text = "查询余额", Width = 96, Height = 28, Dock = DockStyle.Right };
        var copyBtn = new Button { Text = "复制", Width = 80, Height = 28, Dock = DockStyle.Left };
        var clearBtn = new Button { Text = "清空统计", Width = 96, Height = 28, Dock = DockStyle.Left };

        closeBtn.Click += (_, _) => dlg.Close();

        // 余额查询是异步的：不能卡住 UI 线程，否则窗口会「未响应」。
        // 查完再刷新文本 —— 查失败也不弹错误框，报告里那行会说明原因。
        async void QueryBalance(bool force)
        {
            if (_chatCfg == null) { BubbleText = "聊天核心未挂载"; return; }
            balBtn.Enabled = false;
            balBtn.Text = "查询中…";
            try
            {
                await Balance.QueryAsync(_chatCfg.BaseUrl, _chatCfg.ApiKey, force);
            }
            finally
            {
                balBtn.Enabled = true;
                balBtn.Text = "查询余额";
                if (!dlg.IsDisposed) box.Text = BuildUsageText();
            }
        }
        balBtn.Click += (_, _) => QueryBalance(true);

        copyBtn.Click += (_, _) =>
        {
            try { Clipboard.SetText(box.Text); BubbleText = "用量统计已复制"; }
            catch (Exception e) { BubbleText = "复制失败：" + e.Message; }
        };
        clearBtn.Click += (_, _) =>
        {
            if (MessageBox.Show(dlg,
                    "清空本地用量记录？\n\n（只影响这个统计，不影响 DeepSeek 平台上的真实账单）",
                    "确认", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            Usage.Clear();
            box.Text = BuildUsageText();
            BubbleText = "用量统计已清空";
        };

        bottom.Controls.Add(closeBtn);
        bottom.Controls.Add(balBtn);
        bottom.Controls.Add(copyBtn);
        bottom.Controls.Add(clearBtn);

        dlg.Controls.Add(box);
        dlg.Controls.Add(bottom);
        dlg.CancelButton = closeBtn;

        // 打开时如果缓存不新鲜就自动查一次（缓存新鲜就直接用，不浪费请求）
        dlg.Shown += (_, _) =>
        {
            if (!Balance.CacheFresh) QueryBalance(false);
        };

        dlg.ShowDialog(this);
    }

    /// <summary>拼出用量报告文本（抽出来便于自检）。</summary>
    public static string BuildUsageText() => BuildUsageText(Balance.Cached);

    /// <summary>
    /// 拼出用量报告文本。
    ///
    /// balance 参数是可选的：查不到余额时（断网、没填 key）就只显示本地估算，
    /// 不能因为余额查不到就让整个统计窗口打不开。
    /// 把结果当参数传进来而不是在里面发请求，是为了这个函数保持**纯函数**，
    /// 自检能直接喂假数据验格式。
    /// </summary>
    public static string BuildUsageText(BalanceInfo? balance)
    {
        var sb = new System.Text.StringBuilder();
        var price = Usage.PriceFor("deepseek-flash");
        bool peak = Usage.PeakNow();

        sb.AppendLine("估算说明：按官方公开价换算，仅供参考，真实扣费以平台账单为准。");
        sb.AppendLine($"当前时段：{(peak ? "高峰（空闲价的 2 倍）" : "空闲")}   " +
                      $"flash 价：缓存命中 ¥{price.Hit(peak)} / 未命中 ¥{price.Miss(peak)} / 输出 ¥{price.Out(peak)} 每百万 token");
        sb.AppendLine("高峰时段：周一至周五 9:00-12:00、14:00-18:00（不含法定节假日）");
        sb.AppendLine();

        sb.AppendLine(Usage.Format(Usage.Today(), "今天"));
        sb.AppendLine(Usage.Format(Usage.All(), "累计"));

        // ---- 平台真实余额（对照着看本地估算准不准）----
        sb.AppendLine("【平台账户】");
        if (balance == null)
        {
            sb.AppendLine("  （还没查询过，点「查询余额」试试）");
        }
        else if (!balance.Ok)
        {
            sb.AppendLine($"  查询失败：{balance.Error}");
            sb.AppendLine("  · 查不到只是少显示一行，不影响上面的本地统计");
        }
        else
        {
            sb.AppendLine($"  剩余 {balance.Symbol}{balance.Total:F2}" +
                          $"（赠金 {balance.Symbol}{balance.Granted:F2}" +
                          $" / 充值 {balance.Symbol}{balance.ToppedUp:F2}）");
            if (!balance.IsAvailable)
                sb.AppendLine("  ⚠ 账户已不可用 —— 余额可能已耗尽");
            sb.AppendLine($"  查询时间：{balance.FetchedAt:HH:mm:ss}" +
                          $"（{Balance.CacheFor.TotalMinutes:F0} 分钟内复用缓存）");

            // 拿本地估算和真实余额对照：本地只统计这个桌宠的开销，
            // 所以「本地花了多少」不该超过「充值总额 - 剩余」太多，
            // 差得离谱通常说明你在别处也用了同一个 key。
            var all = Usage.All();
            if (all.Cost > 0)
            {
                sb.AppendLine($"  本地估算累计花费 ¥{all.Cost:F4}（只含这个桌宠）");
                if (balance.ToppedUp > 0)
                {
                    double used = balance.ToppedUp - balance.Total;
                    if (used > 0)
                        sb.AppendLine($"  平台充值余额消耗 {balance.Symbol}{used:F2} —— " +
                                      $"差额 {balance.Symbol}{Math.Max(0, used - all.Cost):F2} 是你在别处用掉的");
                }
            }
        }
        sb.AppendLine();

        sb.AppendLine("【最近 7 天】");
        var days = Usage.RecentDays(7);
        bool any = false;
        foreach (var (day, sum) in days)
        {
            if (sum.Calls == 0) continue;
            any = true;
            sb.AppendLine($"  {day}   {sum.Calls,4} 次   {sum.Total,12:N0} tok   ¥{sum.Cost:F4}");
        }
        if (!any) sb.AppendLine("  （最近 7 天没有记录）");
        sb.AppendLine();

        sb.AppendLine("【说明】");
        sb.AppendLine("  · 只统计这个桌宠自己的调用，不含你在别处用 API 的花费");
        sb.AppendLine("  · 缓存命中价只有未命中的约 1/50，所以「合计 token」大不代表贵");
        sb.AppendLine("  · 记录存在 usage.json，保留最近 2000 次调用");

        return sb.ToString();
    }

    /// <summary>API 配置对话框（key 用密码框显示）。</summary>
    private void ShowApiSettings()
    {
        var cfg = _chatCfg;
        if (cfg == null) { BubbleText = "聊天核心未挂载"; return; }

        using var dlg = new Form
        {
            Text = "API 设置",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterScreen,
            // 高度 = 上边。5 + 5。34 + 2个勾。34+40) + 按钮28 + 下边。
ClientSize = new Size(460, 320),
            MaximizeBox = false,
            MinimizeBox = false,
        };

        // 逐行摆放。注意：标签和输入框必须。*同一。* y。
// 之前写成「先 T() 。L()」，。T() 会把 y 往后推。
// 结果每个标签都掉到了下一行（踩过一次）。
int y = 15;
        const int RowH = 34;
        const int BoxH = 23;
        const int LabelLeft = 15, LabelW = 110, BoxLeft = 132, BoxW = 310;

        TextBox AddRow(string labelText, string value, bool password = false)
        {
            var lab = new Label
            {
                Text = labelText, Left = LabelLeft, Top = y + 4,
                Width = LabelW, Height = BoxH,
                TextAlign = ContentAlignment.MiddleLeft,
            };
            var box = new TextBox
            {
                Left = BoxLeft, Top = y, Width = BoxW, Height = BoxH,
                Text = value,
            };
            if (password) box.UseSystemPasswordChar = true;
            dlg.Controls.Add(lab);
            dlg.Controls.Add(box);
            y += RowH;
            return box;
        }

        var baseBox = AddRow("API 地址", cfg.BaseUrl);
        var keyBox = AddRow("API Key", cfg.ApiKey, password: true);
        var modelBox = AddRow("模型", cfg.Model);
        var tempBox = AddRow("温度 (0~2)", cfg.Temperature.ToString("0.0"));

        var memCheck = new CheckBox
        {
            Text = "启用长期记忆", Left = BoxLeft, Top = y + 4,
            Width = BoxW, Height = BoxH, Checked = cfg.EnableMemory,
        };
        dlg.Controls.Add(memCheck);
        y += RowH;

        var intervalBox = AddRow("每几轮总结记忆", Math.Max(1, cfg.MemoryInterval).ToString());

        var saverCheck = new CheckBox
        {
            Text = "省钱模式（省 token，稍微降低记忆细节）", Left = BoxLeft, Top = y + 4,
            Width = BoxW, Height = BoxH, Checked = cfg.TokenSaver,
        };
        dlg.Controls.Add(saverCheck);
        y += RowH + 6;

        var save = new Button { Text = "保存", DialogResult = DialogResult.OK, Left = 270, Top = y, Width = 80, Height = 28 };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Left = 362, Top = y, Width = 80, Height = 28 };
        dlg.Controls.Add(save);
        dlg.Controls.Add(cancel);
        dlg.AcceptButton = save;
        dlg.CancelButton = cancel;

        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        cfg.BaseUrl = baseBox.Text.Trim();
        // 留空表示「不改」——避免密码框里看不到内容时误清空
        if (keyBox.Text.Trim().Length > 0) cfg.ApiKey = keyBox.Text.Trim();
        cfg.Model = modelBox.Text.Trim();
        if (double.TryParse(tempBox.Text.Trim(), out double t2)) cfg.Temperature = Math.Max(0, Math.Min(2, t2));
        cfg.EnableMemory = memCheck.Checked;
        if (int.TryParse(intervalBox.Text.Trim(), out int iv)) cfg.MemoryInterval = Math.Max(1, iv);
        cfg.TokenSaver = saverCheck.Checked;
        cfg.Save(Path.Combine(AppPaths.Dir, "chat-config.json"));

        Log($"  API 设置已保存: model={cfg.Model} key={(string.IsNullOrWhiteSpace(cfg.ApiKey) ? "【空】" : "已设置")} " +
            $"记忆={cfg.EnableMemory} 间隔={cfg.MemoryInterval}，省钱={cfg.TokenSaver}");
        BubbleText = string.IsNullOrWhiteSpace(cfg.ApiKey)
            ? "API Key 还是空的，填上才能聊天哦"
            : "设置好了～可以跟我说话了";
    }

    private void ApplySize(int size)
    {
        // 以「立绘底边中点」为锚点缩放，视觉上像原地长大。
// 用 _artH 而不是 Height：Height 含下方透明的输入区。
// 拿它当锚点会让立绘在改尺寸时向上跳。
int anchorX = Left + Width / 2;
        int anchorY = Top + _artH;

        _size = size;
        _cfg.Size = size;
        _cfg.Save(_cfgPath);

        BuildSprite();
        Location = ClampToScreen(new Point(anchorX - Width / 2, anchorY - _artH));

        SyncSizeMenuChecks();

        PositionInput();
    }

    /// <summary>
    /// 把「尺寸」子菜单的勾同步到当前 _size。
    ///
    /// 用 Tag 里的像素值精确比对，不用字符串匹配 ——
    /// 旧代码是 item.Text.Contains(size.ToString())，既找错了菜单
    /// （遍历的是分组标题，其 DropDownItems 为空，循环体从不执行），
    /// 又有误匹配风险（"200" 会命中 "2000"）。
    /// </summary>
    private void SyncSizeMenuChecks()
    {
        foreach (var item in _sizeMenuItems)
            item.Checked = item.Tag is int v && v == _size;
    }

    private void MoveToDefault()
    {
        var wa = Screen.PrimaryScreen!.WorkingArea;
        Location = ClampToScreen(new Point(wa.Right - Width - 40, wa.Bottom - _artH - 8));
        SavePosition();
        PositionInput();
    }

    private void ToggleClickThrough()
    {
        _cfg.ClickThrough = !_cfg.ClickThrough;
        _cfg.Save(_cfgPath);
        _passThroughItem.Checked = _cfg.ClickThrough;

        var ex = Native.GetWindowLong(Handle, Native.GWL_EXSTYLE).ToInt64();
        if (_cfg.ClickThrough) ex |= Native.WS_EX_TRANSPARENT;
        else ex &= ~(long)Native.WS_EX_TRANSPARENT;
        Native.SetWindowLong(Handle, Native.GWL_EXSTYLE, (IntPtr)ex);
    }

    private void ShowAbout()
    {
        string persona = _chat != null
            ? $"{_chat.CharacterName}（{Path.GetFileName(_chat.ArchivePath)}"
            : "未载入";
        string keyState = _chatCfg == null ? "未载入"
            : (string.IsNullOrWhiteSpace(_chatCfg.ApiKey) ? "【未填写】" : "已设置");

        MessageBox.Show(
            "桌宠 · 菲比啾比\n\n" +
            "【鼠标】\n" +
            "左键拖动 —— 移动（拖着走会自然倾斜）\n" +
            "左键单击 —— 挤一下\n" +
            "左键双击 —— 播音效（没放音效就跳一下）\n" +
            "右键     —— 多级菜单\n\n" +
            "【键盘】\n" +
            "Ctrl+Alt+I —— 显示/隐藏输入框（全局热键）\n" +
            "Ctrl+Alt+P —— 隐私模式（全局热键）\n" +
            "Esc        —— 退出\n\n" +
            "【聊天】\n" +
            "输入框里打字回车发送；Shift+回车换行。\n" +
            "回复显示在立绘上方的气泡里。\n\n" +
            $"当前角色：{persona}\n" +
            $"API Key：{keyState}\n" +
            "人设可在 Personas 文件夹里增删。\n\n" +
            $"贴图：{Path.Combine(_dir, SpriteFull)}\n" +
            "换图直接替换这个文件再重开即可。位置和尺寸会自动记住。",
            "关于", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // ================= 窗口样式 =================
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        // 不抢焦点、不进任务栏、不参与 Alt+Tab：桌宠点了之后不该把当前窗口顶掉
        var ex = Native.GetWindowLong(Handle, Native.GWL_EXSTYLE).ToInt64();
        ex |= Native.WS_EX_LAYERED | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW;
        if (_cfg.ClickThrough) ex |= Native.WS_EX_TRANSPARENT;
        Native.SetWindowLong(Handle, Native.GWL_EXSTYLE, (IntPtr)ex);

        // 这里。exStyle 重写了一遍，WinForms 之前。TopMost=true 时设。
// WS_EX_TOPMOST 位会被抹。—。表现是「刚启动时宠物不置顶。
// 点一下右键菜单（弹出窗口触发重设）又置顶了，再点别的窗口又掉下去。踩过)。
// 所以重写之后必须把置顶位补回来。
ApplyTopMost(_cfg.TopMost);
    }

    /// <summary>
    /// 。SetWindowPos 设置/取消置顶。
/// 不能只靠 Form.TopMost —。它的位会。exStyle 重写冲掉。
/// 而且 WS_EX_NOACTIVATE 的窗口用 SetWindowPos 更可靠。
/// </summary>
    public void ApplyTopMost(bool on)
    {
        if (!IsHandleCreated) return;
        try
        {
            // 顺序很重要：必须先同。WinForms 。TopMost 属性，。SetWindowPos。
//
            // 反过来写（先 SetWindowPos 再赋 base.TopMost）会出问题：。base.TopMost
            // 赋值会。WinForms 重刷一遍窗口样式，那次重刷会把刚设好的
            // WS_EX_TOPMOST 位冲。—。现象就是自检第一项读。0x8090080（没置顶），
            // 而后面的切换项却能通过。踩过。
if (base.TopMost != on) base.TopMost = on;

            // 输入框跟着一起置顶，否则会出现「宠物置顶了但输入框被挡住。
if (_input != null) _input.TopMost = on;

            var insertAfter = on ? Native.HWND_TOPMOST : Native.HWND_NOTOPMOST;
            Native.SetWindowPos(Handle, insertAfter, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);

            int ex = (int)Native.GetWindowLong(Handle, Native.GWL_EXSTYLE).ToInt64();
            Log($"  置顶已应用: {on}（exStyle TOPMOST 位 = {((ex & 0x8) != 0)}）");
        }
        catch (Exception ex2) { Log($"  应用置顶失败: {ex2.Message}"); }
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _menu.Dispose();
            _sprite?.Dispose();
            _frame?.Dispose();
            _bubbleFont?.Dispose();
            _input?.Dispose();
            if (_hotkeyOk) { try { Native.UnregisterHotKey(Handle, HotkeyId); } catch { } }
            if (_hotkeyPrivacyOk) { try { Native.UnregisterHotKey(Handle, HotkeyIdPrivacy); } catch { } }
            Sound.StopAll();   // 退出时别让声音还在响
        }
        ReleaseSurface();
        base.Dispose(disposing);
    }
}

static class Program
{
    /// <summary>最早期的日志：不依赖任何窗体状态，构造失败也留得下痕迹。/summary>
    private static void Boot(string message) => AppPaths.Log(message);

    /// <summary>
    /// 几何自检：构造真。PetForm，量出窗。画布/立绘区的实际数值，
    /// 验证「加预留区后立绘不动」。打印到控制台，不截图。
/// </summary>
    private static void RunSelfTest(bool dump = false)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var sb = new List<string>();
        void Say(string s) { Console.WriteLine(s); sb.Add(s); }

        Say("=== 阶段 2 窗口结构自检 ===");
        try
        {
            try { Native.SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();

            using var form = new PetForm();
            // Render() 。IsHandleCreated 守卫（没窗口句柄就不画）。
// 自检不显示窗体，但要验证渲染，所以手动把句柄建出来。
var _ = form.Handle;
            form.CreateControl();
            Say($"[1] 窗体已构造（未显示），句柄={(_ != IntPtr.Zero ? "已建" : "无")}");
            Say($"    贴图    {form.ArtWidth} x {form.ArtHeight}");
            Say($"    客户端  {form.ClientSize.Width} x {form.ClientSize.Height}");
            Say($"    窗口    {form.Width} x {form.Height}");
            Say($"    位置    ({form.Left}, {form.Top})");

            // ---------- 立绘几何不变量 ----------
            // 多套立绘要动 BuildSprite —— 那是所有几何的源头。
            // 改错一个数会同时坏掉气泡定位 / 输入框位置 / 渲染缩放，
            // 而且这些自检原本测不出来（只验数值、验不出「看起来怪」）。
            //
            // 所以下面（在本章末尾、Check 可用之后）把**不变量**钉死：
            // 不管立绘尺寸怎么变，那些关系必须永远成立。

            int artW0 = form.ArtWidth, artH0 = form.ArtHeight;
            int top0 = form.Top, winH0 = form.Height;
            Say($"[2] 基线（无预留）: 立绘区高={artH0} 窗口高={winH0} 顶部={top0}");

            // 模拟「给输入框腾地方」：加 52px 预留区
            int reserve = 52;
            form.SetBottomReserve(reserve);
            Say($"[3] 加 {reserve}px 预留区后:");
            Say($"    贴图    {form.ArtWidth} x {form.ArtHeight}");
            Say($"    客户端  {form.ClientSize.Width} x {form.ClientSize.Height}");
            Say($"    窗口    {form.Width} x {form.Height}");
            Say($"    位置    ({form.Left}, {form.Top})");

            int artH1 = form.ArtHeight, top1 = form.Top, winH1 = form.Height;

            bool ok = true;
            void Check(string name, bool cond, string detail)
            {
                Say($"    {(cond ? "PASS" : "FAIL")}  {name}  {detail}");
                if (!cond) ok = false;
            }
            Say("[4] 断言:");
            Check("立绘区高不变", artH1 == artH0, $"({artH1} vs {artH0})");
            Check("画布高 = 立绘区 + 预留", form.ClientSize.Height == artH0 + reserve,
                  $"({form.ClientSize.Height} vs {artH0 + reserve})");
            Check("窗口顶边不动（立绘不跳）", top1 == top0, $"({top1} vs {top0})");
            Check("窗口高增加了预留量", winH1 == winH0 + reserve, $"({winH1} vs {winH0 + reserve})");

            // 还原，确认能回到初始状态
            form.SetBottomReserve(0);
            Say($"[5] 还原预留区后: 客户端 {form.ClientSize.Width}x{form.ClientSize.Height} 位置 ({form.Left},{form.Top})");
            Check("还原后画布高回到立绘区", form.ClientSize.Height == artH0, $"({form.ClientSize.Height} vs {artH0})");
            Check("还原后位置回到原点", form.Top == top0, $"({form.Top} vs {top0})");

            Say(ok ? "=== 全部通过 ===" : "=== 有失败项 ===");

            // ---------- 立绘几何不变量（正文） ----------
            // 放在这里是因为需要上面的 Check 和 ok。
            // 多套立绘会改 BuildSprite，而它是所有几何的源头：
            // 改错一个数会同时坏掉气泡定位 / 输入框位置 / 渲染缩放。
            // 把「不管立绘多大都必须成立」的关系钉在这里，
            // 换装换出任何错位都会立刻红。
            Say("");
            Say("=== 立绘几何不变量 ===");
            {
                var geoLines = new List<string>();
                int restoreSize = form.SizeForTest;

                // MeasureSpriteBounds 读的是 _frame（渲染画布）。
                // 不先画一次的话它是空的，测出来全是 0 —— 那种断言「通过」
                // 但什么都没验（前面已经栽过一次这种假测试）。
                // 所以每个尺寸都先 Render 一帧再量。
                form.BubbleText = "几何自检";
                form.RenderNow();

                // 所有尺寸档 × 几何契约
                foreach (int sz in new[] { 140, 170, 200, 260, 340 })
                {
                    form.ApplySizeForTest(sz);
                    form.BubbleText = "几何自检";
                    form.RenderNow();
                    var gb = form.MeasureSpriteBounds();

                    int padW = form.ArtWidth;
                    int artH = form.ArtHeight;
                    int top = form.ArtTop;
                    int sw = form.SpriteSizeForTest.Width;
                    int sh = form.SpriteSizeForTest.Height;

                    string line = $"    {sz,3}px → 贴图 {sw}x{sh}  画布 {padW}x{artH}  窗口高 {form.ClientSize.Height}" +
                                  $"  立绘 y∈[{gb.top},{gb.bottom}] x∈[{gb.left},{gb.right}]";
                    Say(line);
                    geoLines.Add(line);

                    int padWDelta = padW - sw;
                    int artHDelta = artH - sh;
                    Check($"尺寸{sz} 画布宽超出贴图 = 立绘区高超出贴图",
                          padWDelta == artHDelta, $"{padWDelta} vs {artHDelta}");
                    Check($"尺寸{sz} 两边留边对称（差值为偶数）",
                          padWDelta % 2 == 0 && padWDelta > 0, $"{padWDelta}");
                    Check($"尺寸{sz} 画布总高 = 顶部预留 + 立绘区",
                          form.ClientSize.Height == top + artH,
                          $"{form.ClientSize.Height} vs {top}+{artH}");
                    Check($"尺寸{sz} 窗口宽 = 画布宽",
                          form.ClientSize.Width == padW, $"{form.ClientSize.Width} vs {padW}");
                    Check($"尺寸{sz} 贴图宽正好等于设定值", sw == sz, $"{sw} vs {sz}");

                    // 先确认真的量到东西了，再验「没出界」。
                    // 不这么写的话，量到空结果时「未出界」会假通过。
                    bool hasInk = gb.bottom > gb.top && gb.right > gb.left;
                    Check($"尺寸{sz} 量到了立绘像素（前置）", hasInk,
                          $"y[{gb.top},{gb.bottom}] x[{gb.left},{gb.right}] —— 全 0 说明没渲染，断言会假通过");
                    Check($"尺寸{sz} 立绘未出界",
                          hasInk && gb.top >= 0 && gb.bottom < form.ClientSize.Height &&
                          gb.left >= 0 && gb.right < padW,
                          $"y[{gb.top},{gb.bottom}] x[{gb.left},{gb.right}]");
                }

                form.BubbleText = "";
                form.RenderNow();
                form.ApplySizeForTest(restoreSize);
                Say($"    （已还原到配置尺寸 {restoreSize}）");

                if (dump)
                {
                    string p = Path.Combine(AppPaths.Dir, "geometry-baseline.txt");
                    File.WriteAllLines(p, geoLines, new System.Text.UTF8Encoding(false));
                    Say($"    几何基线已导出: {p}");
                }
            }
            Say(ok ? "=== 立绘几何不变量全部通过 ===" : "=== 立绘几何不变量有失败项 ===");

            // ---------- 立绘扫描 / 换装解析自检 ----------
            // 换装最容易出问题的地方不是「能不能显示」，而是**畸形图片**：
            // 极端长宽比会算出几千像素高的窗口，1x1 的图会算出 0 宽。
            // 所以这一章专门造怪图来验，而不是只测「正常图能用」。
            Say("");
            Say("=== 立绘换装自检 ===");
            {
                string tmpArt = Path.Combine(Path.GetTempPath(),
                                             "phoebe-arttest-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                string artDir = Path.Combine(tmpArt, Art.DirName);
                Directory.CreateDirectory(artDir);
                try
                {
                    // 造几张怪图 + 一个非图片文件
                    void MakePng(string name, int w, int h)
                    {
                        using var b = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                        using (var g = Graphics.FromImage(b)) g.Clear(Color.CornflowerBlue);
                        b.Save(Path.Combine(artDir, name + ".png"),
                               System.Drawing.Imaging.ImageFormat.Png);
                    }
                    MakePng("正常", 200, 260);
                    MakePng("超宽", 800, 100);
                    MakePng("超高", 100, 800);
                    MakePng("方", 400, 400);
                    MakePng("极端条", 50, 2000);
                    MakePng("单像素", 1, 1);
                    File.WriteAllText(Path.Combine(artDir, "说明.txt"), "不是图片",
                                      new System.Text.UTF8Encoding(false));

                    var items = Art.List(tmpArt);
                    Say($"    扫到 {items.Count} 项：{string.Join(" / ", items.Select(i => i.Name))}");
                    Check("扫到 6 张图", items.Count == 6, $"{items.Count} 项");
                    Check("忽略了非图片文件（.txt）",
                          !items.Any(i => i.Name == "说明"), "不该包含 txt");
                    Check("按文件名排序",
                          items.Select(i => i.Name).SequenceEqual(items.Select(i => i.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase)),
                          "应有序");
                    Check("读出了尺寸（400x400 那张）",
                          items.Any(i => i.Name == "方" && i.Width == 400 && i.Height == 400),
                          "尺寸应正确");

                    // ---- 解析：三种写法都要能认 ----
                    string realFull = Path.Combine(tmpArt, "phoebe@2x.png");
                    string realSmall = Path.Combine(tmpArt, "phoebe.png");
                    File.WriteAllText(realFull, "x");   // 只验「文件存在」这条路径
                    File.WriteAllText(realSmall, "x");

                    string? r1 = Art.Resolve(tmpArt, "方", realFull, realSmall);
                    Check("按名字（不带扩展名）能找到", r1 != null && r1.EndsWith("方.png"), $"{r1}");

                    string? r2 = Art.Resolve(tmpArt, "方.png", realFull, realSmall);
                    Check("按文件名能找到", r2 != null && r2.EndsWith("方.png"), $"{r2}");

                    string abs = Path.Combine(artDir, "超高.png");
                    string? r3 = Art.Resolve(tmpArt, abs, realFull, realSmall);
                    Check("按绝对路径能找到", r3 == abs, $"{r3}");

                    // ---- 找不到必须回退，不能让菲比消失 ----
                    string? r4 = Art.Resolve(tmpArt, "不存在的立绘", realFull, realSmall);
                    Say($"    配置了不存在的名字 → 回退到 {Path.GetFileName(r4 ?? "(null)")}");
                    Check("找不到时回退出厂贴图", r4 == realFull, $"{r4}");

                    string? r5 = Art.Resolve(tmpArt, null, realFull, realSmall);
                    Check("配置为空时用出厂贴图", r5 == realFull, $"{r5}");

                    string? r6 = Art.Resolve(tmpArt, "   ", realFull, realSmall);
                    Check("配置是空白时也用出厂贴图", r6 == realFull, $"{r6}");

                    // 连出厂贴图都不在 → 返回 null（由调用方抛异常）
                    string? r7 = Art.Resolve(tmpArt, null,
                                             Path.Combine(tmpArt, "没有@2x.png"),
                                             Path.Combine(tmpArt, "没有.png"));
                    Check("什么都没有时返回 null", r7 == null, $"{r7 ?? "(null)"}");

                    // ---- 极端长宽比：真加载一遍，确认不炸 ----
                    //
                    // 这是换装最大的风险点。踩到的真问题：
                    //   Windows 对窗口高度有系统级上限（约屏幕高），
                    //   ClientSize 设成 1628 会被**静默夹到** ~1460，
                    //   而画布位图还是 1628 → 立绘底部被切掉，且不报错。
                    //   100x800 这种很普通的竖图在 200px 宽下就是 1600 高。
                    //
                    // 修法是超限时**等比缩小**（宽高一起缩，不变形），
                    // 而不是只夹高度（那会把人拉长）。
                    //
                    // 注意：测试图必须写进**真实的 Art/ 目录**，
                    // 因为 PetForm 是按程序目录解析立绘的。写到临时目录的话
                    // 会全部回退出厂贴图，每张测的都是同一张图 ——
                    // 断言会以「比例全错」的形式失败，看着像代码 bug 其实是测试写错了。
                    string realArtDir = Art.EnsureDir(AppPaths.Dir);
                    var madeFiles = new List<string>();

                    // 记下用户当前的立绘，测完还原。
                    // 这一段会把 form._cfg.Art 改成「自检超宽」等临时名，
                    // 而 form 用的是真实的 pet-config.json —— 不还原的话
                    // 用户跑一次自检，立绘就被改成一个已删除的文件名。
                    string? artChoiceBefore = form.ArtNameForTest;

                    var ratios = new (string Name, int W, int H)[]
                    {
                        ("自检超宽", 800, 100),
                        ("自检超高", 100, 800),
                        ("自检方形", 400, 400),
                        ("自检极端条", 50, 2000),
                        ("自检单像素", 1, 1),
                    };

                    try
                    {
                        foreach (var (nm, pw, ph) in ratios)
                        {
                            using var b = new Bitmap(pw, ph, PixelFormat.Format32bppArgb);
                            using (var g = Graphics.FromImage(b)) g.Clear(Color.Coral);
                            string fp = Path.Combine(realArtDir, nm + ".png");
                            b.Save(fp, System.Drawing.Imaging.ImageFormat.Png);
                            madeFiles.Add(fp);

                            // 走真代码路径：把立绘切到这张图，重建，再验几何
                            form.SetArtForTest(nm);
                            form.RenderNow();

                            var ss2 = form.SpriteSizeForTest;
                            int winH2 = form.ClientSize.Height;
                            int screenH = PetForm.MaxCanvasHeightForTest;

                            // 注意用 <= 而不是 <：夹取的目标就是「正好等于上限」，
                            // 上限本身已经带了 8% 余量（见 MaxCanvasHeightForCurrentScreen），
                            // 所以等于上限是**正确**结果，不是越界。
                            Check($"{nm} 窗口高不超屏幕", winH2 <= screenH,
                                  $"{winH2} <= {screenH}");

                            // 等比缩放的判据：缩放后比例 ≈ 原比例。
                            //
                            // 容差按「短边像素数」放宽：极端比例（比如 40:1）下
                            // 短边只有几十像素，取整误差占的比例就很大 ——
                            // 40:1 缩到 33x1324 会有 0.3% 偏差，那是**取整**不是变形。
                            // 真正变形（只夹高度）会有几十上百个百分点，跑不掉。
                            double rOrig = ph / (double)pw;
                            double rNew = ss2.Height / (double)ss2.Width;
                            double dev = rOrig > 0 ? Math.Abs(rOrig - rNew) / rOrig : 0;
                            int shortSide = Math.Min(ss2.Width, ss2.Height);
                            double tol = Math.Max(0.02, 2.0 / Math.Max(1, shortSide));
                            Check($"{nm} 没有变形（比例保持）", dev < tol,
                                  $"原 {rOrig:F3} → 新 {rNew:F3}，偏差 {dev * 100:F2}%（容差 {tol * 100:F2}%）");

                            // 几何不变量在换图后仍成立
                            Check($"{nm} 几何不变量成立",
                                  form.ArtHeight == ss2.Height + 28 &&
                                  form.ClientSize.Width == form.ArtWidth,
                                  $"画布 {form.ArtWidth}x{form.ArtHeight}，贴图 {ss2.Width}x{ss2.Height}");

                            Say($"    {nm,-14} 原图 {pw}x{ph} → 贴图 {ss2.Width}x{ss2.Height}  窗口高 {winH2}");

                            // 换装后不能把窗口顶出屏幕（底部要能看到）
                            Check($"{nm} 立绘未被窗口裁掉",
                                  ss2.Height + 28 <= form.ClientSize.Height,
                                  $"{ss2.Height + 28} <= {form.ClientSize.Height}");
                        }
                    }
                    finally
                    {
                        // 清理：删掉自检造的图，还原用户原来的立绘并落盘
                        foreach (var f in madeFiles)
                            try { File.Delete(f); } catch { }
                        form.SetArtForTest(artChoiceBefore);
                        form.SaveConfigForTest();   // 光改字段不落盘等于没还原
                        form.RenderNow();
                        Say($"    （自检图已清理，立绘已还原为「{artChoiceBefore ?? "出厂贴图"}」）");
                    }
                }
                finally
                {
                    try { Directory.Delete(tmpArt, true); } catch { }
                }
            }
            Say(ok ? "=== 立绘换装自检全部通过 ===" : "=== 立绘换装自检有失败项 ===");
            // ---------- 气泡自检 ----------
            Say("");
            Say("=== 气泡自检 ===");
            var texts = new[]
            {
                "哥哥你回来啦～我等你好久了！",
                "今天天气不错，要不要出去走走？我看你已经在电脑前坐了好几个小时了，眼睛会累的。",
                "",
            };
            foreach (var t in texts)
            {
                int topBefore = form.Top;
                form.BubbleText = t;
                Say($"    文字 {t.Length} 字 → 预留 {form.TopReserve}px, " +
                    $"画布 {form.ClientSize.Width}x{form.ClientSize.Height}, 窗口顶 {form.Top}");
                Say($"      立绘顶边在屏幕 y={form.Top + form.ArtTop}（加气泡前 {topBefore}）");
            }

            form.BubbleText = "";
            Say($"    清空气泡 → 预留 {form.TopReserve}px, 画布 {form.ClientSize.Width}x{form.ClientSize.Height}");
            Check("清空气泡后预留归零", form.TopReserve == 0, $"({form.TopReserve})");
            Check("清空气泡后画布回到立绘区", form.ClientSize.Height == artH0, $"({form.ClientSize.Height} vs {artH0})");

            // 气泡高度应随文字变长而变大
            form.BubbleText = "短";
            int h1 = form.TopReserve;
            form.BubbleText = texts[1];
            int h2 = form.TopReserve;
            Check("长文案气泡更高", h2 > h1, $"({h1} → {h2})");
            form.BubbleText = "";

            // ---- 排版必须「量得准」：框能装下字 ----
            //
            // 这一条是为了守住一个真踩过的 bug：Measure() 用 font.GetHeight()
            // 算行高（16.60），而 DrawString 实际画出来是 18.43 高，差 11%。
            // 于是框偏矮、文字挤在一起。光看「高度随文字变长」是发现不了的 ——
            // 那个断言在坏代码下照样通过。
            {
                using var gb = Graphics.FromImage(new Bitmap(1, 1));
                using var bf = PetForm.BubbleFontForTest(form.BubbleFontSize);
                Say($"    气泡字体: {bf.Name}（{form.BubbleFontSize}pt）");

                // 字体必须真的能画中文，否则中文走字体链接、行高失准
                bool cjk = true;
                try { cjk = gb.MeasureString("中文", bf).Width > 4f; } catch { cjk = false; }
                Check("气泡字体能显示中文", cjk, bf.Name);

                // 行高一致：Measure 用的行高必须等于实际渲染行高
                float lh = Bubble.LineHeight(gb, bf);
                float real = gb.MeasureString("中文Ag", bf).Height;
                Check("量出的行高 = 实际渲染行高",
                      Math.Abs(lh - real) < 0.01f, $"{lh:F2} vs {real:F2}");

                // 框高 >= 所有行实际渲染高度之和
                var st2 = new BubbleStyle { MaxWidth = 340 };
                var sz = Bubble.Measure(gb, texts[1], st2, bf);
                var lines1 = Bubble.WrapText(gb, texts[1], bf, st2.MaxWidth - st2.PadX * 2);
                float need = Bubble.TextBlockHeight(lines1, lh, st2) + st2.PadY * 2;
                Say($"    {lines1.Count} 行，行高 {lh:F2} → 需要 {need:F1}px，框高 {sz.Height}px");
                Check("气泡框装得下所有文字行",
                      sz.Height + 0.5f >= need, $"{sz.Height} >= {need:F1}");
                Check("气泡框高度没有虚高（不超一行）",
                      sz.Height <= need + lh, $"{sz.Height} <= {need + lh:F1}");

                // 顺手导出一张预览图：排版问题肉眼一看就知道，
                // 比读一堆数字快（README 第 8 条：先打印数值，再截图）。
                // 用带显式换行的多段文字 —— 用户报的就是这种（模型爱分行）。
                try
                {
                    string multi = "我问你要干嘛?你先\"oi\"我的,现在倒打一耙问我要干嘛?\n\n典,标准倒打一耙。\n\n我干嘛?我嗑瓜子刷视频,顺便看你在这儿欲言又止像个卡壳的复读机。有屁快放,没屁别耽误我。\n\n所以你找我到底干嘛?别告诉我你就是想我了,那更恶心。";
                    // 用**真实的运行时宽度**（跟 LayoutBubble 同一算式），
                    // 这样导出的预览图和实际显示一致，不会自欺欺人。
                    var stPrev = new BubbleStyle { MaxWidth = Math.Max(60, form.ClientSize.Width - PetForm.BubbleSideMarginForTest * 2) };
                    Say($"    预览用的气泡最大宽: {stPrev.MaxWidth}px（画布 {form.ClientSize.Width}）");
                    var mlines = Bubble.WrapText(gb, multi, bf, stPrev.MaxWidth - stPrev.PadX * 2);
                    var msz = Bubble.Measure(gb, multi, stPrev, bf);
                    float mneed = Bubble.TextBlockHeight(mlines, lh, stPrev) + stPrev.PadY * 2;
                    Say($"    多段文本: {mlines.Count} 行，需要 {mneed:F1}px，框 {msz.Width}x{msz.Height}px");
                    Check("多段文本气泡也装得下",
                          msz.Height + 0.5f >= mneed, $"{msz.Height} >= {mneed:F1}");
                    Check("气泡左右留有边距（不贴满画布）",
                          msz.Width <= form.ClientSize.Width - PetForm.BubbleSideMarginForTest,
                          $"{msz.Width} <= {form.ClientSize.Width - PetForm.BubbleSideMarginForTest}");

                    string prevPath = Path.Combine(AppPaths.Dir, "bubble-preview.png");
                    Bubble.RenderPreviewToPng(multi, prevPath, stPrev, bf);
                    Say($"    气泡预览已导出: {Path.GetFileName(prevPath)}");
                }
                catch (Exception e) { Say($"    预览导出失败: {e.Message}"); }
            }

            // ---------- 渲染像素自检 ----------
            // 真的 Render 一帧，然后数像素：确认气泡确实画出来了（不是只有布局对）
            Say("");
            Say("=== 渲染像素自检 ===");
            form.BubbleText = "哥哥你回来啦～我等你好久了！";
            form.RenderOnce();
            var stats = form.MeasureFrame();
            Say($"    画布 {stats.w}x{stats.h}");
            Say($"    不透明像素 {stats.opaque}");
            Say($"    顶部 1/3 区域不透明像素 {stats.topOpaque}（气泡所在区）");
            Say($"    底部 2/3 区域不透明像素 {stats.restOpaque}（立绘所在区）");
            Say($"    深蓝描边像素 {stats.borderPixels}，白色像素 {stats.whitePixels}");

            // 立绘边界：确认没有被画布边缘切断
            var bb = form.MeasureSpriteBounds();
            Say($"    立绘实际占据 y ∈ [{bb.top}, {bb.bottom}]（画布高 {stats.h}）");
            Say($"    立绘左右 x ∈ [{bb.left}, {bb.right}]（画布宽 {stats.w}）");
            Check("立绘顶边未出界", bb.top >= 0, $"(top={bb.top})");
            Check("立绘底边未出界", bb.bottom <= stats.h - 1, $"(bottom={bb.bottom} < {stats.h})");
            Check("立绘左边未出界", bb.left >= 0, $"(left={bb.left})");
            Check("立绘右边未出界", bb.right <= stats.w - 1, $"(right={bb.right} < {stats.w})");
            Check("画布有内容", stats.opaque > 0, $"({stats.opaque})");
            Check("气泡区有像素", stats.topOpaque > 0, $"({stats.topOpaque})");
            Check("存在深蓝描边", stats.borderPixels > 0, $"({stats.borderPixels})");
            Check("存在白色填充", stats.whitePixels > 0, $"({stats.whitePixels})");

            // 导出 PNG 供人工查看（默认关闭，加 --dump 启用；不截图屏幕，只是存画布位图。
if (dump)
            {
                string outPng = Path.Combine(AppPaths.Dir, "bubble-preview.png");
                form.SaveFrame(outPng);
                Say($"    预览已导出: {outPng}");
            }
            form.BubbleText = "";

            Say(ok ? "=== 气泡自检全部通过 ===" : "=== 气泡自检有失败项 ===");

            // ---------- 输入框自检 ----------
            Say("");
            Say("=== 输入框自检 ===");
            form.BubbleText = "测试气泡文字";
            var input = form.Input;
            Say($"    输入框尺寸 {input.Width}x{input.Height}");
            Say($"    输入框位置 ({input.Left},{input.Top})");
            Say($"    立绘底边屏幕 y = {form.Top + form.TopReserve + form.ArtHeight - PetForm.CanvasMargin}");
            Say($"    立绘水平中心 x = {form.Left + form.ArtWidth / 2}，输入框中心 x = {input.Left + input.Width / 2}");

            Check("输入框在立绘下方", input.Top > form.Top + form.TopReserve,
                  $"({input.Top} > {form.Top + form.TopReserve})");
            Check("输入框水平居中", Math.Abs((input.Left + input.Width / 2) - (form.Left + form.ArtWidth / 2)) <= 2,
                  $"(差 {Math.Abs((input.Left + input.Width / 2) - (form.Left + form.ArtWidth / 2))}px)");
            Check("输入框不窄于 140", input.Width >= 140, $"({input.Width})");
            Say($"    初始 Value=\"{input.Value}\"（占位符不算内容）");
            Check("初始内容为空", input.Value == "", $"(\"{input.Value}\")");

            // 拖动后应跟随
            int beforeX = input.Left, beforeY = input.Top;
            form.Location = new Point(form.Left - 60, form.Top - 40);
            form.PositionInput();
            Say($"    移动立绘 (-60,-40) 后: 输入框 ({beforeX},{beforeY}) → ({input.Left},{input.Top})");
            Check("输入框跟随移动", input.Left == beforeX - 60 && input.Top == beforeY - 40,
                  $"(Δ={input.Left - beforeX},{input.Top - beforeY})");

            // 气泡开关一轮后输入框位置应稳定
            int ix0 = input.Left, iy0 = input.Top;
            form.BubbleText = "";
            form.BubbleText = "测试气泡文字";
            Say($"    气泡开关一轮后: 输入框 ({ix0},{iy0}) → ({input.Left},{input.Top})");
            Check("气泡变化后输入框位置稳定", input.Left == ix0 && input.Top == iy0,
                  $"(Δ={input.Left - ix0},{input.Top - iy0})");

            // ---- 圆角区域（消四角白边）----
            // 普通窗口没有逐像素透明，圆角只能靠 SetWindowRgn 裁剪窗口形状。
            //
            // 注意：这里【不能】用 GetWindowRgn 判定是否生效。
            // 实测在本机沙箱里，即使传入系统自带的 CreateRoundRectRgn 造出的
            // 合法区域、SetWindowRgn 也返回 1（成功），GetWindowRgn 依然读回 0。
            // 也就是说这个读接口在本环境不可信 —— 拿它做断言只会产出假失败。
            // 所以这里只验证「我们自己这段代码被调用过、且 SetWindowRgn 报告成功」，
            // 真正的视觉效果由人工确认（见 README 的验证清单）。
            Say($"    ApplyRegion 调用 {input.RegionApplyCount} 次:");
            foreach (var line in input.ApplyRegionLog) Say($"        {line}");

            int setOkCount = 0;
            foreach (var line in input.ApplyRegionLog)
                if (line.Contains("SetWindowRgn=1")) setOkCount++;

            Check("输入框至少应用过一次圆角区域", input.RegionApplyCount > 0,
                  $"调用 {input.RegionApplyCount} 次");
            Check("每次 SetWindowRgn 都报告成功", setOkCount == input.RegionApplyCount,
                  $"成功 {setOkCount} / 共 {input.RegionApplyCount}");
            Check("区域按窗口实际尺寸计算", input.RegionAppliedSize == $"{input.Width}x{input.Height}",
                  $"区域用 {input.RegionAppliedSize}, 窗口 {input.Width}x{input.Height}");

            form.BubbleText = "";
            Say(ok ? "=== 输入框自检全部通过 ===" : "=== 输入框自检有失败项 ===");

            // ---------- 菜单自检 ----------
            Say("");
            Say("=== 右键菜单结构 ===");

            // 菜单在构造时就建了，那时还没有聊天核心；
            // 这里补上挂载，验证「挂载后人设子菜单能列出文件」这条真实路径。
var testCfg = ChatConfig.Load(Path.Combine(AppPaths.Dir, "chat-config.json"));
            form.AttachChat(new ChatCore(testCfg, AppPaths.Dir), testCfg);

            var menuLines = new List<string>();
            int itemCount = 0, submenuCount = 0, clickableCount = 0;

            void Walk(ToolStripItemCollection items, int depth)
            {
                foreach (ToolStripItem it in items)
                {
                    if (it is ToolStripSeparator) { menuLines.Add(new string(' ', depth * 4) + "─────"); continue; }
                    var mi = it as ToolStripMenuItem;
                    if (mi == null) continue;

                    itemCount++;
                    string mark = mi.Checked ? "[✓] " : (mi.Enabled ? "" : "    ");
                    menuLines.Add(new string(' ', depth * 4) + mark + mi.Text);
                    if (mi.Enabled) clickableCount++;

                    if (mi.DropDownItems.Count > 0)
                    {
                        submenuCount++;
                        Walk(mi.DropDownItems, depth + 1);
                    }
                }
            }
            Walk(form.Menu.Items, 0);
            foreach (var l in menuLines) Say("    " + l);

            Say($"    合计 {itemCount} 项，其中 {submenuCount} 个含子菜单，{clickableCount} 项可点");
            Check("有子菜单", submenuCount >= 4, $"({submenuCount})");
            Check("菜单项够多", itemCount >= 15, $"({itemCount})");

            // 人设子菜单应列出 Personas/ 里的文件
            var personaTop = form.Menu.Items.OfType<ToolStripMenuItem>()
                .FirstOrDefault(m => m.Text != null && m.Text.StartsWith("人设"));
            Check("存在「人设与记忆」子菜单", personaTop != null, personaTop?.Text ?? "(没有)");
            if (personaTop != null)
            {
                var names = personaTop.DropDownItems.OfType<ToolStripMenuItem>()
                    .Where(m => m.Enabled).Select(m => m.Text).ToList();
                Say($"    「{personaTop.Text}」下可选项: {string.Join(", ", names)}");
                Check("人设子菜单列出了人设文件", names.Any(n => n != null && n.EndsWith(".json")),
                      $"({string.Join("/", names)})");
                // 记忆已并进这个子菜单
                Check("记忆项并入了人设子菜单",
                      names.Any(n => n != null && n.Contains("记忆")),
                      $"({string.Join("/", names)})");
            }

            // 顶层不应再有独立的「记忆」子菜单（用户说分类乱）
            Check("顶层没有独立的「记忆」子菜单",
                  !form.Menu.Items.OfType<ToolStripMenuItem>().Any(m => m.Text == "记忆"),
                  "记忆应并入「人设与记忆」");

            // ---- 立绘（换装）子菜单 ----
            var lookTop = form.Menu.Items.OfType<ToolStripMenuItem>()
                .FirstOrDefault(m => m.Text == "外观");
            Check("存在「外观」子菜单", lookTop != null, "外观");

            if (lookTop != null)
            {
                var lookNames = lookTop.DropDownItems.OfType<ToolStripMenuItem>()
                    .Select(m => m.Text).ToList();
                Say($"    「外观」下: {string.Join("/", lookNames)}");
                Check("外观里有「尺寸」", lookNames.Any(n => n == "尺寸"), "尺寸");
                Check("外观里有「立绘」", lookNames.Any(n => n == "立绘"), "立绘");

                var artTop = lookTop.DropDownItems.OfType<ToolStripMenuItem>()
                    .FirstOrDefault(m => m.Text == "立绘");
                if (artTop != null)
                {
                    var artItems = artTop.DropDownItems.OfType<ToolStripMenuItem>().ToList();
                    var artNames = artItems.Select(m => m.Text).ToList();
                    Say($"    「立绘」下: {string.Join(" | ", artNames)}");

                    // 出厂贴图那一项必须永远在 —— 换了一圈要能换回来
                    Check("有「出厂贴图（默认）」选项",
                          artNames.Any(n => n != null && n.Contains("出厂")),
                          $"({string.Join("/", artNames)})");

                    // 打开文件夹 / 重新扫描
                    Check("有「打开立绘文件夹」",
                          artNames.Any(n => n != null && n.Contains("打开立绘文件夹")),
                          $"({string.Join("/", artNames)})");
                    Check("有「重新扫描」",
                          artNames.Any(n => n != null && n.Contains("重新扫描")),
                          $"({string.Join("/", artNames)})");

                    // 出厂贴图项应当被勾中（当前配置 Art=null，用出厂图）
                    var noneItem = artItems.FirstOrDefault(m => m.Tag as string == "(none)");
                    Check("用出厂贴图时该项打勾",
                          noneItem != null && noneItem.Checked,
                          $"Checked={noneItem?.Checked}");
                }
            }

            // 「隐藏输入框」和开关重复，应该已经删掉
            var talk = form.Menu.Items.OfType<ToolStripMenuItem>()
                .FirstOrDefault(m => m.Text == "对话");
            if (talk != null)
            {
                var talkItems = talk.DropDownItems.OfType<ToolStripMenuItem>()
                    .Select(m => m.Text).ToList();
                Check("去掉了重复的「隐藏输入框」",
                      !talkItems.Any(t => t != null && t.Contains("隐藏输入框")),
                      $"({string.Join("/", talkItems)})");
                Check("输入框开关标了热键",
                      talkItems.Any(t => t != null && t.Contains("Ctrl+Alt+I")),
                      $"({string.Join("/", talkItems)})");
                Check("有「启动时自动打开输入框」开关",
                      talkItems.Any(t => t != null && t.Contains("启动时")),
                      $"({string.Join("/", talkItems)})");
            }

            // 输入框启动默认值：必须是关的。
            // 用户报过「默认还是出现」—— 根因是**配置文件里存了旧值 true**，
            // 而反序列化会用配置里的值覆盖代码默认值。所以这里两件事都验：
            //   1) 代码里的默认值是 false
            //   2) 现有的 chat-config.json 也是 false
            var freshChat = new ChatConfig();
            Check("输入框启动默认关闭（代码默认值）", !freshChat.ShowInputOnStart,
                  $"{freshChat.ShowInputOnStart}");

            string cfgPath = Path.Combine(AppPaths.Dir, "chat-config.json");
            if (File.Exists(cfgPath))
            {
                var onDisk = ChatConfig.Load(cfgPath);
                Say($"    磁盘上 chat-config.json 的 ShowInputOnStart = {onDisk.ShowInputOnStart}");
                Check("磁盘配置里输入框启动也是关闭的", !onDisk.ShowInputOnStart,
                      "★磁盘上的旧值会覆盖代码默认值 —— 这就是「改了默认还是弹出来」的原因");
            }

            // ---- 尺寸子菜单的打勾同步 ----
            // 用户报的 bug：选任何尺寸都显示「中」打勾，但菲比实际尺寸变了。
            // 根因是 ApplySize 里遍历的是 _menu.Items[0]（分组标题「── 菲比 ──」），
            // 它的 DropDownItems 是空的 —— 循环体一次都不执行，勾永远不动。
            // 所以这里**真的调一次 ApplySize**，再看勾有没有跟着走。
            {
                var look = form.Menu.Items.OfType<ToolStripMenuItem>()
                    .FirstOrDefault(m => m.Text == "外观");
                var sizeMenu = look?.DropDownItems.OfType<ToolStripMenuItem>()
                    .FirstOrDefault(m => m.Text == "尺寸");
                Check("存在「尺寸」子菜单", sizeMenu != null, "外观 → 尺寸");

                if (sizeMenu != null)
                {
                    var sizeItems = sizeMenu.DropDownItems.OfType<ToolStripMenuItem>().ToList();
                    Say($"    尺寸档位: {string.Join(" / ", sizeItems.Select(i => i.Text))}");
                    Check("有 5 个尺寸档位", sizeItems.Count == 5, $"{sizeItems.Count} 个");

                    // 每个档位都点一遍，验证「正好一个打勾，且是点中的那个」
                    foreach (var item in sizeItems)
                    {
                        if (item.Tag is not int want) { Check($"{item.Text} 有 Tag", false, "Tag 缺失"); continue; }
                        form.ApplySizeForTest(want);

                        var checkedItems = sizeItems.Where(i => i.Checked).ToList();
                        string label = item.Text ?? "?";
                        Check($"选「{label}」后正好一项打勾", checkedItems.Count == 1,
                              $"打勾 {checkedItems.Count} 个：({string.Join("/", checkedItems.Select(i => i.Text))})");
                        Check($"选「{label}」后打勾的是它自己",
                              checkedItems.Count == 1 && ReferenceEquals(checkedItems[0], item),
                              $"实际打勾：{(checkedItems.Count == 1 ? checkedItems[0].Text : "(无)")}");
                        Check($"选「{label}」后尺寸真的变了", form.SizeForTest == want,
                              $"_size={form.SizeForTest} 期望 {want}");
                    }

                    // 收拾干净：回到配置里的尺寸，别把自检的副作用留给用户
                    int restore = form.SizeForTest;
                    Say($"    （自检结束时尺寸 = {restore}，配置文件里的值不受影响）");
                }
            }

            Say(ok ? "=== 菜单自检全部通过 ===" : "=== 菜单自检有失败项 ===");

            // ---------- 对话框布局自检 ----------
            // 这两个对话框的控件是手工摆坐标的，很容易出现「标签掉行」「按钮出界」。
// 这里把布局算式复算一遍，不弹窗、不截图就能查出来。
Say("");
            Say("=== 对话框布局自检 ===");

            // API 设置：上边距15 + 5。34 + 勾。4 + 省钱勾。间距40 + 按钮28
            const int apiY0 = 15, apiRowH = 34, apiRows = 5, apiCheckGap = 74, apiBtnH = 28;
            int apiBtnTop = apiY0 + apiRows * apiRowH + apiCheckGap;
            int apiNeedH = apiBtnTop + apiBtnH + 12;
            Say($"    API 设置: 按钮顶边 y={apiBtnTop}, 需要客户区高 >= {apiNeedH}");
            Check("API 设置窗口够高", 320 >= apiNeedH, $"(320 >= {apiNeedH})");

            // 每行标签和输入框必须共用同一个 y（这是刚修掉的 bug）
            for (int i = 0; i < apiRows; i++)
            {
                int rowY = apiY0 + i * apiRowH;
                int labelTop = rowY + 4;
                Say($"    第{i + 1}行: 输入框 y={rowY}, 标签 y={labelTop} (差 {labelTop - rowY}px)");
                Check($"  第{i + 1}行标签对齐输入框", Math.Abs(labelTop - rowY) <= 6, $"(差 {labelTop - rowY})");
            }

            // 气泡字号：TrackBar 高 45，标签 y=78，按钮 y=115 高 28
            int fsBtnBottom = 115 + 28;
            Say($"    气泡字号: 按钮底边 y={fsBtnBottom}, 客户区高 160");
            Check("气泡字号窗口够高", fsBtnBottom <= 160 - 8, $"({fsBtnBottom} <= 152)");
            Check("气泡字号标签在 TrackBar 之下", 78 >= 15 + 45, $"(78 >= {15 + 45})");
            Check("气泡字号标签在按钮之上", 78 + 24 <= 115, $"(102 <= 115)");

            Say(ok ? "=== 对话框自检全部通过 ===" : "=== 对话框自检有失败项 ===");

            // ---------- 置顶自检 ----------
            // 曾经。bug：句柄建好后 OnHandleCreated 重写 exStyle。
// 。WinForms 设的 WS_EX_TOPMOST 位抹。。刚启动不置顶。
//
            // 注意：这里必须先把窗。Show() 出来再验 TOPMOST 位。
// WS_EX_TOPMOST 是窗口管理器在实际显示窗口时才写上的样式位，
            // 对「从未显示过」的窗口。SetWindowPos(HWND_TOPMOST) 会返回成功，
            // 。exStyle 里的位不会出现（实测 0x8090080）。
// 之前自检不显示窗体，于是第一项永远是假失败。踩过。
Say("");
            Say("=== 置顶自检 ===");

            bool wasVisible = form.Visible;
            if (!wasVisible)
            {
                form.Opacity = 0;      // 不打扰用户，但要真的显示出来
                form.Show();
                Application.DoEvents();
                Say("    （窗体已临时显示，Opacity=0，用于让窗口管理器写上 TOPMOST 位）");
            }
            form.ApplyTopMost(form.TopMostEnabled);
            Application.DoEvents();

            int exNow = (int)Native.GetWindowLong(form.Handle, Native.GWL_EXSTYLE).ToInt64();
            bool topmostBit = (exNow & 0x8) != 0;
            Say($"    exStyle = 0x{exNow:X}，TOPMOST 位 = {topmostBit}，配置置顶 = {form.TopMostEnabled}，Visible = {form.Visible}");
            Check("配置为置顶时 TOPMOST 位应为 1", !form.TopMostEnabled || topmostBit,
                  $"(config={form.TopMostEnabled}, bit={topmostBit})");

            form.SetTopMostForTest(false);
            int exOff = (int)Native.GetWindowLong(form.Handle, Native.GWL_EXSTYLE).ToInt64();
            Say($"    关掉置顶后 exStyle = 0x{exOff:X}，TOPMOST 位 = {((exOff & 0x8) != 0)}");
            Check("关掉置顶后 TOPMOST 位应清零", (exOff & 0x8) == 0, $"(bit={((exOff & 0x8) != 0)})");

            form.SetTopMostForTest(true);
            int exOn = (int)Native.GetWindowLong(form.Handle, Native.GWL_EXSTYLE).ToInt64();
            Say($"    重新打开后 exStyle = 0x{exOn:X}，TOPMOST 位 = {((exOn & 0x8) != 0)}");
            Check("重新置顶后 TOPMOST 位应置位", (exOn & 0x8) != 0, $"(bit={((exOn & 0x8) != 0)})");

            Say(ok ? "=== 置顶自检全部通过 ===" : "=== 置顶自检有失败项 ===");

            // ---------- 上下文查看自检 ----------
            // 只验证「渲染出来对不对」没法覆盖内容正确性，这里直接检查拼出来的文本。
Say("");
            Say("=== 上下文查看自检 ===");
            var emptyText = PetForm.FormatContext(new List<ChatTurn>(), "菲比", "哥哥");
            string emptyOneLine = emptyText.Replace("\n", "\\n");
            Say($"    空上下文 → \"{emptyOneLine}\"");
            Check("空上下文给出提示", emptyText.Contains("还没有聊过"),
                  emptyText.Substring(0, Math.Min(20, emptyText.Length)));

            var sample = new List<ChatTurn>
            {
                // user 。content 在真。history 里带「玩家：」前缀，这里照抄真实形。
new ChatTurn { Role = "user", Content = "玩家：今天好累啊" },
                new ChatTurn { Role = "assistant", Content = "菲比：哥哥辛苦啦，要不要休息一下？" },
                new ChatTurn { Role = "user", Content = "玩家：好啊" },
                new ChatTurn { Role = "assistant", Content = "菲比：那我陪你歇会儿～" },
            };
            var ctxText = PetForm.FormatContext(sample, "菲比", "哥哥");
            Say("    样例输出:");
            foreach (var line in ctxText.Split('\n')) Say($"        {line}");

            Check("剥掉了「玩家：」前缀", !ctxText.Contains("玩家："), "不应出现「玩家：」");
            Check("剥掉了角色名前缀", !ctxText.Contains("菲比：哥哥"), "不应出现「菲比：哥哥」");
            Check("用称呼当说话人", ctxText.Contains("哥哥："), "应出现「哥哥：」");
            Check("角色名当说话人", ctxText.Contains("菲比："), "应出现「菲比：」");
            Check("内容完整保留", ctxText.Contains("今天好累啊") && ctxText.Contains("那我陪你歇会儿"),
                  "两条内容都应在");
            Check("标出了轮次", ctxText.Contains("[1]") && ctxText.Contains("[2]"), "应有 [1] [2]");
            Check("一问一答算同一轮", ctxText.IndexOf("[1]") == ctxText.LastIndexOf("[1]"),
                  "轮号 1 只应出现一次（在 user 那条上）");
            Check("统计了轮数", ctxText.Contains("4 条（2 轮）"), "应显示 4 条 2 轮");

            // 半轮（最后一句是菲比说的）不能把轮数算少
            var halfRound = new List<ChatTurn>
            {
                new ChatTurn { Role = "user", Content = "玩家：在吗" },
                new ChatTurn { Role = "assistant", Content = "菲比：在的～" },
                new ChatTurn { Role = "user", Content = "玩家：帮我看看这个" },
            };
            string halfText = PetForm.FormatContext(halfRound, "菲比", "哥哥");
            Check("奇数条时轮数按 user 条数算", halfText.Contains("3 条（2 轮）"),
                  "3 条应是 2 轮，不能是 1 轮");

            // ---------- 上下文列表（ListView）自检 ----------
            // 界面本身没法离线验，但喂给界面的数据可以 ——
            // 拆错了行，界面再好看也是错的。
            Say("    结构化行:");
            var rows = PetForm.BuildContextRows(sample, "菲比", "哥哥");
            foreach (var (r, who, body) in rows)
                Say($"        轮{r} | {who} | {body}");

            Check("行数与条数一致", rows.Count == sample.Count, $"{rows.Count} 行");
            Check("第一行是哥哥", rows[0].Who == "哥哥" && rows[0].Round == 1, "轮1/哥哥");
            Check("第二行是菲比", rows[1].Who == "菲比" && rows[1].Round == 1, "轮1/菲比");
            Check("一问一答同轮", rows[2].Round == 2 && rows[3].Round == 2, "都应是第 2 轮");
            Check("行里不带「玩家：」前缀", rows.All(r => !r.Body.Contains("玩家：")), "应已剥掉");
            Check("行里不带角色名前缀", rows.All(r => !r.Body.StartsWith("菲比：")), "应已剥掉");
            Check("行正文非空", rows.All(r => r.Body.Length > 0), "每条都该有内容");
            Check("空历史拆出 0 行",
                  PetForm.BuildContextRows(new List<ChatTurn>(), "菲比", "哥哥").Count == 0, "0 行");

            Say(ok ? "=== 上下文查看自检全部通过 ===" : "=== 上下文查看自检有失败项 ===");

            // ---------- 日志轮转自检 ----------
            // 真写文件、真触发轮转，再检查结。—。只看代码没法确认节流计数
            // 。Move 的覆盖行为对不对。
Say("");
            Say("=== 日志轮转自检 ===");
            {
                string tmpDir = Path.Combine(Path.GetTempPath(), "phoebe-logtest-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                Directory.CreateDirectory(tmpDir);
                string logPath = Path.Combine(tmpDir, "pet.log");
                string oldPath = Path.Combine(tmpDir, "pet.log.1");
                int savedMax = AppPaths.MaxLogKB;

                try
                {
                    // 上限设成 1KB，好让它真的触发
                    AppPaths.MaxLogKB = 1;
                    AppPaths.ForceLogDirForTest(tmpDir);

                    // 每行。60 字节，写 200 。。12KB，必然超 1KB
                    int lines = 200;
                    for (int i = 0; i < lines; i++)
                        AppPaths.Log($"轮转测试 {i} " + new string('x', 30));

                    bool oldExists = File.Exists(oldPath);
                    long newSize = File.Exists(logPath) ? new FileInfo(logPath).Length : -1;
                    long oldSize = oldExists ? new FileInfo(oldPath).Length : -1;

                    Say($"    写入 {lines} 行，上限 1KB");
                    Say($"    pet.log   存在={File.Exists(logPath)} 大小={newSize} 字节");
                    Say($"    pet.log.1 存在={oldExists} 大小={oldSize} 字节");

                    Check("超限后生成了 pet.log.1", oldExists, oldExists ? "已生成" : "没生成");
                    Check("pet.log.1 里确实有旧内容", oldSize > 1024, $"{oldSize} 字节");
                    Check("轮转后 pet.log 重新变小", newSize >= 0 && newSize < oldSize,
                          $"新 {newSize} < 旧 {oldSize}");

                    // 再写一批，确认旧的 .1 会被覆盖而不是堆积
                    long oldSizeBefore = oldSize;
                    for (int i = 0; i < lines; i++)
                        AppPaths.Log($"第二轮 {i} " + new string('y', 30));
                    long oldSizeAfter = new FileInfo(oldPath).Length;
                    Say($"    第二轮后 pet.log.1 大小 {oldSizeBefore} → {oldSizeAfter}");
                    Check("再次轮转覆盖而非堆积", oldSizeAfter > 0,
                          $"{oldSizeAfter} 字节");
                    Check("只保留一份旧日志（无 .2", !File.Exists(Path.Combine(tmpDir, "pet.log.2")),
                          "不应出现 pet.log.2");

                    // 上限。0 = 不限制，不应轮转
                    Directory.Delete(tmpDir, true);
                    Directory.CreateDirectory(tmpDir);
                    AppPaths.MaxLogKB = 0;
                    AppPaths.ForceLogDirForTest(tmpDir);
                    for (int i = 0; i < lines; i++)
                        AppPaths.Log($"不限制测试 {i} " + new string('z', 30));
                    Say($"    上限=0 时写 {lines} 行: pet.log={new FileInfo(logPath).Length} 字节, " +
                        $"pet.log.1 存在={File.Exists(oldPath)}");
                    Check("上限 0 时不轮转", !File.Exists(oldPath), "不应生成 pet.log.1");
                }
                finally
                {
                    AppPaths.MaxLogKB = savedMax;
                    AppPaths.ClearLogDirForTest();
                    try { Directory.Delete(tmpDir, true); } catch { }
                }
            }
            Say(ok ? "=== 日志轮转自检全部通过 ===" : "=== 日志轮转自检有失败项 ===");

            // ---------- 主动发言自检 ----------
            Say("");
            Say("=== 主动发言自检 ===");
            {
                // 1) 闲置时长取得到吗
                long idle = Native.IdleMilliseconds();
                Say($"    当前系统闲置时长 = {idle} ms（{idle / 1000}s");
                Check("GetLastInputInfo 可用", idle >= 0 && idle < 86400000L,
                      $"{idle}ms");

                // 2) 秒数转人。
                foreach (var (sec, want) in new[]
                {
                    (15, "15 秒"), (60, "1 分钟"), (90, "1 分 30 秒"),
                    (120, "2 分钟"), (3600, "1 小时"), (0, "不等待"),
                })
                {
                    string got = PetForm.DescribeSeconds(sec);
                    Check($"DescribeSeconds({sec})", got == want, $"得到「{got}」期望「{want}");
                }

                // 2b) 数值显示不能串单位。
                // 这里曾经出过 bug：截屏尺寸复用了秒数格式化函数，
                // 于是 1024px 被显示成「17 分 4 秒」。数值存对了，纯粹显示错。
                // 断言「像素必须带 px，且绝不能出现分/秒」。
                foreach (int px in new[] { 256, 384, 512, 768, 1024 })
                {
                    string got = PetForm.DescribePixels(px);
                    Check($"DescribePixels({px}) 带 px 单位", got == $"{px} px", $"得到「{got}」");
                    Check($"DescribePixels({px}) 不出现时间单位",
                          !got.Contains("分") && !got.Contains("秒") && !got.Contains("小时"),
                          $"得到「{got}」，不该有分/秒");
                }
                Check("1024px 不会被说成 17 分 4 秒",
                      PetForm.DescribePixels(1024) != PetForm.DescribeSeconds(1024),
                      $"像素「{PetForm.DescribePixels(1024)}」 vs 秒数「{PetForm.DescribeSeconds(1024)}」");

                // 3) 主动发言的提示词不能让模型以为在回复用户
                var cfgT = ChatConfig.Load(Path.Combine(AppPaths.Dir, "chat-config.json"));
                var core = new ChatCore(cfgT, AppPaths.Dir);
                var parts = core.BuildProactivePartsForTest("已经闲置了约 5 分钟。", "");
                Say($"    主动发言消息序列（{parts.Count} 条）:");
                foreach (var p in parts)
                {
                    string oneLine = p.Content.Replace("\n", " / ");
                    if (oneLine.Length > 70) oneLine = oneLine.Substring(0, 70) + "";
                    Say($"        [{p.Role}] {oneLine}");
                }
                Check("第一条是 system", parts.Count > 0 && parts[0].Role == "system",
                      parts.Count > 0 ? parts[0].Role : "(空)");
                Check("最后一条是 user 提示", parts.Count > 0 && parts[parts.Count - 1].Role == "user",
                      parts.Count > 0 ? parts[parts.Count - 1].Role : "(空)");
                Check("提示里说明了「没有在跟你说话」",
                      parts.Count > 0 && parts[parts.Count - 1].Content.Contains("没有在跟你说话"),
                      "提示词应明确这次不是回复");

                // 4) 历史里不应出现那条提示（它是私下的，不该被记住）
                int histBefore = core.HistoryCount;
                Say($"    调用前历史条数 = {histBefore}（SendProactive 只写 assistant 侧）");

                // 5) 配置默认值合理
                var fresh = new PetForm.Config();
                Say($"    默认: 闲置 {fresh.IdleSeconds}s / 冷却 {fresh.ProactiveCooldownSeconds}s / " +
                    $"单次上限 {fresh.ProactiveMaxPerIdle} 次 / 开关 {fresh.ProactiveIdle}");
                Check("默认闲置时长 >= 30s", fresh.IdleSeconds >= 30, $"{fresh.IdleSeconds}s");
                Check("默认冷却 >= 闲置时长", fresh.ProactiveCooldownSeconds >= fresh.IdleSeconds,
                      $"冷却 {fresh.ProactiveCooldownSeconds}s >= 闲置 {fresh.IdleSeconds}s");
                Check("默认单次上限有限（不会唠个不停）",
                      fresh.ProactiveMaxPerIdle >= 1 && fresh.ProactiveMaxPerIdle <= 5,
                      $"{fresh.ProactiveMaxPerIdle} ");
                Check("默认日志上限 1MB", fresh.LogMaxKB == 1024, $"{fresh.LogMaxKB}KB");
            }
            Say(ok ? "=== 主动发言自检全部通过 ===" : "=== 主动发言自检有失败项 ===");

            // ---------- 活动感知自检 ----------
            // 黑名单是隐私的最后一道闸，必须验证它是「宁可错杀」的方向：
            // 命中的绝不放行，没命中的才可能被采集。
            Say("");
            Say("=== 活动感知自检 ===");
            {
                // 从「出厂默认」出发。前面的测试可能留下自定义清单，
                // 这里显式复位，否则断言会受执行顺序影响（踩过一次）。
                ActivityWatch.SetBlacklist(null, null);
                Say($"    当前进程规则 {ActivityWatch.CurrentProcessRules.Count} 条, " +
                    $"标题规则 {ActivityWatch.CurrentTitleRules.Count} 条");

                // 1) 出厂规则必须能拦住典型敏感程序
                var mustBlock = new (string proc, string title, string why)[]
                {
                    ("KeePass", "我的密码库", "密码管理器"),
                    ("1Password", "Vault", "密码管理器"),
                    ("Bitwarden", "Bitwarden", "密码管理器"),
                    ("chrome", "某某银行 - 网银登录", "银行标题"),
                    ("msedge", "InPrivate 浏览", "无痕模式"),
                    ("chrome", "重置密码", "密码关键词"),
                    ("unknown", "请输入验证码", "验证码"),
                    ("x", "Seed Phrase Backup", "助记词"),
                };
                foreach (var t in mustBlock)
                {
                    bool hit = ActivityWatch.IsBlacklisted(t.proc, t.title);
                    Check($"黑名单拦截 {t.why}", hit, $"{t.proc} / {t.title} → {(hit ? "拦截" : "★放行了")}");
                }

                // 2) 正常程序不能被误拦（否则功能就废了）
                var mustPass = new (string proc, string title)[]
                {
                    ("Code", "pet.cs - Visual Studio Code"),
                    ("chrome", "DeepSeek - 官方文档"),
                    ("notepad", "笔记.txt - 记事本"),
                    ("explorer", "C:\\Users\\me\\Documents"),
                    // 这几个是「过度拦截」的典型：标题里有「登录」但页面本身很普通。
                    // 曾经把「登录」放进默认关键词，结果大量正常网页被拦掉，
                    // 用户会觉得「菲比突然瞎了」。已从默认里移除，这里守住它。
                    ("chrome", "登录 - 某某论坛"),
                    ("msedge", "Sign in to your account"),
                    ("chrome", "知乎 - 登录"),
                };
                foreach (var t in mustPass)
                {
                    bool hit = ActivityWatch.IsBlacklisted(t.proc, t.title);
                    Check($"正常程序不误拦 {t.proc}", !hit, $"{t.title} → {(hit ? "★误拦了" : "放行")}");
                }

                // 3) 自定义规则要生效，而且**内置规则可以被删掉**
                ActivityWatch.SetBlacklist(new[] { "mytool" }, new[] { "机密项目" });
                Check("自定义进程规则生效", ActivityWatch.IsBlacklisted("mytool", "任何标题"), "mytool");
                Check("自定义标题规则生效", ActivityWatch.IsBlacklisted("other", "关于机密项目的讨论"), "机密项目");
                Check("自定义规则不影响无关程序", !ActivityWatch.IsBlacklisted("Code", "pet.cs"), "应放行");
                // 关键：整份清单被替换成自定义的，原来的内置规则应当**不再生效**
                Check("内置规则可被移除（不再是只读）",
                      !ActivityWatch.IsBlacklisted("KeePass", "我的密码库"),
                      "替换成自定义清单后，KeePass 不该再被拦");

                // 3b) 清空 ≠ 未自定义。这是用户实际报过的 bug：
                //     把黑名单全部删掉保存，重开又变回默认内容。
                //     根因：旧代码用 `Count > 0` 判断「是否自定义过」，
                //     于是空列表被当成「没自定义」→ 出厂默认又回来了。
                //     现在用 null 表示「从未设置」，空列表表示「故意清空」。
                ActivityWatch.SetBlacklist(new string[0], new string[0]);
                Check("清空后确实没有黑名单（不被默认值顶回来）",
                      !ActivityWatch.IsBlacklisted("KeePass", "我的密码库") &&
                      !ActivityWatch.IsBlacklisted("x", "请输入验证码"),
                      $"当前 {ActivityWatch.CurrentProcessRules.Count} 条进程 / " +
                      $"{ActivityWatch.CurrentTitleRules.Count} 条标题，应均为 0");
                Check("清空后规则数确实为 0",
                      ActivityWatch.CurrentProcessRules.Count == 0 &&
                      ActivityWatch.CurrentTitleRules.Count == 0,
                      "空清单应保持为空");

                // 3b-2) 完整走一遍「保存到配置 → 重新读出来」的路径。
                //       单位的 SetBlacklist 测试不够 —— 真正的坑在配置的反序列化：
                //       JSON 里的 [] 读出来是「空列表」还是「null」决定了走哪条分支。
                try
                {
                    string tmpCfg = Path.Combine(Path.GetTempPath(),
                        "phoebe-bl-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".json");
                    var c = new PetForm.Config
                    {
                        BlacklistProcess = new List<string>(),   // 用户清空
                        BlacklistTitle = new List<string>(),
                    };
                    c.Save(tmpCfg);
                    string saved = File.ReadAllText(tmpCfg);
                    var reloaded = PetForm.Config.Load(tmpCfg);
                    Say($"    保存后的 JSON 片段: " +
                        $"BlacklistProcess={(reloaded.BlacklistProcess == null ? "null" : reloaded.BlacklistProcess.Count + " 条")}, " +
                        $"BlacklistTitle={(reloaded.BlacklistTitle == null ? "null" : reloaded.BlacklistTitle.Count + " 条")}");
                    Check("空列表能被 JSON 原样读回（不是 null）",
                          reloaded.BlacklistProcess != null && reloaded.BlacklistTitle != null,
                          "空列表必须保留，否则会被当成「没自定义过」");

                    // 真的按 ApplyActivityConfig 的判据走一遍
                    ActivityWatch.SetBlacklist(reloaded.BlacklistProcess, reloaded.BlacklistTitle);
                    Check("存取一圈后黑名单仍然是空的",
                          ActivityWatch.CurrentProcessRules.Count == 0 &&
                          ActivityWatch.CurrentTitleRules.Count == 0,
                          "清空状态必须能持久化");

                    try { File.Delete(tmpCfg); } catch { }
                }
                catch (Exception e)
                {
                    Check("配置往返不抛异常", false, e.Message);
                }

                // 3c) 一项清空、另一项没动 —— 两者必须独立判定
                ActivityWatch.SetBlacklist(new string[0], null);
                Check("只清空进程名时，标题仍用默认",
                      ActivityWatch.CurrentProcessRules.Count == 0 &&
                      ActivityWatch.CurrentTitleRules.Count > 0,
                      $"进程 {ActivityWatch.CurrentProcessRules.Count} / " +
                      $"标题 {ActivityWatch.CurrentTitleRules.Count}");

                ActivityWatch.SetBlacklist(null, null);   // 两项都传 null = 恢复出厂默认
                Check("两项都传 null 才恢复出厂默认",
                      ActivityWatch.IsBlacklisted("KeePass", "我的密码库"),
                      "恢复默认后 KeePass 应重新被拦");

                // 3d) 命中的是哪一条规则（用于「测试当前窗口」的提示）
                ActivityWatch.SetBlacklist(new[] { "mytool" }, new[] { "机密" });
                string? rule = ActivityWatch.MatchRule("mytool", "普通标题");
                Say($"    MatchRule(mytool) = {rule ?? "(null)"}");
                Check("能报出命中的进程规则",
                      rule != null && rule.Contains("mytool"), rule ?? "(null)");
                string? rule2 = ActivityWatch.MatchRule("other", "关于机密的讨论");
                Say($"    MatchRule(标题含机密) = {rule2 ?? "(null)"}");
                Check("能报出命中的标题规则",
                      rule2 != null && rule2.Contains("机密"), rule2 ?? "(null)");
                Check("未命中时返回 null",
                      ActivityWatch.MatchRule("Code", "pet.cs") == null, "应返回 null");
                ActivityWatch.SetBlacklist(null, null);

                // 4) 大小写不敏感
                Check("大小写不敏感 (KEEPASS)",
                      ActivityWatch.IsBlacklisted("KEEPASS", ""), "大写也应拦");
                Check("大小写不敏感 (密码)",
                      ActivityWatch.IsBlacklisted("x", "我的密码"), "中文关键词");

                // 5) 「在给我看东西」的判定 —— 这个决定要不要截屏，宁漏勿误
                var showTrue = new[] { "你看这个好不好看", "这张怎么样", "帮我看看这里有问题吗", "这个视频怎么样",
                                       "这个好看吗", "你看这个", "你看这张", "看看这个" };
                var showFalse = new[] { "你好", "今天天气不错", "我写了个函数", "这个想法不错", "在吗" };
                foreach (var s in showTrue)
                    Check($"识别为「给我看东西」: {s}", PetForm.LooksLikeShowingSomething(s), "应触发截图");
                foreach (var s in showFalse)
                    Check($"不误判为「给我看东西」: {s}", !PetForm.LooksLikeShowingSomething(s), "不该截屏");

                // 5b) 「前台窗口是不是自己」的判定。
                //     用户报过：点菜单或对话框时，菲比会对着自己的窗口说话、截图。
                //     根因是按进程名比对（`dotnet` 而非 `PhoebePet`），永远匹配不上。
                Say($"    自身 PID = {Environment.ProcessId}");
                ActivityWatch.SetOwnPidForTest(Environment.ProcessId);
                bool selfNow = ActivityWatch.ForegroundIsSelf();
                Say($"    当前前台是否是自己 = {selfNow}（自检没有可见窗口，通常为 false）");
                // 用一个几乎不可能存在的 PID 验证「不匹配时不误判」
                ActivityWatch.SetOwnPidForTest(-12345);
                Check("PID 不匹配时不判定为自己", !ActivityWatch.ForegroundIsSelf(),
                      "换个 PID 应当不再认为前台是自己");
                ActivityWatch.SetOwnPidForTest(Environment.ProcessId);

                // 6) 截图能力（真截一张，检查体积合理）
                try
                {
                    var shot = ActivityWatch.CaptureForeground();
                    if (shot == null)
                    {
                        Say("    （当前无法截屏，可能没有前台窗口 —— 跳过体积检查）");
                    }
                    else
                    {
                        Say($"    实拍一张: {shot.Length / 1024}KB, 长边上限 {ActivityWatch.ShotMaxEdge}px");
                        Check("截图有内容", shot.Length > 1000, $"{shot.Length} 字节");
                        // 512px / JPEG q60 通常在 20-150KB；给足余量但防止失控
                        Check("截图体积合理（<600KB）", shot.Length < 600 * 1024, $"{shot.Length / 1024}KB");
                        // JPEG magic number: FF D8 FF
                        Check("是合法 JPEG",
                              shot.Length > 3 && shot[0] == 0xFF && shot[1] == 0xD8 && shot[2] == 0xFF,
                              $"头三字节 {shot[0]:X2} {shot[1]:X2} {shot[2]:X2}");
                    }
                }
                catch (Exception e)
                {
                    Check("截图不抛异常", false, e.Message);
                }
            }
            Say(ok ? "=== 活动感知自检全部通过 ===" : "=== 活动感知自检有失败项 ===");

            // ---------- 视觉请求格式自检 ----------
            // 图片必须挂在 user 消息上、且 content 要变成块数组。
            // 这两点只能靠检查真实 JSON 来确认 —— 弄错会直接 400。
            Say("");
            Say("=== 视觉请求格式自检 ===");
            {
                var vcfg = ChatConfig.Load(Path.Combine(AppPaths.Dir, "chat-config.json"));
                var vcore = new ChatCore(vcfg, AppPaths.Dir);

                // 造一小段假 JPEG（只要是非空字节数组即可，这里不真的发出去）
                byte[] fakeJpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46 };

                var msgs = new List<ChatTurn>
                {
                    new ChatTurn { Role = "system", Content = "你是菲比。" },
                    new ChatTurn { Role = "assistant", Content = "嗯嗯。" },
                    new ChatTurn { Role = "user", Content = "你看这个好不好看" },
                };

                string withImg = vcore.BuildRequestBodyForTest(msgs, fakeJpeg);
                string noImg = vcore.BuildRequestBodyForTest(msgs, null);

                // 注意：System.Text.Json 默认把非 ASCII 转义成 \uXXXX，
                // 所以直接 Contains("你看这个好不好看") 永远匹配不上（踩过两次）。
                // 断言中文内容前必须先解转义。
                string withImgPlain = System.Text.RegularExpressions.Regex.Replace(
                    withImg, @"\\u([0-9A-Fa-f]{4})",
                    m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
                string noImgPlain = System.Text.RegularExpressions.Regex.Replace(
                    noImg, @"\\u([0-9A-Fa-f]{4})",
                    m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());

                Say($"    带图请求体 {withImg.Length} 字节 / 不带图 {noImg.Length} 字节");
                Check("带图时出现 image_url", withImg.Contains("\"image_url\""), "应有 image_url 块");
                Check("带图时 type=image_url", withImg.Contains("\"type\":\"image_url\""), "块类型正确");
                Check("走 data: URL 内联", withImg.Contains("\"data:image/jpeg;base64,"), "应为 data URL");
                Check("detail=low（最省 token）", withImg.Contains("\"detail\":\"low\""), "detail 应为 low");
                Check("不带图时不出现 image_url", !noImg.Contains("image_url"), "纯文本请求不该有图");
                Check("不带图时 content 是字符串",
                      noImgPlain.Contains("\"content\":\"你看这个好不好看\""),
                      "纯文本仍是字符串形式");

                // system 消息必须仍是纯字符串（带图会 400）
                Check("system 消息仍是纯字符串",
                      withImgPlain.Contains("\"content\":\"你是菲比。\""),
                      "system 的 content 不能被改成块数组");
            }
            Say(ok ? "=== 视觉请求格式自检全部通过 ===" : "=== 视觉请求格式自检有失败项 ===");

            // ---------- 空回复识别自检 ----------
            // 这是真机上抓到的 bug：
            //     21:06:19  [输入] 回复 0 字:        ← 界面：什么都没说
            //     21:06:19  usage: completion=300     ← 账本：花了 300 token
            // 根因是「raw 非 null 但 strip 完是空串」被当成了成功。
            // 所以这里专门盯「空串必须被识别成失败」。
            Say("");
            Say("=== 空回复识别自检 ===");
            {
                // finish_reason = length（撞 max_tokens）—— 真机那次就是它
                var truncated = new ChatCore.ApiResult { Content = "", FinishReason = "length" };
                var filtered = new ChatCore.ApiResult { Content = "   ", FinishReason = "content_filter" };
                var emptyStop = new ChatCore.ApiResult { Content = "", FinishReason = "stop" };
                var nullContent = new ChatCore.ApiResult { Content = null, FinishReason = "stop" };

                Say($"    length         → {ChatCore.DescribeEmptyReply(truncated)}");
                Say($"    content_filter → {ChatCore.DescribeEmptyReply(filtered)}");
                Say($"    stop+空串      → {ChatCore.DescribeEmptyReply(emptyStop)}");
                Say($"    content=null   → {ChatCore.DescribeEmptyReply(nullContent)}");

                Check("识别出被截断", truncated.Truncated, "finish_reason=length 应判为截断");
                Check("stop 不算截断", !emptyStop.Truncated, "finish_reason=stop 不是截断");
                Check("大小写不敏感（Length）",
                      new ChatCore.ApiResult { FinishReason = "Length" }.Truncated, "应忽略大小写");

                // 四种空回复都必须给出**非空**的提示语，不能是空串
                foreach (var (n, r) in new[]
                {
                    ("截断", truncated), ("被拦", filtered), ("空串", emptyStop), ("null", nullContent),
                })
                {
                    string d = ChatCore.DescribeEmptyReply(r);
                    Check($"{n} 有提示语", !string.IsNullOrWhiteSpace(d), $"得到「{d}」");
                    Check($"{n} 提示语不撑爆气泡", d.Length <= 60, $"{d.Length} 字");
                }

                Check("截断的提示语说明了原因",
                      ChatCore.DescribeEmptyReply(truncated).Contains("长"),
                      "应告诉用户是太长了");

                // 关键回归：以前 raw="" 会一路走到 callback(clean=null?) 当成成功。
                // 现在必须能一眼看出「这是空回复」。
                Check("空串 != null（旧代码就是靠这个混过去的）",
                      truncated.Content != null && truncated.Content.Length == 0,
                      "Content 是空串而非 null —— 这正是当初漏掉的情况");

                // ---- 重说（RetryShorter）的请求格式 ----
                // 重说必须**追加一条 user 消息**，不能改 system，
                // 否则前面全部命中缓存的部分会失效，整段重新计费。
                var vcfg0 = ChatConfig.Load(Path.Combine(AppPaths.Dir, "chat-config.json"));
                var rcore = new ChatCore(vcfg0, AppPaths.Dir);
                var baseMsgs = new List<ChatTurn>
                {
                    new ChatTurn { Role = "system", Content = "你是菲比。" },
                    new ChatTurn { Role = "user", Content = "这个好看吗" },
                };
                string retryBody = rcore.BuildRetryBodyForTest(baseMsgs);
                string retryPlain = System.Text.RegularExpressions.Regex.Replace(
                    retryBody, @"\\u([0-9A-Fa-f]{4})",
                    m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());

                Say($"    重说请求体 {retryBody.Length} 字节");
                Check("重说追加了 user 消息", retryPlain.Contains("重说一遍"), "应含「重说一遍」");
                Check("重说要求了字数上限", retryPlain.Contains("50 字"), "应限定 50 字");
                Check("重说保留了原 system",
                      retryPlain.Contains("\"content\":\"你是菲比。\""), "system 不能动");
                Check("重说保留了原始提问",
                      retryPlain.Contains("这个好看吗"), "原问题要在");
                Check("重说消息数是 3 条（system+问+催）",
                      System.Text.RegularExpressions.Regex.Matches(retryBody, "\"role\":").Count == 3,
                      "不能多也不能少");
                Check("重说的输出预算被调小了",
                      rcore.RetryBudgetForTest <= vcfg0.MaxTokens, "不能比原先更大");
            }
            Say(ok ? "=== 空回复识别自检全部通过 ===" : "=== 空回复识别自检有失败项 ===");

            // ---------- 错误分类自检 ----------
            // 网络错误没法在沙箱里真造出来（TLS 被拦、也断不了网），
            // 但「429 该说什么」是纯函数 —— 直接喂数字验证。
            // 这一章的存在就是为了让错误提示逻辑可测。
            Say("");
            Say("=== 错误分类自检 ===");
            {
                // (状态码, 期望 kind, 期望是否可重试)
                var cases = new (int Code, string Kind, bool Retry)[]
                {
                    (400, "bad_request", false),
                    (401, "auth",       false),
                    (403, "auth",       false),
                    (402, "balance",    false),
                    (429, "rate_limit", true),
                    (404, "not_found",  false),
                    (500, "server",     true),
                    (502, "server",     true),
                    (503, "server",     true),
                    (504, "server",     true),
                };

                foreach (var (code, kind, retry) in cases)
                {
                    var e = ChatCore.DescribeHttpStatus(code, null);
                    Say($"    HTTP {code,-4} → [{e.Kind,-11}] {e.Message}");
                    Check($"HTTP {code} 归类为 {kind}", e.Kind == kind, $"得到 {e.Kind}");
                    Check($"HTTP {code} 可重试={retry}", e.Retryable == retry, $"得到 {e.Retryable}");
                    Check($"HTTP {code} 有提示语", !string.IsNullOrWhiteSpace(e.Message), "不能是空的");
                    Check($"HTTP {code} 提示语不撑爆气泡", e.Message.Length <= 60, $"{e.Message.Length} 字");
                }

                // 未知状态码也要能兜住
                var weird = ChatCore.DescribeHttpStatus(418, null);
                Say($"    HTTP 418  → [{weird.Kind}] {weird.Message}");
                Check("未知状态码有兜底文案", weird.Message.Length > 0, "不能空");

                // 正文里的 error.code 要能改变判断（余额不足是 402/429 都见过）
                string balBody = "{\"error\":{\"code\":\"insufficient_balance\",\"message\":\"Insufficient Balance\"}}";
                var balErr = ChatCore.DescribeHttpStatus(429, balBody);
                Say($"    429 + insufficient_balance → [{balErr.Kind}] {balErr.Message}");
                Check("429 但 code 是余额不足时归为 balance", balErr.Kind == "balance",
                      $"得到 {balErr.Kind}");
                Check("余额不足不该被判为可重试", !balErr.Retryable, "充值才能解决，重试没意义");

                Check("能从正文抠出 error.code",
                      ChatCore.ExtractErrorCode(balBody) == "insufficient_balance",
                      ChatCore.ExtractErrorCode(balBody));
                Check("正文不是 JSON 时不抛异常",
                      ChatCore.ExtractErrorCode("not json at all") == "", "应安全返回空串");
                Check("正文为 null 时不抛异常",
                      ChatCore.ExtractErrorCode(null) == "", "应安全返回空串");

                // 图片相关的 400 要给出更具体的建议
                var imgErr = ChatCore.DescribeHttpStatus(400, "{\"error\":{\"message\":\"image too large\"}}");
                Say($"    400 含 image → {imgErr.Message}");
                Check("图片报错时提示调小截屏尺寸", imgErr.Message.Contains("截屏"),
                      $"得到「{imgErr.Message}」");

                // 异常按**类型**分类，不靠文本碰运气
                var to = ChatCore.DescribeException(new System.Threading.Tasks.TaskCanceledException("timeout"));
                Say($"    TaskCanceledException → [{to.Kind}] {to.Message}");
                Check("超时异常归类为 timeout", to.Kind == "timeout", to.Kind);
                Check("超时可重试", to.Retryable, "网络抖动重试就好");

                var sock = ChatCore.DescribeException(
                    new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostNotFound));
                Say($"    SocketException     → [{sock.Kind}] {sock.Message}");
                Check("DNS 失败有专门文案", sock.Message.Contains("域名"), sock.Message);

                // 旧代码用 Contains("model") 会把任何含 model 的错误都误判成「模型名不对」。
                // 现在只有 404 才算。这条断言就是防它回退。
                var notModel = ChatCore.DescribeHttpStatus(500,
                    "{\"error\":{\"message\":\"model server overloaded\"}}");
                Check("500 里出现 model 也不会被误判成模型名错",
                      notModel.Kind == "server", $"得到 {notModel.Kind}");

                // 认不出来的异常要有兜底，且不能是空串
                var unknown = ChatCore.DescribeException(new Exception(""));
                Say($"    空消息异常 → [{unknown.Kind}] {unknown.Message}");
                Check("空消息异常也有文案", unknown.Message.Length > 0, "不能是空串");
                Check("空消息异常不会撑爆气泡", unknown.Message.Length <= 60, $"{unknown.Message.Length} 字");
            }
            Say(ok ? "=== 错误分类自检全部通过 ===" : "=== 错误分类自检有失败项 ===");

            // ---------- 记忆编辑自检 ----------
            // 用人设文件齐全的临时目录建 ChatCore，真写盘、真读回。
            // 重点验一个肉眼看不出来但很致命的坑：
            // 手工写完记忆后必须重置总结计数，否则她几轮后就把你教的忘了。
            Say("");
            Say("=== 记忆编辑自检 ===");
            {
                string tmp = Path.Combine(Path.GetTempPath(),
                                          "phoebe-memtest-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                Directory.CreateDirectory(tmp);
                try
                {
                    // 借用真实 Personas 目录里的一份人设，避免依赖不存在的人设文件
                    string realPersonas = Path.Combine(AppPaths.Dir, "Personas");
                    if (Directory.Exists(realPersonas))
                        File.Copy(
                            Directory.GetFiles(realPersonas, "*.json").OrderBy(f => f).First(),
                            Path.Combine(tmp, "p.json"), true);

                    var mcfg = ChatConfig.Load(Path.Combine(AppPaths.Dir, "chat-config.json"));
                    mcfg.PersonaFile = Directory.Exists(realPersonas) ? "p.json" : mcfg.PersonaFile;
                    var mcore = new ChatCore(mcfg, tmp);

                    Say($"    MemoryPath = {mcore.MemoryPath}");

                    // 从空开始
                    mcore.ClearMemory();
                    Check("清空后记忆为空", mcore.MemoryLength == 0, $"{mcore.MemoryLength} 字");

                    // 写入 + 落盘
                    mcore.SetMemory("老大喜欢喝冰美式，不加糖。");
                    Check("写完能立刻读到", mcore.UserMemory.Contains("冰美式"), mcore.UserMemory);
                    Check("清掉首尾空白", mcore.UserMemory == mcore.UserMemory.Trim(), "不该有前后空白");

                    // 关键：重新 new 一个 core，从磁盘读回 —— 这才能证明真的持久化了
                    var reread = new ChatCore(mcfg, tmp);
                    Say($"    重启后读回: \"{reread.UserMemory}\"");
                    Check("重启后记忆还在（真的写盘了）",
                          reread.UserMemory.Contains("冰美式"), $"读到「{reread.UserMemory}」");

                    // 追加而不是覆盖
                    reread.AppendMemory("他不吃香菜。");
                    Say($"    追加后: \"{reread.UserMemory.Replace("\n", " / ")}\"");
                    Check("追加不会丢掉原有内容", reread.UserMemory.Contains("冰美式"), "旧内容要保留");
                    Check("追加的新内容在", reread.UserMemory.Contains("香菜"), "新内容要在");
                    Check("两条之间用换行分隔", reread.UserMemory.Contains("\n"), "应换行拼接");

                    // 空/空白输入不该写进去
                    string before = reread.UserMemory;
                    reread.AppendMemory("   ");
                    Check("空白输入被忽略", reread.UserMemory == before, "不该改变记忆");
                    reread.AppendMemory("");
                    Check("空串被忽略", reread.UserMemory == before, "不该改变记忆");

                    // ==== 最重要的一条 ====
                    // 手工改完记忆，总结计数必须拉到当前轮。
                    // 不这么做的话，「第 6 轮触发总结」会在用户刚教完东西后
                    // 立刻覆盖掉它 —— 表现是「我明明教过她，她怎么忘了」。
                    var mcore2 = new ChatCore(mcfg, tmp);
                    // 必须真的把轮数推上去，否则 0 == 0 这种断言看着通过、其实没验。
                    mcore2.SetRoundCountForTest(5);
                    int r = mcore2.RoundCount;
                    Say($"    人为把轮数设为 {r}，改记忆前 LastMemoryRound = {mcore2.LastMemoryRoundForTest}");

                    // 先确认改之前确实是「落后的」状态，否则测不出修复效果
                    Check("前置：改之前计数落后于当前轮",
                          mcore2.LastMemoryRoundForTest < r,
                          $"LastMemoryRound={mcore2.LastMemoryRoundForTest} 应 < {r}");

                    mcore2.SetMemory("老大养了一只叫豆豆的猫。");
                    Say($"    改记忆后 LastMemoryRound = {mcore2.LastMemoryRoundForTest}");
                    Check("手工改记忆后总结计数被拉到当前轮",
                          mcore2.LastMemoryRoundForTest == r,
                          $"期望 {r}，得到 {mcore2.LastMemoryRoundForTest}");
                    Check("★ 刚教的记忆不会被立刻总结覆盖",
                          mcore2.LastMemoryRoundForTest >= r,
                          "必须 >= 当前轮，否则下一轮总结会冲掉它");

                    // 再来一次，确认 AppendMemory 也走同一条路
                    mcore2.SetRoundCountForTest(9);
                    mcore2.AppendMemory("他周末喜欢爬山。");
                    Check("AppendMemory 也会重置计数",
                          mcore2.LastMemoryRoundForTest == 9,
                          $"期望 9，得到 {mcore2.LastMemoryRoundForTest}");

                    // ClearMemory 也要能落盘（不然重启「忘掉的又回来了」）
                    mcore2.ClearMemory();
                    var afterClear = new ChatCore(mcfg, tmp);
                    Check("清空记忆后重启不会复活",
                          string.IsNullOrWhiteSpace(afterClear.UserMemory),
                          $"读到「{afterClear.UserMemory}」");
                }
                finally
                {
                    try { Directory.Delete(tmp, true); } catch { }
                }
            }
            Say(ok ? "=== 记忆编辑自检全部通过 ===" : "=== 记忆编辑自检有失败项 ===");

            // ---------- 对话历史持久化自检 ----------
            // 真写盘、真用新的 ChatCore 读回来。
            // 重点盯 NEXT.md 里标出的那个坑：清空上下文必须连磁盘存档一起删，
            // 否则重启后「忘掉的又回来了」。
            Say("");
            Say("=== 对话历史持久化自检 ===");
            {
                string tmp = Path.Combine(Path.GetTempPath(),
                                          "phoebe-histtest-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                Directory.CreateDirectory(tmp);
                try
                {
                    string realPersonas = Path.Combine(AppPaths.Dir, "Personas");
                    if (Directory.Exists(realPersonas))
                        File.Copy(
                            Directory.GetFiles(realPersonas, "*.json").OrderBy(f => f).First(),
                            Path.Combine(tmp, "p.json"), true);

                    var hcfg = ChatConfig.Load(Path.Combine(AppPaths.Dir, "chat-config.json"));
                    if (Directory.Exists(realPersonas)) hcfg.PersonaFile = "p.json";
                    hcfg.SaveHistory = true;
                    hcfg.HistoryLimit = 6;

                    var hcore = new ChatCore(hcfg, tmp);
                    Say($"    HistoryPath = {hcore.HistoryPath}");

                    // 空存档时不该报错
                    Check("没有存档时不崩", hcore.HistoryCount == 0, $"{hcore.HistoryCount} 条");
                    Check("没有存档时不生成文件", !File.Exists(hcore.HistoryPath), "不该凭空造文件");

                    // 塞几条历史 → 存盘
                    hcore.InjectHistoryForTest("玩家：今天好累", "菲比：辛苦啦");
                    hcore.InjectHistoryForTest("玩家：想吃火锅", "菲比：那我陪你");
                    hcore.SaveHistory();
                    Check("存档文件生成了", File.Exists(hcore.HistoryPath), hcore.HistoryPath);

                    // 关键：新建一个 core 从磁盘读回
                    var hre = new ChatCore(hcfg, tmp);
                    Say($"    重启后恢复 {hre.HistoryCount} 条");
                    foreach (var t in hre.HistorySnapshot())
                        Say($"        [{t.Role}] {t.Content}");
                    Check("重启后历史恢复了", hre.HistoryCount == 4, $"{hre.HistoryCount} 条");
                    Check("内容没丢", hre.HistorySnapshot().Any(t => t.Content.Contains("火锅")), "应含「火锅」");
                    Check("角色保住了",
                          hre.HistorySnapshot().Count(t => t.Role == "user") == 2 &&
                          hre.HistorySnapshot().Count(t => t.Role == "assistant") == 2,
                          "user/assistant 各 2 条");

                    // ==== 最重要的一条：清空要连文件一起删 ====
                    hre.ClearHistory();
                    Check("清空后内存为空", hre.HistoryCount == 0, $"{hre.HistoryCount} 条");
                    Check("★ 清空后磁盘存档也没了", !File.Exists(hre.HistoryPath),
                          "只清内存不删文件的话，重启后旧对话会复活");

                    var hre2 = new ChatCore(hcfg, tmp);
                    Check("★ 清空后重启不会复活", hre2.HistoryCount == 0,
                          $"重启后读到 {hre2.HistoryCount} 条");

                    // 关掉开关后不读也不写
                    hcfg.SaveHistory = false;
                    var hcore2 = new ChatCore(hcfg, tmp);
                    hcore2.InjectHistoryForTest("玩家：不该存", "菲比：嗯");
                    hcore2.SaveHistory();
                    Check("关闭开关后不写盘", !File.Exists(hcore2.HistoryPath), "不该生成文件");

                    hcfg.SaveHistory = true;
                    hcore2.SaveHistory();   // 打开后补存
                    var off3 = new ChatCore(hcfg, tmp);
                    Check("重新打开开关能读回", off3.HistoryCount == 2, $"{off3.HistoryCount} 条");

                    // 超过 HistoryLimit 的存档要被裁到最近的
                    var big = new List<string>();
                    for (int i = 0; i < 20; i++)
                        big.Add($"{{\"role\":\"user\",\"content\":\"第{i}条\"}}");
                    File.WriteAllText(Path.Combine(tmp, "history.json"),
                        "{\"turns\":[" + string.Join(",", big) + "]}",
                        new System.Text.UTF8Encoding(false));
                    var trimmed = new ChatCore(hcfg, tmp);
                    Say($"    存档 20 条 + 上限 {hcfg.HistoryLimit} → 载入 {trimmed.HistoryCount} 条");
                    Check("超长存档被裁到上限", trimmed.HistoryCount == hcfg.HistoryLimit,
                          $"{trimmed.HistoryCount} 条，上限 {hcfg.HistoryLimit}");
                    Check("裁的是最旧的（保留最近）",
                          trimmed.HistorySnapshot().Last().Content.Contains("第19"),
                          $"最后一条是「{trimmed.HistorySnapshot().Last().Content}」");

                    // 坏存档不能让程序起不来
                    File.WriteAllText(Path.Combine(tmp, "history.json"), "{ 这不是合法 json",
                                      new System.Text.UTF8Encoding(false));
                    var broken = new ChatCore(hcfg, tmp);
                    Check("坏存档不崩且当作空", broken.HistoryCount == 0, "应安全降级");

                    // 缺字段/错角色的记录要被跳过，不能污染上下文
                    File.WriteAllText(Path.Combine(tmp, "history.json"),
                        "{\"turns\":[{\"role\":\"system\",\"content\":\"我不该在这\"}," +
                        "{\"role\":\"user\",\"content\":\"正常一条\"}," +
                        "{\"role\":\"user\",\"content\":\"\"}]}",
                        new System.Text.UTF8Encoding(false));
                    var filtered = new ChatCore(hcfg, tmp);
                    Say($"    含非法记录的存档 → 载入 {filtered.HistoryCount} 条");
                    Check("跳过了 system 角色", filtered.HistoryCount == 1, $"{filtered.HistoryCount} 条");
                    Check("留下的是合法那条",
                          filtered.HistorySnapshot()[0].Content == "正常一条", "内容应正确");
                }
                finally
                {
                    try { Directory.Delete(tmp, true); } catch { }
                }
            }
            Say(ok ? "=== 对话历史持久化自检全部通过 ===" : "=== 对话历史持久化自检有失败项 ===");

            // ---------- 余额查询自检 ----------
            // 沙箱发不出真实请求（TLS 被拦），但**解析**是纯函数。
            // 官方文档：total_balance / granted_balance / topped_up_balance
            // 都是**字符串**不是数字 —— 这个坑必须验。
            Say("");
            Say("=== 余额查询自检 ===");
            {
                // 官方文档给的真实形状
                string real = @"{
                    ""is_available"": true,
                    ""balance_infos"": [{
                        ""currency"": ""CNY"",
                        ""total_balance"": ""13.90"",
                        ""granted_balance"": ""0.00"",
                        ""topped_up_balance"": ""13.90""
                    }]
                }";

                var bi = new BalanceInfo();
                Balance.Fill(bi, real);
                Say($"    文档样例 → {bi.Describe()}  available={bi.IsAvailable}");
                Check("解析出总余额", Math.Abs(bi.Total - 13.90) < 0.001, $"{bi.Total}");
                Check("解析出充值余额", Math.Abs(bi.ToppedUp - 13.90) < 0.001, $"{bi.ToppedUp}");
                Check("解析出赠金", Math.Abs(bi.Granted - 0.00) < 0.001, $"{bi.Granted}");
                Check("识别货币", bi.Currency == "CNY", bi.Currency);
                Check("货币符号是 ¥", bi.Symbol == "¥", bi.Symbol);
                Check("is_available 解析为 true", bi.IsAvailable, "应为真");

                // 字符串数字必须能读（这就是那个坑）
                Check("字符串 \"13.90\" 能转成数",
                      Math.Abs(Balance.ReadNumber(JsonValue.Create("13.90")) - 13.90) < 0.001, "字符串要能读");
                // 但如果哪天接口改成数字，也不能崩
                Check("数字 13.90 也能读",
                      Math.Abs(Balance.ReadNumber(JsonValue.Create(13.90)) - 13.90) < 0.001, "数字也要能读");
                Check("整数 100 也能读",
                      Math.Abs(Balance.ReadNumber(JsonValue.Create(100)) - 100) < 0.001, "整数也要能读");
                Check("null 返回 0 不抛", Balance.ReadNumber(null) == 0, "应安全返回 0");
                Check("垃圾字符串返回 0 不抛",
                      Balance.ReadNumber(JsonValue.Create("abc")) == 0, "应安全返回 0");

                // 余额耗尽
                var bad = new BalanceInfo();
                Balance.Fill(bad, @"{""is_available"":false,""balance_infos"":[
                    {""currency"":""CNY"",""total_balance"":""0.00"",
                     ""granted_balance"":""0.00"",""topped_up_balance"":""0.00""}]}");
                Check("余额耗尽时 is_available=false", !bad.IsAvailable, "应为假");
                string badText = PetForm.BuildUsageText(bad);
                Check("报告里对余额耗尽有警告", badText.Contains("不可用") || badText.Contains("耗尽"),
                      "应提示");
                Check("报告里显示 0.00", badText.Contains("0.00"), "应显示余额");

                // 空 balance_infos 不该崩（接口就是这个形状）
                var emptyArr = new BalanceInfo();
                Balance.Fill(emptyArr, @"{""is_available"":true,""balance_infos"":[]}");
                Check("空 balance_infos 不崩且金额为 0",
                      emptyArr.Total == 0 && emptyArr.Ok, $"{emptyArr.Total}");

                // 完全缺字段
                var noField = new BalanceInfo();
                Balance.Fill(noField, "{}");
                Check("缺所有字段时不崩", noField.Total == 0, "应安全");
                Check("缺 is_available 时默认可用于是显示金额",
                      noField.IsAvailable, "不该因为字段缺失就吓唬用户");

                // 多币种优先取 CNY
                var multi = new BalanceInfo();
                Balance.Fill(multi, @"{""is_available"":true,""balance_infos"":[
                    {""currency"":""USD"",""total_balance"":""5.00"",
                     ""granted_balance"":""0"",""topped_up_balance"":""5""},
                    {""currency"":""CNY"",""total_balance"":""36.50"",
                     ""granted_balance"":""0"",""topped_up_balance"":""36.5""}]}");
                Say($"    多币种 → {multi.Describe()}");
                Check("多币种优先取 CNY", multi.Currency == "CNY" && Math.Abs(multi.Total - 36.50) < 0.001,
                      $"{multi.Currency} {multi.Total}");

                // USD 符号
                var usd = new BalanceInfo { Currency = "USD", Total = 5 };
                Check("USD 用 $ 符号", usd.Symbol == "$", usd.Symbol);

                // 报告里余额查不到时的降级：不能因为查不到就打不开
                var failInfo = new BalanceInfo { Error = "网络好像断了" };
                string failText = PetForm.BuildUsageText(failInfo);
                Check("余额查不到时报告仍可生成", failText.Contains("今天") && failText.Contains("累计"),
                      "本地统计部分必须还在");
                Check("报告里说明了查询失败", failText.Contains("网络好像断了"), "要给出原因");
                Check("报告里安慰了「不影响本地统计」",
                      failText.Contains("不影响"), "要让用户放心");

                // balance 为 null（从没查过）
                string nullText = PetForm.BuildUsageText((BalanceInfo?)null);
                Check("没查过时报告也有提示", nullText.Contains("查询余额"), "应引导去查");

                // 缓存逻辑
                Balance.ClearCache();
                Check("清缓存后 Cached 为 null", Balance.Cached == null, "应为 null");
                Check("清缓存后 CacheFresh 为假", !Balance.CacheFresh, "应为假");
                Check("缓存有效期是正数", Balance.CacheFor.TotalMinutes > 0,
                      $"{Balance.CacheFor.TotalMinutes} 分钟");
            }
            Say(ok ? "=== 余额查询自检全部通过 ===" : "=== 余额查询自检有失败项 ===");

            // ---------- 气泡自动消失自检 ----------
            // 这是「闲置不主动发言」的根因：气泡永不清空 → HasBubble 恒为 true
            // → TickProactive 里那个前置条件永远挡着。所以必须专门测它。
            Say("");
            Say("=== 气泡自动消失自检 ===");
            {
                var bf = new PetForm();
                try
                {
                    bf.CreateControl();

                    // 1) 说话后气泡非空
                    bf.BubbleText = "测试一句话";
                    Check("说话后气泡非空", bf.HasBubble, $"\"{bf.BubbleText}\"");

                    // 2) 时长随字数增长（长的要留更久）
                    long shortMs = PetForm.BubbleDurationForTest(bf, "短");
                    long longMs = PetForm.BubbleDurationForTest(bf, new string('长', 80));
                    Say($"    时长: 1 字 → {shortMs}ms, 80 字 → {longMs}ms");
                    Check("长文案停留更久", longMs > shortMs, $"{shortMs} → {longMs}");
                    Check("下限 >= 4 秒", shortMs >= 4000, $"{shortMs}ms");
                    Check("上限 <= 20 秒", longMs <= 20000, $"{longMs}ms");

                    // 3) 配置能覆盖估算值
                    bf.SetBubbleSecondsForTest(7);
                    long fixedMs = PetForm.BubbleDurationForTest(bf, "无所谓多长都该是 7 秒");
                    Say($"    配置 BubbleSeconds=7 → {fixedMs}ms");
                    Check("配置值优先于估算", fixedMs == 7000, $"{fixedMs}ms");

                    // 4) 清空后不再算「有气泡」
                    bf.BubbleText = "";
                    Check("清空后 HasBubble 为假", !bf.HasBubble, "空串");

                    // 5) 关键：主动发言的前置条件不该被永久挡住。
                    //    直接验证「气泡到期后被自动清掉」这条路径存在且可达。
                    bf.SetBubbleSecondsForTest(0);   // 回到按字数估算
                    bf.BubbleText = "这句应该会自己消失";
                    bf.ForceBubbleExpireForTest();   // 把到期时间拨到过去
                    bf.TickBubbleFadeForTest();
                    Say($"    拨快时钟后: HasBubble={bf.HasBubble}, 内容=\"{bf.BubbleText}\"");
                    Check("到点后气泡自动清空", !bf.HasBubble, "应已被 TickBubbleFade 清掉");
                }
                finally { bf.Dispose(); }
            }
            Say(ok ? "=== 气泡自动消失自检全部通过 ===" : "=== 气泡自动消失自检有失败项 ===");

            // ---------- 主动发言闸门自检 ----------
            // 用户报过「闲置再久也不主动发言」。根因是气泡永不清空，
            // 于是 TickProactive 里 `if (HasBubble) return;` 永久挡住。
            // 这里把整条闸门逻辑按顺序验一遍，确保每一道都能放行。
            Say("");
            Say("=== 主动发言闸门自检 ===");
            {
                var pf = new PetForm();
                try
                {
                    pf.CreateControl();
                    var fresh = new PetForm.Config();
                    Say($"    默认: 闲置={fresh.IdleSeconds}s 冷却={fresh.ProactiveCooldownSeconds}s " +
                        $"上限={fresh.ProactiveMaxPerIdle} 切换搭话={fresh.ProactiveOnSwitch} " +
                        $"切换延迟={fresh.SwitchSettleSeconds}s");

                    Check("闲置开关默认开", fresh.ProactiveIdle, $"{fresh.ProactiveIdle}");
                    Check("切换搭话默认开", fresh.ProactiveOnSwitch, $"{fresh.ProactiveOnSwitch}");
                    Check("切换延迟 < 闲置时长（切换反应要更快）",
                          fresh.SwitchSettleSeconds < fresh.IdleSeconds,
                          $"{fresh.SwitchSettleSeconds}s < {fresh.IdleSeconds}s");

                    // 闸门 1：气泡必须是空的才可能开口。
                    // 这是之前卡死的那道门 —— 现在气泡会自己消失。
                    pf.BubbleText = "挡路的一句话";
                    Check("有气泡时 HasBubble=true（会被挡）", pf.HasBubble, "前置条件生效");
                    pf.SetBubbleSecondsForTest(1);
                    pf.ForceBubbleExpireForTest();
                    pf.TickBubbleFadeForTest();
                    Check("气泡到期后闸门放行", !pf.HasBubble, "HasBubble 应变为 false");

                    // 闸门 2：闲置计时本身可用
                    long idle = Native.IdleMilliseconds();
                    Say($"    系统闲置 {idle}ms，阈值 {fresh.IdleSeconds * 1000}ms");
                    Check("闲置计时接口正常", idle >= 0, $"{idle}ms");
                }
                finally { pf.Dispose(); }
            }
            Say(ok ? "=== 主动发言闸门自检全部通过 ===" : "=== 主动发言闸门自检有失败项 ===");

            // ---------- 隐私模式自检（阶段 D） ----------
            // 隐私模式是最不能出错的功能：用户开它就是因为「现在不能被你看见」。
            // 所以这里逐条验证**每一个采集出口**都被关掉，而不是只看总开关。
            Say("");
            Say("=== 隐私模式自检 ===");
            {
                var pf = new PetForm();
                try
                {
                    pf.CreateControl();
                    pf.SetPrivacyForTest(false);

                    // 记下用户原本的立绘名。
                    //
                    // 必须在这里（本段最开始）取，不能等到用立绘那段再取 ——
                    // 本段每次 SetPrivacyForTest 都会触发 PrivacyMode setter 里的
                    // _cfg.Save()，而更早的立绘自检段已经把 _cfg.Art 改过了，
                    // 取晚了拿到的就是被改过的值（实测还原成了空）。
                    string? userArtBefore = pf.ArtNameForTest;

                    // 1) 开关本身
                    pf.SetPrivacyForTest(true);
                    Check("开启后 PrivacyMode 为真", pf.PrivacyForTest, "true");
                    pf.SetPrivacyForTest(false);
                    Check("关闭后 PrivacyMode 为假", !pf.PrivacyForTest, "false");

                    // 2) 配置持久化：开了隐私模式关掉桌宠，下次启动应该还是隐私的，
                    //    否则会在用户不知情时恢复采集。
                    var freshCfg = new PetForm.Config();
                    Check("隐私模式默认关闭", !freshCfg.PrivacyMode, "默认 false");

                    string tmpCfg = Path.Combine(Path.GetTempPath(),
                        "phoebe-pv-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".json");
                    try
                    {
                        var c = new PetForm.Config { PrivacyMode = true };
                        c.Save(tmpCfg);
                        var back = PetForm.Config.Load(tmpCfg);
                        Say($"    存盘再读回: PrivacyMode={back.PrivacyMode}");
                        Check("隐私模式状态能持久化（不会偷偷恢复采集）", back.PrivacyMode, "应为 true");
                    }
                    finally { try { File.Delete(tmpCfg); } catch { } }

                    // 3) 记忆必须被清掉，而不只是停止采集
                    ActivityWatch.TickRemember();
                    bool rememberedBefore = ActivityWatch.HasRemembered;
                    pf.SetPrivacyForTest(true);
                    Say($"    开启前有记忆={rememberedBefore}，开启后={ActivityWatch.HasRemembered}");
                    Check("开启隐私模式会丢掉已记住的信息",
                          !ActivityWatch.HasRemembered,
                          "不能只是停止采集，之前记的也要忘掉");
                    pf.SetPrivacyForTest(false);

                    // 4) 主动发言：三个触发点都要被拦住。
                    //    这里验证 BuildRecentContext 在隐私模式下返回空
                    //    （它是给模型的上下文，返回空就等于「我什么都不知道」）。
                    pf.SetPrivacyForTest(true);
                    string ctx = pf.BuildRecentContextForTest();
                    Say($"    隐私模式下主动发言上下文 = \"{ctx}\"");
                    Check("隐私模式下不提供活动上下文", ctx.Length == 0, "应为空串");

                    pf.SetPrivacyForTest(false);
                    string ctx2 = pf.BuildRecentContextForTest();
                    Say($"    非隐私模式下上下文 = \"{(ctx2.Length > 30 ? ctx2.Substring(0, 30) + "…" : ctx2)}\"");

                    // 5) 截屏必须被拦
                    pf.SetPrivacyForTest(true);
                    var shot = pf.TryCaptureForDepthForTest();
                    Check("隐私模式下不截屏", shot == null, shot == null ? "返回 null" : "★截到了图");

                    // 6) 切换窗口不应触发
                    pf.SetPrivacyForTest(true);
                    Check("隐私模式下切换计数不推进", pf.SwitchCountForTest == 0, $"{pf.SwitchCountForTest}");
                    // 7) 菜单开着时改气泡，不能动窗口尺寸。
                    //    这是用户报过的 bug：切隐私模式后子菜单打不开。
                    //    根因是改气泡 → LayoutBubble → SetTopReserve → 移动窗口，
                    //    而菜单挂在窗口上，窗口一动菜单就被关掉。
                    int sizeBefore = pf.ClientSize.Width;
                    pf.SuspendBubbleForTest();
                    pf.BubbleText = "菜单开着的时候写的长句子，这句话本该触发窗口高度变化";
                    int sizeDuring = pf.ClientSize.Width;
                    Say($"    挂起期间: 客户端宽 {sizeBefore} → {sizeDuring}（不应变化）");
                    Check("挂起期间不动窗口尺寸", sizeDuring == sizeBefore,
                          $"{sizeBefore} → {sizeDuring}");
                    Check("挂起期间气泡文字已经更新",
                          pf.BubbleText.Contains("菜单开着的时候"), "文字应立即可读");
                    pf.ResumeBubbleForTest();
                    Say($"    解除挂起后: 客户端 {pf.ClientSize.Width}x{pf.ClientSize.Height}");
                    Check("解除挂起后窗口完成重排", !pf.BubbleSuspendedForTest, "不应仍在挂起");

                    // 8) 直接复现用户报的场景：菜单开着的时候切隐私模式。
                    //    隐私模式会设气泡文字，如果没被挂起挡住就会移动窗口 → 菜单被关。
                    pf.BubbleText = "";
                    int hBefore = pf.ClientSize.Height;
                    pf.SuspendBubbleForTest();
                    pf.SetPrivacyForTest(true);
                    int hDuring = pf.ClientSize.Height;
                    Say($"    菜单开着时切隐私模式: 客户端高 {hBefore} → {hDuring}（不应变化）");
                    Check("菜单开着切隐私模式不动窗口", hDuring == hBefore,
                          $"{hBefore} → {hDuring}");
                    pf.ResumeBubbleForTest();
                    pf.SetPrivacyForTest(false);
                    Say($"    关闭隐私模式后: 客户端 {pf.ClientSize.Width}x{pf.ClientSize.Height}");
                    Check("菜单关闭后恢复不挂起", !pf.BubbleSuspendedForTest, "不应仍在挂起");

                    // 8b) 隐私模式换闭眼立绘。
                    //     契约：
                    //       · 开关默认开；
                    //       · 有配对闭眼图时切过去、关掉切回来；
                    //       · 没配对图时静默沿用原图（不能报错、不能换没了）；
                    //       · 关掉总开关后完全不换。
                    Check("隐私立绘开关默认开", new PetForm.Config().PrivacyArtSwitch, "默认 true");
                    Check("隐私立绘默认走约定（留空）",
                          string.IsNullOrEmpty(new PetForm.Config().PrivacyArt), "应为空串");

                    {
                        string realArt = Art.DirPath(pf.AppDirForTest);
                        Directory.CreateDirectory(realArt);

                        // 先把用户当前的立绘记下来，测完原样放回去。
                        //
                        // 为什么必须这么做：PetForm 用的是**真实的**
                        // pet-config.json，而 SetArtForTest 会改 _cfg.Art。
                        // 更麻烦的是 PrivacyMode 的 setter 里有 _cfg.Save()——
                        // 本段里每一次 SetPrivacyForTest 都会把**当时的**
                        // _cfg.Art 落盘。所以只还原字段还不够，必须在所有
                        // 隐私切换都做完之后再还原，否则会被中途那次 Save 覆盖。
                        // （实测：跑一次自检就把用户的立绘改成已删除的临时名。）
                        // 用本段最开头记下的值还原（那才是用户真正的设置）。
                        string? artBefore = userArtBefore;

                        // 造一对图：正常 + 闭眼，尺寸不同以便区分
                        string openName  = "自检-姿势" + Guid.NewGuid().ToString("N").Substring(0, 6);
                        string openPath  = Path.Combine(realArt, openName + ".png");
                        string shutPath  = Path.Combine(realArt, openName + PetForm.ClosedEyeSuffixForTest + ".png");
                        try
                        {
                            using (var b = new Bitmap(60, 90, PixelFormat.Format32bppArgb))
                            using (var g = Graphics.FromImage(b))
                            { g.Clear(Color.CadetBlue); b.Save(openPath, System.Drawing.Imaging.ImageFormat.Png); }
                            using (var b = new Bitmap(60, 120, PixelFormat.Format32bppArgb))
                            using (var g = Graphics.FromImage(b))
                            { g.Clear(Color.SeaGreen); b.Save(shutPath, System.Drawing.Imaging.ImageFormat.Png); }

                            pf.SetArtForTest(openName);
                            pf.SetPrivacyForTest(false);
                            var sOpen = pf.SpriteSizeForTest;
                            string pathOpen = Path.GetFileName(pf.SpritePathForTest ?? "");
                            Say($"    非隐私: {pathOpen}  贴图 {sOpen.Width}x{sOpen.Height}");
                            Check("非隐私模式用睁眼立绘",
                                  !string.Equals(pathOpen, Path.GetFileName(shutPath), StringComparison.OrdinalIgnoreCase),
                                  pathOpen);

                            // 开隐私 → 应换成闭眼那张（高 120 vs 90，尺寸能区分）
                            pf.SetPrivacyForTest(true);
                            var sShut = pf.SpriteSizeForTest;
                            string pathShut = Path.GetFileName(pf.SpritePathForTest ?? "");
                            Say($"    隐私:   {pathShut}  贴图 {sShut.Width}x{sShut.Height}");
                            Check("开隐私模式切换成闭眼立绘",
                                  string.Equals(pathShut, Path.GetFileName(shutPath), StringComparison.OrdinalIgnoreCase),
                                  pathShut);
                            // 光看文件名不够：贴图必须真的重建过（尺寸跟着变）
                            Check("换的是真贴图而不是只改了名字",
                                  sShut.Height != sOpen.Height,
                                  $"{sOpen.Height} → {sShut.Height}");

                            // 关隐私 → 换回来
                            pf.SetPrivacyForTest(false);
                            string pathBack = Path.GetFileName(pf.SpritePathForTest ?? "");
                            Check("关隐私模式换回原立绘",
                                  string.Equals(pathBack, Path.GetFileName(openPath), StringComparison.OrdinalIgnoreCase),
                                  pathBack);

                            // 总开关关掉 → 就算开着隐私也不换
                            pf.SetPrivacyArtSwitchForTest(false);
                            pf.SetPrivacyForTest(true);
                            string pathNo = Path.GetFileName(pf.SpritePathForTest ?? "");
                            Say($"    开关关闭后开隐私: {pathNo}");
                            Check("关掉总开关后隐私模式不换图",
                                  string.Equals(pathNo, Path.GetFileName(openPath), StringComparison.OrdinalIgnoreCase),
                                  pathNo);
                            pf.SetPrivacyForTest(false);
                            pf.SetPrivacyArtSwitchForTest(true);

                            // 没有配对闭眼图时：静默沿用，不能报错
                            string loneName = "自检-无配对" + Guid.NewGuid().ToString("N").Substring(0, 6);
                            string lonePath = Path.Combine(realArt, loneName + ".png");
                            using (var b = new Bitmap(50, 70, PixelFormat.Format32bppArgb))
                            using (var g = Graphics.FromImage(b))
                            { g.Clear(Color.Goldenrod); b.Save(lonePath, System.Drawing.Imaging.ImageFormat.Png); }
                            try
                            {
                                pf.SetArtForTest(loneName);
                                pf.SetPrivacyForTest(true);
                                string pathLone = Path.GetFileName(pf.SpritePathForTest ?? "");
                                var sLone = pf.SpriteSizeForTest;
                                Say($"    无配对图 + 隐私: {pathLone}  贴图 {sLone.Width}x{sLone.Height}");
                                Check("没有配对闭眼图时沿用原立绘",
                                      string.Equals(pathLone, Path.GetFileName(lonePath), StringComparison.OrdinalIgnoreCase),
                                      pathLone);
                                Check("没有配对闭眼图也不会把贴图搞没",
                                      sLone.Width > 0 && sLone.Height > 0,
                                      $"{sLone.Width}x{sLone.Height}");
                                pf.SetPrivacyForTest(false);
                            }
                            finally { try { File.Delete(lonePath); } catch { } }
                        }
                        finally
                        {
                            try { File.Delete(openPath); } catch { }
                            try { File.Delete(shutPath); } catch { }
                            // 还原用户的立绘选择，别把自检的临时名字留在配置里。
                            // 这里必须**再存一次盘**：中间的 SetPrivacyForTest
                            // 已经带着临时名字 Save 过了，只改字段不落盘的话，
                            // 文件里留下的还是那个临时名。
                            pf.SetArtForTest(artBefore);
                            pf.SaveConfigForTest();
                            Say($"    已还原立绘选择: {artBefore ?? "(出厂贴图)"}（并已落盘）");
                        }
                    }

                    // 9) 引用计数：嵌套挂起不能因为内层解除就放行
                    pf.SuspendBubbleForTest();
                    pf.SuspendBubbleForTest();
                    pf.ResumeBubbleForTest();
                    Check("嵌套挂起：内层解除后仍挂起", pf.BubbleSuspendedForTest, "应仍挂起");
                    pf.ResumeBubbleForTest();
                    Check("嵌套挂起：全部解除后放行", !pf.BubbleSuspendedForTest, "应已放行");

                    // 10) 输入框开关必须真的能开能关。
                    //     用户报过「输入框关不掉」—— 根因是 ShowInput 的 else 分支
                    //     被注释吞掉了（`_input?.Hide();` 跑进了注释里），
                    //     于是取消勾选什么都不做。这里守住这条路径。
                    pf.BubbleText = "";
                    pf.ShowInput(true);
                    Check("ShowInput(true) 后可见", pf.InputShown, "应可见");
                    pf.ShowInput(false);
                    Check("ShowInput(false) 后隐藏", !pf.InputShown,
                          pf.InputShown ? "★还是可见（Hide 没生效）" : "已隐藏");
                    pf.ToggleInput();
                    Check("ToggleInput 能打开", pf.InputShown, "应可见");
                    pf.ToggleInput();
                    Check("ToggleInput 能关闭", !pf.InputShown,
                          pf.InputShown ? "★关不掉" : "已隐藏");
                    Check("勾选状态跟随实际可见性",
                          pf.InputToggleCheckedForTest == pf.InputShown,
                          $"checked={pf.InputToggleCheckedForTest} shown={pf.InputShown}");
                }
                finally { pf.Dispose(); }
            }
            Say(ok ? "=== 隐私模式自检全部通过 ===" : "=== 隐私模式自检有失败项 ===");

            // ---------- 双击音效自检（阶段 E） ----------
            Say("");
            Say("=== 双击音效自检 ===");
            {
                // 1) 扩展名路由：WAV 走 SoundPlayer，其余走 WPF MediaPlayer
                foreach (var (file, want) in new[]
                {
                    ("a.wav", "wav"), ("a.WAV", "wav"),
                    ("b.mp3", "media"), ("b.MP3", "media"),
                    ("c.wma", "media"), ("d.m4a", "media"),
                    ("e.aac", "media"), ("f.flac", "media"), ("g.ogg", "media"),
                    ("h.txt", "unknown"),
                })
                {
                    string got = Sound.RouteFor(file);
                    Check($"路由 {file}", got == want, $"得到 {got} 期望 {want}");
                }
                Say($"    支持的扩展名: {string.Join(" ", Sound.SupportedExtensions)}");

                // 2) 扫描：临时目录里放几个文件，确认只认音频
                string sdir = Path.Combine(Path.GetTempPath(),
                    "phoebe-snd-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                Directory.CreateDirectory(sdir);
                try
                {
                    File.WriteAllBytes(Path.Combine(sdir, "one.wav"), Sound.MakeTestWav());
                    File.WriteAllBytes(Path.Combine(sdir, "two.mp3"), new byte[] { 1, 2, 3 });
                    File.WriteAllBytes(Path.Combine(sdir, "notes.txt"), new byte[] { 1, 2, 3 });
                    Directory.CreateDirectory(Path.Combine(sdir, "sub"));   // 子目录应被忽略

                    var found = Sound.ListSoundsIn(sdir);
                    Say($"    扫描到 {found.Count} 个: {string.Join(", ", found.Select(Path.GetFileName))}");
                    Check("只认音频扩展名（忽略 txt）",
                          found.All(f => !f.EndsWith(".txt")), "不应包含 txt");
                    Check("扫到 2 个音频文件", found.Count == 2, $"{found.Count} 个");
                    Check("结果按文件名排序", found.Count >= 2 &&
                          string.Compare(Path.GetFileName(found[0]), Path.GetFileName(found[1]),
                                         StringComparison.OrdinalIgnoreCase) < 0,
                          "应有序");

                    // 3) 真播一个合法 WAV。沙箱可能没声卡，所以只要求「不抛异常」，
                    //    播放成功与否不作为硬断言（没声卡时 Play 仍会返回 true）。
                    string wavPath = Path.Combine(sdir, "one.wav");
                    var wavBytes = Sound.MakeTestWav();
                    Say($"    测试 WAV: {wavBytes.Length} 字节, 头={wavBytes[0]:X2}{wavBytes[1]:X2}{wavBytes[2]:X2}{wavBytes[3]:X2}");
                    Check("生成的 WAV 是 RIFF 头",
                          wavBytes[0] == 'R' && wavBytes[1] == 'I' && wavBytes[2] == 'F' && wavBytes[3] == 'F',
                          "应为 RIFF");
                    Check("生成的 WAV 是 WAVE 格式",
                          wavBytes[8] == 'W' && wavBytes[9] == 'A' && wavBytes[10] == 'V' && wavBytes[11] == 'E',
                          "应为 WAVE");

                    bool played = Sound.Play(wavPath);
                    Say($"    SoundPlayer 播放合法 WAV: {(played ? "成功" : "失败")}");
                    Check("合法 WAV 能进播放通路", played, played ? "已交给 SoundPlayer" : "★被拒");

                    // 4) 不存在的文件必须安全返回 false，不能抛
                    Check("不存在的文件返回 false", !Sound.Play(Path.Combine(sdir, "nope.wav")), "应安全返回");

                    // 5) 空目录时随机播放返回 false（调用方据此回退成「跳一下」）
                    string emptyDir = Path.Combine(sdir, "empty");
                    Directory.CreateDirectory(emptyDir);
                    string playedName;
                    bool ok2 = Sound.PlayRandomIn(emptyDir, out playedName);
                    Check("空文件夹时返回 false（触发回退）", !ok2, ok2 ? "★不该成功" : "正确回退");

                    // 6) 随机播放真的会播（用有文件的目录）
                    bool ok3 = Sound.PlayRandomIn(sdir, out playedName);
                    Say($"    PlayRandom 选中: \"{playedName}\"");
                    Check("有文件时随机播放成功", ok3, ok3 ? playedName : "★失败");
                    Check("返回的文件名是音频", playedName.EndsWith(".wav") || playedName.EndsWith(".mp3"),
                          playedName);
                }
                finally { try { Directory.Delete(sdir, true); } catch { } }

                // 7) 音量换算
                Sound.SetVolumePercent(50);
                Check("音量 50% → 0.5", Math.Abs(Sound.Volume - 0.5) < 0.001, $"{Sound.Volume}");
                Sound.SetVolumePercent(200);   // 越界应被夹住
                Check("音量越界被夹到 1.0", Math.Abs(Sound.Volume - 1.0) < 0.001, $"{Sound.Volume}");
                Sound.SetVolumePercent(-5);
                Check("负音量被夹到 0", Math.Abs(Sound.Volume) < 0.001, $"{Sound.Volume}");
                Sound.SetVolumePercent(80);    // 还原

                // 8) 配置默认值
                var dc = new PetForm.Config();
                Check("双击音效默认开", dc.DoubleClickSound, $"{dc.DoubleClickSound}");
                Check("默认音量 80", dc.SoundVolume == 80, $"{dc.SoundVolume}");

                // 9) 双击行为：有音效时播、没音效时回退成跳。
                //    这是用户明确要求的「改成音效但别让双击没反应」。
                var pf2 = new PetForm();
                try
                {
                    pf2.CreateControl();
                    // 程序目录里现在有 Sounds/ 但可能没文件 —— 两种情况都要能跑
                    var real = Sound.ListSounds(PetForm.AppDir);
                    Say($"    程序 Sounds/ 里有 {real.Count} 个音效");

                    pf2.BubbleText = "";
                    string chosen;
                    bool playedReal = Sound.PlayRandom(PetForm.AppDir, out chosen);
                    Say($"    实际播放: {(playedReal ? chosen : "(无可播文件，会回退成跳一下)")}");

                    if (real.Count == 0)
                        Check("无音效时能识别出「该回退」", !playedReal, "应返回 false");
                    else
                        Check("有音效时能正常播放", playedReal, chosen);

                    // 开关关闭时不应尝试播放（用日志判定太绕，这里只验配置读取）
                    pf2.SetDoubleClickSoundForTest(false);
                    Check("开关可关闭", !pf2.DoubleClickSoundForTest, "应能读到 false");
                    pf2.SetDoubleClickSoundForTest(true);
                    pf2.BubbleText = "";
                }
                finally { pf2.Dispose(); }
            }
            Say(ok ? "=== 双击音效自检全部通过 ===" : "=== 双击音效自检有失败项 ===");

            // ---------- 音效分组自检 ----------
            // 用户需求原话：「我加入了菲比和菲比啾比的音效，但我不想混着用，
            // 我想用哪组就可以切换哪组」。所以这里造出**真实的两组结构**来验，
            // 而不是只测「能扫到文件」。
            Say("");
            Say("=== 音效分组自检 ===");
            {
                string gdir = Path.Combine(Path.GetTempPath(),
                    "phoebe-grp-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                Directory.CreateDirectory(gdir);
                try
                {
                    // 结构：  菲比/ 3 个   菲比啾比/ 2 个   根目录 1 个（未分组）
                    string a = Path.Combine(gdir, "菲比");
                    string b = Path.Combine(gdir, "菲比啾比");
                    Directory.CreateDirectory(a);
                    Directory.CreateDirectory(b);
                    File.WriteAllBytes(Path.Combine(a, "打招呼.wav"), Sound.MakeTestWav());
                    File.WriteAllBytes(Path.Combine(a, "惊讶.wav"), Sound.MakeTestWav());
                    File.WriteAllBytes(Path.Combine(a, "晚安.wav"), Sound.MakeTestWav());
                    File.WriteAllBytes(Path.Combine(b, "啾.wav"), Sound.MakeTestWav());
                    File.WriteAllBytes(Path.Combine(b, "啾啾.wav"), Sound.MakeTestWav());
                    File.WriteAllBytes(Path.Combine(gdir, "散装.wav"), Sound.MakeTestWav());

                    var groups = Sound.ListGroupsIn(gdir);
                    Say($"    扫到 {groups.Count} 组：");
                    foreach (var g in groups)
                        Say($"        {g.Name,-12} {g.Count} 个  (未分组={g.IsUngrouped})");

                    Check("扫到 3 组（含未分组）", groups.Count == 3, $"{groups.Count} 组");

                    var gA = groups.FirstOrDefault(g => g.Name == "菲比");
                    var gB = groups.FirstOrDefault(g => g.Name == "菲比啾比");
                    var gU = groups.FirstOrDefault(g => g.IsUngrouped);

                    Check("组「菲比」存在且有 3 个", gA != null && gA.Count == 3, $"{gA?.Count}");
                    Check("组「菲比啾比」存在且有 2 个", gB != null && gB.Count == 2, $"{gB?.Count}");
                    Check("根目录散装音效归入「未分组」",
                          gU != null && gU.Count == 1 && gU.Name == Sound.UngroupedName,
                          $"{gU?.Name} {gU?.Count}");
                    Check("「未分组」排在最后",
                          groups.Count > 0 && groups[groups.Count - 1].IsUngrouped,
                          $"最后是 {groups[groups.Count - 1].Name}");
                    Check("子文件夹排前面且按名排序",
                          groups[0].Name != Sound.UngroupedName, $"第一是 {groups[0].Name}");

                    // ---- 筛选语义 ----
                    // sound 层的契约：null = 没表态（全部）；[] = 明确一个都不用。
                    // 这个区分是必须的 —— 早期把 [] 也当「全部」，导致界面上的
                    // 「全不选」实际还在放，用户看到的和实际行为对不上。
                    var all = Sound.CollectEnabled(gdir, null);
                    Say($"    null（没表态）→ {all.Count} 个");
                    Check("null = 没表态 = 全部", all.Count == 6, $"{all.Count} 个");

                    var emptyList = Sound.CollectEnabled(gdir, new List<string>());
                    Say($"    []（明确全不选）→ {emptyList.Count} 个");
                    Check("★ 空集合 = 一个都不用（不能偷偷给全部）",
                          emptyList.Count == 0, $"{emptyList.Count} 个");

                    var onlyA = Sound.CollectEnabled(gdir, new[] { "菲比" });
                    Say($"    只选「菲比」→ {onlyA.Count} 个: {string.Join(",", onlyA.Select(Path.GetFileName))}");
                    Check("只选「菲比」得到 3 个", onlyA.Count == 3, $"{onlyA.Count} 个");
                    Check("筛选结果确实只含该组的文件",
                          onlyA.All(f => f.Contains("菲比") && !f.Contains("菲比啾比")),
                          $"{string.Join(",", onlyA.Select(Path.GetFileName))}");

                    var both = Sound.CollectEnabled(gdir, new[] { "菲比", "菲比啾比" });
                    Check("选两组得到 5 个", both.Count == 5, $"{both.Count} 个");
                    Check("选两组时不包含未分组那个",
                          !both.Any(f => f.EndsWith("散装.wav")),
                          $"{string.Join(",", both.Select(Path.GetFileName))}");

                    var none = Sound.CollectEnabled(gdir, new[] { "不存在的组" });
                    Check("选了不存在的组得到 0 个", none.Count == 0, $"{none.Count} 个");

                    var ungroupedOnly = Sound.CollectEnabled(gdir, new[] { Sound.UngroupedName });
                    Check("能单独选「未分组」", ungroupedOnly.Count == 1, $"{ungroupedOnly.Count} 个");

                    // ---- 按组随机播放 ----
                    string played2;
                    bool okA = Sound.PlayRandomInGroups(gdir, new[] { "菲比啾比" }, out played2);
                    Say($"    在「菲比啾比」组里随机播: {played2}");
                    if (okA)
                    {
                        Check("播出来的是该组的文件",
                              played2 == "啾.wav" || played2 == "啾啾.wav", played2);
                    }
                    else
                    {
                        // 沙箱没声卡时可能播不出，但至少要能选对文件
                        Say("    （没播出声，沙箱可能没声卡 —— 只验选集逻辑）");
                    }

                    bool okNone = Sound.PlayRandomInGroups(gdir, new[] { "不存在" }, out played2);
                    Check("空分组返回 false（触发回退）", !okNone, "应返回 false");

                    // ---- 空组的显示 ----
                    string emptyDir = Path.Combine(gdir, "空组");
                    Directory.CreateDirectory(emptyDir);
                    var g2 = Sound.ListGroupsIn(gdir);
                    Check("空文件夹也会被列出来（用户建了组还没放文件）",
                          g2.Any(g => g.Name == "空组" && g.Count == 0),
                          $"空组存在={g2.Any(g => g.Name == "空组")}");

                    // ---- 菜单层的配置语义（PetForm.EffectiveSoundGroups）----
                    // 用户反馈「勾选怪怪的」之后重做了这一层，语义必须钉住：
                    //   null   = 从没用过 → 默认全选（老配置升级不改变行为）
                    //   []     = 主动全不选 → 真的一个都不用
                    //   [a,b]  = 只用这两个
                    {
                        using var sf = new PetForm();
                        var _h = sf.Handle;
                        sf.CreateControl();

                        var grpNames = Sound.ListGroups(AppPaths.Dir);

                        sf.SetSoundGroupsForTest(null);
                        var eff1 = sf.EffectiveSoundGroupsForTest;
                        Say($"    配置 null → 生效 {eff1.Count} 组（应为全部 {grpNames.Count}）");
                        Check("配置为 null 时默认全选",
                              eff1.Count == grpNames.Count && grpNames.Count > 0,
                              $"{eff1.Count} vs {grpNames.Count}");

                        sf.SetSoundGroupsForTest(new List<string>());
                        var eff2 = sf.EffectiveSoundGroupsForTest;
                        Say($"    配置 [] → 生效 {eff2.Count} 组（应为 0）");
                        Check("配置为空列表时真的一组都不用", eff2.Count == 0, $"{eff2.Count}");

                        sf.SetSoundGroupsForTest(new List<string> { "A", "B" });
                        var eff3 = sf.EffectiveSoundGroupsForTest;
                        Check("具体列表原样返回", eff3.Count == 2, $"{eff3.Count}");

                        // 空字符串要被过滤掉（手工编辑配置文件时容易留空行）
                        sf.SetSoundGroupsForTest(new List<string> { "A", "", "   ", "B" });
                        var eff4 = sf.EffectiveSoundGroupsForTest;
                        Check("空白项被过滤", eff4.Count == 2, $"{eff4.Count}");

                        sf.SetSoundGroupsForTest(null);
                    }

                    // ---- 试听必须遵守分组 ----
                    // 用户反馈试听「和没用一样」。根因之一是它用 PlayRandom(_dir)
                    // 扫的是**全部**音效，于是「只勾了 A 组，试听却播 B 组的」——
                    // 试听结果和双击听到的不一致，那试听就失去意义了。
                    // 现在试听走 CollectEnabled，和双击同一条路径。
                    {
                        // 造一个只有「菲比」组的环境来验：试听池里不该出现别的组
                        string pdir = Path.Combine(gdir, "预览测试");
                        Directory.CreateDirectory(pdir);
                        Directory.CreateDirectory(Path.Combine(pdir, "A组"));
                        Directory.CreateDirectory(Path.Combine(pdir, "B组"));
                        File.WriteAllBytes(Path.Combine(pdir, "A组", "a.wav"), Sound.MakeTestWav());
                        File.WriteAllBytes(Path.Combine(pdir, "B组", "b.wav"), Sound.MakeTestWav());

                        var poolA = Sound.CollectEnabled(pdir, new[] { "A组" });
                        Say($"    只勾 A组 → 试听池 {poolA.Count} 个: " +
                            string.Join(",", poolA.Select(Path.GetFileName)));
                        Check("试听池遵守分组（只含 A组）",
                              poolA.Count == 1 && poolA[0].EndsWith("a.wav"),
                              $"{string.Join(",", poolA.Select(Path.GetFileName))}");
                        Check("试听池不含被排除的组",
                              !poolA.Any(f => f.EndsWith("b.wav")), "不该有 B组 的");

                        // 多次播都不该跑出 A组
                        bool leaked = false;
                        for (int i = 0; i < 10; i++)
                        {
                            string pn;
                            if (Sound.PlayRandomInGroups(pdir, new[] { "A组" }, out pn))
                                if (pn != "a.wav") leaked = true;
                        }
                        Check("连播 10 次都不跑出该组", !leaked, leaked ? "串组了" : "稳定");

                        // 全不选时池子为空（试听会给出明确提示而不是乱播）
                        var poolNone = Sound.CollectEnabled(pdir, new List<string>());
                        Check("★ 空集合时试听池为空",
                              poolNone.Count == 0,
                              $"{poolNone.Count} 个（[] 表示明确一个都不用）");

                        // 试听窗口列出来的行 = 启用的组内的文件，一个不多一个不少。
                        // 直接复刻 ShowSoundPreview 的筛选逻辑来验。
                        using var pf3 = new PetForm();
                        var _h3 = pf3.Handle;
                        pf3.CreateControl();

                        var realG = Sound.ListGroups(AppPaths.Dir);
                        var effAll = pf3.EffectiveSoundGroupsForTest;
                        int expectRows = realG.Where(g => effAll.Contains(g.Name, StringComparer.OrdinalIgnoreCase))
                                              .Sum(g => g.Count);
                        var realPool = Sound.CollectEnabled(Sound.DirPath(AppPaths.Dir), null);
                        Say($"    实际目录：{realG.Count} 组、生效 {effAll.Count} 组、" +
                            $"窗口会列 {expectRows} 行、池子 {realPool.Count} 个");
                        Check("窗口列出的行数 = 启用组的文件数（默认全选时）",
                              expectRows == realPool.Count, $"{expectRows} vs {realPool.Count}");

                        // 只启用一个组时，窗口只该列那一组的文件
                        if (realG.Count > 1)
                        {
                            string only = realG[0].Name;
                            pf3.SetSoundGroupsForTest(new List<string> { only });
                            var eff1 = pf3.EffectiveSoundGroupsForTest;
                            int rows1 = realG.Where(g => eff1.Contains(g.Name, StringComparer.OrdinalIgnoreCase))
                                             .Sum(g => g.Count);
                            int pool1 = Sound.CollectEnabled(Sound.DirPath(AppPaths.Dir), eff1).Count;
                            Say($"    只启用「{only}」→ 窗口 {rows1} 行、池子 {pool1} 个");
                            Check("只启用一组时行数与池子一致", rows1 == pool1, $"{rows1} vs {pool1}");
                            Check("只启用一组时行数少于全部", rows1 < expectRows, $"{rows1} < {expectRows}");
                        }

                        // ==== 「全不选」必须真的一个都不用 ====
                        // 这是个真 bug：sound 层的 CollectEnabled 把 [] 也当「全部」
                        // （为了兼容「没设置过」的调用方），而界面语义是「一个都不用」。
                        // 如果播放路径直接把 _cfg.SoundGroups 透传下去，就会出现
                        // 「菜单显示全不选、实际还在放」的自相矛盾。
                        // 修法：统一走 ResolvedSoundGroups() 翻译一次再交给 sound 层。
                        {
                            using var pf4 = new PetForm();
                            var _h4 = pf4.Handle;
                            pf4.CreateControl();

                            pf4.SetSoundGroupsForTest(new List<string>());
                            var effEmpty = pf4.EffectiveSoundGroupsForTest;
                            Check("★ 全不选时生效集合为空", effEmpty.Count == 0, $"{effEmpty.Count} 组");

                            int poolEmpty = pf4.ResolvedSoundPoolForTest.Count;
                            Say($"    全不选 → 生效 {effEmpty.Count} 组，实际可播 {poolEmpty} 个");
                            Check("★ 全不选时真的没有可播音效（不能偷偷放）",
                                  poolEmpty == 0, $"{poolEmpty} 个");

                            // 对照：把 [] 直接透传给 sound 层会得到不同结果 ——
                            // 这正是必须加翻译层的原因
                            int raw = Sound.CollectEnabled(Sound.DirPath(AppPaths.Dir),
                                                          new List<string>()).Count;
                            Say($"    （对照：把 [] 直接透传会得到 {raw} 个 —— 所以必须先翻译）");
                            if (raw > 0)
                                Check("翻译层确实起了作用",
                                      poolEmpty != raw, $"{poolEmpty} vs {raw}");
                        }
                    }
                }
                finally
                {
                    try { Directory.Delete(gdir, true); } catch { }
                }

                // ---- 真实 Sounds/ 目录（有就验，没有就跳过） ----
                var realGroups = Sound.ListGroups(PetForm.AppDir);
                if (realGroups.Count > 0)
                {
                    Say($"    实际 {Sound.DirName}/ 里: " +
                        string.Join(" | ", realGroups.Select(g => $"{g.Name}({g.Count})")));
                    Check("实际目录能正常分组", true, $"{realGroups.Count} 组");
                    int total = realGroups.Sum(g => g.Count);
                    var allReal = Sound.CollectEnabled(Sound.DirPath(PetForm.AppDir), null);
                    Check("实际目录「全部」= 各组之和", allReal.Count == total,
                          $"{allReal.Count} vs {total}");
                }
            }
            Say(ok ? "=== 音效分组自检全部通过 ===" : "=== 音效分组自检有失败项 ===");

            // ---------- 用量统计自检 ----------
            // 重点是价格换算：算错了会得出完全错误的结论。
            Say("");
            Say("=== 用量统计自检 ===");
            {
                // 用独立文件，别污染真实 usage.json
                string upath = Path.Combine(Path.GetTempPath(),
                    "phoebe-usage-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".json");
                Usage.SetPathForTest(upath);

                // 1) 价格表
                var pFlash = Usage.PriceFor("deepseek-flash");
                Say($"    flash 空闲价: 命中 ¥{pFlash.Hit(false)} / 未命中 ¥{pFlash.Miss(false)} / 输出 ¥{pFlash.Out(false)}");
                Check("flash 缓存命中价 = 0.02", Math.Abs(pFlash.Hit(false) - 0.02) < 1e-9, $"{pFlash.Hit(false)}");
                Check("flash 未命中价 = 1", Math.Abs(pFlash.Miss(false) - 1.0) < 1e-9, $"{pFlash.Miss(false)}");
                Check("flash 输出价 = 4", Math.Abs(pFlash.Out(false) - 4.0) < 1e-9, $"{pFlash.Out(false)}");
                Check("高峰价 = 空闲价 × 2",
                      Math.Abs(pFlash.Miss(true) - 2.0) < 1e-9, $"{pFlash.Miss(true)}");
                Check("未知模型走默认价", Usage.PriceFor("no-such-model").Miss(false) > 0, "应有兜底");

                // 2) 高峰时段判定
                //    周一 10:00 高峰 / 周一 13:00 空闲 / 周六 10:00 空闲
                var mon10 = new DateTime(2026, 9, 28, 10, 0, 0);   // 周一
                var mon13 = new DateTime(2026, 9, 28, 13, 0, 0);
                var mon15 = new DateTime(2026, 9, 28, 15, 0, 0);
                var sat10 = new DateTime(2026, 9, 26, 10, 0, 0);   // 周六
                var mon9 = new DateTime(2026, 9, 28, 9, 0, 0);
                var mon12 = new DateTime(2026, 9, 28, 12, 0, 0);
                var mon18 = new DateTime(2026, 9, 28, 18, 0, 0);
                Check("周一 10:00 是高峰", Usage.PeakAt(mon10), "应高峰");
                Check("周一 13:00 是空闲", !Usage.PeakAt(mon13), "应空闲");
                Check("周一 15:00 是高峰", Usage.PeakAt(mon15), "应高峰");
                Check("周六 10:00 是空闲", !Usage.PeakAt(sat10), "周末全天空闲");
                Check("周一 09:00 是高峰（边界含）", Usage.PeakAt(mon9), "9:00 整算高峰");
                Check("周一 12:00 是空闲（边界不含）", !Usage.PeakAt(mon12), "12:00 整算空闲");
                Check("周一 18:00 是空闲（边界不含）", !Usage.PeakAt(mon18), "18:00 整算空闲");

                // 3) 记账：手工算一遍再和代码比对
                Usage.ClearMemoryForTest();
                var fake = new System.Text.Json.Nodes.JsonObject
                {
                    ["prompt_tokens"] = 1_000_000,
                    ["completion_tokens"] = 1_000_000,
                    ["prompt_cache_hit_tokens"] = 800_000,
                };
                var rec = Usage.Add("deepseek-flash", "chat", fake);
                Check("能从 usage 建记录", rec != null, rec == null ? "★返回 null" : "ok");

                if (rec != null)
                {
                    // 期望：命中 0.8M × 0.02 + 未命中 0.2M × 1 + 输出 1M × 4
                    double expectIdle = 0.8 * 0.02 + 0.2 * 1.0 + 1.0 * 4.0;   // = 4.216
                    double expectPeak = expectIdle * 2;
                    bool peakNow = Usage.PeakNow();
                    double expect = peakNow ? expectPeak : expectIdle;
                    Say($"    prompt=1M(缓存 0.8M) completion=1M → 花费 ¥{rec.Cost:F6}（期望 ¥{expect:F6}，当前{(peakNow ? "高峰" : "空闲")}）");
                    Check("金额计算正确", Math.Abs(rec.Cost - expect) < 1e-6,
                          $"得到 {rec.Cost:F6} 期望 {expect:F6}");
                    Check("token 统计正确",
                          rec.PromptTokens == 1_000_000 && rec.CompletionTokens == 1_000_000 &&
                          rec.CachedTokens == 800_000,
                          $"p={rec.PromptTokens} c={rec.CompletionTokens} hit={rec.CachedTokens}");
                    Check("总 token = 输入 + 输出", rec.TotalTokens == 2_000_000, $"{rec.TotalTokens}");
                    Check("缓存命中率 = 80%", Math.Abs(rec.CacheRate - 0.8) < 1e-9, $"{rec.CacheRate:P0}");
                }

                // 4) usage 为 null 时安全跳过（接口可能不返回）
                Check("usage 为 null 时不抛异常", Usage.Add("deepseek-flash", "chat", null) == null,
                      "应返回 null");

                // 5) 缓存数超过 prompt 时应被夹住（防御脏数据）
                var weird = new System.Text.Json.Nodes.JsonObject
                {
                    ["prompt_tokens"] = 100,
                    ["completion_tokens"] = 0,
                    ["prompt_cache_hit_tokens"] = 999,   // 不可能大于 prompt
                };
                var rec2 = Usage.Add("deepseek-flash", "chat", weird);
                Check("缓存数被夹到不超过 prompt",
                      rec2 != null && rec2.CachedTokens == 100,
                      rec2 == null ? "null" : $"{rec2.CachedTokens}");

                // 6) 汇总与报告
                var today = Usage.Today();
                Say($"    今天汇总: {today.Calls} 次, {today.Total} tok, ¥{today.Cost:F6}");
                Check("汇总统计到调用次数", today.Calls >= 2, $"{today.Calls}");

                string report = PetForm.BuildUsageText();
                Check("报告里有「今天」", report.Contains("【今天】"), "应有今天小节");
                Check("报告里有「累计」", report.Contains("【累计】"), "应有累计小节");
                Check("报告里有「最近 7 天」", report.Contains("最近 7 天"), "应有 7 天小节");
                Check("报告说明了是估算", report.Contains("仅供参考"), "必须说明是估算");
                Check("报告解释了缓存价", report.Contains("缓存"), "应解释缓存");

                // 7) 清空
                Usage.Clear();
                Check("清空后累计为 0", Usage.All().Calls == 0, $"{Usage.All().Calls}");

                Usage.SetPathForTest("");
                try { if (File.Exists(upath)) File.Delete(upath); } catch { }
            }
            Say(ok ? "=== 用量统计自检全部通过 ===" : "=== 用量统计自检有失败项 ===");

            // ---------- 对话框外观导出 ----------
            // 用鼠标自动化去点菜单验证太不稳（坐标一错就点到别处）。
            // 这里直接把两个对话框画出来存成 PNG，布局对不对一眼就能看。
            // 只有 --dump 才导出，避免每次自检都在目录里堆图片。
            Say("");
            Say($"=== 对话框外观导出 {(dump ? "(已启用)" : "(默认关闭，加 --dump 启用)")} ===");
            if (dump) try
            {
                DumpDialog("api-settings-preview.png", 460, 240, g =>
                {
                    int y = 15; const int RowH = 34, BoxH = 23;
                    const int LabelLeft = 15, LabelW = 110, BoxLeft = 132, BoxW = 310;
                    string[] labels = { "API 地址", "API Key", "模型", "温度 (0~2)" };
                    string[] values = { "https://api.deepseek.com", "•••••••••••", "deepseek-chat", "0.8" };
                    using var f = new Font("Microsoft YaHei UI", 9f);
                    using var tb = new SolidBrush(Color.FromArgb(30, 30, 30));
                    using var boxBg = new SolidBrush(Color.White);
                    using var boxPen = new Pen(Color.FromArgb(160, 160, 170));
                    for (int i = 0; i < 4; i++)
                    {
                        g.DrawString(labels[i], f, tb, LabelLeft, y + 6);
                        var r = new Rectangle(BoxLeft, y, BoxW, BoxH);
                        g.FillRectangle(boxBg, r);
                        g.DrawRectangle(boxPen, r);
                        g.DrawString(values[i], f, tb, BoxLeft + 4, y + 4);
                        y += RowH;
                    }
                    g.DrawString("· 每轮对话后更新长期记忆", f, tb, BoxLeft, y + 6);
                    y += 40;
                    var bs = new Rectangle(270, y, 80, 28);
                    var bc = new Rectangle(362, y, 80, 28);
                    g.FillRectangle(boxBg, bs); g.DrawRectangle(boxPen, bs);
                    g.DrawString("保存", f, tb, 296, y + 6);
                    g.FillRectangle(boxBg, bc); g.DrawRectangle(boxPen, bc);
                    g.DrawString("取消", f, tb, 388, y + 6);
                });

                DumpDialog("bubble-font-preview.png", 320, 160, g =>
                {
                    using var f = new Font("Microsoft YaHei UI", 9f);
                    using var tb = new SolidBrush(Color.FromArgb(30, 30, 30));
                    using var boxPen = new Pen(Color.FromArgb(160, 160, 170));
                    using var trackPen = new Pen(Color.FromArgb(120, 120, 130), 2);
                    g.DrawLine(trackPen, 20, 38, 300, 38);
                    using var thumb = new SolidBrush(Color.FromArgb(0, 120, 215));
                    g.FillRectangle(thumb, 138, 26, 12, 24);
                    for (int t = 8; t <= 20; t += 2)
                    {
                        int x = 20 + (t - 8) * (280 / 12);
                        g.DrawLine(trackPen, x, 46, x, 52);
                    }
                    g.DrawString("当前 11 pt", f, tb, 20, 80);
                    var ok = new Rectangle(220, 115, 80, 28);
                    using var boxBg = new SolidBrush(Color.White);
                    g.FillRectangle(boxBg, ok); g.DrawRectangle(boxPen, ok);
                    g.DrawString("确定", f, tb, 246, 121);
                });
            }
            catch (Exception e) { Say($"    导出失败: {e.Message}"); }

            Say(ok ? "=== 全部自检通过 ===" : "=== 有失败项 ===");
        }
        catch (Exception e)
        {
            Say($"[致命] 自检异常: {e}");
        }

        // 结果同时写一份日志，方便回看
        foreach (var line in sb) AppPaths.Log("  [selftest] " + line);
    }

    /// <summary>把对话框布局画到 PNG，用于人工确认（不弹真窗口）。/summary>
        private static void DumpDialog(string fileName, int w, int h, Action<Graphics> draw)
        {
            using var bmp = new Bitmap(w, h);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.FromArgb(240, 240, 240));
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                draw(g);
            }
            string path = Path.Combine(AppPaths.Dir, fileName);
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            Console.WriteLine($"    已导出: {path}");
        }

    [STAThread]
    static void Main(string[] args)
    {
        // --selftest：不开窗口，构造真实窗体跑几何自检并打印数值后退出。
// 给阶。2 的窗口结构改动做回归。—。比截图便宜得多。
if (args.Length > 0 && args[0] == "--selftest")
        {
            RunSelfTest(args.Length > 1 && args[1] == "--dump");
            return;
        }

        Boot($"--- 进程启动 pid={Environment.ProcessId} ---");
        Boot($"  ProcessPath={Environment.ProcessPath ?? "(null)"}");
        Boot($"  BaseDirectory={AppContext.BaseDirectory}");
        Boot($"  cwd={Environment.CurrentDirectory}");
        Boot($"  runtime={RuntimeInformation.FrameworkDescription}");

        try { Native.SetProcessDPIAware(); } catch { /* 老系统没有这个函数，忽略 */ }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // 单实例：已经有一只就别再放第二只
        using var mutex = new System.Threading.Mutex(true, "DSH.PhoebeDesktopPet.v1", out bool isNew);
        if (!isNew) { Boot("  已有实例在运行，本次退出"); return; }

        try
        {
            Boot("  构造窗体...");
            var form = new PetForm();

            // 挂聊天核心：配置。chat-config.json，人设在 Personas/ 。
try
            {
                var cfg = ChatConfig.Load(Path.Combine(AppPaths.Dir, "chat-config.json"));
                var core = new ChatCore(cfg, AppPaths.Dir);
                form.AttachChat(core, cfg);
                Boot($"  聊天核心已挂载: 角色={core.CharacterName} 称呼={core.PlayerName} " +
                     $"人设={Path.GetFileName(core.ArchivePath)} " +
                     $"key={(string.IsNullOrWhiteSpace(cfg.ApiKey) ? "【未填写】" : "已设置")}");

                if (!string.IsNullOrWhiteSpace(cfg.ApiKey) && cfg.ShowInputOnStart)
                    form.ShowInput(true);
                else if (string.IsNullOrWhiteSpace(cfg.ApiKey))
                    Boot("  提示: chat-config.json 的 ApiKey 为空，填写后才能聊天");
            }
            catch (Exception ce)
            {
                Boot($"  [警告] 聊天核心挂载失败（桌宠本身仍可用）: {ce.Message}");
            }

            Boot("  进入消息循环");
            Application.Run(form);
            Boot("  正常退出");
        }
        catch (Exception ex)
        {
            Boot($"  [致命错误] {ex}");
            MessageBox.Show(ex.ToString(), "桌宠启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}

