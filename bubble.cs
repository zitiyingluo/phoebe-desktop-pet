// bubble.cs —— 气泡绘制（仿用户给的效果图）
//
// 样式：圆角白色气泡 + 深蓝描边 + 底部小尾巴，文字深蓝色居中。
// 纯 GDI+ 自绘，不依赖任何第三方库。
//
// 为什么单独一个文件：气泡的排版/换行/圆角都是纯计算，可以脱离窗口单测。
// 这与 pet.cs 里「先打印数值再截图」（README 踩坑第 8 条）的做法一致。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace PhoebePet
{
    /// <summary>气泡的配色与尺寸参数。</summary>
    public sealed class BubbleStyle
    {
        public Color Fill = Color.FromArgb(255, 255, 255);        // 白底
        public Color Border = Color.FromArgb(31, 56, 100);        // 深蓝描边
        public Color Text = Color.FromArgb(31, 56, 100);          // 深蓝文字
        public float BorderWidth = 3f;
        public int CornerRadius = 14;
        public int PadX = 14;                                     // 文字到边框的水平内边距
        public int PadY = 10;                                     // 垂直内边距
        public int TailWidth = 16;                                // 尾巴根部宽
        public int TailHeight = 12;                               // 尾巴伸出高度
        public float FontSize = 11f;
        public int MaxWidth = 240;                                // 气泡最大宽度
        public int LineSpacing = 2;                               // 行距

        /// <summary>
        /// 空行（段落之间的空行）占一行高度的比例。
        ///
        /// 为什么要压：模型特别爱用「\n\n」分段，于是每两段之间就多出一个空行。
        /// 按整行高算的话，4 段文本 3 个空行能吃掉 26% 的高度 —— 肉眼就是
        /// 「每一句中间空隙太大」（用户报过）。压到 0.55 行既能看出分段，
        /// 又不会把气泡撑得很空。
        /// </summary>
        public float BlankLineScale = 0.55f;
    }

    public static class Bubble
    {
        /// <summary>
        /// 把文字按最大宽度折行。中英文混排：中文按字断，英文尽量按单词断。
        /// 返回折好的行列表（至少一行，可能为空串）。
        /// </summary>
        public static List<string> WrapText(Graphics g, string text, Font font, int maxTextWidth)
        {
            var lines = new List<string>();
            if (string.IsNullOrEmpty(text)) { lines.Add(""); return lines; }

            // 先按显式换行切段，段内再自动折行
            foreach (var rawPara in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                string para = rawPara;
                if (para.Length == 0) { lines.Add(""); continue; }

                var cur = new System.Text.StringBuilder();
                int i = 0;
                while (i < para.Length)
                {
                    // 取下一个「断点单元」：一个中文字，或一个英文单词/空格
                    int unitLen = MeasureUnit(para, i);
                    string unit = para.Substring(i, unitLen);
                    string test = cur.ToString() + unit;

                    if (cur.Length > 0 && g.MeasureString(test, font).Width > maxTextWidth)
                    {
                        lines.Add(cur.ToString());
                        cur.Clear();
                        // 折行后行首若是空格，丢掉，避免视觉缩进
                        if (unit == " ") { i += unitLen; continue; }
                    }
                    cur.Append(unit);
                    i += unitLen;
                }
                lines.Add(cur.ToString());
            }
            if (lines.Count == 0) lines.Add("");
            return lines;
        }

        /// <summary>从位置 i 开始，取一个不可分割的排版单元的长度。</summary>
        private static int MeasureUnit(string s, int i)
        {
            char c = s[i];
            // CJK / 全角标点：一字一断，但标点要「贴」在前一个字后面，
            // 否则会出现「……好久」+「！」这种把感叹号挤到下一行的难看断法。
            if (IsCjk(c))
            {
                int j = i + 1;
                while (j < s.Length && IsNoLineStart(s[j])) j++;
                return j - i;
            }
            if (c == ' ') return 1;
            int k = i;
            while (k < s.Length && s[k] != ' ' && !IsCjk(s[k])) k++;
            return Math.Max(1, k - i);
        }

        /// <summary>不能出现在行首的字符（收尾标点、后引号等）。</summary>
        private static bool IsNoLineStart(char c)
        {
            const string noStart = "，。！？；：、）」』】》〉”’…—～·!?,.;:)]}";
            return noStart.IndexOf(c) >= 0;
        }

        private static bool IsCjk(char c)
        {
            return (c >= 0x4E00 && c <= 0x9FFF)     // 中日韩统一表意
                || (c >= 0x3000 && c <= 0x303F)     // CJK 标点
                || (c >= 0xFF00 && c <= 0xFFEF);    // 全角字符
        }

        /// <summary>
        /// 一行的实际行高。
        ///
        /// 为什么不能用 font.GetHeight()：那是字体的**设计行高**，
        /// 而 DrawString 实际占的高度是 MeasureString 的返回值。两者不等 ——
        /// 实测 11pt 下 GetHeight=16.60 而中文实画 18.43（差 11%）。
        /// 用 GetHeight 排版会让 Measure() 算出的高度比真实需要的小，
        /// 于是气泡框偏矮、文字挤在一起或溢出（用户报过这个）。
        ///
        /// 解决：**排版和测量共用这一个函数**，保证两者永远一致。
        /// 用固定探针串取高度，避免空行取到 0 导致行距塌陷。
        /// </summary>
        public static float LineHeight(Graphics g, Font font)
        {
            // 探针用汉字：拉丁字符的高度和 CJK 不同，气泡里主要是中文。
            var sz = g.MeasureString(ProbeText, font);
            float h = sz.Height;
            // 兜底：万一探针测出 0（字体坏了），退回设计行高而不是给 0
            return h > 0.5f ? h : font.GetHeight(g);
        }

        /// <summary>量行高用的探针串（有汉字也有标点，覆盖真实排版）。</summary>
        private const string ProbeText = "中文Ag";

        /// <summary>
        /// 一行占的垂直高度（含行距）。空行按 BlankLineScale 压缩。
        ///
        /// **测量和绘制都必须走这里**，否则框和字会对不上。
        /// </summary>
        public static float LineAdvance(string line, float lineH, BubbleStyle st)
        {
            bool blank = string.IsNullOrEmpty(line) || line.Trim().Length == 0;
            return blank
                ? lineH * st.BlankLineScale + st.LineSpacing
                : lineH + st.LineSpacing;
        }

        /// <summary>整段文本需要的文字区高度（不含 PadY）。</summary>
        public static float TextBlockHeight(List<string> lines, float lineH, BubbleStyle st)
        {
            if (lines.Count == 0) return 0;
            float h = 0;
            for (int i = 0; i < lines.Count; i++)
            {
                h += LineAdvance(lines[i], lineH, st);
                // 最后一行不带行距 —— 行距是「行与行之间」的间隔，不是行内边距
                if (i == lines.Count - 1)
                    h -= st.LineSpacing;
            }
            return h;
        }

        /// <summary>量出气泡（不含尾巴）需要的尺寸。</summary>
        public static Size Measure(Graphics g, string text, BubbleStyle st, Font font)
        {
            int maxTextW = Math.Max(20, st.MaxWidth - st.PadX * 2);
            var lines = WrapText(g, text, font, maxTextW);

            float widest = 0;
            foreach (var ln in lines)
                if (ln.Length > 0) widest = Math.Max(widest, g.MeasureString(ln, font).Width);

            float lineH = LineHeight(g, font);
            float textW = Math.Min(maxTextW, widest);
            float textH = TextBlockHeight(lines, lineH, st);

            int w = (int)Math.Ceiling(textW) + st.PadX * 2;
            int h = (int)Math.Ceiling(textH) + st.PadY * 2;
            return new Size(Math.Max(w, 40), Math.Max(h, 24));
        }

        /// <summary>
        /// 在 g 上画一个气泡。左上角在 (x,y)，尺寸 w x h，尾巴朝下、水平居于 tailCx。
        /// text 传 null/空 则只画框不画字（用于静态阶段自检）。
        /// </summary>
        public static void Draw(Graphics g, string text, BubbleStyle st, Font font,
                                float x, float y, float w, float h, float tailCx)
        {
            var prevMode = g.SmoothingMode;
            var prevText = g.TextRenderingHint;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            float r = st.CornerRadius;

            // 主体圆角矩形 + 尾巴，合成一条路径，这样描边不会在接缝处断开
            using var path = new GraphicsPath();
            var body = RoundedRect(new RectangleF(x, y, w, h), r);
            path.AddPath(body, false);

            // 尾巴：从底边伸出的三角，左右各留一点与圆角错开
            float tw = st.TailWidth, th = st.TailHeight;
            float cx = Math.Max(x + r + tw / 2, Math.Min(tailCx, x + w - r - tw / 2));
            var tail = new GraphicsPath();
            tail.AddPolygon(new[]
            {
                new PointF(cx - tw / 2, y + h - st.BorderWidth / 2),
                new PointF(cx + tw / 2, y + h - st.BorderWidth / 2),
                new PointF(cx,           y + h + th),
            });
            path.AddPath(tail, false);

            using (var fill = new SolidBrush(st.Fill))
                g.FillPath(fill, path);
            using (var pen = new Pen(st.Border, st.BorderWidth) { LineJoin = LineJoin.Round })
                g.DrawPath(pen, path);

            // 文字
            if (!string.IsNullOrEmpty(text))
            {
                int maxTextW = Math.Max(20, st.MaxWidth - st.PadX * 2);
                var lines = WrapText(g, text, font, maxTextW);
                // 必须和 Measure() 用同一个行高 —— 否则框和字对不上
                float lineH = LineHeight(g, font);
                float ty = y + st.PadY;
                using var tb = new SolidBrush(st.Text);
                var fmt = new StringFormat(StringFormatFlags.NoWrap)
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Near,
                };
                foreach (var ln in lines)
                {
                    // 空行不画字，只推进一点点高度（见 BlankLineScale）
                    bool blank = string.IsNullOrEmpty(ln) || ln.Trim().Length == 0;
                    if (!blank)
                    {
                        // 用整行高度做矩形、LineAlignment=Near 顶部对齐
                        g.DrawString(ln, font, tb,
                            new RectangleF(x + st.PadX, ty, w - st.PadX * 2, lineH), fmt);
                    }
                    ty += LineAdvance(ln, lineH, st);
                }
                fmt.Dispose();
            }

            g.SmoothingMode = prevMode;
            g.TextRenderingHint = prevText;
        }

        /// <summary>
        /// 自检/预览用：把一段文字画成一张独立 PNG（深色底 + 气泡）。
        /// 返回文件路径。用于肉眼确认排版，不需要启动桌宠。
        /// </summary>
        public static string RenderPreviewToPng(string text, string outPath,
                                                BubbleStyle st, Font font, int pad = 12)
        {
            Size size;
            using (var probe = Graphics.FromImage(new Bitmap(1, 1)))
                size = Measure(probe, text, st, font);

            int w = size.Width + pad * 2;
            int h = size.Height + st.TailHeight + pad * 2;
            using var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.FromArgb(255, 32, 38, 52));
                Draw(g, text, st, font, pad, pad, size.Width, size.Height, w / 2f);
            }
            bmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
            return outPath;
        }

        /// <summary>
        /// 构造圆角矩形路径。
        ///
        /// 注意：四段 AddArc 之间必须显式 AddLine 连接。
        /// AddArc 只负责画弧本身，不会把上一段弧的终点连到下一段弧的起点；
        /// 中间那四条直边如果不补线，路径就是断的（右边和下边尤其明显）。
        /// 之前气泡自绘没暴露这个问题 —— 气泡是白色填充，缺一条边看不出来；
        /// 输入框拿它做窗口区域（SetWindowRgn）时才炸出来：边线上的像素
        /// 被判成区域外，描边缺一截。
        /// </summary>
        public static GraphicsPath RoundedRect(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 0) { p.AddRectangle(r); return p; }

            // 顺时针：左上弧 -> 上边 -> 右上弧 -> 右边 -> 右下弧 -> 下边 -> 左下弧 -> 左边
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddLine(r.X + d / 2, r.Y, r.Right - d / 2, r.Y);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddLine(r.Right, r.Y + d / 2, r.Right, r.Bottom - d / 2);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddLine(r.Right - d / 2, r.Bottom, r.X + d / 2, r.Bottom);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.AddLine(r.X, r.Bottom - d / 2, r.X, r.Y + d / 2);
            p.CloseFigure();
            return p;
        }
    }
}
