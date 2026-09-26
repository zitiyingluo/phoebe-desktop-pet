// art.cs —— 立绘（换装）管理
//
// 怎么用：
//   往程序目录的 Art/ 文件夹里丢 .png，文件名就是菜单里显示的名字。
//   比如 Art/菲比-校服.png → 菜单里出现「菲比-校服」。
//
// 为什么要单独一个文件夹而不是直接放程序目录：
//   程序目录已经有一堆文件（.cs / .json / .png / .cmd），
//   立绘混在里面分不清哪个是「正在用的」、哪个是备用的。
//   单独一个文件夹一目了然，加图也不用改代码。
//
// 和 build-pet-asset.mjs 生成的 phoebe.png / phoebe@2x.png 的关系：
//   那两张是**出厂默认**，Art/ 为空时回退到它们。
//   所以用户不建 Art/ 文件夹也能正常跑，行为与改动前完全一致。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PhoebePet
{
    /// <summary>一张可选立绘。</summary>
    public sealed class ArtItem
    {
        /// <summary>显示名（不含扩展名）。</summary>
        public string Name = "";

        /// <summary>完整路径。</summary>
        public string Path = "";

        /// <summary>是否正在使用。</summary>
        public bool Active;

        /// <summary>像素尺寸（读失败时为 0x0）。</summary>
        public int Width;
        public int Height;

        /// <summary>长宽比（高/宽）。用来判断换装后窗口会不会突然变很高。</summary>
        public double Aspect => Width > 0 ? Height / (double)Width : 1;

        public override string ToString() => $"{Name} ({Width}x{Height})";
    }

    public static class Art
    {
        /// <summary>立绘文件夹名（放在程序目录下）。</summary>
        public const string DirName = "Art";

        /// <summary>支持的图片扩展名。</summary>
        public static readonly string[] SupportedExtensions =
            { ".png", ".jpg", ".jpeg", ".bmp", ".gif" };

        /// <summary>立绘文件夹路径（不存在时也返回路径，便于「打开文件夹」）。</summary>
        public static string DirPath(string appDir) => System.IO.Path.Combine(appDir, DirName);

        /// <summary>确保文件夹存在。</summary>
        public static string EnsureDir(string appDir)
        {
            string d = DirPath(appDir);
            try { if (!Directory.Exists(d)) Directory.CreateDirectory(d); }
            catch (Exception e) { AppPaths.Log($"  [立绘] 建目录失败: {e.Message}"); }
            return d;
        }

        /// <summary>
        /// 扫描立绘文件夹。返回按文件名排序的列表。
        ///
        /// 尺寸是**读图片头**得到的（不是完整解码），失败就给 0 ——
        /// 一张坏图不该让整个菜单打不开。
        /// </summary>
        public static List<ArtItem> ListIn(string artDir, string? activePath = null)
        {
            var list = new List<ArtItem>();
            try
            {
                if (!Directory.Exists(artDir)) return list;

                foreach (var f in Directory.GetFiles(artDir).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    string ext = System.IO.Path.GetExtension(f).ToLowerInvariant();
                    if (!SupportedExtensions.Contains(ext)) continue;

                    var item = new ArtItem
                    {
                        Name = System.IO.Path.GetFileNameWithoutExtension(f),
                        Path = f,
                        Active = activePath != null &&
                                 string.Equals(System.IO.Path.GetFullPath(f),
                                               System.IO.Path.GetFullPath(activePath),
                                               StringComparison.OrdinalIgnoreCase),
                    };

                    // 只读尺寸，不解码整张图 —— 立绘可能有几 MB，
                    // 只是列个菜单没必要把它们全读进内存。
                    try
                    {
                        using var img = System.Drawing.Image.FromFile(f);
                        item.Width = img.Width;
                        item.Height = img.Height;
                    }
                    catch (Exception e)
                    {
                        AppPaths.Log($"  [立绘] 读不了尺寸 {System.IO.Path.GetFileName(f)}: {e.Message}");
                    }

                    list.Add(item);
                }
            }
            catch (Exception e)
            {
                AppPaths.Log($"  [立绘] 扫描失败: {e.Message}");
            }
            return list;
        }

        /// <summary>扫描程序目录下的 Art/。</summary>
        public static List<ArtItem> List(string appDir) => ListIn(DirPath(appDir));

        /// <summary>
        /// 把配置里存的值解析成实际图片路径。
        ///
        /// 支持两种写法（用户可读性优先）：
        ///   "菲比-校服"          → Art/菲比-校服.png（扩展名可省）
        ///   "Art/菲比-校服.png"  → 相对程序目录
        ///   "D:\pics\x.png"      → 绝对路径
        ///
        /// **找不到就回退出厂贴图**，绝不返回不存在的路径 ——
        /// 配置写错不该让菲比消失。
        /// </summary>
        public static string? Resolve(string appDir, string? configured,
                                      string fallbackFull, string fallbackSmall)
        {
            if (!string.IsNullOrWhiteSpace(configured))
            {
                string v = configured.Trim();

                // 绝对路径
                if (System.IO.Path.IsPathRooted(v))
                {
                    if (File.Exists(v)) return v;
                    AppPaths.Log($"  [立绘] 配置的绝对路径不存在: {v}");
                }
                else
                {
                    // 相对程序目录（可能带扩展名，也可能不带）
                    string p1 = System.IO.Path.Combine(appDir, v);
                    if (File.Exists(p1)) return p1;

                    // 没写扩展名 → 在 Art/ 里按名字找
                    string artDir = DirPath(appDir);
                    string baseName = System.IO.Path.GetFileNameWithoutExtension(v);

                    foreach (var ext in SupportedExtensions)
                    {
                        string cand = System.IO.Path.Combine(artDir, baseName + ext);
                        if (File.Exists(cand)) return cand;
                    }

                    // 用户可能直接写了文件名，也试着在 Art/ 下按原名找
                    string direct = System.IO.Path.Combine(artDir, v);
                    if (File.Exists(direct)) return direct;

                    AppPaths.Log($"  [立绘] 找不到配置的立绘「{v}」，回退出厂贴图");
                }
            }

            // 出厂默认：优先高分辨率版
            if (File.Exists(fallbackFull)) return fallbackFull;
            if (File.Exists(fallbackSmall)) return fallbackSmall;
            return null;
        }
    }
}
