// usage.cs —— 用量统计
//
// 记什么：
//   · 每次 API 调用返回的 usage（prompt / completion / 缓存命中 token）
//   · 按「今天」和「累计」两个维度累计
//   · 按价格换算成金额（价格写死在 PriceTable 里，改价时改那一处）
//
// 为什么缓存命中要单独记：
//   DeepSeek 的上下文缓存命中价只有未命中的约 1/10。
//   桌宠每轮都要重发整个历史，绝大部分 prompt token 其实是缓存命中的。
//   不区分的话会严重高估花费 —— 实测 3.5 亿 token 只花了十几块，
//   正是因为大头走了缓存价。
//
// 持久化到 usage.json，跨重启累计。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace PhoebePet
{
    /// <summary>一次调用的用量。</summary>
    public sealed class UsageRecord
    {
        public string Day = "";              // yyyy-MM-dd
        public long At;                      // unix ms
        public string Model = "";
        public string Kind = "chat";         // chat / proactive / memory
        public long PromptTokens;
        public long CachedTokens;            // 缓存命中的 prompt token
        public long CompletionTokens;
        public double Cost;                  // 人民币
        public long TotalTokens => PromptTokens + CompletionTokens;
        public double CacheRate => PromptTokens <= 0 ? 0 : CachedTokens / (double)PromptTokens;
    }

    /// <summary>模型价格（人民币 / 百万 token）。改价只改这里。</summary>
    public sealed class PriceInfo
    {
        public double CacheHit;      // 缓存命中的输入（空闲时段）
        public double CacheMiss;     // 未命中的输入（空闲时段）
        public double Output;        // 输出（空闲时段）

        /// <summary>高峰时段价格是空闲时段的两倍。</summary>
        public const double PeakMultiplier = 2.0;

        public PriceInfo(double hit, double miss, double output)
        { CacheHit = hit; CacheMiss = miss; Output = output; }

        public double Hit(bool peak) => CacheHit * (peak ? PeakMultiplier : 1);
        public double Miss(bool peak) => CacheMiss * (peak ? PeakMultiplier : 1);
        public double Out(bool peak) => Output * (peak ? PeakMultiplier : 1);
    }

    public static class Usage
    {
        /// <summary>
        /// 价格表（元 / 百万 token，**空闲时段**价）。
        ///
        /// 数据来源：官方「模型 & 价格」页（2026-09 抓取）。
        /// 高峰时段价格是空闲的两倍，见 PeakNow()。
        ///
        /// 注意：这是**估算**，不是账单。真实扣费以平台为准。
        /// 改价只需改这里，界面会跟着变。
        /// </summary>
        private static readonly Dictionary<string, PriceInfo> PriceTable =
            new(StringComparer.OrdinalIgnoreCase)
            {
                // deepseek-flash：缓存命中 0.02 / 未命中 1 / 输出 4（空闲时段）
                ["deepseek-flash"] = new PriceInfo(0.02, 1.0, 4.0),
                // 旧模型名仍可调用，由同一个 Flash 模型承接，按 Flash 价计费
                ["deepseek-v4-flash"] = new PriceInfo(0.02, 1.0, 4.0),
                ["deepseek-v4-flash-vision-exp"] = new PriceInfo(0.02, 1.0, 4.0),
                // deepseek-v4-pro：缓存命中 0.15 / 未命中 4.5 / 输出 13.5（空闲时段）
                ["deepseek-v4-pro"] = new PriceInfo(0.15, 4.5, 13.5),
            };

        private static readonly PriceInfo DefaultPrice = new PriceInfo(0.02, 1.0, 4.0);

        /// <summary>取某个模型的价格。</summary>
        public static PriceInfo PriceFor(string model)
        {
            if (model != null && PriceTable.TryGetValue(model, out var p)) return p;
            return DefaultPrice;
        }

        /// <summary>
        /// 现在是不是高峰时段。
        ///
        /// 官方定义：北京时间**周一至周五**（不含法定节假日）
        /// 9:00-12:00 与 14:00-18:00 为高峰，其余时段价格减半。
        ///
        /// 这里只判断星期和时刻 —— **没有实现法定节假日**（那需要一份
        /// 节假日表，且每年都变）。所以节假日当天的估算会偏高一点。
        /// 界面上会注明这是估算。
        /// </summary>
        public static bool PeakNow() => PeakAt(DateTime.Now);

        /// <summary>判断某个时刻是否高峰（自检用，便于传任意时间）。</summary>
        public static bool PeakAt(DateTime t)
        {
            if (t.DayOfWeek == DayOfWeek.Saturday || t.DayOfWeek == DayOfWeek.Sunday)
                return false;
            int hm = t.Hour * 100 + t.Minute;
            bool morning = hm >= 900 && hm < 1200;
            bool afternoon = hm >= 1400 && hm < 1800;
            return morning || afternoon;
        }

        // ---------- 记录 ----------
        private static readonly List<UsageRecord> Records = new List<UsageRecord>();
        private static string _path = "";
        private static readonly object Lock = new object();

        /// <summary>从磁盘载入（程序启动时调用一次）。</summary>
        public static void Load(string appDir)
        {
            lock (Lock)
            {
                _path = Path.Combine(appDir, "usage.json");
                Records.Clear();
                try
                {
                    if (!File.Exists(_path)) return;
                    var root = JsonNode.Parse(File.ReadAllText(_path));
                    var arr = root?["records"]?.AsArray();
                    if (arr == null) return;
                    foreach (var n in arr)
                    {
                        if (n == null) continue;
                        Records.Add(new UsageRecord
                        {
                            Day = n["day"]?.ToString() ?? "",
                            At = ReadLong(n["at"]),
                            Model = n["model"]?.ToString() ?? "",
                            Kind = n["kind"]?.ToString() ?? "chat",
                            PromptTokens = ReadLong(n["prompt"]),
                            CachedTokens = ReadLong(n["cached"]),
                            CompletionTokens = ReadLong(n["completion"]),
                            Cost = ReadDouble(n["cost"]),
                        });
                    }
                    AppPaths.Log($"  [用量] 已载入 {Records.Count} 条历史记录");
                }
                catch (Exception e)
                {
                    AppPaths.Log($"  [用量] 载入失败（忽略）: {e.Message}");
                    Records.Clear();
                }
            }
        }

        /// <summary>
        /// 记一次调用。usage 节点直接来自 API 响应。
        /// usage 为 null（比如接口没返回）时安全跳过。
        /// </summary>
        public static UsageRecord? Add(string model, string kind, JsonNode? usageNode)
        {
            if (usageNode == null) return null;

            try
            {
                long prompt = ReadLong(usageNode["prompt_tokens"]);
                long completion = ReadLong(usageNode["completion_tokens"]);
                // 缓存命中字段名有两种写法，都试一下
                long cached = ReadLong(usageNode["prompt_cache_hit_tokens"]);
                if (cached == 0)
                    cached = ReadLong(usageNode["prompt_tokens_details"]?["cached_tokens"]);
                if (cached > prompt) cached = prompt;   // 防御：不该超过总数

                var price = PriceFor(model);
                long miss = Math.Max(0, prompt - cached);
                // 按**调用时刻**的时段定价 —— 跨时段时按各次调用分别算，更准
                bool peak = PeakNow();
                double cost = cached / 1e6 * price.Hit(peak)
                            + miss / 1e6 * price.Miss(peak)
                            + completion / 1e6 * price.Out(peak);

                var rec = new UsageRecord
                {
                    Day = DateTime.Now.ToString("yyyy-MM-dd"),
                    At = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
                    Model = model ?? "",
                    Kind = kind ?? "chat",
                    PromptTokens = prompt,
                    CachedTokens = cached,
                    CompletionTokens = completion,
                    Cost = cost,
                };

                lock (Lock) { Records.Add(rec); }
                Save();
                return rec;
            }
            catch (Exception e)
            {
                AppPaths.Log($"  [用量] 记录失败: {e.Message}");
                return null;
            }
        }

        /// <summary>只留最近这么多条，防止文件无限增长。</summary>
        private const int KeepRecords = 2000;

        /// <summary>
        /// 从一个 JSON 节点读整数。
        ///
        /// 为什么不能直接 `node.GetValue&lt;long&gt;()`：
        ///   System.Text.Json 会把小的整数解析成 int，此时强转 long 会抛
        ///   「A value of type 'System.Int32' cannot be converted to a 'System.Int64'」。
        ///   token 数动不动就是几百万，正好落在 int 范围内，所以这个坑必然踩到。
        ///   统一走 double 再取整，兼容 int / long / double 三种存储。
        /// </summary>
        /// <summary>
        /// 从 JSON 节点读整数。
        ///
        /// 这个函数被改了三版，值得记下为什么：
        ///   System.Text.Json 的 JsonNode 有两种**不同的内部表示**：
        ///     · JsonNode.Parse(...)          → JsonValuePrimitive&lt;JsonElement&gt;
        ///     · new JsonObject { ["a"] = 1 } → JsonValuePrimitive&lt;int&gt;
        ///   取值方式对这两种并不通用 —— `GetValue&lt;JsonElement&gt;()` 在后者上
        ///   会抛 "Int32 cannot be converted to JsonElement"，`AsValue()` 也一样。
        ///   而两者的 `ToJsonString()` 都正常。
        ///
        ///   所以这里用 ToJsonString() 反解 —— 通用、无分支、不依赖内部类型。
        ///   真实 API 响应走 Parse 那条路，但自检用的是初始化器，
        ///   两条路都得能读，否则会出现「线上对、测试错」或反之。
        /// </summary>
        private static long ReadLong(JsonNode? node)
        {
            if (node == null) return 0;
            try
            {
                string raw = node.ToJsonString().Trim();
                if (raw.Length >= 2 && raw[0] == '"') raw = raw.Substring(1, raw.Length - 2);
                return long.TryParse(raw, out long l) ? l : 0;
            }
            catch { /* 读不出来就当 0，统计不值得为此报错 */ }
            return 0;
        }

        /// <summary>同上，读浮点数（价格可能很小，必须保留小数）。</summary>
        private static double ReadDouble(JsonNode? node)
        {
            if (node == null) return 0;
            try
            {
                string raw = node.ToJsonString().Trim();
                if (raw.Length >= 2 && raw[0] == '"') raw = raw.Substring(1, raw.Length - 2);
                return double.TryParse(raw, out double d) ? d : 0;
            }
            catch { }
            return 0;
        }

        private static void Save()
        {
            lock (Lock)
            {
                if (_path.Length == 0) return;
                try
                {
                    // 只保留最近的，避免文件越滚越大
                    if (Records.Count > KeepRecords)
                        Records.RemoveRange(0, Records.Count - KeepRecords);

                    var arr = new JsonArray();
                    foreach (var r in Records)
                    {
                        arr.Add(new JsonObject
                        {
                            ["day"] = r.Day,
                            ["at"] = r.At,
                            ["model"] = r.Model,
                            ["kind"] = r.Kind,
                            ["prompt"] = r.PromptTokens,
                            ["cached"] = r.CachedTokens,
                            ["completion"] = r.CompletionTokens,
                            ["cost"] = Math.Round(r.Cost, 8),
                        });
                    }
                    var root = new JsonObject { ["records"] = arr };
                    File.WriteAllText(_path,
                        root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                        new UTF8Encoding(false));
                }
                catch (Exception e)
                {
                    AppPaths.Log($"  [用量] 保存失败: {e.Message}");
                }
            }
        }

        // ---------- 汇总 ----------
        public sealed class Summary
        {
            public int Calls;
            public long Prompt, Cached, Completion, Total;
            public double Cost;
            public double CacheRate => Prompt <= 0 ? 0 : Cached / (double)Prompt;
            public long CostMillis => (long)Math.Round(Cost * 1000);
        }

        private static Summary Summarize(IEnumerable<UsageRecord> src)
        {
            var s = new Summary();
            foreach (var r in src)
            {
                s.Calls++;
                s.Prompt += r.PromptTokens;
                s.Cached += r.CachedTokens;
                s.Completion += r.CompletionTokens;
                s.Total += r.TotalTokens;
                s.Cost += r.Cost;
            }
            return s;
        }

        public static Summary Today()
        {
            string today = DateTime.Now.ToString("yyyy-MM-dd");
            lock (Lock) { return Summarize(Records.Where(r => r.Day == today)); }
        }

        public static Summary All()
        {
            lock (Lock) { return Summarize(Records); }
        }

        /// <summary>最近 N 天的逐日汇总（含今天）。</summary>
        public static List<(string Day, Summary Sum)> RecentDays(int days)
        {
            var list = new List<(string, Summary)>();
            lock (Lock)
            {
                for (int i = days - 1; i >= 0; i--)
                {
                    string d = DateTime.Now.AddDays(-i).ToString("yyyy-MM-dd");
                    list.Add((d, Summarize(Records.Where(r => r.Day == d))));
                }
            }
            return list;
        }

        /// <summary>把一段汇总渲染成可读文本。</summary>
        public static string Format(Summary s, string title)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"【{title}】");
            if (s.Calls == 0)
            {
                sb.AppendLine("  （还没有调用记录）");
                return sb.ToString();
            }
            sb.AppendLine($"  调用次数   {s.Calls}");
            sb.AppendLine($"  输入 token {s.Prompt:N0}");
            sb.AppendLine($"    └ 缓存命中 {s.Cached:N0}（{s.CacheRate:P0}）");
            sb.AppendLine($"    └ 未命中   {s.Prompt - s.Cached:N0}");
            sb.AppendLine($"  输出 token {s.Completion:N0}");
            sb.AppendLine($"  合计 token {s.Total:N0}");
            sb.AppendLine($"  估算花费   ¥{s.Cost:F4}");
            return sb.ToString();
        }

        /// <summary>清空统计（菜单用）。</summary>
        public static void Clear()
        {
            lock (Lock) { Records.Clear(); }
            Save();
        }

        /// <summary>自检用：记录条数。</summary>
        public static int Count { get { lock (Lock) { return Records.Count; } } }

        /// <summary>自检用：临时指定存储路径，避免污染真实 usage.json。</summary>
        public static void SetPathForTest(string path) { lock (Lock) { _path = path; } }

        /// <summary>自检用：直接塞一条记录（不落盘）。</summary>
        public static void AddForTest(UsageRecord r) { lock (Lock) { Records.Add(r); } }

        /// <summary>自检用：清空内存里的记录（不落盘）。</summary>
        public static void ClearMemoryForTest() { lock (Lock) { Records.Clear(); } }
    }
}
