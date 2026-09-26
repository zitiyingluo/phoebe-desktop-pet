// balance.cs —— 账户余额查询
//
// 为什么单独一个文件：
//   usage.cs 是「本地估算花了多少」，它是纯离线的累计。
//   这里是「平台账户还剩多少」，要发网络请求、要缓存、会失败。
//   两件事的失败模式和依赖完全不同，混在一起会把 usage.cs 搞脏
//   （它现在 387 行、职责清晰，不该为这个再长）。
//
// 接口：GET https://api.deepseek.com/user/balance
//   文档：https://api-docs.deepseek.com/zh-cn/api/get-user-balance
//
// 注意一个坑（文档里没强调，但实测字段类型如此）：
//   total_balance / granted_balance / topped_up_balance 都是**字符串**，
//   不是数字。直接 GetValue<double>() 会抛异常。
//   这和 usage.cs 里踩过的「Int32 不能转 Int64」是同一类问题 ——
//   JSON 数值类型不保证按你期望的类型来，一律走 ToJsonString() 反解。

using System;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace PhoebePet
{
    /// <summary>一次余额查询的结果。</summary>
    public sealed class BalanceInfo
    {
        /// <summary>账户是否还能调用（false 通常意味着余额耗尽）。</summary>
        public bool IsAvailable;

        /// <summary>总可用余额（含赠金 + 充值）。</summary>
        public double Total;

        /// <summary>未过期的赠金。</summary>
        public double Granted;

        /// <summary>充值余额。</summary>
        public double ToppedUp;

        /// <summary>货币单位，CNY 或 USD。</summary>
        public string Currency = "CNY";

        /// <summary>查询成功的时间。</summary>
        public DateTime FetchedAt = DateTime.Now;

        /// <summary>失败原因（成功时为空）。</summary>
        public string? Error;

        public bool Ok => Error == null;

        /// <summary>货币符号。</summary>
        public string Symbol => Currency == "USD" ? "$" : "¥";

        /// <summary>给气泡/报告用的一行摘要。</summary>
        public string Describe()
            => Ok
                ? $"{Symbol}{Total:F2}（赠金 {Symbol}{Granted:F2} / 充值 {Symbol}{ToppedUp:F2}）"
                : $"查询失败：{Error}";
    }

    /// <summary>
    /// 余额查询 + 缓存。
    ///
    /// 为什么要缓存：
    ///   每次打开用量统计窗口都打一次接口太浪费，而且平台那边
    ///   对余额接口也可能限流。默认 5 分钟内复用上次结果。
    /// </summary>
    public static class Balance
    {
        /// <summary>缓存有效期。</summary>
        public static TimeSpan CacheFor = TimeSpan.FromMinutes(5);

        private static BalanceInfo? _cache;

        /// <summary>上一次查询结果（可能已过期）。没有则返回 null。</summary>
        public static BalanceInfo? Cached => _cache;

        /// <summary>缓存是否还有效。</summary>
        public static bool CacheFresh =>
            _cache != null && _cache.Ok && (DateTime.Now - _cache.FetchedAt) < CacheFor;

        /// <summary>清掉缓存（自检用，也用于「强制刷新」）。</summary>
        public static void ClearCache() => _cache = null;

        /// <summary>
        /// 查询余额。
        ///
        /// force=false 时若缓存还新鲜就直接返回缓存，不发请求。
        /// **失败不会抛异常** —— 余额查不到只是少显示一行，
        /// 不该让用量统计窗口打不开。
        /// </summary>
        public static async Task<BalanceInfo> QueryAsync(string baseUrl, string apiKey, bool force = false)
        {
            if (!force && CacheFresh) return _cache!;

            var info = new BalanceInfo();

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                info.Error = "还没填 API Key";
                _cache = info;
                return info;
            }

            try
            {
                string url = (baseUrl ?? "https://api.deepseek.com").TrimEnd('/') + "/user/balance";
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Add("Authorization", "Bearer " + apiKey);

                using var resp = await ChatCore.Http.SendAsync(req).ConfigureAwait(false);
                string text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                if (!resp.IsSuccessStatusCode)
                {
                    int code = (int)resp.StatusCode;
                    info.Error = code == 401 || code == 403
                        ? "API Key 被拒（检查 Key 是否正确）"
                        : $"HTTP {code}";
                    AppPaths.Log($"  [余额] 查询失败 HTTP {code}: {Truncate(text, 200)}");
                    _cache = info;
                    return info;
                }

                Fill(info, text);
                AppPaths.Log($"  [余额] {info.Describe()}（is_available={info.IsAvailable}）");
            }
            catch (Exception e)
            {
                // 和主对话一样，错误要先分类再说人话
                var err = ChatCore.DescribeException(e);
                info.Error = err.Message;
                AppPaths.Log($"  [余额] 查询异常 kind={err.Kind}: {err.Detail}");
            }

            _cache = info;
            return info;
        }

        /// <summary>
        /// 解析余额响应。抽成 public static 是为了能离线自检 ——
        /// 沙箱里发不出真实请求（TLS 被拦），但解析逻辑是纯函数。
        /// </summary>
        public static void Fill(BalanceInfo info, string json)
        {
            var node = JsonNode.Parse(json);

            // is_available 缺失时按「有余额」处理：宁可显示金额，
            // 也不要因为字段没给就吓唬用户说余额没了。
            info.IsAvailable = ReadBool(node?["is_available"]) ?? true;

            var arr = node?["balance_infos"]?.AsArray();
            if (arr == null || arr.Count == 0)
            {
                // 没有明细时金额留 0，但不算错误 —— 接口就是这个形状
                info.Total = info.Granted = info.ToppedUp = 0;
                return;
            }

            // 有多币种时优先人民币（这个项目按人民币估算花费，对齐着看更直观）
            JsonNode? pick = null;
            foreach (var n in arr)
            {
                string cur = n?["currency"]?.ToString() ?? "";
                if (string.Equals(cur, "CNY", StringComparison.OrdinalIgnoreCase)) { pick = n; break; }
                pick ??= n;
            }

            info.Currency = pick?["currency"]?.ToString() ?? "CNY";
            info.Total = ReadNumber(pick?["total_balance"]);
            info.Granted = ReadNumber(pick?["granted_balance"]);
            info.ToppedUp = ReadNumber(pick?["topped_up_balance"]);
        }

        /// <summary>
        /// 读一个可能是字符串、也可能是数字的 JSON 值。
        ///
        /// 文档说是 string，但真实接口哪天改成数字也不该崩 ——
        /// 一律 ToJsonString() 拿原文再解析，两种情况通吃。
        /// </summary>
        public static double ReadNumber(JsonNode? node)
        {
            if (node == null) return 0;
            try
            {
                string raw = node.ToJsonString().Trim();
                if (raw.Length >= 2 && raw[0] == '"') raw = raw.Substring(1, raw.Length - 2);
                return double.TryParse(raw, NumberStyles.Float,
                                       CultureInfo.InvariantCulture, out double d) ? d : 0;
            }
            catch { return 0; }
        }

        /// <summary>同上，读布尔。</summary>
        public static bool? ReadBool(JsonNode? node)
        {
            if (node == null) return null;
            try
            {
                string raw = node.ToJsonString().Trim();
                if (raw == "true") return true;
                if (raw == "false") return false;
                if (raw.Length >= 2 && raw[0] == '"')
                    raw = raw.Substring(1, raw.Length - 2);
                if (bool.TryParse(raw, out bool b)) return b;
                return null;
            }
            catch { return null; }
        }

        private static string Truncate(string s, int n) =>
            string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s.Substring(0, n) + "…");
    }
}
