// sound.cs —— 双击音效
//
// 用户把音频文件丢进 Sounds/ 文件夹，双击桌宠时随机播一个。
//
// 为什么分两条播放路径：
//   WAV  → System.Media.SoundPlayer（走 winmm 的 PlaySound，最简单最稳）
//   其他 → WPF 的 MediaPlayer（支持 MP3 / WMA / M4A / AAC 等）
// 两条都不用第三方包。WPF 程序集（PresentationCore / WindowsBase）随
// .NET 桌面运行时一起装，本项目已经引用得到（实测 MediaPlayer 可实例化）。
//
// 为什么不能只用 MediaPlayer 播 WAV：
//   它对 WAV 的支持依赖系统解码器，个别机器上会静默失败。WAV 是最常见的
//   素材格式，所以给它一条更可靠的独立通路。
//
// 播放失败一律静默降级（返回 false），由调用方决定要不要回退成「跳一下」。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Media;

namespace PhoebePet
{
    /// <summary>双击音效播放器。</summary>
    public static class Sound
    {
        /// <summary>音效文件夹名（放在程序目录下）。</summary>
        public const string DirName = "Sounds";

        /// <summary>支持的扩展名。WAV 走 SoundPlayer，其余走 WPF MediaPlayer。</summary>
        private static readonly string[] WavExt = { ".wav" };
        private static readonly string[] MediaExt = { ".mp3", ".wma", ".m4a", ".aac", ".flac", ".ogg" };

        /// <summary>普通音量 0..1。</summary>
        public static double Volume = 0.8;

        private static readonly Random Rng = new Random();

        // MediaPlayer 不能同时播多个；复用一个实例并显式 Close，避免句柄泄漏。
        // 全程在 UI 线程使用（双击走的是窗口消息），所以不用加锁。
        private static System.Windows.Media.MediaPlayer? _player;
        private static SoundPlayer? _wavPlayer;

        /// <summary>
        /// 扫描音效文件夹，返回所有可播放文件的完整路径。
        ///
        /// 参数是**音效文件夹本身**（不是程序目录）—— 早期版本内部又拼了一次
        /// "Sounds"，导致拿目录当参数调用时永远扫不到东西。已改成直给目录。
        /// </summary>
        public static List<string> ListSoundsIn(string soundDir)
        {
            var list = new List<string>();
            try
            {
                if (!Directory.Exists(soundDir)) return list;

                foreach (var f in Directory.GetFiles(soundDir))
                {
                    string ext = Path.GetExtension(f).ToLowerInvariant();
                    if (WavExt.Contains(ext) || MediaExt.Contains(ext)) list.Add(f);
                }
                list.Sort(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception e)
            {
                AppPaths.Log($"  [音效] 扫描失败: {e.Message}");
            }
            return list;
        }

        /// <summary>扫描程序目录下的 Sounds/ 文件夹。参数是程序目录。</summary>
        public static List<string> ListSounds(string appDir) => ListSoundsIn(DirPath(appDir));

        // ---------- 音效分组 ----------
        //
        // 怎么用：在 Sounds/ 下建子文件夹，文件夹名就是组名。
        //   Sounds/菲比/*.wav        → 组「菲比」
        //   Sounds/菲比啾比/*.wav    → 组「菲比啾比」
        //   Sounds/*.wav            → 属于「（未分组）」
        //
        // 为什么要分组：不同角色的音效混在一起随机播会很出戏
        // （用户原话：「我加入了菲比和菲比啾比的音效，但我不想混着用」）。
        // 分完组可以整组切换，也可以同时启用多组。
        //
        // 分组名和内容**完全由用户自定义** —— 代码里没有任何硬编码的组名，
        // 扫描到什么就是什么。加一组不用改代码。

        /// <summary>未分组音效的显示名（直接放在 Sounds/ 根目录下的那些）。</summary>
        public const string UngroupedName = "（未分组）";

        /// <summary>一个音效分组。</summary>
        public sealed class SoundGroup
        {
            /// <summary>组名（= 子文件夹名，或 UngroupedName）。</summary>
            public string Name = "";

            /// <summary>文件夹完整路径。</summary>
            public string Dir = "";

            /// <summary>组内的音效文件完整路径。</summary>
            public List<string> Files = new();

            /// <summary>是否是「未分组」（根目录下的散装音效）。</summary>
            public bool IsUngrouped;

            public int Count => Files.Count;

            public override string ToString() => $"{Name}（{Count} 个）";
        }

        /// <summary>
        /// 扫描所有分组。
        ///
        /// 顺序：子文件夹按名字排序在前，「（未分组）」永远排最后 ——
        /// 它是个兜底分类，不是用户主动建的组，放后面不干扰视线。
        /// 空文件夹也会列出来（用户建了组还没来得及放音效时需要看到它）。
        /// </summary>
        public static List<SoundGroup> ListGroupsIn(string soundDir)
        {
            var groups = new List<SoundGroup>();
            try
            {
                if (!Directory.Exists(soundDir)) return groups;

                // 1) 子文件夹 = 分组
                foreach (var d in Directory.GetDirectories(soundDir)
                                          .OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                {
                    var g = new SoundGroup
                    {
                        Name = Path.GetFileName(d),
                        Dir = d,
                        Files = ListSoundsIn(d),
                    };
                    groups.Add(g);
                }

                // 2) 根目录下的散装音效 = 未分组（放最后）
                var loose = ListSoundsIn(soundDir);
                if (loose.Count > 0)
                {
                    groups.Add(new SoundGroup
                    {
                        Name = UngroupedName,
                        Dir = soundDir,
                        Files = loose,
                        IsUngrouped = true,
                    });
                }
            }
            catch (Exception e)
            {
                AppPaths.Log($"  [音效] 分组扫描失败: {e.Message}");
            }
            return groups;
        }

        /// <summary>扫描程序目录下的 Sounds/ 的所有分组。</summary>
        public static List<SoundGroup> ListGroups(string appDir)
            => ListGroupsIn(DirPath(appDir));

        /// <summary>
        /// 按「启用的组」收集音效文件。
        ///
        /// 契约（**必须分清 null 和空集合**，这里踩过一次）：
        ///   `null`          = 调用方没表态 → 用全部（兼容「没设置过」的老调用）
        ///   `[]`（空集合）  = 调用方明确表示「一个都不用」→ 返回空
        ///   `["A"]`         = 只用 A 组
        ///
        /// 早期版本把 `[]` 也当「全部」，理由是「防手滑」。后果是
        /// 界面上的「全不选」（存成 `[]`）会变成「其实还在放」——
        /// 用户看到的和实际行为对不上，这比手滑严重得多。
        ///
        /// 「没设置过」应该由调用方用 `null` 表达，不要拿空集合兼职。
        /// </summary>
        public static List<string> CollectEnabled(string soundDir, IEnumerable<string>? enabledGroups)
        {
            var all = ListGroupsIn(soundDir);

            // null = 没表态 → 全部
            if (enabledGroups == null)
            {
                var everything = new List<string>();
                foreach (var g in all) everything.AddRange(g.Files);
                return everything;
            }

            var want = enabledGroups.Where(g => !string.IsNullOrWhiteSpace(g)).ToList();

            // 空集合 = 明确「一个都不用」→ 返回空，不要偷偷给全部
            if (want.Count == 0) return new List<string>();

            var picked = new List<string>();
            foreach (var g in all)
                if (want.Contains(g.Name, StringComparer.OrdinalIgnoreCase))
                    picked.AddRange(g.Files);
            return picked;
        }

        /// <summary>
        /// 随机播一个**指定分组范围内**的音效。
        /// enabledGroups 为空 = 用全部（不分组行为）。
        /// </summary>
        public static bool PlayRandomInGroups(string soundDir, IEnumerable<string>? enabledGroups,
                                              out string playedFile)
        {
            playedFile = "";
            var pool = CollectEnabled(soundDir, enabledGroups);
            if (pool.Count == 0) return false;

            var order = pool.OrderBy(_ => Rng.Next()).ToList();
            int tries = Math.Min(3, order.Count);
            for (int i = 0; i < tries; i++)
            {
                if (Play(order[i]))
                {
                    playedFile = Path.GetFileName(order[i]);
                    return true;
                }
            }
            return false;
        }

        /// <summary>音效文件夹路径（不存在时也返回路径，便于「打开文件夹」）。</summary>
        public static string DirPath(string appDir) => Path.Combine(appDir, DirName);

        /// <summary>确保音效文件夹存在（用于菜单「打开音效文件夹」）。</summary>
        public static string EnsureDir(string appDir)
        {
            string dir = DirPath(appDir);
            try { if (!Directory.Exists(dir)) Directory.CreateDirectory(dir); }
            catch { }
            return dir;
        }

        /// <summary>
        /// 随机播一个音效。列表为空或全部失败时返回 false。
        /// 调用方收到 false 可以回退成「跳一下」，保证双击始终有反馈。
        ///
        /// appDir 是**程序目录**（内部会去找它下面的 Sounds/）。
        /// </summary>
        public static bool PlayRandom(string appDir, out string playedFile)
            => PlayRandomIn(DirPath(appDir), out playedFile);

        /// <summary>随机播一个音效。参数是音效文件夹本身。</summary>
        public static bool PlayRandomIn(string soundDir, out string playedFile)
        {
            playedFile = "";
            var sounds = ListSoundsIn(soundDir);
            if (sounds.Count == 0) return false;

            // 随机顺序试：某个文件坏了不该让整个功能失效，
            // 但也不要无限重试 —— 最多试 min(3, 总数) 个。
            var order = sounds.OrderBy(_ => Rng.Next()).ToList();
            int tries = Math.Min(3, order.Count);
            for (int i = 0; i < tries; i++)
            {
                if (Play(order[i]))
                {
                    playedFile = Path.GetFileName(order[i]);
                    return true;
                }
            }
            return false;
        }

        /// <summary>播放指定文件。成功返回 true。</summary>
        public static bool Play(string path)
        {
            try
            {
                if (!File.Exists(path)) return false;
                string ext = Path.GetExtension(path).ToLowerInvariant();

                if (WavExt.Contains(ext)) return PlayWav(path);
                if (MediaExt.Contains(ext)) return PlayMedia(path);

                // 扩展名不认识：先按 WAV 试（有些素材没有扩展名或写错），
                // 再按媒体文件试。
                return PlayWav(path) || PlayMedia(path);
            }
            catch (Exception e)
            {
                AppPaths.Log($"  [音效] 播放失败 {Path.GetFileName(path)}: {e.Message}");
                return false;
            }
        }

        /// <summary>把音量设成 0-100 的百分数。</summary>
        public static void SetVolumePercent(int percent)
        {
            Volume = Math.Max(0, Math.Min(100, percent)) / 100.0;
            try { if (_player != null) _player.Volume = Volume; } catch { }
        }

        private static bool PlayWav(string path)
        {
            try
            {
                _wavPlayer?.Dispose();
                _wavPlayer = new SoundPlayer(path);
                _wavPlayer.Play();      // 异步播放，不阻塞 UI
                return true;
            }
            catch (Exception e)
            {
                AppPaths.Log($"  [音效] WAV 播放失败: {e.Message}");
                _wavPlayer?.Dispose();
                _wavPlayer = null;
                return false;
            }
        }

        private static bool PlayMedia(string path)
        {
            try
            {
                _player ??= new System.Windows.Media.MediaPlayer();
                _player.Close();        // 停掉上一个，避免叠加
                _player.Open(new Uri(path, UriKind.Absolute));
                _player.Volume = Math.Max(0, Math.Min(1, Volume));
                _player.Play();
                return true;
            }
            catch (Exception e)
            {
                AppPaths.Log($"  [音效] MediaPlayer 播放失败: {e.Message}");
                try { _player?.Close(); } catch { }
                _player = null;
                return false;
            }
        }

        /// <summary>停掉正在播的声音（退出时调用）。</summary>
        public static void StopAll()
        {
            try { _wavPlayer?.Stop(); } catch { }
            try { _wavPlayer?.Dispose(); } catch { }
            _wavPlayer = null;
            try { _player?.Close(); } catch { }
            _player = null;
        }

        /// <summary>自检用：某个扩展名走哪条通路。</summary>
        public static string RouteFor(string fileName)
        {
            string ext = Path.GetExtension(fileName ?? "").ToLowerInvariant();
            if (WavExt.Contains(ext)) return "wav";
            if (MediaExt.Contains(ext)) return "media";
            return "unknown";
        }

        /// <summary>自检用：支持的扩展名清单。</summary>
        public static IReadOnlyList<string> SupportedExtensions =>
            WavExt.Concat(MediaExt).ToList();

        /// <summary>
        /// 自检用：生成一个合法的短 WAV 文件（正弦波），用来真的验证播放通路。
        /// 44 字节标准头 + 16 位单声道 PCM。
        /// </summary>
        public static byte[] MakeTestWav(int ms = 120, int freq = 440, int rate = 22050)
        {
            int samples = Math.Max(1, rate * ms / 1000);
            int dataBytes = samples * 2;
            using var ms2 = new MemoryStream();
            using var w = new BinaryWriter(ms2);

            w.Write(new[] { 'R', 'I', 'F', 'F' });
            w.Write(36 + dataBytes);            // ChunkSize
            w.Write(new[] { 'W', 'A', 'V', 'E' });
            w.Write(new[] { 'f', 'm', 't', ' ' });
            w.Write(16);                        // Subchunk1Size (PCM)
            w.Write((short)1);                  // AudioFormat = PCM
            w.Write((short)1);                  // NumChannels = mono
            w.Write(rate);                      // SampleRate
            w.Write(rate * 2);                  // ByteRate
            w.Write((short)2);                  // BlockAlign
            w.Write((short)16);                 // BitsPerSample
            w.Write(new[] { 'd', 'a', 't', 'a' });
            w.Write(dataBytes);

            for (int i = 0; i < samples; i++)
            {
                double t = i / (double)rate;
                short v = (short)(Math.Sin(2 * Math.PI * freq * t) * 8000);
                w.Write(v);
            }
            w.Flush();
            return ms2.ToArray();
        }
    }
}
