// inputbox.cs —— 桌宠的输入框（独立窗口）
//
// 为什么单独一个窗口（而不是画在宠物的分层窗口里）：
//   分层窗口（UpdateLayeredWindow）是把一整张位图推上去的，任何子控件都会被
//   覆盖掉。要在里面做输入框，就得自己实现光标闪烁、选区、剪贴板、尤其是
//   **输入法候选窗定位** —— 中文输入法这块极容易翻车。
//   独立窗口挂系统原生 TextBox，上面这些全部免费拿到。
//
// 为什么这样还能保住「点了桌宠不顶掉当前窗口」：
//   宠物窗口始终是 WS_EX_NOACTIVATE，本窗口只在被点击时才激活。
//   两者互不影响。

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PhoebePet
{
    public sealed class InputBoxStyle
    {
        public Color Fill = Color.FromArgb(250, 250, 252);
        public Color Border = Color.FromArgb(31, 56, 100);
        public Color Text = Color.FromArgb(31, 56, 100);
        public Color Placeholder = Color.FromArgb(150, 150, 165);
        public float BorderWidth = 2f;
        public int CornerRadius = 12;
        public float FontSize = 10.5f;
        public int PadX = 10;
        public int PadY = 7;
    }

    /// <summary>无边框圆角输入框。回车发送，Shift+回车换行，Esc 失焦。</summary>
    public sealed class InputBoxWindow : Form
    {
        private readonly InputBoxStyle _st = new InputBoxStyle();
        private readonly TextBox _text = new TextBox();
        private readonly Font _font;
        private bool _placeholderOn;
        private string _placeholder = "说点什么…";

        /// <summary>按下回车（非 Shift）时触发，参数是输入内容。</summary>
        public event Action<string>? Submitted;

        /// <summary>输入框内容变化。</summary>
        public event Action<string>? TextChanged2;

        public InputBoxWindow()
        {
            _font = PetForm.CreateBubbleFont(_st.FontSize);

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            // 不占 Alt+Tab；ToolWindow 让它不出现在任务栏
            ShowInTaskbar = false;
            Text = "菲比 · 输入";

            BackColor = Color.FromArgb(250, 250, 252);
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

            _text.BorderStyle = BorderStyle.None;
            _text.Font = _font;
            _text.ForeColor = _st.Text;
            _text.BackColor = _st.Fill;
            _text.Multiline = true;          // 允许长文本自动换行；回车另作处理
            _text.AcceptsReturn = false;     // 回车不插入换行，交给 KeyDown 处理
            _text.ScrollBars = ScrollBars.None;

            Controls.Add(_text);
            _text.KeyDown += OnTextKeyDown;
            _text.TextChanged += (_, _) => { UpdatePlaceholder(); TextChanged2?.Invoke(_text.Text); };
            _text.GotFocus += (_, _) => { if (_placeholderOn) ClearPlaceholder(); Invalidate(); };
            _text.LostFocus += (_, _) => { if (_text.Text.Length == 0) SetPlaceholder(); Invalidate(); };
            _text.MouseDown += (_, _) => Invalidate();

            SetPlaceholder();
            LayoutText();
            Size = new Size(228, 34);
        }

        /// <summary>当前真实输入内容（占位符不算）。</summary>
        public string Value => _placeholderOn ? "" : _text.Text;

        public string Placeholder
        {
            get => _placeholder;
            set { _placeholder = value ?? ""; if (_placeholderOn) { _text.Text = _placeholder; } }
        }

        /// <summary>高度足够时显示多行。字数多就长高，最多 4 行。</summary>
        public int DesiredHeight
        {
            get
            {
                int lines = Math.Min(4, Math.Max(1, _text.Lines.Length));
                // GetLineFromCharIndex 在长文本自动折行时才准；这里按换行数近似
                if (!_text.Multiline) lines = 1;
                int lineH = _text.Font.Height;
                return lines * lineH + _st.PadY * 2 + (int)_st.BorderWidth * 2 + 2;
            }
        }

        private void LayoutText()
        {
            int bw = (int)Math.Ceiling(_st.BorderWidth);
            _text.SetBounds(
                bw + _st.PadX,
                bw + _st.PadY,
                Math.Max(10, ClientSize.Width - (bw + _st.PadX) * 2),
                Math.Max(10, ClientSize.Height - (bw + _st.PadY) * 2));
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutText();
            ApplyRegion();
        }

        private void OnTextKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Return && !e.Shift)
            {
                e.SuppressKeyPress = true;
                e.Handled = true;
                string v = Value.Trim();
                if (v.Length > 0)
                {
                    Submitted?.Invoke(v);
                    Clear();
                }
            }
            else if (e.KeyCode == Keys.Escape)
            {
                e.SuppressKeyPress = true;
                e.Handled = true;
                Clear();
                // 交给宠物窗口（不激活它），并把焦点还给用户原本的窗口
                Native.SetFocus(IntPtr.Zero);
            }
        }

        /// <summary>清空内容并恢复占位符。</summary>
        public void Clear()
        {
            _text.Text = "";
            SetPlaceholder();
        }

        /// <summary>
        /// 把内容放回输入框并把光标移到末尾。
        ///
        /// 用途：请求失败时**不能让用户白打一遍字**。
        /// 原来是发出去就清空，一旦网络断了或者余额不足，
        /// 那段话就没了，只能凭记忆重敲。现在失败分支会把原文还回来。
        /// </summary>
        public void SetValue(string text)
        {
            ClearPlaceholder();
            _text.Text = text ?? "";
            _text.SelectionStart = _text.TextLength;
            _text.SelectionLength = 0;
        }

        /// <summary>把焦点交给输入框（会激活本窗口，从而中文输入法正常工作）。</summary>
        public void FocusInput()
        {
            if (!Visible) Show();
            Activate();
            _text.Focus();
            if (_placeholderOn) ClearPlaceholder();
            Native.SetFocus(_text.Handle);
        }

        /// <summary>供自检读取：当前实际生效的窗口区域类型与包围盒。</summary>
        public string RegionInfo
        {
            get
            {
                IntPtr h = Native.GetWindowRgn(Handle);
                if (h == IntPtr.Zero) return "无区域(矩形窗口)";
                Native.RECT r;
                int type = Native.GetRgnBox(h, out r);                // GetWindowRgn 返回的是句柄副本，用完要自己释放
                Native.DeleteObject(h);
                string kind = type switch
                {
                    1 => "NULLREGION(空)",
                    2 => "SIMPLEREGION(单矩形)",
                    3 => "COMPLEXREGION(圆角)",
                    _ => "ERROR(无效)",
                };
                return $"{kind} (0,0)-({r.Right},{r.Bottom}) 窗口 {Width}x{Height}";
            }
        }

        private void SetPlaceholder()
        {
            _placeholderOn = true;
            _text.ForeColor = _st.Placeholder;
            _text.Text = _placeholder;
        }

        private void ClearPlaceholder()
        {
            if (!_placeholderOn) return;
            _placeholderOn = false;
            _text.Text = "";
            _text.ForeColor = _st.Text;
        }

        private void UpdatePlaceholder()
        {
            // 用户开始打字就恢复正常颜色
            if (_placeholderOn && _text.Text != _placeholder) _placeholderOn = false;
            if (!_placeholderOn) _text.ForeColor = _st.Text;
        }

        // 圆角 + 描边自绘（底色用 BackColor 铺，这里只画边框和圆角）
        //
        // 关于四个角的白边：本窗口是普通窗口，没有逐像素透明（那是宠物分层窗口
        // 才有的能力），只有两种透明手段：
        //   1) TransparencyKey —— 色键透明，反锯齿的过渡像素会被判成「不等于色键」
        //      而保留下来，圆角外沿留一圈半白杂边；
        //   2) Region 裁剪     —— 硬边，但不留杂色。
        // 这里选 2：把窗口形状直接改成圆角矩形，圆角外面根本不属于本窗口，
        // 自然没有白边。代价是圆角本身是锯齿状 —— 对 12px 半径、2px 描边来说
        // 看不出来，换来的是彻底干净的四角。
        private void ApplyRegion()
        {
            RegionApplyCount++;

            if (Width <= 0 || Height <= 0)
            {
                ApplyRegionLog.Add($"#{RegionApplyCount} 跳过: 尺寸 {Width}x{Height}");
                return;
            }

            using var path = Bubble.RoundedRect(
                new RectangleF(0, 0, Width, Height), _st.CornerRadius);

            // SetWindowRgn 会接管这个 HRGN 的所有权，之后不能自己 DeleteObject
            IntPtr h = Native.PathToHrgn(path);
            if (h == IntPtr.Zero)
            {
                ApplyRegionLog.Add($"#{RegionApplyCount} 跳过: PathToHrgn 返回 0");
                return;
            }

            int ok = Native.SetWindowRgn(Handle, h, true);
            if (ok == 0)
            {
                // 设置失败时所有权仍在我们手上，得自己释放，否则泄漏 GDI 对象
                Native.DeleteObject(h);
            }
            RegionAppliedSize = $"{Width}x{Height}";
            ApplyRegionLog.Add($"#{RegionApplyCount} Handle=0x{Handle.ToInt64():X} "
                             + $"{Width}x{Height} SetWindowRgn={ok}");
        }

        /// <summary>自检用：ApplyRegion 被调用的次数。</summary>
        public int RegionApplyCount { get; private set; }

        /// <summary>自检用：最后一次计算区域时用的窗口尺寸。</summary>
        public string RegionAppliedSize { get; private set; } = "(未应用)";

        /// <summary>自检用：每次 ApplyRegion 的轨迹（句柄、尺寸、SetWindowRgn 返回值）。</summary>
        public readonly System.Collections.Generic.List<string> ApplyRegionLog =
            new System.Collections.Generic.List<string>();

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(_st.Fill);

            // 描边要落在可见区域内，所以往里缩 BorderWidth/2
            using var path = Bubble.RoundedRect(
                new RectangleF(_st.BorderWidth / 2, _st.BorderWidth / 2,
                               Width - _st.BorderWidth, Height - _st.BorderWidth),
                _st.CornerRadius);
            using var pen = new Pen(_st.Border, _st.BorderWidth) { LineJoin = LineJoin.Round };
            g.DrawPath(pen, path);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // 工具窗口：不进任务栏、不参与 Alt+Tab
            var ex = Native.GetWindowLong(Handle, Native.GWL_EXSTYLE).ToInt64();
            ex |= Native.WS_EX_TOOLWINDOW;
            Native.SetWindowLong(Handle, Native.GWL_EXSTYLE, (IntPtr)ex);
            ApplyRegion();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _font.Dispose();
            base.Dispose(disposing);
        }
    }
}
