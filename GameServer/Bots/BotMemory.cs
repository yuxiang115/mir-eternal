using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

namespace GameServer.Bots
{
    /// <summary>一条长期记忆(事件/里程碑都走这个结构,重要性参与检索排序)。</summary>
    public class MemoryEntry
    {
        public DateTime Time;
        /// <summary>1=琐事 3=一般 5=大事(升级/好装备/结仇)。</summary>
        public int Importance;
        public string Text;

        public MemoryEntry() { }
        public MemoryEntry(DateTime time, int importance, string text)
        {
            Time = time; Importance = importance; Text = text;
        }
    }

    /// <summary>一条带时间的承诺/计划:到点由服务器唤醒 bot 兑现(如"明晚8点和123213组队打祖玛")。</summary>
    public class BotCommitment
    {
        public string Description;
        /// <summary>相关玩家名(可空),便于提醒时直接想起是谁。</summary>
        public string WithPlayer;
        /// <summary>该兑现的时间(服务器本地时间)。</summary>
        public DateTime DueAt;
        /// <summary>pending=待兑现 done=已兑现 missed=过期没兑现(bot 会记得自己放鸽子)。</summary>
        public string Status = "pending";
        public bool Reminded;
        public DateTime CreatedAt;

        public BotCommitment() { }
        public BotCommitment(string description, string withPlayer, DateTime dueAt)
        {
            Description = description;
            WithPlayer = withPlayer;
            DueAt = dueAt;
            CreatedAt = DateTime.Now;
        }
    }

    /// <summary>对一个玩家的结构化关系:熟人/朋友/仇人 + 好感(-100敌对 ~ +100挚友)。</summary>
    public class BotRelationship
    {
        /// <summary>路人/熟人/朋友/兄弟/仇人</summary>
        public string Relation = "路人";
        /// <summary>-100 ~ +100。负数=敌对,0=无感,正数=亲近。</summary>
        public int Affinity;
        public string Note = "";

        public BotRelationship() { }
        public BotRelationship(string relation, int affinity, string note)
        {
            Relation = relation; Affinity = affinity; Note = note ?? "";
        }
    }

    /// <summary>
    /// 机器人的长期记忆,跨服务器重启持久化(BotMemory/<角色名>.json)。
    /// 分层(参考 Generative Agents / MemGPT 的裁剪版):
    ///   Milestones 里程碑(升级/重要装备/重要关系,量少全量注入)
    ///   Episodes    经历的事件流(带重要度,检索注入最近+重要的)
    ///   Reflections 反思洞察(定期由 LLM 从近期经历提炼)
    ///   People      人物关系印象
    ///   Progress    自动进度快照(等级/金币/装备,最新一条注入)
    /// 写入途径:LLM remember 工具 + 服务器自动事件(升级/死亡/捡到装备/进度)。
    /// </summary>
    public class BotMemory
    {
        public List<MemoryEntry> Milestones = new List<MemoryEntry>();
        public List<MemoryEntry> Episodes = new List<MemoryEntry>();
        public List<MemoryEntry> Reflections = new List<MemoryEntry>();
        public Dictionary<string, string> People = new Dictionary<string, string>();
        /// <summary>结构化关系:玩家名 → 关系类型+好感。朋友还是仇人,数据说了算。</summary>
        public Dictionary<string, BotRelationship> Relationships = new Dictionary<string, BotRelationship>();
        /// <summary>游戏知识库:agent 玩游戏摸出来的规律(怪物数值/掉落/地图/NPC/物价),像玩家的攻略本,越玩越厚。</summary>
        public List<string> Knowledge = new List<string>();
        /// <summary>长期目标(无截止日,如"冲40级""攒钱买裁决"):观察常驻,由 set_goal/drop_goal 管理。</summary>
        public List<string> Goals = new List<string>();
        /// <summary>带时间的承诺/计划,到点唤醒兑现;跨重启存活 —— 约了明天就是明天。</summary>
        public List<BotCommitment> Commitments = new List<BotCommitment>();
        /// <summary>旧版进度快照字段(兼容旧存档);进度现随每轮观察实时生成,不再写入长期记忆。</summary>
        public string Progress = "";
        /// <summary>是否已领过启动资金/装备(每个号只发一次)。</summary>
        public bool StartupKitGranted;
        /// <summary>自我近况/心事(LLM 可改写)。</summary>
        public string SelfNote = "";
        /// <summary>尚未被反思消化的事件数(达到阈值触发反思)。</summary>
        public int UnreflectedCount;

        [JsonIgnore]
        public bool Dirty;

        private static string MemoryDir
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BotMemory"); }
        }

        private static string PathOf(string botName)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                botName = botName.Replace(c, '_');
            return Path.Combine(MemoryDir, botName + ".json");
        }

        public static BotMemory Load(string botName)
        {
            try
            {
                var path = PathOf(botName);
                if (File.Exists(path))
                {
                    var memory = JsonConvert.DeserializeObject<BotMemory>(File.ReadAllText(path, Encoding.UTF8));
                    if (memory != null)
                        return memory;
                }
            }
            catch (Exception ex)
            {
                MainProcess.AddSystemLog("[Bot] 读取 " + botName + " 的记忆失败(重新开始): " + ex.Message);
            }
            return new BotMemory();
        }

        public void Save(string botName)
        {
            try
            {
                Directory.CreateDirectory(MemoryDir);
                File.WriteAllText(PathOf(botName), JsonConvert.SerializeObject(this, Formatting.Indented), Encoding.UTF8);
                Dirty = false;
            }
            catch (Exception ex)
            {
                MainProcess.AddSystemLog("[Bot] 保存 " + botName + " 的记忆失败: " + ex.Message);
            }
        }

        // ===================== 写入 =====================

        public void RecordMilestone(string text, int importance = 5)
        {
            Milestones.Add(new MemoryEntry(DateTime.Now, importance, text));
            Trim(Milestones, 60);
            Dirty = true;
        }

        public void RecordEpisode(string text, int importance = 3)
        {
            var entry = new MemoryEntry(DateTime.Now, importance, text);
            Episodes.Add(entry);
            UnreflectedCount++;
            Trim(Episodes, 200);
            Dirty = true;
        }

        public void RecordReflection(string text)
        {
            Reflections.Add(new MemoryEntry(DateTime.Now, 4, text));
            Trim(Reflections, 30);
            UnreflectedCount = 0;
            Dirty = true;
        }

        private static void Trim(List<MemoryEntry> list, int max)
        {
            if (list.Count <= max)
                return;
            // 保最近的,也保重要的:先按重要度留下高位,再按时间排序
            list.Sort((a, b) => b.Time.CompareTo(a.Time));
            var kept = list.OrderByDescending(e => e.Importance).Take(max * 3 / 4).ToList();
            kept.AddRange(list.Where(e => !kept.Contains(e)).Take(max - kept.Count));
            list.Clear();
            list.AddRange(kept.OrderByDescending(e => e.Time));
        }

        /// <summary>record 工具入口:按关键词路由到 人物印象/近况/里程碑/事件。</summary>
        public string Remember(string text)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0)
                return "没有内容";

            var colon = text.IndexOf(':');
            if (colon > 2 && colon < 20 && text.StartsWith("关于"))
            {
                var name = text.Substring(2, colon - 2).Trim();
                if (name.Length > 0)
                {
                    var merged = People.ContainsKey(name) && People[name].Length > 0 && !People[name].Contains(text.Substring(colon + 1).Trim())
                        ? People[name] + ";" + text.Substring(colon + 1).Trim()
                        : text.Substring(colon + 1).Trim();
                    People[name] = merged;
                    Dirty = true;
                    return "已更新对 " + name + " 的印象";
                }
            }

            if (text.StartsWith("近况") || text.StartsWith("我目前"))
            {
                SelfNote = text;
                Dirty = true;
                return "已记下近况";
            }

            if (text.StartsWith("知识"))
            {
                var fact = text.Substring(2).TrimStart(':', '：', ' ');
                if (fact.Length < 3)
                    return "知识内容太短";
                // 去重:已记住的知识不重复记
                if (Knowledge.Any(k => k.Contains(fact.Substring(0, Math.Min(8, fact.Length)))))
                    return "已经知道了";
                Knowledge.Add(fact);
                if (Knowledge.Count > 40)
                    Knowledge = Knowledge.Skip(Knowledge.Count - 40).ToList();
                Dirty = true;
                return "新知识+1";
            }

            if (text.StartsWith("里程碑") || text.Contains("升到") && text.Contains("级"))
            {
                RecordMilestone(text, 5);
                return "已记入里程碑";
            }

            RecordEpisode(text, 3);
            return "已记入记忆";
        }

        // ===================== 承诺(Goal/Commitment) =====================

        /// <summary>登记一条承诺;when 支持 "HH:mm"、"明天HH:mm"、"MM-dd HH:mm"、自然语言兜底(1小时后)。</summary>
        public BotCommitment MakeCommitment(string what, string when, string withPlayer)
        {
            var due = ParseDueTime(when);
            var commitment = new BotCommitment(what, withPlayer, due);
            Commitments.Add(commitment);
            if (Commitments.Count > 40)
                Commitments = Commitments.Skip(Commitments.Count - 40).ToList();
            Dirty = true;
            return commitment;
        }

        /// <summary>按描述子串完成/放弃一条承诺。找不到返回 null。</summary>
        public BotCommitment CompleteCommitment(string whatContains, string status = "done")
        {
            var commitment = Commitments.LastOrDefault(c => c.Status == "pending" && c.Description.Contains(whatContains ?? ""));
            if (commitment != null)
            {
                commitment.Status = status;
                Dirty = true;
            }
            return commitment;
        }

        private static DateTime ParseDueTime(string when)
        {
            when = (when ?? "").Trim();
            try
            {
                var now = DateTime.Now;
                when = when.Replace(":", ":").Replace("：", ":").Replace("点", ":").Replace("分", "");
                when = when.Replace("今晚", "").Replace("今天", "").Replace("现在马上", "");

                if (when.StartsWith("明天") || when.StartsWith("明晚") || when.StartsWith("明日"))
                {
                    when = when.Substring(2);
                    var time = ParseHourMinute(when);
                    if (time.HasValue)
                        return now.Date.AddDays(1).Add(time.Value);
                }
                else if (when.StartsWith("后天"))
                {
                    when = when.Substring(2);
                    var time = ParseHourMinute(when);
                    if (time.HasValue)
                        return now.Date.AddDays(2).Add(time.Value);
                }
                else
                {
                    // 带日期 "10-09 20:00" 或 "10月9日 20:00"
                    var dateMatch = System.Text.RegularExpressions.Regex.Match(when, @"(\d{1,2})[-/月](\d{1,2})日?\s*(\d{1,2}):?(\d{2})?");
                    if (dateMatch.Success)
                    {
                        var month = int.Parse(dateMatch.Groups[1].Value);
                        var day = int.Parse(dateMatch.Groups[2].Value);
                        var hour = int.Parse(dateMatch.Groups[3].Value);
                        var minute = dateMatch.Groups[4].Success ? int.Parse(dateMatch.Groups[4].Value) : 0;
                        return new DateTime(now.Year, month, day, hour, minute, 0);
                    }

                    var time2 = ParseHourMinute(when);
                    if (time2.HasValue)
                    {
                        var due = now.Date.Add(time2.Value);
                        if (due <= now.AddMinutes(10)) // 今天这个点已过 → 当作明天
                            due = due.AddDays(1);
                        return due;
                    }

                    // "X小时后" / "X分钟后"
                    var spanMatch = System.Text.RegularExpressions.Regex.Match(when, @"(\d+)\s*(小时|分钟)后?");
                    if (spanMatch.Success)
                    {
                        var value = int.Parse(spanMatch.Groups[1].Value);
                        return spanMatch.Groups[2].Value == "小时" ? now.AddHours(value) : now.AddMinutes(value);
                    }
                }
            }
            catch { }
            return DateTime.Now.AddHours(1); // 解析不了:一小时内提醒一次,至少不失约
        }

        private static TimeSpan? ParseHourMinute(string text)
        {
            var match = System.Text.RegularExpressions.Regex.Match(text, @"(\d{1,2}):?(\d{2})|(\d{1,2})点半|晚上(\d{1,2})|(\d{1,2})点");
            if (!match.Success)
                return null;
            int hour, minute = 0;
            if (match.Groups[1].Success)
            {
                hour = int.Parse(match.Groups[1].Value);
                minute = int.Parse(match.Groups[2].Value);
            }
            else if (match.Groups[3].Success)
            {
                hour = int.Parse(match.Groups[3].Value);
                minute = 30;
            }
            else if (match.Groups[4].Success)
            {
                hour = int.Parse(match.Groups[4].Value);
                if (hour <= 6) hour += 12; // "晚上8点"
            }
            else
            {
                hour = int.Parse(match.Groups[5].Value);
                if ((text.Contains("晚") || text.Contains("夜")) && hour <= 6) hour += 12;
            }
            if (hour < 0 || hour > 23) return null;
            return new TimeSpan(hour, minute, 0);
        }

        // ===================== 检索(注入 system prompt) =====================

        /// <summary>
        /// 组装长期记忆的稳定层(注入 messages[1],低频重写以保前缀缓存):
        /// 里程碑/反思/近期事件/近况。高频内容(当前状态快照、眼前玩家的印象)不在这 ——
        /// 它们每轮随观察尾部发送,见 BotSnapshot/BotBrain.BuildObservation。
        /// </summary>
        public string BuildPromptSection(IReadOnlyCollection<string> relevantNames, int maxChars = 3000)
        {
            var parts = new List<string>();

            if (!string.IsNullOrWhiteSpace(SelfNote))
                parts.Add("心事: " + SelfNote);

            if (Relationships.Count > 0)
            {
                var relations = Relationships.OrderByDescending(r => Math.Abs(r.Value.Affinity)).Take(8);
                parts.Add("你和这些人的关系:\n" + string.Join("\n", relations.Select(r =>
                    "- " + r.Key + ": " + r.Value.Relation + "(好感" + (r.Value.Affinity >= 0 ? "+" : "") + r.Value.Affinity + ")"
                    + (r.Value.Note != "" ? " " + r.Value.Note : ""))));
            }

            if (Knowledge.Count > 0)
                parts.Add("你知道的游戏门道(自己摸出来的):\n" + string.Join("\n", Knowledge.TakeLast(30).Select(k => "- " + k)));

            if (Milestones.Count > 0)
            {
                var milestones = Milestones.OrderByDescending(m => m.Time).Take(6);
                parts.Add("人生大事:\n" + string.Join("\n", milestones.Select(m => "- " + m.Time.ToString("MM-dd ") + m.Text)));
            }

            if (Reflections.Count > 0)
            {
                var reflections = Reflections.OrderByDescending(r => r.Time).Take(4);
                parts.Add("我悟出来的道理:\n" + string.Join("\n", reflections.Select(r => "- " + r.Text)));
            }

            // "最近的经历"不再注入记忆层 —— 击杀/捡装备让它每几秒变一次,
            // 打碎 prefix cache(全部对话 miss)。观察尾部已有当轮事件,足够决策。
            // 只有超过 1 小时的老经历才值得进记忆(已经过了反思消化)
            if (false && Episodes.Count > 0)
            {
                // 综合分 = 重要度 + 时间新鲜度;带出相关的玩家名事件加权;同类重复去重
                var now = DateTime.Now;
                var scored = Episodes
                    .Select(e => new
                    {
                        Entry = e,
                        Score = e.Importance * 2
                            + Math.Max(0, 6 - (now - e.Time).TotalHours)
                            + (relevantNames != null && relevantNames.Any(n => e.Text.Contains(n)) ? 4 : 0),
                    })
                    .OrderByDescending(x => x.Score)
                    .ThenByDescending(x => x.Entry.Time);
                var picked = new List<string>();
                foreach (var x in scored)
                {
                    // 去重:前12字相同视为同类事件,只留最新一条
                    var key = x.Entry.Text.Length > 12 ? x.Entry.Text.Substring(0, 12) : x.Entry.Text;
                    if (picked.Any(p => p.Length > 12 && p.Substring(0, 12) == key))
                        continue;
                    picked.Add("- " + x.Entry.Time.ToString("MM-dd ") + x.Entry.Text);
                    if (picked.Count >= 10)
                        break;
                }
                parts.Add("最近的经历:\n" + string.Join("\n", picked));
            }

            if (parts.Count == 0)
                return "(你还是个新人,还没有什么记忆)";

            var text = string.Join("\n\n", parts);
            if (text.Length > maxChars)
                text = text.Substring(0, maxChars) + "\n(更早的记忆记不清了)";
            return text;
        }
    }
}
