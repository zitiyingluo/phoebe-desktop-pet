// chat.cs —— 桌宠的「聊天核心」
//
// 本项目的前身是一个 Python 版的聊天核心，这里把它重写为纯 C#：
// 去掉了 PyQt5 / openai / requests 等依赖，只用 .NET 自带的 HttpClient。
//
// 为什么手搓 HTTP 而不用 SDK：本机沙箱出不了网，NuGet 还原必失败，
// 所以 pet 这个工程是零第三方依赖的（见 README 的「踩过的坑」第 1 条）。
// DeepSeek 的接口就是 POST {base_url}/v1/chat/completions，手搓完全够用。
//
// JSON 同理：.NET 自带的 System.Text.Json 足够，不引 Newtonsoft。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace PhoebePet
{
    // ================= 配置 =================
    // 注意：这里用属性而不是字段。System.Text.Json 默认只序列化属性，
    // 用字段会得到空对象 {}（踩过一次）。
    public sealed class ChatConfig
    {
        public string BaseUrl { get; set; } = "https://api.deepseek.com";
        public string ApiKey { get; set; } = "";          // 留空，由用户自己填
        public string Model { get; set; } = "deepseek-chat";
        public double Temperature { get; set; } = 0.8;
        // 默认给 700：300 对「中文 + 看图回答」太紧（实测撞过一次，
        // completion 正好顶到 300，钱花了但一个字都没显示出来）。
        // 现在撞了会自动重说，但这个默认值本身也该给足。
        public int MaxTokens { get; set; } = 700;

        /// <summary>对话历史保留条数（不含 system）。源程序是 22。</summary>
        public int HistoryLimit { get; set; } = 22;

        /// <summary>
        /// 是否把对话历史存盘（history.json）。
        ///
        /// 默认**开**：关掉就每次启动都是新的，聊到一半关程序就全没了。
        /// 但有人就是想要「每次都是全新的开始」，所以给个开关。
        /// 关掉时只是不读不写，**已有的文件不会删**。
        /// </summary>
        public bool SaveHistory { get; set; } = true;

        /// <summary>是否启用长期记忆。</summary>
        public bool EnableMemory { get; set; } = true;

        /// <summary>
        /// 每几轮对话总结一次记忆。
        /// 原来是「每轮都总结」，等于每轮多烧一次 API 调用，很贵。
        /// 默认 6 轮；设 1 = 恢复旧行为。
        /// </summary>
        public int MemoryInterval { get; set; } = 6;

        /// <summary>
        /// 记忆总结用的模型。留空 = 用便宜的小模型（省 token）。
        /// 想让总结质量更高就填主模型名。
        /// </summary>
        public string MemoryModel { get; set; } = "";

        /// <summary>
        /// 省钱开关：开启后
        ///   1) 记忆总结用便宜模型
        ///   2) 历史上下文按条数+字数双重裁剪
        ///   3) 长期记忆注入时限制长度
        /// 关掉则一切按最大量来。
        /// </summary>
        public bool TokenSaver { get; set; } = true;

        /// <summary>人设档名，对应人设文件里 prompts 下的 key。留空 = 自动用第一个档。</summary>
        public string PersonaKey { get; set; } = "";

        /// <summary>
        /// 人设文件名（放在程序目录下的 Personas 子文件夹里）。
        /// 留空 = 自动用 Personas 里的第一个 .json。
        /// 换人设只需换这个文件名或往文件夹里丢新文件。
        /// </summary>
        public string PersonaFile { get; set; } = "";

        /// <summary>角色名；留空则用人设文件里的名字，再空则用「菲比」。</summary>
        public string CharacterName { get; set; } = "";

        /// <summary>玩家称呼；空则用存档里的名字，再空则用「哥哥」。</summary>
        public string PlayerName { get; set; } = "";

        /// <summary>
        /// 启动时是否自动打开输入框。
        ///
        /// 默认 **false** —— 用户要求「打开菲比时输入框默认关闭」。
        /// 输入框是个独立窗口，一启动就弹出来会挡住屏幕、也会抢焦点，
        /// 想聊天时按 Ctrl+Alt+I 或右键菜单打开更自然。
        /// </summary>
        public bool ShowInputOnStart { get; set; } = false;

        private static readonly JsonSerializerOptions Opts = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        /// <summary>读配置；文件不存在就写一份带注释说明的模板出来（key 留空）。</summary>
        public static ChatConfig Load(string path)
        {
            if (!File.Exists(path))
            {
                var fresh = new ChatConfig();
                fresh.Save(path);
                AppPaths.Log($"  [chat] 配置不存在，已生成模板: {path}（api_key 留空，请自行填写）");
                return fresh;
            }

            try
            {
                var cfg = JsonSerializer.Deserialize<ChatConfig>(File.ReadAllText(path)) ?? new ChatConfig();
                AppPaths.Log($"  [chat] 配置已载入: model={cfg.Model} base={cfg.BaseUrl} " +
                             $"key={(string.IsNullOrWhiteSpace(cfg.ApiKey) ? "【空】" : "已设置")}");
                return cfg;
            }
            catch (Exception e)
            {
                AppPaths.Log($"  [chat] 配置解析失败，回退默认值: {e.Message}");
                return new ChatConfig();
            }
        }

        public void Save(string path)
        {
            File.WriteAllText(path, JsonSerializer.Serialize(this, Opts), new UTF8Encoding(false));
        }
    }

    // ================= 一条对话 =================
    public sealed class ChatTurn
    {
        public string Role = "user";       // user / assistant
        public string Content = "";
    }

    // ================= 聊天核心 =================
    public sealed class ChatCore
    {
        private readonly ChatConfig _cfg;
        private readonly string _dir;
        // 共享一个 HttpClient：balance.cs 也要发请求，
        // 各建一个会多一份连接池，而且 HttpClient 本身设计成复用。
        internal static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };

        public string CharacterName { get; private set; } = "菲比";
        public string PlayerName { get; private set; } = "哥哥";

        /// <summary>人格描述 / 性格 / 情景。</summary>
        private string _personaDesc = "";
        private string _personaPersonality = "";
        private string _personaScenario = "";

        private string _userMemory = "";

        /// <summary>本次实际用到的人格档名（配置留空时是文件里的第一个）。</summary>
        private string _usedPersonaKey = "";

        /// <summary>历史对话（不含 system）。</summary>
        private readonly List<ChatTurn> _history = new List<ChatTurn>();

        /// <summary>人设文件夹：把任意 .json 人设丢进来即可切换。</summary>
        public string PersonasDir => Path.Combine(_dir, "Personas");

        /// <summary>当前实际使用的人设文件完整路径。</summary>
        public string ArchivePath { get; private set; }

        public string MemoryPath => Path.Combine(_dir, "user_memory.json");

        /// <summary>
        /// 决定用哪个人设文件。优先级：
        ///   1) 配置里指定的 PersonaFile（在 Personas/ 下找，找不到再看程序目录）
        ///   2) Personas/ 里的第一个 .json（按文件名排序）
        ///   3) 都没有 → 返回空串，让调用方报一个明确的错
        /// </summary>
        private string ResolveArchivePath()
        {
            string result;
            // 1) 配置指定
            if (!string.IsNullOrWhiteSpace(_cfg.PersonaFile))
            {
                string inFolder = Path.Combine(PersonasDir, _cfg.PersonaFile);
                if (File.Exists(inFolder)) { result = inFolder; AppPaths.Log($"  [chat] 人设解析: 配置指定 → {inFolder}"); return result; }
                string inRoot = Path.Combine(_dir, _cfg.PersonaFile);
                if (File.Exists(inRoot))
                {
                    AppPaths.Log($"  [chat] 人设 '{_cfg.PersonaFile}' 在 Personas/ 下没找到，改用程序目录的同名文件");
                    return inRoot;
                }
                AppPaths.Log($"  [chat] 配置指定的人设 '{_cfg.PersonaFile}' 不存在，回退自动选择");
            }

            // 2) Personas/ 里第一个
            try
            {
                if (Directory.Exists(PersonasDir))
                {
                    var files = Directory.GetFiles(PersonasDir, "*.json");
                    Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                    if (files.Length > 0)
                    {
                        AppPaths.Log($"  [chat] 人设解析: 自动选第一个 → {files[0]}");
                        return files[0];
                    }
                }
            }
            catch (Exception e) { AppPaths.Log($"  [chat] 扫描 Personas 目录失败: {e.Message}"); }

            // 3) 没有可用人设。返回空串，由 LoadPersona 报错。
            //    这里曾经兜底到程序目录的「人设和日记.json」，那个旧布局已经废弃、
            //    文件也删了，继续指过去只会得到一个「文件不存在」的迷惑报错。
            AppPaths.Log($"  [chat] 人设解析: 没找到任何人设文件（{PersonasDir}）");
            return "";
        }

        /// <summary>列出所有可选人设（文件名，不含路径）。</summary>
        public List<string> ListPersonaFiles()
        {
            var list = new List<string>();
            try
            {
                if (Directory.Exists(PersonasDir))
                {
                    var files = Directory.GetFiles(PersonasDir, "*.json");
                    Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                    foreach (var f in files) list.Add(Path.GetFileName(f));
                }
            }
            catch { }
            return list;
        }

        /// <summary>切换人设文件并重新载入。</summary>
        public void SetPersonaFile(string fileName)
        {
            _cfg.PersonaFile = fileName;
            ArchivePath = ResolveArchivePath();
            LoadPersona();
            // 换人设 = 换人格，旧上下文不再适用。
            // 磁盘存档也要删 —— 否则重启后旧人格的对话又回来了。
            _history.Clear();
            DeleteHistoryFile();
            AppPaths.Log($"  [chat] 已切换人设: {Path.GetFileName(ArchivePath)}（旧上下文与存档已清除）");
        }

        public ChatCore(ChatConfig cfg, string dir)
        {
            _cfg = cfg;
            _dir = dir;
            ArchivePath = ResolveArchivePath();
            LoadPersona();
            LoadMemory();
            LoadHistory();
        }

        // ---------- 人设 ----------
        public void LoadPersona()
        {
            try
            {
                if (!File.Exists(ArchivePath))
                {
                    AppPaths.Log($"  [chat] 人设文件不存在: {ArchivePath}，先用内置兜底人设");
                    ApplyFallbackPersona();
                    return;
                }

                var root = JsonNode.Parse(File.ReadAllText(ArchivePath));
                var data = root?["data"];
                if (data == null) { ApplyFallbackPersona(); return; }

                // 角色名：配置优先 > 人设文件 > 兜底「菲比」
                string archiveName = data["gameData"]?["characterSystemData"]?["character"]?["name"]?.ToString() ?? "";
                if (!string.IsNullOrWhiteSpace(_cfg.CharacterName)) CharacterName = _cfg.CharacterName;
                else if (!string.IsNullOrWhiteSpace(archiveName)) CharacterName = archiveName;
                else CharacterName = "菲比";

                // 玩家名：配置优先 > 存档 > 兜底
                string archivePlayer = data["gameData"]?["playerInfo"]?["name"]?.ToString() ?? "";
                if (!string.IsNullOrWhiteSpace(_cfg.PlayerName)) PlayerName = _cfg.PlayerName;
                else if (!string.IsNullOrWhiteSpace(archivePlayer)) PlayerName = archivePlayer;
                else PlayerName = "哥哥";

                // 人格档
                var promptsObj = data["prompts"]?.AsObject();
                JsonNode persona = null;
                string usedKey = _cfg.PersonaKey;

                if (string.IsNullOrWhiteSpace(_cfg.PersonaKey))
                {
                    // 没指定档 → 用第一个（换人设文件时不用改配置）
                    if (promptsObj != null && promptsObj.Count > 0)
                    {
                        usedKey = promptsObj.First().Key;
                        persona = promptsObj.First().Value?["data"];
                    }
                }
                else
                {
                    persona = data["prompts"]?[_cfg.PersonaKey]?["data"];
                    if (persona == null && promptsObj != null && promptsObj.Count > 0)
                    {
                        usedKey = promptsObj.First().Key;
                        AppPaths.Log($"  [chat] 人格档 '{_cfg.PersonaKey}' 不存在，改用 '{usedKey}'");
                        persona = promptsObj.First().Value?["data"];
                    }
                }
                _usedPersonaKey = usedKey;

                if (persona != null)
                {
                    _personaDesc = persona["description"]?.ToString() ?? "";
                    _personaPersonality = persona["personality"]?.ToString() ?? "";
                    _personaScenario = persona["scenario"]?.ToString() ?? "";
                }
                else ApplyFallbackPersona();

                // 日记功能已移除：人设文件里即使还有 diary 字段也会被忽略。
                AppPaths.Log($"  [chat] 人设载入成功: 文件={Path.GetFileName(ArchivePath)} " +
                             $"角色={CharacterName} 称呼={PlayerName} 人格档={_usedPersonaKey} " +
                             $"描述={_personaDesc.Length}字 " +
                             $"性格={_personaPersonality.Length}字 情景={_personaScenario.Length}字");
            }
            catch (Exception e)
            {
                AppPaths.Log($"  [chat] 人设载入失败: {e.Message}");
                ApplyFallbackPersona();
            }
        }

        private void ApplyFallbackPersona()
        {
            _personaDesc = "一个亲昵温柔、爱撒娇的妹妹";
            _personaPersonality = "亲昵黏人，喜欢夸奖，情绪丰富";
            _personaScenario = "与哥哥的甜蜜日常";
            if (string.IsNullOrWhiteSpace(_cfg.PlayerName)) PlayerName = "哥哥";
        }

        // ---------- 对话历史持久化 ----------
        /// <summary>历史存档路径。跟记忆分开存：一个是「她记得什么」，一个是「聊过什么」。</summary>
        public string HistoryPath => Path.Combine(_dir, "history.json");

        /// <summary>
        /// 从磁盘恢复对话历史。
        ///
        /// 几个刻意的取舍：
        ///   · **存原始 Content**（带「玩家：」前缀那种），不是剥离后的。
        ///     因为发给模型的就是原始形式，存剥过的会在下一轮拼请求时丢信息。
        ///   · **载入时按 HistoryLimit 截断**：文件里可能存了几百条
        ///     （用户改过上限，或者手工编辑过），全读进来会顶爆 token。
        ///     只保留**最近的**，和运行时裁剪保持一致的语义。
        ///   · 关掉开关时**不读**，但也不删文件 —— 用户可能只是暂时不想带上下文。
        /// </summary>
        public void LoadHistory()
        {
            try
            {
                if (!_cfg.SaveHistory)
                {
                    AppPaths.Log("  [chat] 历史持久化已关闭，本次不载入");
                    return;
                }
                if (!File.Exists(HistoryPath))
                {
                    AppPaths.Log("  [chat] 没有历史存档");
                    return;
                }

                var root = JsonNode.Parse(File.ReadAllText(HistoryPath));
                var arr = root?["turns"]?.AsArray();
                if (arr == null) { AppPaths.Log("  [chat] 历史存档格式不对，忽略"); return; }

                var loaded = new List<ChatTurn>();
                foreach (var n in arr)
                {
                    string role = n?["role"]?.ToString() ?? "";
                    string content = n?["content"]?.ToString() ?? "";
                    // 只认这两种角色；残缺记录直接跳过而不是让它们污染上下文
                    if (role != "user" && role != "assistant") continue;
                    if (content.Length == 0) continue;
                    loaded.Add(new ChatTurn { Role = role, Content = content });
                }

                // 文件里可能远超上限，只留最近的
                int max = Math.Max(1, _cfg.HistoryLimit);
                if (loaded.Count > max) loaded.RemoveRange(0, loaded.Count - max);

                _history.Clear();
                _history.AddRange(loaded);
                AppPaths.Log($"  [chat] 历史已恢复: {_history.Count} 条" +
                             $"（存档里原本 {arr.Count} 条，上限 {max}）");
            }
            catch (Exception e)
            {
                // 存档坏了不该让程序起不来 —— 清空继续跑，日志留痕
                AppPaths.Log($"  [chat] 历史载入失败（已忽略）: {e.Message}");
                _history.Clear();
            }
        }

        /// <summary>
        /// 把当前历史写盘。
        ///
        /// 存的是**当前内存里的那份**（已经被 HistoryLimit 裁过），
        /// 所以文件不会无限增长 —— 不需要再单独维护一个保留条数。
        /// </summary>
        public void SaveHistory()
        {
            try
            {
                if (!_cfg.SaveHistory) return;

                var arr = new JsonArray();
                foreach (var t in _history)
                {
                    arr.Add(new JsonObject
                    {
                        ["role"] = t.Role,
                        ["content"] = t.Content,
                    });
                }

                var obj = new JsonObject
                {
                    ["savedAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    ["persona"] = Path.GetFileName(ArchivePath),   // 换人设时好判断这份存档属于谁
                    ["turns"] = arr,
                };

                File.WriteAllText(HistoryPath,
                    obj.ToJsonString(new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                    }), new UTF8Encoding(false));
            }
            catch (Exception e) { AppPaths.Log($"  [chat] 历史保存失败: {e.Message}"); }
        }

        /// <summary>
        /// 删除历史存档。
        ///
        /// **必须和 ClearHistory 一起用** —— 只清内存不删文件的话，
        /// 重启后「忘记的又回来了」，这是最容易让人困惑的行为。
        /// </summary>
        public void DeleteHistoryFile()
        {
            try
            {
                if (File.Exists(HistoryPath))
                {
                    File.Delete(HistoryPath);
                    AppPaths.Log("  [chat] 历史存档已删除");
                }
            }
            catch (Exception e) { AppPaths.Log($"  [chat] 历史存档删除失败: {e.Message}"); }
        }

        // ---------- 记忆 ----------
        public void LoadMemory()
        {
            try
            {
                if (!File.Exists(MemoryPath)) { _userMemory = ""; return; }
                var root = JsonNode.Parse(File.ReadAllText(MemoryPath));
                _userMemory = root?["summary"]?.ToString() ?? "";
                AppPaths.Log($"  [chat] 记忆已载入: {_userMemory.Length}字");
            }
            catch (Exception e)
            {
                AppPaths.Log($"  [chat] 记忆载入失败: {e.Message}");
                _userMemory = "";
            }
        }

        private void SaveMemory(string summary)
        {
            try
            {
                var obj = new JsonObject { ["summary"] = summary };
                File.WriteAllText(MemoryPath,
                    obj.ToJsonString(new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                    }), new UTF8Encoding(false));
            }
            catch (Exception e) { AppPaths.Log($"  [chat] 记忆保存失败: {e.Message}"); }
        }

        // ---------- prompt 组装 ----------
        private string BuildSystemPrompt()
        {
            // 省钱：语气规则合并成一行，不再逐条罗列。
            // 人设本身（描述/性格/情景）原样保留 —— 那是沉浸感的来源，不能砍。
            return $"你是 {CharacterName}，{_personaDesc}\n" +
                   $"你的性格：{_personaPersonality}\n" +
                   $"当前情景：{_personaScenario}\n" +
                   $"你称呼玩家为 {PlayerName}。" +
                   "回复要自然、有感情，不超过 150 字；只输出角色说的话，不要任何解释或旁白。";
        }

        private static readonly string[] TimeKeywords =
            { "几点了", "几点", "时间", "现在几点", "当前时间", "什么时候", "报时" };

        /// <summary>本会话已完成的对话轮数（用于记忆总结间隔）。</summary>
        private int _rounds;

        /// <summary>上次做记忆总结时的轮数。</summary>
        private int _lastMemoryRound;

        /// <summary>
        /// 省钱：按「条数 + 总字数」双重裁剪历史。
        /// 只按条数裁的话，遇到长回复依然会顶满 token。
        /// </summary>
        private List<ChatTurn> TrimHistory()
        {
            int maxTurns = _cfg.HistoryLimit;
            int maxChars = _cfg.TokenSaver ? 1200 : int.MaxValue;

            var kept = new List<ChatTurn>();
            int chars = 0;
            // 从最近的往回取，保证最近的对话一定保留
            for (int i = _history.Count - 1; i >= 0; i--)
            {
                var t = _history[i];
                int len = t.Content?.Length ?? 0;
                if (kept.Count >= maxTurns) break;
                if (chars + len > maxChars && kept.Count >= 2) break;   // 至少留一条往返
                kept.Add(t);
                chars += len;
            }
            kept.Reverse();
            return kept;
        }

        /// <summary>
        /// 发送一轮对话。回调在后台线程触发：(回复, 错误)。
        /// screenshot 不为空时，作为图片挂在这次输入上（深度模式下用户
        /// 聊起当前窗口内容时用，例如「你看这个好不好看」）。
        /// </summary>
        public void Send(string userInput, Action<string, string> callback, byte[]? screenshot = null)
        {
            Task.Run(async () =>
            {
                try
                {
                    // 1. 时间关键词 → 给输入补一个当前时间前缀
                    bool needsTime = TimeKeywords.Any(k => userInput.Contains(k));
                    string enriched = needsTime
                        ? $"[当前时间：{DateTime.Now:yyyy年MM月dd日 HH:mm:ss}]\n{userInput}"
                        : userInput;

                    // 2. 动态上下文：长期记忆。
                    //    省钱：注入时截断，避免记忆越长越贵（记忆本身不删，只是不进 prompt 那么多）
                    var sb = new StringBuilder();
                    if (!string.IsNullOrWhiteSpace(_userMemory))
                    {
                        string mem = _userMemory;
                        int cap = _cfg.TokenSaver ? 300 : int.MaxValue;
                        if (mem.Length > cap) mem = mem.Substring(0, cap) + "…";
                        sb.Append($"【你记住的关于 {PlayerName} 的事情】\n{mem}\n\n");
                    }

                    // 3. 组装消息：system + 动态上下文 + 历史 + 本次输入
                    var msgs = new List<ChatTurn> { new ChatTurn { Role = "system", Content = BuildSystemPrompt() } };
                    if (sb.Length > 0)
                        msgs.Add(new ChatTurn { Role = "user", Content = sb.ToString() });
                    msgs.AddRange(TrimHistory());
                    msgs.Add(new ChatTurn { Role = "user", Content = enriched });

                    // 4. 请求（截图可选，会挂在这条 user 消息上）
                    var res = await CallApi(msgs, _cfg.Model, _cfg.Temperature, _cfg.MaxTokens, screenshot, "chat").ConfigureAwait(false);
                    string raw = res.Content ?? "";
                    string clean = StripTags(raw).Trim();

                    // 4b. 撞到 token 上限 → 让菲比**重说一遍短一点的**。
                    //
                    // 为什么不直接把截断的半句话显示出来：
                    //   半句话读起来像 bug（「哥哥辛苦啦，要不要休」），
                    //   而且用户不知道发生了什么。重说一次钱花得很少
                    //   （输入走缓存、输出更短），体验却是完整的。
                    //
                    // 只在「有内容但被截断」时重试；空回复没有补救价值，直接报错。
                    if (res.Truncated && clean.Length > 0)
                    {
                        AppPaths.Log($"  [chat] 被截断（{clean.Length} 字），让菲比重说短一点");
                        var shorter = await RetryShorter(msgs, screenshot).ConfigureAwait(false);
                        if (shorter != null && shorter.Length > 0)
                        {
                            raw = shorter;
                            clean = StripTags(shorter).Trim();
                            res = new ApiResult { Content = shorter, FinishReason = "stop" };
                        }
                        else
                        {
                            // 重试也失败 → 用原来那半句，总比什么都没有强
                            AppPaths.Log("  [chat] 重说也失败，沿用截断内容");
                        }
                    }

                    // 空回复必须走失败分支。
                    //
                    // 踩过的坑：以前这里是
                    //     if (raw == null) { callback(null, "接口无返回内容"); return; }
                    //     string clean = StripTags(raw).Trim();
                    //     callback(clean, null);
                    // 「raw 非 null 但 clean 是空串」被当成了成功 ——
                    // 实测 completion=300（正好等于 MaxTokens，被截断），
                    // 界面显示「回复 0 字」，钱花了却什么都没看到，还没有任何提示。
                    if (clean.Length == 0)
                    {
                        AppPaths.Log($"  [chat] 空回复: finish_reason={res.FinishReason ?? "(无)"} " +
                                     $"raw长度={raw.Length} max_tokens={_cfg.MaxTokens}");
                        callback(null, DescribeEmptyReply(res));
                        return;
                    }

                    // 5. 记历史（只记纯对话，不记动态上下文）
                    _history.Add(new ChatTurn { Role = "user", Content = enriched });
                    _history.Add(new ChatTurn { Role = "assistant", Content = raw });
                    while (_history.Count > _cfg.HistoryLimit)
                        _history.RemoveAt(0);
                    _rounds++;

                    SaveHistory();   // 每轮落盘，关掉程序也不丢

                    callback(clean, null);

                    // 6. 记忆总结：按间隔触发，不再每轮都跑。
                    //    原来是每轮一次，等于每轮多烧一次完整 API 调用。
                    if (_cfg.EnableMemory)
                    {
                        int every = Math.Max(1, _cfg.MemoryInterval);
                        if (_rounds % every == 0)
                        {
                            try
                            {
                                // 省钱：总结喂最近的对话，不喂全部历史
                                string recent = string.Join("\n", TrimHistory()
                                    .TakeLast(4)
                                    .Select(t => (t.Role == "user" ? PlayerName : CharacterName) + "：" + t.Content));
                                await UpdateMemory(recent, clean).ConfigureAwait(false);
                                _lastMemoryRound = _rounds;
                                AppPaths.Log($"  [chat] 记忆总结已在第 {_rounds} 轮触发（每 {every} 轮一次）");
                            }
                            catch (Exception e) { AppPaths.Log($"  [chat] 记忆总结异常: {e.Message}"); }
                        }
                    }
                }
                catch (Exception e)
                {
                    var err = Classify(e);
                    AppPaths.Log($"  [chat] 发送失败 kind={err.Kind} retryable={err.Retryable}: {err.Detail}");
                    callback(null, err.Message);
                }
            });
        }

        private static string StripTags(string s) =>
            System.Text.RegularExpressions.Regex.Replace(s ?? "", "<[^>]+>", "");

        /// <summary>
        /// 主动搭话：没人说话时由菲比先开口。
        ///
        /// 和 Send 的区别：
        ///   - 历史里【不写入】一条假的 user 消息。否则上下文会变成
        ///     「（系统提示：用户闲置了）→ 菲比：…」，下一轮真实对话时
        ///     模型会以为自己刚才在跟系统说话。
        ///   - 但要把这句主动说的话写进历史（assistant），这样用户接着聊时
        ///     菲比知道自己刚说过什么，不会重复。
        ///   - 用一个独立的提示词，让人设知道这次是「自己起的话头」。
        /// </summary>
        public void SendProactive(string reason, string recentContext, Action<string, string> callback,
                                  byte[]? screenshot = null)
        {
            Task.Run(async () =>
            {
                try
                {
                    var msgs = BuildProactivePartsForTest(reason, recentContext);

                    var res = await CallApi(msgs, _cfg.Model, _cfg.Temperature, _cfg.MaxTokens, screenshot, "proactive").ConfigureAwait(false);
                    string raw = res.Content ?? "";
                    string clean = StripTags(raw).Trim();

                    // 和 Send 同样的坑：空串不能当成功。
                    // 主动发言尤其明显 —— 气泡里什么都没有，用户只会觉得「菲比卡住了」。
                    // 这里不回错误文案，直接静默放弃：主动搭话失败不该弹提示打扰人。
                    if (clean.Length == 0)
                    {
                        AppPaths.Log($"  [chat] 主动发言空回复: finish_reason={res.FinishReason ?? "(无)"} " +
                                     $"raw长度={raw.Length} max_tokens={_cfg.MaxTokens}");
                        return;
                    }

                    // 只记 assistant 侧，不记那条提示
                    _history.Add(new ChatTurn { Role = "assistant", Content = raw });
                    while (_history.Count > _cfg.HistoryLimit) _history.RemoveAt(0);

                    SaveHistory();   // 主动说的话也要存，否则重启后她会重复讲同一件事

                    callback(clean, null);
                }
                catch (Exception e)
                {
                    var err = Classify(e);
                    AppPaths.Log($"  [chat] 主动搭话失败 kind={err.Kind} retryable={err.Retryable}: {err.Detail}");
                    callback(null, err.Message);
                }
            });
        }

        /// <summary>
        /// 组装主动发言要发的消息序列。
        ///
        /// 抽成独立方法是为了让自检能直接检查这段内容 ——
        /// SendProactive 内部会真的发网络请求，自检没法验。
        /// </summary>
        public List<ChatTurn> BuildProactivePartsForTest(string reason, string recentContext)
        {
            var msgs = new List<ChatTurn> { new ChatTurn { Role = "system", Content = BuildSystemPrompt() } };

            // 长期记忆照常注入，让主动搭话也能接上之前聊过的事
            if (!string.IsNullOrWhiteSpace(_userMemory))
            {
                string mem = _userMemory;
                int cap = _cfg.TokenSaver ? 300 : int.MaxValue;
                if (mem.Length > cap) mem = mem.Substring(0, cap) + "…";
                msgs.Add(new ChatTurn { Role = "user", Content = $"【你记住的关于 {PlayerName} 的事情】\n{mem}" });
            }

            msgs.AddRange(TrimHistory());

            // 最后一条是「私下提示」而不是玩家发言：措辞刻意写成旁白口吻，
            // 避免模型把它当成用户在说话然后去回复它。
            string hint =
                $"（现在{PlayerName}没有在跟你说话。{reason}\n" +
                $"你想主动跟{PlayerName}说点什么。请直接说出一句话，自然一点，" +
                $"不要提到这是系统提示，也不要问「你在吗」这种空话。）";
            if (!string.IsNullOrWhiteSpace(recentContext))
                hint = $"（{recentContext}）\n" + hint;

            msgs.Add(new ChatTurn { Role = "user", Content = hint });
            return msgs;
        }

        /// <summary>
        /// 一次 API 调用的结果。
        ///
        /// 为什么不能只返回一个 string：
        ///   「返回 null」以前被当成「接口无返回内容」，但**空字符串**被当成了成功。
        ///   实测踩到：completion=300（正好等于 MaxTokens），钱花了，
        ///   但 content 是空串，界面显示「回复 0 字」——用户什么都看不到。
        ///   所以要能把「截断」和「真没内容」分开，才能给出有用的提示。
        /// </summary>
        public sealed class ApiResult
        {
            /// <summary>模型正文（未 strip）。null = 响应里根本没有 content 字段。</summary>
            public string? Content;

            /// <summary>stop / length / content_filter / null。</summary>
            public string? FinishReason;

            /// <summary>被截断（撞到 max_tokens）。</summary>
            public bool Truncated =>
                string.Equals(FinishReason, "length", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 撞到 token 上限后的补救：追加一句「说短点」再问一次。
        ///
        /// 为什么要追加一条 user 消息而不是改 system：
        ///   改 system 会让这一轮和前面的缓存前缀不一致，整段 prompt 重新计费。
        ///   追加在末尾只多付这一小段的钱，前面全部命中缓存。
        ///
        /// 返回 null 表示重试也失败了，调用方应沿用原来的截断内容。
        /// </summary>
        private async Task<string?> RetryShorter(List<ChatTurn> msgs, byte[]? screenshot)
        {
            try
            {
                var retry = BuildRetryMessages(msgs);

                // 输出额度给足一点：我们要的是「短」，但别又因为不够而再截断。
                int budget = RetryBudgetForTest;
                var r = await CallApi(retry, _cfg.Model, _cfg.Temperature, budget, screenshot, "retry")
                    .ConfigureAwait(false);

                string c = (r.Content ?? "").Trim();
                if (r.Truncated && c.Length == 0) return null;
                return c.Length == 0 ? null : c;
            }
            catch (Exception e)
            {
                AppPaths.Log($"  [chat] 重说失败: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// 正文为空时，按 finish_reason 给一句人话。
        /// 返回 null 表示「不算失败」——不用，留着接口对称。
        /// </summary>
        public static string DescribeEmptyReply(ApiResult? r)
        {
            if (r == null) return "服务器没给回话，再试一次？";
            if (r.Truncated)
                return "我想说的话太长了，被截断没说出来……你再说一次，我讲短点。";
            if (string.Equals(r.FinishReason, "content_filter", StringComparison.OrdinalIgnoreCase))
                return "这个话题我不太方便接，换个说法好吗？";
            return "我刚才走神了，什么都没说出来……再说一次？";
        }

        /// <summary>
        /// 把异常翻译成气泡里能看的一句话。
        /// 原来的 e.Message 会往气泡里塞整段英文，既看不懂又撑爆气泡。
        /// 完整堆栈仍然写进 pet.log。
        /// </summary>
        /// <summary>
        /// 一次失败的结构化描述。把「出了什么事」和「怎么跟用户说」分开。
        ///
        /// 为什么不再用一堆 string.Contains 判断：
        ///   旧实现是把「HTTP 500: {...}」和异常消息拼成一个大字符串，
        ///   再靠 Contains("402")、Contains("balance") 去猜分类。问题有三：
        ///     1) 顺序敏感 —— Contains("model") 会抢在别的前面命中，
        ///        任何含 "model" 的错误都被说成「模型名不对」；
        ///     2) 服务器返回的正文里如果恰好含某个数字就会误判；
        ///     3) 分类逻辑散在 if 链里，加一种错误要动全身。
        ///   现在改成：HTTP 状态码单独存字段，按码精确分派；
        ///   异常按类型分派；只有实在认不出来才退回文本匹配。
        /// </summary>
        public sealed class ChatError
        {
            /// <summary>给用户看的一句话（放气泡里）。</summary>
            public string Message = "";

            /// <summary>归类标签，只用于日志和自检。</summary>
            public string Kind = "unknown";

            /// <summary>是否值得重试（网络抖动、限流、服务端 5xx）。</summary>
            public bool Retryable;

            /// <summary>HTTP 状态码（不是 HTTP 错误时为 0）。</summary>
            public int Status;

            /// <summary>原始错误文本，写进日志。</summary>
            public string Detail = "";

            public override string ToString() => $"[{Kind}] {Message}";
        }

        /// <summary>
        /// HTTP 状态码 → 人话。非 HTTP 的错误走 FriendlyError。
        ///
        /// 单独抽出来是为了能离线自检 —— 网络错误没法在沙箱里真造出来，
        /// 但「429 该说什么」是纯函数，可以直接喂数字验。
        /// </summary>
        public static ChatError DescribeHttpStatus(int status, string? body)
        {
            string detail = Truncate(body ?? "", 300);

            // 优先用服务器正文里的 error.code/type（DeepSeek 会区分
            // insufficient_balance / invalid_api_key 等），拿不到再看状态码。
            string code = ExtractErrorCode(body);

            var err = new ChatError { Status = status, Detail = detail };

            switch (status)
            {
                case 400:
                    err.Kind = "bad_request";
                    err.Message = code == "invalid_request_error" || code.Length == 0
                        ? "这条消息服务器读不懂……换个说法再试试？"
                        : "请求被服务器拒绝了，可能是内容格式的问题。";
                    if (detail.Contains("image", StringComparison.OrdinalIgnoreCase))
                        err.Message = "截图太大或格式不对，服务器不收。可以试试调小截屏尺寸。";
                    break;

                case 401:
                case 403:
                    err.Kind = "auth";
                    err.Message = "API Key 不对，服务器拒绝了。右键 → 设置 → API 配置 检查一下。";
                    break;

                case 402:
                case 429 when code.Contains("insufficient", StringComparison.OrdinalIgnoreCase):
                    err.Kind = "balance";
                    err.Message = "账户余额不够了，去 DeepSeek 平台充值吧。";
                    break;

                case 429:
                    err.Kind = "rate_limit";
                    err.Retryable = true;
                    err.Message = "问得太快了，缓一下再说。";
                    break;

                case 404:
                    err.Kind = "not_found";
                    err.Message = "模型名或接口地址不对。右键 → 设置 → API 配置 里改一下。";
                    break;

                case 500:
                case 502:
                case 503:
                case 504:
                    err.Kind = "server";
                    err.Retryable = true;
                    err.Message = $"服务器那边出问题了（HTTP {status}），等会儿再试。";
                    break;

                default:
                    err.Kind = "http_" + status;
                    err.Retryable = status >= 500;
                    err.Message = $"服务器返回 HTTP {status}，我不太明白发生了什么。";
                    break;
            }
            return err;
        }

        /// <summary>从错误正文里抠出 error.code / error.type（抠不到返回空串）。</summary>
        public static string ExtractErrorCode(string? body)
        {
            if (string.IsNullOrWhiteSpace(body)) return "";
            try
            {
                var n = JsonNode.Parse(body);
                return n?["error"]?["code"]?.ToString()
                    ?? n?["error"]?["type"]?.ToString()
                    ?? "";
            }
            catch { return ""; }   // 正文不是 JSON 就算了，不值得为此报错
        }

        /// <summary>
        /// 把异常按**类型**归类，而不是靠消息文本碰运气。
        /// 返回给用户的话 + 是否值得重试。
        /// </summary>
        public static ChatError DescribeException(Exception e)
        {
            var ex = e;
            while (ex.InnerException != null) ex = ex.InnerException;

            var err = new ChatError { Detail = Truncate(e.ToString(), 400) };

            switch (ex)
            {
                case TaskCanceledException:
                case OperationCanceledException:
                    err.Kind = "timeout";
                    err.Retryable = true;
                    err.Message = "服务器没响应，超时了。等会儿再试？";
                    return err;

                case System.Net.Sockets.SocketException se:
                    err.Kind = "network";
                    err.Retryable = true;
                    err.Message = se.SocketErrorCode switch
                    {
                        System.Net.Sockets.SocketError.HostNotFound =>
                            "域名解析不了，检查一下网络。",
                        System.Net.Sockets.SocketError.NetworkUnreachable =>
                            "网络不通，检查一下网线或者 WiFi。",
                        System.Net.Sockets.SocketError.ConnectionRefused =>
                            "连接被拒绝了，可能被防火墙拦了。",
                        _ => "网络好像断了，我够不着服务器。",
                    };
                    return err;

                case System.Net.Http.HttpRequestException:
                    err.Kind = "http_transport";
                    err.Retryable = true;
                    // 证书问题单独说，因为它通常是代理/抓包软件造成的
                    if (ex.Message.Contains("SSL", StringComparison.OrdinalIgnoreCase) ||
                        ex.Message.Contains("certificate", StringComparison.OrdinalIgnoreCase))
                        err.Message = "连不上服务器（SSL 证书问题）。检查一下代理设置。";
                    else
                        err.Message = "连不上服务器，检查一下网络。";
                    return err;

                case System.Text.Json.JsonException:
                    err.Kind = "bad_json";
                    err.Message = "服务器返回的内容我看不懂（不是合法 JSON）。";
                    return err;
            }

            // 认不出来的，退回文本匹配兜底
            return DescribeByText(e.Message);
        }

        /// <summary>
        /// 最后一道兜底：没有类型信息时按关键字猜。
        /// 只有 DescribeException 认不出来才会走到这里。
        /// </summary>
        public static ChatError DescribeByText(string? raw)
        {
            string msg = raw ?? "";
            var err = new ChatError { Kind = "unknown", Detail = Truncate(msg, 300) };

            if (msg.Contains("API Key 未填写"))
            {
                err.Kind = "no_key";
                err.Message = "还没填 API Key 呢。右键 → 设置 → API 配置。";
                return err;
            }
            if (msg.Contains("SSL") || msg.Contains("certificate"))
            {
                err.Kind = "ssl";
                err.Retryable = true;
                err.Message = "连不上服务器（SSL 问题）。检查一下代理设置。";
                return err;
            }
            if (msg.Contains("timed out") || msg.Contains("timeout"))
            {
                err.Kind = "timeout";
                err.Retryable = true;
                err.Message = "服务器没响应，超时了。等会儿再试？";
                return err;
            }
            if (msg.Contains("No such host") || msg.Contains("Name or service not known"))
            {
                err.Kind = "dns";
                err.Retryable = true;
                err.Message = "域名解析不了，检查一下网络。";
                return err;
            }

            // 真兜底：截断原文，别让气泡撑爆
            err.Message = msg.Length <= 60 ? msg : msg.Substring(0, 60) + "…";
            if (err.Message.Length == 0) err.Message = "出了点问题，但我也说不清是什么……";
            return err;
        }

        /// <summary>
        /// 兼容旧接口：只要一句话。
        /// </summary>
        private static string FriendlyError(Exception e) => DescribeException(e).Message;

        /// <summary>
        /// 带上「已经归好类」的错误的异常。
        ///
        /// 为什么需要它：HTTP 状态码在 CallApi 里最清楚（那里才拿得到
        /// response），但「怎么跟用户说」要等到最外层 catch 才知道。
        /// 用这个类型把分类结果一路带出去，省得在外面靠字符串反推。
        /// </summary>
        public sealed class ChatException : Exception
        {
            public ChatError Error { get; }

            public ChatException(ChatError err)
                : base($"[{err.Kind}] {err.Message}")   // Message 供日志用
            {
                Error = err;
            }
        }

        /// <summary>把任意异常转成分类结果（已经是 ChatException 就直接取出来）。</summary>
        public static ChatError Classify(Exception e)
            => e is ChatException ce ? ce.Error : DescribeException(e);

        // ---------- HTTP ----------
        /// <summary>
        /// 发请求。attachedImage 不为空时，把它作为图片挂在**最后一条 user 消息**上。
        ///
        /// 为什么必须先找到最后一条 user 消息：
        ///   视觉模型只接受 user 消息携带图片，system / assistant 带图会返回 400。
        ///   而且 content 要从「字符串」变成「块数组」：
        ///     [{"type":"text","text":...}, {"type":"image_url","image_url":{"url":"data:..."}}]
        ///   非 user 消息（包括 system）保持纯字符串不动。
        /// </summary>
        private async Task<ApiResult> CallApi(List<ChatTurn> msgs, string model, double temp, int maxTokens,
                                              byte[]? attachedImage = null, string kind = "chat")
        {
            if (string.IsNullOrWhiteSpace(_cfg.ApiKey))
                throw new InvalidOperationException("API Key 未填写（chat-config.json 的 ApiKey）");

            int lastUser = -1;
            if (attachedImage != null && attachedImage.Length > 0)
            {
                for (int i = msgs.Count - 1; i >= 0; i--)
                    if (msgs[i].Role == "user") { lastUser = i; break; }
            }

            var arr = new JsonArray();
            for (int i = 0; i < msgs.Count; i++)
            {
                var m = msgs[i];
                if (i != lastUser)
                {
                    arr.Add(new JsonObject { ["role"] = m.Role, ["content"] = m.Content });
                    continue;
                }

                string b64 = Convert.ToBase64String(attachedImage!);
                var blocks = new JsonArray
                {
                    new JsonObject { ["type"] = "text", ["text"] = m.Content },
                    new JsonObject
                    {
                        ["type"] = "image_url",
                        // detail=low 会把图缩到 512 再进模型，最省 token（每张上限 1024）
                        ["image_url"] = new JsonObject
                        {
                            ["url"] = "data:image/jpeg;base64," + b64,
                            ["detail"] = "low",
                        },
                    },
                };
                arr.Add(new JsonObject { ["role"] = m.Role, ["content"] = blocks });
            }

            var body = new JsonObject
            {
                ["model"] = model,
                ["messages"] = arr,
                ["temperature"] = temp,
                ["max_tokens"] = maxTokens,
            };

            string url = _cfg.BaseUrl.TrimEnd('/') + "/v1/chat/completions";
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Add("Authorization", "Bearer " + _cfg.ApiKey);
            req.Content = new StringContent(body.ToJsonString(), new UTF8Encoding(false), "application/json");

            using var resp = await Http.SendAsync(req).ConfigureAwait(false);
            string text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

            // 非 2xx：按状态码精确分类，而不是把状态码塞进字符串让外层去猜。
            if (!resp.IsSuccessStatusCode)
            {
                var httpErr = DescribeHttpStatus((int)resp.StatusCode, text);
                AppPaths.Log($"  [chat] HTTP {(int)resp.StatusCode} kind={httpErr.Kind} " +
                             $"code={ExtractErrorCode(text)} body={Truncate(text, 200)}");
                throw new ChatException(httpErr);
            }

            var node = JsonNode.Parse(text);

            // 记用量。失败不影响对话 —— 统计是附加功能，不能拖累主流程。
            try { Usage.Add(model, kind, node?["usage"]); }
            catch (Exception ue) { AppPaths.Log($"  [用量] 记录异常: {ue.Message}"); }

            // content 可能是 null（JSON null）→ ToString() 得到空串。
            // finish_reason 一定要读：撞到 max_tokens 时它是 "length"，
            // 这是「为什么没内容」唯一的线索。
            var choice = node?["choices"]?[0];
            return new ApiResult
            {
                Content = choice?["message"]?["content"]?.ToString(),
                FinishReason = choice?["finish_reason"]?.ToString(),
            };
        }

        private static string Truncate(string s, int n) =>
            string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s.Substring(0, n) + "…");

        // ---------- 记忆总结 ----------
        /// <summary>
        /// 更新长期记忆。
        /// 省钱：只在到达间隔时被调用（见 Send 里的 _rounds % every）。
        /// </summary>
        private async Task UpdateMemory(string recentDialogue, string lastReply)
        {
            string current = _userMemory;
            // 提示词尽量短：它是每次总结都要重发一遍的固定成本
            string prompt =
                "根据下面的对话，更新关于用户的长期记忆。\n\n" +
                "已知：\n" + (string.IsNullOrWhiteSpace(current) ? "（暂无）" : current) + "\n\n" +
                "对话：\n" + recentDialogue + "\n" +
                $"{CharacterName}：{lastReply}\n\n" +
                "只提取值得长期记住的事实（喜好、习惯、生日、重要经历等）。" +
                "输出一段完整的新总结，语言自然；没有新事实就原样输出已知内容。只输出总结本身。";

            string model = string.IsNullOrWhiteSpace(_cfg.MemoryModel)
                ? (_cfg.TokenSaver ? CheapModel : _cfg.Model)
                : _cfg.MemoryModel;

            var msgs = new List<ChatTurn> { new ChatTurn { Role = "user", Content = prompt } };
            var memRes = await CallApi(msgs, model, 0.3, 300, null, "memory").ConfigureAwait(false);
            string? newSummary = memRes.Content;
            if (newSummary != null)
            {
                newSummary = newSummary.Trim();
                if (newSummary.Length > 0 && newSummary != current)
                {
                    SaveMemory(newSummary);
                    _userMemory = newSummary;
                    AppPaths.Log($"  [chat] 记忆已更新({model}): {newSummary.Length}字");
                }
            }
        }

        /// <summary>省钱模式下用来做记忆总结的模型。</summary>
        private const string CheapModel = "deepseek-flash";

        // ---------- 自检钩子（只有测试程序用） ----------

        /// <summary>塞一条历史进上下文，用于测试裁剪效果。</summary>
        public void InjectHistoryForTest(string user, string assistant)
        {
            _history.Add(new ChatTurn { Role = "user", Content = user });
            _history.Add(new ChatTurn { Role = "assistant", Content = assistant });
            while (_history.Count > _cfg.HistoryLimit) _history.RemoveAt(0);
        }

        /// <summary>
        /// 把轮数推到指定值（自检用）。
        ///
        /// 为什么需要它：InjectHistoryForTest 只塞历史、不动 _rounds，
        /// 于是「改记忆后计数被拉到当前轮」那条断言会变成 0 == 0 ——
        /// 看着通过，其实什么都没验。这里给个能把轮数推上去的钩子，
        /// 让断言真的有意义。
        /// </summary>
        public void SetRoundCountForTest(int rounds) => _rounds = rounds;

        /// <summary>按当前配置组装一次请求体，返回其字节数（不真的发出去）。</summary>
        public int BuildRequestSizeForTest(string userInput)
        {
            return BuildRequestPartsForTest(userInput).total;
        }

        /// <summary>
        /// 重说时追加的那条催促消息。抽成常量是为了自检能断言它的内容 ——
        /// 「限定 50 字」这种要求如果哪天被改掉，测试会立刻发现。
        /// </summary>
        public const string RetryPrompt =
            "（刚才那段太长了被截断了。请用**一句话**简短地重说一遍，" +
            "控制在 50 字以内，不要解释原因。）";

        /// <summary>重说时给模型的输出预算。</summary>
        public int RetryBudgetForTest => Math.Max(160, _cfg.MaxTokens / 2);

        /// <summary>
        /// 构造重说用的消息序列：**追加**一条催促，前面原样保留 ——
        /// 这样前缀缓存仍然命中，只多付这一小段。
        ///
        /// 真实路径和自检路径共用这一个函数，否则测试可能测的是
        /// 一份和线上不一样的构造逻辑（这个坑在 usage.cs 那边踩过）。
        /// </summary>
        private static List<ChatTurn> BuildRetryMessages(List<ChatTurn> msgs)
            => new List<ChatTurn>(msgs)
            {
                new ChatTurn { Role = "user", Content = RetryPrompt },
            };

        /// <summary>
        /// 组装重说请求的 JSON 体（自检用）。
        /// </summary>
        public string BuildRetryBodyForTest(List<ChatTurn> msgs)
            => BuildRequestBodyForTest(BuildRetryMessages(msgs), null);

        /// <summary>
        /// 组装真正要发出去的 JSON 请求体（自检用，不发网络请求）。
        ///
        /// 和 CallApi 里那段是同一套逻辑：有图时最后一条 user 消息的 content
        /// 要变成块数组。之所以抽出来，是因为「块数组 vs 纯字符串」这件事
        /// 只能靠检查真实 JSON 来确认 —— 视觉接口对格式很挑，
        /// 图片放错角色（system/assistant）会直接 400。
        /// </summary>
        public string BuildRequestBodyForTest(List<ChatTurn> msgs, byte[]? image)
        {
            int lastUser = -1;
            if (image != null && image.Length > 0)
            {
                for (int i = msgs.Count - 1; i >= 0; i--)
                    if (msgs[i].Role == "user") { lastUser = i; break; }
            }

            var arr = new JsonArray();
            for (int i = 0; i < msgs.Count; i++)
            {
                var m = msgs[i];
                if (i != lastUser)
                {
                    arr.Add(new JsonObject { ["role"] = m.Role, ["content"] = m.Content });
                    continue;
                }
                string b64 = Convert.ToBase64String(image!);
                arr.Add(new JsonObject
                {
                    ["role"] = m.Role,
                    ["content"] = new JsonArray
                    {
                        new JsonObject { ["type"] = "text", ["text"] = m.Content },
                        new JsonObject
                        {
                            ["type"] = "image_url",
                            ["image_url"] = new JsonObject
                            {
                                ["url"] = "data:image/jpeg;base64," + b64,
                                ["detail"] = "low",
                            },
                        },
                    },
                });
            }

            var body = new JsonObject
            {
                ["model"] = _cfg.Model,
                ["messages"] = arr,
                ["temperature"] = _cfg.Temperature,
                ["max_tokens"] = _cfg.MaxTokens,
            };
            return body.ToJsonString();
        }

        /// <summary>返回各部分明细，用来定位 token 花在哪。</summary>
        public (int system, int memory, int history, int input, int total, int histTurns)
            BuildRequestPartsForTest(string userInput)
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(_userMemory))
            {
                string mem = _userMemory;
                int cap = _cfg.TokenSaver ? 300 : int.MaxValue;
                if (mem.Length > cap) mem = mem.Substring(0, cap) + "…";
                sb.Append($"【你记住的关于 {PlayerName} 的事情】\n{mem}\n\n");
            }

            string sys = BuildSystemPrompt();
            var trimmed = TrimHistory();

            var msgs = new List<ChatTurn> { new ChatTurn { Role = "system", Content = sys } };
            if (sb.Length > 0) msgs.Add(new ChatTurn { Role = "user", Content = sb.ToString() });
            msgs.AddRange(trimmed);
            msgs.Add(new ChatTurn { Role = "user", Content = userInput });

            int total = 0;
            foreach (var m in msgs) total += m.Content.Length + m.Role.Length + 20;

            int histChars = 0;
            foreach (var t in trimmed) histChars += t.Content.Length;

            return (sys.Length, sb.Length, histChars, userInput.Length, total, trimmed.Count);
        }

        // ---------- 给菜单用的操作 ----------
        /// <summary>
        /// 清空对话上下文。
        ///
        /// **必须同时删掉磁盘上的存档** —— 只清内存的话，
        /// 重启后「刚才忘掉的又回来了」，用户会觉得清空功能坏了。
        /// 这是历史持久化最容易踩的坑（NEXT.md 里专门标过）。
        /// </summary>
        public void ClearHistory()
        {
            _history.Clear();
            DeleteHistoryFile();
            AppPaths.Log("  [chat] 上下文已清空（含磁盘存档）");
        }
        public int HistoryCount => _history.Count;

        /// <summary>
        /// 历史对话的只读快照（「查看上下文」菜单用）。
        /// 返回副本而不是 _history 本身：菜单是异步弹出的，
        /// 期间可能正好有一次回复写进 _history，直接暴露引用会在遍历时抛
        /// 「集合已修改」。这里复制一份，代价可以忽略。
        /// </summary>
        public List<ChatTurn> HistorySnapshot() => new List<ChatTurn>(_history);

        public string UserMemory => _userMemory;
        /// <summary>当前人设的描述文本（自检用）。</summary>
        public string PersonaDescription => _personaDesc;
        /// <summary>本会话累计轮数（用于记忆总结间隔）。</summary>
        public int RoundCount => _rounds;
        public void ClearMemory() { _userMemory = ""; SaveMemory(""); AppPaths.Log("  [chat] 记忆已清空"); }

        /// <summary>
        /// 直接改写记忆（用户手工编辑）。
        ///
        /// 两个必须做的事：
        ///   1) 持久化到磁盘，不然重启就没了；
        ///   2) **把 _lastMemoryRound 拉回当前轮数** —— 否则如果刚好
        ///      差一两轮就要触发自动总结，用户刚写进去的东西会被
        ///      「根据对话重新总结」覆盖掉。这是最容易踩的坑：
        ///      用户手动教了菲比一件事，几轮后发现她忘了。
        ///      拉回来等于「从现在起重新开始计间隔」。
        /// </summary>
        public void SetMemory(string text)
        {
            string v = (text ?? "").Trim();
            _userMemory = v;
            SaveMemory(v);
            _lastMemoryRound = _rounds;
            AppPaths.Log($"  [chat] 记忆已被手工改写：{v.Length} 字" +
                         $"（总结间隔从第 {_rounds} 轮重新计）");
        }

        /// <summary>
        /// 追加一条记忆（「让菲比记住…」用）。
        /// 用换行拼，不覆盖原有的 —— 用户往往只是想补一句。
        /// </summary>
        public void AppendMemory(string text)
        {
            string v = (text ?? "").Trim();
            if (v.Length == 0) return;

            string cur = (_userMemory ?? "").Trim();
            string merged = cur.Length == 0 ? v : cur + "\n" + v;
            SetMemory(merged);
        }

        /// <summary>记忆的字数（自检和菜单显示用）。</summary>
        public int MemoryLength => (_userMemory ?? "").Length;

        /// <summary>
        /// 上次做记忆总结时的轮数（自检用）。
        ///
        /// 暴露它是为了验证一个很容易踩、但肉眼看不出来的坑：
        /// 用户手工写完记忆后，_lastMemoryRound 必须跟着拉到当前轮，
        /// 否则下一次自动总结会把他刚写的东西覆盖掉。
        /// </summary>
        public int LastMemoryRoundForTest => _lastMemoryRound;

        /// <summary>切换人格档并重载。</summary>
        public void SetPersona(string key)
        {
            _cfg.PersonaKey = key;
            LoadPersona();
        }

        /// <summary>
        /// 自检用：当前人设拼出来的 system prompt。
        ///
        /// 暴露它是因为人设是**纯数据**驱动的 —— 文件写错了（字段名拼错、
        /// 层级放错）不会抛异常，只会让菲比默默变成默认性格。
        /// 能直接看到拼出来的提示词，才能验证「人设真的生效了」。
        /// </summary>
        public string SystemPromptForTest => BuildSystemPrompt();

        /// <summary>自检用：当前实际用的人设档名。</summary>
        public string UsedPersonaKeyForTest => _usedPersonaKey;

        /// <summary>列出存档里可用的人格档名。</summary>
        public List<string> ListPersonas()        {
            var list = new List<string>();
            try
            {
                if (!File.Exists(ArchivePath)) return list;
                var prompts = JsonNode.Parse(File.ReadAllText(ArchivePath))?["data"]?["prompts"]?.AsObject();
                if (prompts != null) foreach (var kv in prompts) list.Add(kv.Key);
            }
            catch { }
            return list;
        }
    }
}
