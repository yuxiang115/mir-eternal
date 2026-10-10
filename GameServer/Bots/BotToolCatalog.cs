using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using GameServer.Maps;
using GameServer.Data;
using GameServer.Networking;
using GameServer.Templates;
using GamePackets.Server;
using Newtonsoft.Json.Linq;

namespace GameServer.Bots
{
    /// <summary>
    /// 暴露给 LLM 的工具集合:OpenAI function calling 定义 + 主线程上的执行器。
    /// 执行器只由主循环线程调用(动作队列消费),因此可以安全操作游戏状态。
    /// </summary>
    public static class BotToolCatalog
    {
        public const uint NearbyChannel = 2415919105U;

        /// <summary>OpenAI tools 参数格式的工具定义。</summary>
        public static readonly JArray Definitions = BuildDefinitions();

        private static JArray BuildDefinitions()
        {
            return new JArray
            {
                Tool("say", "在附近频道喊话(周围约20格能听到)。用你自己的说话风格,像真人打字,别刷屏。",
                    Param("text", "string", "要说的内容")),
                Tool("whisper", "私聊指定玩家(全服任何距离)。",
                    Param("player", "string", "目标玩家名字"),
                    Param("text", "string", "要说的内容")),
                Tool("grind_nearby", "开始/停止自动打怪练级(自己找怪、追击、捡掉落,像真人挂机)。打完想停就传 false。",
                    Param("start", "boolean", "true=开始挂机练级,false=停下", required: false)),
                Tool("follow_player", "持续跟随一名玩家,保持约3格距离。",
                    Param("player", "string", "要跟随的玩家名字")),
                Tool("goto_player", "走到指定玩家身边后停下(一次性接近)。",
                    Param("player", "string", "目标玩家名字")),
                Tool("move_to", "走到当前地图指定坐标。",
                    Param("x", "integer", "目标X坐标"),
                    Param("y", "integer", "目标Y坐标")),
                Tool("attack", "攻击附近的一只怪物,会自动追击并施放技能,直到它死亡。",
                    Param("target_id", "integer", "观察里怪物的Id")),
                Tool("equip_item", "穿上背包里的一件装备(自动放到正确部位)。换装前先对比观察里 Inventory 的属性(攻/防/需求)和已穿的 Equipment,只穿更好的。",
                    Param("name", "string", "背包里的装备名字(要和观察里完全一致)")),
                Tool("pickup", "捡起脚下及周围两格内的地面物品(装备/金币/药),先走过去再捡。",
                    Param("radius", "integer", "搜索半径(格,1-2)", required: false)),
                Tool("check_inventory", "仔细查看自己的背包和全身装备(全量属性)。喊话卖东西/收东西之前先看一眼,别吹自己没有的牛。"),
                Tool("check_skills", "查看自己学会的技能(编号+名字+射程),用技能前先看。"),
                Tool("use_skill", "对目标怪放一个指定技能(法师群攻/道士毒和治疗都靠它)。技能编号从 check_skills 拿。",
                    Param("skill_id", "integer", "技能编号"),
                    Param("target_id", "integer", "目标怪物Id")),
                Tool("drop_item", "把一件物品丢在地上(传奇式交易:丢地上让对方捡,或者清背包)。只能丢脚边。",
                    Param("name", "string", "背包里的物品名")),
                Tool("give_gold", "直接给某玩家金币(交易/还债/送人)。",
                    Param("player", "string", "玩家名"),
                    Param("amount", "integer", "金额")),
                Tool("shout", "全服大喇叭喊话(收货/卖货/找人都用它),一次1000金币,别乱喊。",
                    Param("text", "string", "喊的内容")),
                Tool("team_invite", "邀请一名玩家组队(他会收到弹窗)。口头说'组队'不如真邀请。",
                    Param("player", "string", "玩家名")),
                Tool("goto_map", "走到传送门去另一张地图(观察里的[出口]写了这张图能去哪)。练级点不对/怪太菜/想去打宝就换图。",
                    Param("map", "string", "目标地图名(看观察里出口的目的地)")),
                Tool("list_quests", "查看自己接了哪些任务(还没交的)。"),
                Tool("learn_skill", "读背包里的技能书学新技能(书可以买、也可以打怪爆)。不写名字就把背包里第一本能读的书读了。学会的技能要多用才熟练(熟练度自动涨),道士的召唤技能学了用出来就有宝宝了。",
                    Param("name", "string", "技能书名字(可空)", required: false)),
                Tool("buy_item", "去村里商店买东西(红药蓝药/技能书:练级前看药够不够,不够就来买;商店有本职业技能书就买来学)。",
                    Param("name", "string", "物品名(如 金创药/魔法药,写一部分也行)"),
                    Param("count", "integer", "买几个", required: false)),
                Tool("workspace_search", "搜自己的记忆/认知:信念/关系/印象/知识/里程碑/待办/目标。只返回当前有效版本(被推翻的旧认知默认不出现);想回顾'我以前为什么那么想'加 include_history。返回末尾会提示还有多少条没列出。",
                    Param("query", "string", "关键词(人名/怪名/地名/物名)"),
                    Param("include_history", "boolean", "要不要带已被推翻的旧认知(溯源用)", required: false)),
                Tool("workspace_edit", "写/改自己的认知。路径:currently=当前惦记(自由改写);belief=对一个具体对象的判断(改了旧的会被标记取代,不许新旧并存——想改看法就用这);knowledge=游戏门道(记规律别记流水账,观察里能再看到的别记);milestone=大事记;people=对某人的印象。写之前先想想:这是规律还是一次流水?一次性大事(第一次被谁击败/被人救)也值得记。",
                    Param("path", "string", "currently/belief/knowledge/milestone/people"),
                    Param("subject", "string", "belief/people 必填:关于谁或什么;其他路径留空"),
                    Param("content", "string", "内容( belief 建议带依据:据谁说的/哪次经历)"),
                    Param("confidence", "number", "belief 置信 0~1:亲历高,传闻低(留空=0.7)", required: false)),
                Tool("goal_manage", "管理目标:action=create(新目标,语义去重)/complete(达成)/abandon(放弃)。目标是从你的性格和经历里长出来的,不是任务清单——完成或放弃都正常,别硬凑。",
                    Param("action", "string", "create/complete/abandon"),
                    Param("text", "string", "目标内容(complete/abandon 写一部分就行)")),
                Tool("plan_manage", "计划管理:action=set_day(当天安排一两句,过天作废)/commit(带时间的约定,到点系统叫你兑现)/done(办完销掉)/set_activity(登记当前主攻的活动:攒钱买X/冲X级/打某件装备——写清怎样算成,系统会帮你盯进度,失败要换法不是硬重复)。",
                    Param("action", "string", "set_day/commit/done/set_activity"),
                    Param("text", "string", "内容(commit 支持时间写法:'20:00'/'明晚8点'/'2小时后')"),
                    Param("success", "string", "set_activity 专用:怎样算完成(如'金币>=800且背包有青铜剑')", required: false)),
                Tool("activity_status", "查当前主攻活动的进度/卡点(系统按真实金币等校验'金币>=N'类条件)。挂机久了抬头看一眼自己在干嘛、干到哪了。"),
                Tool("npc_talk", "和NPC说话/接任务/交任务/开商店。先走到NPC旁边(move_to它站的位置),再调这个。回执是NPC说的话+可选选项(带编号),要选就再调一次带上 option。任务就在对话里接和交。",
                    Param("npc", "string", "NPC名字(一部分)"),
                    Param("option", "integer", "选第几项(第一次对话不填)", required: false)),
                Tool("sell_item", "把东西卖给商店换钱(只收对应类型的店:药品店收药/武器店收武器…)。先人要在店附近:不知道店在哪就先在村里转转或问人。回执带卖了什么/单价/现在金币。",
                    Param("name", "string", "卖什么(名字一部分)"),
                    Param("count", "integer", "卖几个(不填=能卖的都卖)", required: false)),

                Tool("check_guide", "查官方攻略:自己这等级该去哪张图练、打什么怪、穿什么武器(本服真实数据生成的)。不知道该干嘛/觉得练得慢/想换图时先查这个。",
                    Param("level", "integer", "查哪个等级段的(不填=自己当前等级)", required: false)),
                Tool("team_accept", "接受刚收到的组队邀请(谁邀请的就跟他一队)。"),
                Tool("team_reject", "拒绝组队邀请(不想跟就说一声,别晾着)。"),
                Tool("leave_team", "退出当前队伍。"),
                Tool("use_potion", "立刻喝一瓶药。",
                    Param("kind", "string", "hp=生命药,mp=魔法药", required: false)),
                Tool("stop_follow", "停止跟随。"),
                Tool("revive", "复活(死了才能用)。想清楚策略再复活:如果刚才死是因为怪太强,换个地方再开打。"),
                Tool("stop", "取消所有当前意图(跟随/攻击/移动),原地待命。"),
            };
        }

        private static JObject Tool(string name, string description, params JObject[] parameters)
        {
            var properties = new JObject();
            var required = new JArray();
            foreach (var p in parameters)
            {
                properties[p["name"].ToString()] = p["schema"];
                if (p.Value<bool?>("required") == true)
                    required.Add(p["name"].ToString());
            }

            var schema = new JObject
            {
                ["type"] = "object",
                ["properties"] = properties,
            };
            if (required.Count > 0)
                schema["required"] = required;

            return new JObject
            {
                ["type"] = "function",
                ["function"] = new JObject
                {
                    ["name"] = name,
                    ["description"] = description,
                    ["parameters"] = schema,
                },
            };
        }

        private static JObject Param(string name, string type, string description, bool required = true)
        {
            return new JObject
            {
                ["name"] = name,
                ["required"] = required,
                ["schema"] = new JObject
                {
                    ["type"] = type,
                    ["description"] = description,
                },
            };
        }

        /// <summary>在主线程执行一次工具调用,返回执行结果描述(供日志与下一轮反馈)。</summary>
        public static string Execute(BotBrain brain, string name, JObject args)
        {
            var player = brain.Player;
            if (player == null)
                return "机器人已下线";

            try
            {
                switch (name)
                {
                    case "say":
                        return Say(brain, args["text"]?.ToString());
                    case "whisper":
                        return Whisper(brain, args["player"]?.ToString(), args["text"]?.ToString());
                    case "workspace_search":
                        return brain.Memory.SearchWorkspace(args["query"]?.ToString() ?? "", args["include_history"]?.Type == JTokenType.Boolean && args["include_history"].Value<bool>());
                    case "workspace_edit":
                        return WorkspaceEdit(brain, args["path"]?.ToString() ?? "", args["subject"]?.ToString() ?? "", args["content"]?.ToString() ?? "", args["confidence"]?.Value<double?>() ?? 0.7);
                    case "goal_manage":
                    {
                        var action = args["action"]?.ToString() ?? "";
                        var text = (args["text"]?.ToString() ?? "").Trim();
                        var mem = brain.Memory;
                        if (action == "create")
                        {
                            if (text.Length < 2) return "目标太空";
                            if (mem.Goals.Count >= 3) return "目标太多记不住(最多3个),先 abandon 一个";
                            var norm = System.Text.RegularExpressions.Regex.Replace(text, @"[\s,，。;；!！?？~～]", "");
                            var dup = mem.Goals.FirstOrDefault(g =>
                            {
                                var ng = System.Text.RegularExpressions.Regex.Replace(g, @"[\s,，。;；!！?？~～]", "");
                                return ng == norm || (ng.Length >= 6 && norm.Contains(ng)) || (norm.Length >= 6 && ng.Contains(norm));
                            });
                            if (dup != null) return "已有相近目标: " + dup + " —— 围着它做就行";
                            mem.Goals.Add(text); mem.Dirty = true;
                            BotLogger.Log(brain.Definition.Name, "memory", "定目标: " + text);
                            return "目标已立: " + text;
                        }
                        var hit = mem.Goals.LastOrDefault(g => g.Contains(text));
                        if (hit == null) return "没这个目标";
                        mem.Goals.Remove(hit); mem.Dirty = true;
                        BotLogger.Log(brain.Definition.Name, "memory", (action == "complete" ? "达" : "弃") + "目标: " + hit);
                        return (action == "complete" ? "目标达成: " : "已放下: ") + hit + (action == "complete" ? "(干成一件事,想想下一个追什么)" : "");
                    }
                    case "plan_manage":
                        return PlanManage(brain, args["action"]?.ToString() ?? "", args["text"]?.ToString() ?? "", args["success"]?.ToString() ?? "");
                    case "npc_talk":
                        return NpcTalk(brain, args["npc"]?.ToString() ?? "", args["option"]?.Value<int?>() ?? 0);
                    case "sell_item":
                        return SellItem(brain, args["name"]?.ToString() ?? "", Math.Max(1, Math.Min(50, args["count"]?.Value<int?>() ?? 99)));
                    case "activity_status":
                    {
                        var a = brain.Memory.Activity;
                        if (a == null) return "当前没有主攻活动(想推什么用 plan_manage set_activity 登记,写清怎样算成)";
                        var gold = brain.Player != null ? brain.Player.NumberGoldCoins : 0;
                        var done = a.Check(gold);
                        if (done == "done" && a.Status == "active")
                        {
                            a.Status = "done"; brain.Memory.Dirty = true;
                            return "[活动完成!] " + a.Goal + "(条件'" + a.SuccessCondition + "'已满足)—— 想想下一个追什么,顺便把这次的经验记下来(workspace_edit knowledge)";
                        }
                        return "当前活动: " + a.Goal + (char)10 + "做法: " + a.Action + (char)10 + "成功条件: " + a.SuccessCondition + (char)10 + "状态: " + a.Status + (a.Blocker.Length > 0 ? (char)10 + "卡点: " + a.Blocker : "") + (a.ProgressNote.Length > 0 ? (char)10 + a.ProgressNote : "");
                    }
                    case "remember":
                    {
                        var reply = brain.Memory.Remember(args["text"]?.ToString());
                        brain.MemoryDirty = true; // 主循环里统一重建 system + 节流落盘
                        BotLogger.Log(brain.Definition.Name, "memory", "remember: " + (args["text"]?.ToString() ?? ""));
                        return reply;
                    }
                    case "make_plan":
                    {
                        var what = (args["what"]?.ToString() ?? "").Trim();
                        if (what.Length == 0)
                            return "没写约定内容";
                        var commitment = brain.Memory.MakeCommitment(what, args["when"]?.ToString(), args["player"]?.ToString());
                        brain.MemoryDirty = true;
                        BotLogger.Log(brain.Definition.Name, "commit", "登记: " + what + " @ " + commitment.DueAt.ToString("MM-dd HH:mm"));
                        return "已记下约定: " + what + " (" + commitment.DueAt.ToString("MM-dd HH:mm") + " 到点提醒你)";
                    }
                    case "plan_done":
                    {
                        var commitment = brain.Memory.CompleteCommitment(args["what"]?.ToString());
                        brain.MemoryDirty = true;
                        if (commitment != null)
                            BotLogger.Log(brain.Definition.Name, "commit", "完成: " + commitment.Description);
                        return commitment != null
                            ? "已销约定: " + commitment.Description
                            : "没找到这条约定(可能已销过)";
                    }
                    case "grind_nearby":
                    {
                        var wantStart = args["start"] == null || args["start"].Value<bool>();
                        if (wantStart && brain.AutoGrind)
                            return "已经在自动练级中了,不用重复开,系统自己找怪打"; // 幂等:省一轮重调
                        brain.AutoGrind = wantStart;
                        if (brain.AutoGrind)
                        {
                            brain.FollowTargetId = 0;
                            brain.MoveTarget = null;
                        }
                        return brain.AutoGrind ? "开始自动练级" : "停止自动练级";
                    }
                    case "follow_player":
                        return Follow(brain, args["player"]?.ToString(), arrivalStops: false);
                    case "goto_player":
                        return Follow(brain, args["player"]?.ToString(), arrivalStops: true);
                    case "move_to":
                        return MoveTo(brain, args["x"]?.Value<int?>() ?? 0, args["y"]?.Value<int?>() ?? 0);
                    case "attack":
                        return Attack(brain, args["target_id"]?.Value<int?>() ?? 0);
                    case "equip_item":
                        return EquipItem(brain, args["name"]?.ToString());
                    case "pickup":
                        return Pickup(brain, Math.Max(1, Math.Min(2, args["radius"]?.Value<int?>() ?? 2)));
                    case "check_inventory":
                        return CheckInventory(brain);
                    case "check_skills":
                        return CheckSkills(brain);
                    case "use_skill":
                        return UseSkill(brain, args["skill_id"]?.Value<int?>() ?? 0, args["target_id"]?.Value<int?>() ?? 0);
                    case "drop_item":
                        return DropItem(brain, args["name"]?.ToString());
                    case "give_gold":
                        return GiveGold(brain, args["player"]?.ToString(), args["amount"]?.Value<int?>() ?? 0);
                    case "shout":
                        return Shout(brain, args["text"]?.ToString());
                    case "team_invite":
                        return TeamInvite(brain, args["player"]?.ToString());
                    case "team_accept":
                        return TeamAccept(brain);
                    case "team_reject":
                    {
                        if (brain.TeamInviterName == null)
                            return "没有待回应的邀请";
                        var rejected = brain.TeamInviterName;
                        var rejectedId = brain.TeamInviterCharId;
                        brain.ClearTeamInvite();
                        brain.Player.回应组队请求(rejectedId, 0, 1);
                        return "已拒绝 " + rejected + " 的组队邀请";
                    }
                    case "leave_team":
                        if (brain.Player.Team == null)
                            return "本来就不在队伍里";
                        brain.Player.申请队员离队(brain.Player.CharacterData.CharId);
                        return "已退出队伍";
                    case "goto_map":
                        return GotoMap(brain, args["map"]?.ToString());
                    case "learn_skill":
                        return LearnSkill(brain, args["name"]?.ToString());
                    case "buy_item":
                        return BuyItem(brain, args["name"]?.ToString(), Math.Max(1, Math.Min(20, args["count"]?.Value<int?>() ?? 5)));
                    case "list_quests":
                        return ListQuests(brain);
                    case "update_relation":
                        return UpdateRelation(brain, args["player"]?.ToString(), args["relation"]?.ToString(),
                            args["affinity"]?.Value<int?>() ?? 0, args["note"]?.ToString());
                    case "check_guide":
                    {
                        var level = args["level"]?.Value<int?>() ?? brain.Player.CurrentLevel;
                        BotLogger.Log(brain.Definition.Name, "act", "check_guide(" + level + "级)");
                        return BotGuide.ForLevel(Math.Max(1, level), brain.Player.CharRole);
                    }
                    case "set_goal":
                    {
                        var goal = (args["text"]?.ToString() ?? "").Trim();
                        if (goal.Length < 2)
                            return "目标太空";
                        if (brain.Memory.Goals.Count >= 3)
                            return "目标太多记不住(最多3个),先 drop_goal 一个";
                        // 语义去重:精确相等或互相包含(去空白标点后)都算重复 ——
                        // 实测出现过"升到10级(姐姐说10级就好玩了)"原样定两次,目标区全是垃圾
                        var norm = System.Text.RegularExpressions.Regex.Replace(goal, @"[\s,，。;；!！?？~～]", "");
                        var dup = brain.Memory.Goals.FirstOrDefault(g =>
                        {
                            var ng = System.Text.RegularExpressions.Regex.Replace(g, @"[\s,，。;；!！?？~～]", "");
                            return ng == norm || (ng.Length >= 6 && norm.Contains(ng)) || (norm.Length >= 6 && ng.Contains(norm));
                        });
                        if (dup != null)
                            return "已经有相近的目标了: " + dup + " —— 别重复立,围着它做就行";
                        brain.Memory.Goals.Add(goal);
                        brain.MemoryDirty = true;
                        BotLogger.Log(brain.Definition.Name, "memory", "定目标: " + goal);
                        return "目标已立: " + goal;
                    }
                    case "set_plan":
                    {
                        var plan = (args["text"]?.ToString() ?? "").Trim();
                        if (plan.Length < 2)
                            return "计划太空";
                        var isNew = string.IsNullOrWhiteSpace(brain.Memory.DailyPlan);
                        brain.Memory.DailyPlan = plan;
                        brain.Memory.PlanDate = DateTime.Now.ToString("MM-dd");
                        brain.MemoryDirty = true;
                        BotLogger.Log(brain.Definition.Name, "memory", (isNew ? "定今日计划: " : "改今日计划: ") + plan);
                        return (isNew ? "今天的计划已记下: " : "计划已改: ") + plan;
                    }
                    case "drop_goal":
                    {
                        var goal = brain.Memory.Goals.LastOrDefault(g => g.Contains(args["text"]?.ToString() ?? ""));
                        if (goal == null)
                            return "没这个目标";
                        brain.Memory.Goals.Remove(goal);
                        brain.MemoryDirty = true;
                        BotLogger.Log(brain.Definition.Name, "memory", "弃目标: " + goal);
                        return "已放下: " + goal;
                    }
                    case "use_potion":
                        return brain.DrinkPotion(args["kind"]?.ToString() == "mp" ? "mp" : "hp") ? "已喝药" : "背包里没有可用的药";
                    case "stop_follow":
                        brain.FollowTargetId = 0;
                        return "已停止跟随";
                    case "revive":
                        if (!brain.Player.Died)
                            return "你还活着";
                        brain.Player.玩家请求复活();
                        BotLogger.Log(brain.Definition.Name, "act", "revive(自主复活)");
                        return "已复活";
                    case "stop":
                        brain.FollowTargetId = 0;
                        brain.CombatTargetId = 0;
                        brain.MoveTarget = null;
                        return "已停止所有行动";
                    default:
                        return "未知工具: " + name;
                }
            }
            catch (Exception ex)
            {
                return "工具执行失败: " + ex.Message;
            }
        }

        private static string Say(BotBrain brain, string text)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0)
                return "没有说话内容";
            if (text.Length > 120)
                text = text.Substring(0, 120);
            if (text.StartsWith("@"))
                text = " " + text; // 避免机器人发言被当成玩家命令执行

            // 节流:两个机器人互相搭话时防止说个不停,15秒内只让一句出去(被私聊回复不限)
            var sinceSay = MainProcess.CurrentTime - brain.LastSayTime;
            if (sinceSay.TotalSeconds < 8 && !brain.IsReplying)
            {
                BotLogger.Log(brain.Definition.Name, "event", "说话被节流(15秒内): " + text.Substring(0, Math.Min(20, text.Length)));
                return "刚说过话,歇会儿(" + (int)(8 - sinceSay.TotalSeconds) + "秒)";
            }
            brain.LastSayTime = MainProcess.CurrentTime;

            var payload = new MemoryStream();
            var writer = new BinaryWriter(payload);
            writer.Write(NearbyChannel);
            writer.Write((byte)0);
            writer.Write(Encoding.UTF8.GetBytes(text + "\0"));
            brain.Player.玩家发送广播(payload.ToArray()); // 服务器路径自带 [General] 落盘,别再记一遍
            BotLogger.Log(brain.Definition.Name, "say", "[附近] " + text);
            return "已喊话";
        }

        private static string Whisper(BotBrain brain, string playerName, string text)
        {
            text = (text ?? "").Trim();
            playerName = (playerName ?? "").Trim();
            if (text.Length == 0 || playerName.Length == 0)
                return "参数不完整";

            var target = FindPlayerCharacter(playerName);
            if (target == null || target.ActiveConnection == null || target.ActiveConnection.Player == null)
                return "玩家 [" + playerName + "] 不在线";

            var player = brain.Player;
            var content = Encoding.UTF8.GetBytes(text + "\0");
            var payload = new MemoryStream();
            var writer = new BinaryWriter(payload);
            writer.Write(player.ObjectId);
            writer.Write(target.CharId);
            writer.Write(1);
            writer.Write((int)player.CurrentLevel);
            writer.Write(content);
            writer.Write(Encoding.UTF8.GetBytes(player.ObjectName));
            writer.Write((byte)0);

            // 目标是机器人时,私聊不会走真人玩家的收包路径 —— 直接投递到它的大脑
            var targetBrain = BotManager.FindBrain(target.CharName.V);
            if (targetBrain != null && targetBrain.Alive)
                targetBrain.RecordChat(player.ObjectName, text, true);

            target.ActiveConnection.SendPacket(new ReceiveChatMessagesPacket
            {
                字节描述 = payload.ToArray(),
            });
            MainProcess.AddChatLog("[Whisper][" + player.ObjectName + "]=>[" + target.CharName + "]: ", content);
            return "已私聊 " + playerName;
        }

        private static string Follow(BotBrain brain, string playerName, bool arrivalStops)
        {
            var target = FindOnlinePlayer(playerName);
            if (target == null)
                return "玩家 [" + playerName + "] 不在线";

            brain.FollowTargetId = target.ObjectId;
            brain.FollowStopsOnArrival = arrivalStops;
            brain.MoveTarget = null;
            return arrivalStops ? "正在走向 " + playerName : "正在跟随 " + playerName;
        }

        private static string MoveTo(BotBrain brain, int x, int y)
        {
            if (x <= 0 || y <= 0)
                return "坐标不合法";

            brain.MoveTarget = new Point(x, y);
            brain.FollowTargetId = 0;
            var distance = Math.Max(Math.Abs(brain.Player.CurrentPosition.X - x), Math.Abs(brain.Player.CurrentPosition.Y - y));
            if (distance > 15)
                brain.RebuildPath(new Point(x, y)); // 远地方先算好路,免得在墙根打转
            return "正在前往 (" + x + "," + y + ")";
        }

        private static string Attack(BotBrain brain, int targetId)
        {
            if (targetId == 0 || !MapGatewayProcess.Objects.TryGetValue(targetId, out var target) || target.Died)
                return "目标不存在或已死亡";

            if (!(target is MonsterObject))
                return "只能攻击怪物";

            if (!brain.IsSafeTarget(brain.Player, target))
                return "打不过 " + target.ObjectName + "(等级/血量差距太大)";
            brain.CombatTargetId = targetId;
            return "正在攻击 " + target.ObjectName;
        }

        /// <summary>穿上背包里的一件装备:按物品类型推导部位(戒指/手镯优先放空槽)。</summary>
        private static string EquipItem(BotBrain brain, string name)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0)
                return "没有指定装备名";

            var player = brain.Player;
            foreach (var item in player.Backpack.Values)
            {
                var equip = item != null ? item.物品模板 as EquipmentItem : null;
                if (equip == null || item.Name != name)
                    continue;

                byte? slot = ResolveEquipSlot(player, equip);
                if (!slot.HasValue)
                    return name + " 不是可穿戴的装备";

                var before = player.Equipment.ContainsKey(slot.Value) ? player.Equipment[slot.Value].Name : null;
                player.TransferItem(1, item.物品位置.V, 0, slot.Value);

                EquipmentData after;
                var worn = player.Equipment.TryGetValue(slot.Value, out after) && after == item;
                return worn
                    ? "已穿上 " + name + (before != null && before != name ? "(换下了 " + before + ")" : "")
                    : name + " 穿戴失败(职业/重量不满足?)";
            }
            return "背包里没有 " + name;
        }

        // ===================== 游戏世界工具(看/用/交易/组队/喊话) =====================

        private static string CheckInventory(BotBrain brain)
        {
            var player = brain.Player;
            var sb = new System.Text.StringBuilder();
            sb.Append("金币:").Append(player.NumberGoldCoins).Append(" 背包:");
            foreach (var item in BotSnapshot.DescribeBackpack(player))
                sb.Append(item.Name).Append(item.Count > 1 ? "x" + item.Count : "").Append(item.Info != null ? "(" + item.Info + ")" : "").Append(' ');
            sb.Append(" | 已穿:");
            foreach (var e in BotSnapshot.DescribeEquipment(player))
                sb.Append(e.Part).Append(':').Append(e.Name).Append(e.Info != null ? "(" + e.Info + ")" : "").Append(' ');
            return sb.ToString();
        }

        private static string CheckSkills(BotBrain brain)
        {
            var player = brain.Player;
            if (player.MainSkills表.Count == 0)
                return "没学过技能";
            var sb = new System.Text.StringBuilder();
            foreach (var pair in player.MainSkills表)
            {
                var name = pair.Key.ToString();
                var range = BotBrain.GetSkillRange(pair.Key);
                foreach (var template in GameSkills.DataSheet.Values)
                {
                    if (template.OwnSkillId == pair.Key)
                    {
                        name = template.SkillName;
                        break;
                    }
                }
                sb.Append(pair.Key).Append(':').Append(name).Append("(射程").Append(range).Append(") ");
            }
            return sb.ToString();
        }

        private static string UseSkill(BotBrain brain, int skillId, int targetId)
        {
            var player = brain.Player;
            if (skillId == 0 || !player.MainSkills表.ContainsKey((ushort)skillId))
                return "没学过技能 " + skillId + "(先 check_skills)";
            if (targetId == 0 || !MapGatewayProcess.Objects.TryGetValue(targetId, out var target) || target.Died)
                return "目标不存在或已死亡";
            var distance = player.GetDistance(target);
            var range = BotBrain.GetSkillRange((ushort)skillId);
            if (distance > range)
                return "距离" + distance + "格,超出射程" + range + "(先走近)";
            brain.UseSkillOnce((ushort)skillId, target);
            return "已施放[" + BotBrain.GetSkillName((ushort)skillId) + "] → " + target.ObjectName;
        }

        private static string DropItem(BotBrain brain, string name)
        {
            name = (name ?? "").Trim();
            var player = brain.Player;
            foreach (var item in player.Backpack.Values)
            {
                if (item == null || item.物品模板 == null || item.Name != name)
                    continue;
                player.玩家丢弃物品(1, item.物品位置.V, 1);
                return "把 " + name + " 丢在地上了";
            }
            return "背包里没有 " + name;
        }

        private static string GiveGold(BotBrain brain, string playerName, int amount)
        {
            var target = FindOnlinePlayer(playerName);
            if (target == null)
                return "玩家 [" + playerName + "] 不在线";
            if (amount <= 0 || brain.Player.NumberGoldCoins < amount)
                return "金币不够(你有 " + brain.Player.NumberGoldCoins + ")";
            brain.Player.NumberGoldCoins -= amount;
            target.NumberGoldCoins += amount;
            return "给了 " + playerName + " " + amount + " 金币";
        }

        private static string Shout(BotBrain brain, string text)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0)
                return "没喊内容";
            if (brain.Player.NumberGoldCoins < 1000)
                return "金币不够(大喇叭一次1000,你有 " + brain.Player.NumberGoldCoins + ")";
            if (text.Length > 100)
                text = text.Substring(0, 100);

            brain.Player.NumberGoldCoins -= 1000;
            var payload = new MemoryStream();
            var writer = new BinaryWriter(payload);
            writer.Write(2415919107U);   // 全服大喇叭频道
            writer.Write(brain.Player.ObjectId);
            writer.Write(1);             // 付费广播
            writer.Write((int)brain.Player.CurrentLevel);
            writer.Write(Encoding.UTF8.GetBytes(text + "\0"));
            writer.Write(Encoding.UTF8.GetBytes(brain.Player.ObjectName));
            writer.Write((byte)0);
            NetworkServiceGateway.发送封包(new ReceiveChatMessagesPacket
            {
                字节描述 = payload.ToArray(),
            });
            MainProcess.AddChatLog("[广播][" + brain.Definition.Name + "]: ", Encoding.UTF8.GetBytes(text));
            return "已全服喊话(花了1000金币)";
        }

        private static string TeamInvite(BotBrain brain, string playerName)
        {
            var target = FindOnlinePlayer(playerName);
            if (target == null)
                return "玩家 [" + playerName + "] 不在线";
            if (brain.Player.Team != null)
                return "你已经在队伍里了";
            // 注意:组队接口按 CharId(角色表键)查人,不是 ObjectId
            brain.Player.申请创建队伍(target.CharacterData.CharId, 0);
            BotLogger.Log(brain.Definition.Name, "act", "team_invite → " + playerName);
            return "已向 " + playerName + " 发出组队邀请";
        }

        /// <summary>读背包里的技能书学技能(UseItem 即学,服务器消耗书并学会)。</summary>

        /// <summary>workspace_edit 路由:currently 自由改写 / belief 矛盾改源头(supersede) / knowledge 去重入册 / milestone / people。</summary>
        /// <summary>G1:和NPC对话。找12格内同名NPC;首次调=开始对话返回首页文本+选项;带option=选那一项继续。</summary>
        private static string NpcTalk(BotBrain brain, string npcName, int option)
        {
            var player = brain.Player;
            npcName = (npcName ?? "").Trim();
            if (npcName.Length == 0) return "要写NPC名字";
            GuardObject npc = null;
            foreach (var g in MapGatewayProcess.NPCs.Values)
            {
                if (g == null || g.CurrentMap != player.CurrentMap) continue;
                if (player.GetDistance(g) > 12) continue;
                if ((g.ObjectName ?? "").Contains(npcName)) { npc = g; break; }
            }
            if (npc == null) return "12格内没有叫[" + npcName + "]的NPC(先move_to走过去;不知道在哪问人)";
            if (option > 0 && (player.对话守卫 == null || !(player.对话守卫.ObjectName ?? "").Contains(npcName)))
                return "对话已断开,重新调一次(不带option)先打开对话";
            if (option > 0) { player.继续Npcc对话(option); }
            else { player.开始Npcc对话(npc.ObjectId); }
            var text = NpcDialogs.GetBufferFromDialogId(player.对话页面);
            var content = System.Text.Encoding.UTF8.GetString(text).TrimEnd('\0');
            content = content.Replace("<#Dft>", "").Replace("<#P0:", "(1)").Replace("<#P1:", "(2)")
                .Replace(">", ". ").Replace("<#", "");
            var brief = content.Length > 500 ? content.Substring(0, 500) + "..." : content;
            BotLogger.Log(brain.Definition.Name, "act", "npc_talk(" + npcName + (option > 0 ? "," + option : "") + ")");
            return npc.ObjectName + "说: " + brief + (brief.Length < 3 ? " (他没说什么,可能只管卖东西/传送,直接试sell_item或goto_map)" : "")
                + " [要选选项就再调npc_talk带上option编号;对话30秒不选会断]";
        }

        /// <summary>G1:卖东西给商店。找同图 12 格内带商店的 NPC 开店,按商店回收类型过滤背包,逐件卖并汇总。</summary>
        private static string SellItem(BotBrain brain, string name, int maxCount)
        {
            var player = brain.Player;
            name = (name ?? "").Trim();
            if (name.Length == 0) return "要写卖什么";

            // 找同图、12格内、有商店且商店有回收类型的NPC
            GuardObject shopkeeper = null;
            GameStore store = null;
            foreach (var npc in MapGatewayProcess.NPCs.Values)
            {
                if (npc == null || npc.CurrentMap != player.CurrentMap || npc.StoreId == 0) continue;
                if (player.GetDistance(npc) > 12) continue;
                if (!GameStore.DataSheet.TryGetValue(npc.StoreId, out var st) || st == null) continue;
                if (st.RecyclingType == ItemsForSale.禁售) continue;
                shopkeeper = npc; store = st;
                break;
            }
            if (shopkeeper == null)
                return "12格内没有收货的店(村里找带商店的NPC站过去再卖;不知道在哪就问人)";
            if (player.对话守卫 != shopkeeper || player.打开商店 != shopkeeper.StoreId)
                player.开始Npcc对话(shopkeeper.ObjectId);

            // 商店回收类型匹配的背包物品
            var sold = new List<string>();
            var goldBefore = player.NumberGoldCoins;
            var remain = maxCount;
            foreach (var kv in player.Backpack.ToList())
            {
                if (remain <= 0) break;
                var item = kv.Value;
                if (item == null || item.物品模板 == null || item.IsBound) continue;
                if (item.出售类型 != store.RecyclingType) continue;
                if (!(item.物品模板.Name ?? "").Contains(name)) continue;
                var qty = Math.Min(remain, Math.Max(1, item.当前持久.V));
                player.玩家出售物品(1, kv.Key, (ushort)qty);
                sold.Add(item.物品模板.Name + "x" + qty);
                remain -= qty;
            }
            if (sold.Count == 0)
                return "没卖掉:背包里没有[" + name + "] 是这家店收的(" + store.RecyclingType.ToString() + "店只收这种;找对应类型的店,或 drop_item 丢地上";
            var delta = player.NumberGoldCoins - goldBefore;
            BotLogger.Log(brain.Definition.Name, "econ", "卖店 +" + delta + "金: " + string.Join(",", sold));
            return "卖了 " + string.Join(", ", sold) + ",进账 " + delta + " 金币,现在共 " + player.NumberGoldCoins + "(行情可以记进 knowledge:什么能卖多少)";
        }

        private static string WorkspaceEdit(BotBrain brain, string path, string subject, string content, double confidence)
        {
            content = (content ?? "").Trim();
            var mem = brain.Memory;
            switch (path)
            {
                case "currently":
                    if (content.Length < 3) return "惦记的事太空";
                    mem.Currently = content; mem.Dirty = true;
                    BotLogger.Log(brain.Definition.Name, "memory", "改当前惦记: " + content);
                    return "当前惦记已更新(它会跟着你做事自然过期,不用刻意维护)";
                case "belief":
                    subject = (subject ?? "").Trim();
                    if (subject.Length == 0 || content.Length < 3) return "belief 要写清关于谁/什么(subject)和判断(content)";
                    var old = mem.Beliefs.FirstOrDefault(b => b.Subject == subject && b.Status == "active");
                    var b2 = mem.ReviseBelief(subject, content, Math.Max(0.1, Math.Min(1, confidence)), 0.7, "");
                    var ev = content.Contains("据") ? "" : " (建议带上依据:亲历还是听谁说的,confidence 相应给)";
                    BotLogger.Log(brain.Definition.Name, "memory", (old != null ? "修订信念 rev" + b2.Revision + ": " : "新信念: ") + subject + " = " + content);
                    return (old != null ? "信念已修订(旧版[" + old.Content + "]自动归档,以后检索只用新版): " : "新信念: ") + subject + " = " + content + ev;
                case "knowledge":
                    if (content.Length < 3) return "知识太短";
                    if (mem.Knowledge.Any(k => k.Contains(content.Substring(0, Math.Min(8, content.Length)))))
                        return "已经知道了(别重复记)";
                    mem.Knowledge.Add(content);
                    if (mem.Knowledge.Count > 40) mem.Knowledge = mem.Knowledge.Skip(mem.Knowledge.Count - 40).ToList();
                    mem.Dirty = true;
                    return "新知识+1(记的是规律不是流水就对)";
                case "milestone":
                    if (content.Length < 3) return "太短";
                    mem.RecordMilestone(content, 5);
                    return "已记入大事记";
                case "people":
                    subject = (subject ?? "").Trim();
                    if (subject.Length == 0) return "people 要写 subject=玩家名";
                    mem.People[subject] = content;
                    mem.Dirty = true;
                    return "已更新对 " + subject + " 的印象";
                default:
                    return "路径要写 currently/belief/knowledge/milestone/people";
            }
        }

        /// <summary>plan_manage:当天安排/带时约定/活动卡。</summary>
        private static string PlanManage(BotBrain brain, string action, string text, string success)
        {
            text = (text ?? "").Trim();
            var mem = brain.Memory;
            switch (action)
            {
                case "set_day":
                    if (text.Length < 2) return "计划太空";
                    var isNew = string.IsNullOrWhiteSpace(mem.DailyPlan);
                    mem.DailyPlan = text; mem.PlanDate = DateTime.Now.ToString("MM-dd"); mem.Dirty = true;
                    BotLogger.Log(brain.Definition.Name, "memory", (isNew ? "定当日计划: " : "改当日计划: ") + text);
                    return (isNew ? "今天的计划已记下: " : "计划已改: ") + text;
                case "commit":
                {
                    if (text.Length < 3) return "约定内容太空(支持'和XX组队 20:00'这种带时间的写法)";
                    var m = System.Text.RegularExpressions.Regex.Match(text, @"(\d{1,2}:\d{2}|明晚?\d{1,2}点?|明天\d{1,2}点?|\d+小时后)");
                    var when = m.Success ? m.Value : "2小时后";
                    var desc = m.Success ? text.Replace(m.Value, "").Trim('，',',','。',' ','）',')') : text;
                    if (desc.Length < 2) desc = text;
                    var commitment = mem.MakeCommitment(desc, when, null);
                    mem.Dirty = true;
                    BotLogger.Log(brain.Definition.Name, "commit", "登记: " + desc + " @ " + commitment.DueAt.ToString("MM-dd HH:mm"));
                    return "已登记,到点叫你: " + desc + " (" + commitment.DueAt.ToString("MM-dd HH:mm") + ")";
                }
                case "done":
                {
                    var c = mem.Commitments.LastOrDefault(x => x.Status == "pending" && x.Description.Contains(text));
                    if (c == null) return "没这个约定";
                    c.Status = "done"; mem.Dirty = true;
                    return "已销: " + c.Description;
                }
                case "set_activity":
                {
                    if (text.Length < 3) return "活动内容太空";
                    var card = new BotActivityCard { Goal = text, Action = text, SuccessCondition = string.IsNullOrWhiteSpace(success) ? "自己判断" : success };
                    // 已有活动未完:自动转暂存(可回来恢复)
                    if (mem.Activity != null && mem.Activity.Status == "active")
                    {
                        mem.Activity.Status = "suspended";
                        card.Suspended = mem.Activity;
                    }
                    mem.Activity = card; mem.Dirty = true;
                    BotLogger.Log(brain.Definition.Name, "memory", "主攻活动: " + text + (card.SuccessCondition != "自己判断" ? " (" + card.SuccessCondition + ")" : ""));
                    return "主攻活动已登记: " + text + (char)10 + "成功条件: " + card.SuccessCondition + (char)10 + "(挂机时系统帮你盯'金币>=N'类进度;卡住了要换方法,不是硬重复)";
                }
                default:
                    return "action 要写 set_day/commit/done/set_activity";
            }
        }

        private static string LearnSkill(BotBrain brain, string bookName)
        {
            bookName = (bookName ?? "").Trim();
            var player = brain.Player;
            if (bookName.Length == 0)
                return "要写书名(如 learn_skill 火球术);不知道背包有啥书先 check_inventory";

            var foundButLearned = false;
            foreach (var item in player.Backpack.Values)
            {
                if (item == null || item.物品模板 == null || item.物品类型 != ItemType.技能书籍)
                    continue;
                var template = item.物品模板.Name ?? "";
                if (!template.Contains(bookName))
                    continue;
                if (player.MainSkills表.ContainsKey(item.SkillId))
                {
                    foundButLearned = true;
                    continue; // 已学过,找下一本
                }

                // 学不会给真实原因,免得反复盲试(实测火球术盲试9次/群体治愈术7次全是"没有或已学过")
                Templates.InscriptionSkill ins;
                Templates.InscriptionSkill.DataSheet.TryGetValue((ushort)(item.SkillId * 10), out ins);
                if (ins != null && ins.Race != GameObjectRace.通用 && ins.Race != player.CharRole)
                    return "《" + template + "》是" + ins.Race + "的书,你不是这个职业,读了白读(留着送人/卖)";
                if (ins != null && ins.MinPlayerLevel != null && ins.MinPlayerLevel.Length > 0 && ins.MinPlayerLevel[0] > player.CurrentLevel)
                    return "《" + template + "》要 " + ins.MinPlayerLevel[0] + " 级才能学,你现在 " + player.CurrentLevel + " 级 —— 书收好,先练级";

                var learned = player.LearnSkill(item.SkillId);
                if (learned)
                {
                    player.ConsumeBackpackItem(1, item);
                    BotLogger.Log(brain.Definition.Name, "act", "learn_skill: " + template);
                    brain.Memory.Remember("知识: 已学会" + template + "(打怪会自动用,多用涨熟练)");
                    return "读了 " + template + ",真的学会了!以后打怪自动用它";
                }
                BotLogger.Log(brain.Definition.Name, "act", "learn_skill失败: " + template);
                return "读《" + template + "》没学会(条件不满足,先 check_guide 查这本书的要求)";
            }
            if (foundButLearned)
                return "《" + bookName + "》你已经学过了,别再读(书可以留着送人)";
            return "背包里没有《" + bookName + "》 —— 商店能买就 buy_item(看金币够不够),买不到就打怪爆或者找人收";
        }

        private static string BuyItem(BotBrain brain, string name, int count)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0)
                return "没写买什么";

            // 全服商店找货:名字模糊匹配 + 钱够
            foreach (var store in GameStore.DataSheet.Values)
            {
                for (var slot = 0; slot < store.Products.Count; slot++)
                {
                    var product = store.Products[slot];
                    GameItems template;
                    if (product == null || !GameItems.DataSheet.TryGetValue(product.Id, out template))
                        continue;
                    if (template.Name == null || !template.Name.Contains(name))
                        continue;

                    var player = brain.Player;
                    var cost = product.Price * count;
                    if (player.NumberGoldCoins < cost)
                        return "钱不够: " + template.Name + " 单价" + product.Price + ",买" + count + "个要" + cost + "(你有" + player.NumberGoldCoins + ")";

                    player.玩家购买物品(store.StoreId, slot, (ushort)count);
                    BotLogger.Log(brain.Definition.Name, "act", "buy_item: " + template.Name + "x" + count + " 花" + cost);
                    return "买了 " + template.Name + " x" + count + ",花了" + cost + "金币";
                }
            }
            return "商店里没有「" + name + "」";
        }

        private static string GotoMap(BotBrain brain, string mapName)
        {
            mapName = (mapName ?? "").Trim();
            if (mapName.Length == 0)
                return "没写地图名";

            // 先按中文图名找到目标图 id,再找当前图通往它的门
            var targetMapIds = new HashSet<byte>();
            foreach (var map in GameMap.DataSheet.Values)
            {
                if (!string.IsNullOrEmpty(map.MapName) && map.MapName.Contains(mapName))
                    targetMapIds.Add((byte)map.MapId);
            }

            var candidates = TeleportGates.DataSheet
                .Where(g => g.FromMapId == brain.Player.CurrentMap.MapId && targetMapIds.Contains(g.ToMapId))
                .OrderBy(g => Math.Abs(g.FromCoords.X - brain.Player.CurrentPosition.X) + Math.Abs(g.FromCoords.Y - brain.Player.CurrentPosition.Y))
                .ToList();
            if (candidates.Count == 0)
                return "这张图没有通往「" + mapName + "」的门(看观察里的出口)";
            if (candidates.Count > 1)
            {
                var names = candidates.Select(g =>
                {
                    GameMap toMap;
                    return GameMap.DataSheet.TryGetValue(g.ToMapId, out toMap) && !string.IsNullOrEmpty(toMap.MapName) ? toMap.MapName : g.ToMapName;
                }).Distinct();
                return "有多个门匹配,说全一点: " + string.Join("/", names);
            }

            var gate = candidates[0];
            brain.SetGateRoute(gate);
            BotLogger.Log(brain.Definition.Name, "act", "goto_map: " + gate.FromMapName + " → " + gate.ToMapName);
            return "正在走向" + gate.TeleportGateName + "(" + gate.FromCoords.X + "," + gate.FromCoords.Y + "),到了自动过图去" + gate.ToMapName;
        }

        private static string ListQuests(BotBrain brain)
        {
            var quests = brain.Player.CharacterData.GetInProgressQuests();
            if (quests == null || quests.Length == 0)
                return "手上没有没交的任务";
            return "进行中: " + string.Join("; ", quests.Take(8).Select(q =>
            {
                var name = q.Info != null && q.Info.V != null ? q.Info.V.Name : ("#" + q.Index.V);
                var done = q.IsCompleted;
                return name + (done ? "(可交)" : "(未完成)");
            }));
        }

        private static string UpdateRelation(BotBrain brain, string playerName, string relation, int affinity, string note)
        {
            playerName = (playerName ?? "").Trim();
            relation = (relation ?? "").Trim();
            if (playerName.Length == 0 || relation.Length == 0)
                return "参数不完整";

            var allowed = new[] { "路人", "熟人", "朋友", "兄弟", "仇人" };
            if (!allowed.Contains(relation))
                return "关系只能是: " + string.Join("/", allowed);

            brain.Memory.Relationships[playerName] = new BotRelationship(relation, Math.Max(-100, Math.Min(100, affinity)), note);
            brain.MemoryDirty = true;
            BotLogger.Log(brain.Definition.Name, "memory", "关系更新: " + playerName + " → " + relation + "(" + affinity + ")" + (note != null && note != "" ? " " + note : ""));
            return "记住了: " + playerName + " 是你" + relation;
        }

        private static string TeamAccept(BotBrain brain)
        {
            if (brain.TeamInviterName == null)
                return "没有待回应的邀请";
            if (brain.Player.Team != null)
            {
                brain.ClearTeamInvite();
                return "你已经在队伍里了";
            }
            var inviter = brain.TeamInviterName;
            var inviterId = brain.TeamInviterCharId;
            brain.ClearTeamInvite();
            brain.Player.回应组队请求(inviterId, 0, 0); // 组队方式0=普通,回应0=接受
            BotLogger.Log(brain.Definition.Name, "act", "team_accept ← " + inviter);
            return brain.Player.Team != null
                ? "已加入 " + inviter + " 的队伍"
                : "接受失败(邀请可能过期了)";
        }

        /// <summary>ItemType -> 装备栏槽位;双槽部位(戒指/手镯)优先空槽。</summary>
        private static byte? ResolveEquipSlot(PlayerObject player, EquipmentItem equip)
        {
            switch (equip.Type)
            {
                case ItemType.武器: return 0;
                case ItemType.衣服: return 1;
                case ItemType.披风: return 2;
                case ItemType.头盔: return 3;
                case ItemType.护肩: return 4;
                case ItemType.护腕: return 5;
                case ItemType.腰带: return 6;
                case ItemType.鞋子: return 7;
                case ItemType.项链: return 8;
                case ItemType.勋章: return 13;
                case ItemType.玉佩: return 14;
                case ItemType.战具: return 15;
                case ItemType.戒指: return FreeOrLeftSlot(player, 9, 10);
                case ItemType.手镯: return FreeOrLeftSlot(player, 11, 12);
                default: return null;
            }
        }

        private static byte FreeOrLeftSlot(PlayerObject player, byte left, byte right)
        {
            if (!player.Equipment.ContainsKey(left)) return left;
            if (!player.Equipment.ContainsKey(right)) return right;
            return left;
        }

        /// <summary>捡起脚下及周围指定半径内的地面物品(金币直接入包)。</summary>
        private static string Pickup(BotBrain brain, int radius)        {
            var player = brain.Player;
            var candidates = new List<Tuple<int, ItemObject>>();

            for (var dx = -radius; dx <= radius; dx++)
            {
                for (var dy = -radius; dy <= radius; dy++)
                {
                    var cell = player.CurrentMap[new Point(player.CurrentPosition.X + dx, player.CurrentPosition.Y + dy)];
                    if (cell == null) continue;
                    foreach (var obj in cell)
                    {
                        var ground = obj as ItemObject;
                        if (ground != null)
                            candidates.Add(Tuple.Create(Math.Max(Math.Abs(dx), Math.Abs(dy)), ground));
                    }
                }
            }

            if (candidates.Count == 0)
                return "脚下没有可捡的东西";

            var picked = 0;
            var names = new HashSet<string>();
            foreach (var pair in candidates.OrderBy(c => c.Item1).Take(10))
            {
                var template = pair.Item2.物品模板;
                var name = template != null ? template.Name : "?";
                var isGold = template != null && template.Id == 1;
                var countBefore = player.Backpack.Count;
                player.玩家拾取物品(pair.Item2);
                if (isGold || player.Backpack.Count > countBefore)
                {
                    picked++;
                    var label = isGold ? "金币x" + pair.Item2.堆叠数量 : name + (pair.Item2.堆叠数量 > 1 ? "x" + pair.Item2.堆叠数量 : "");
                    names.Add(label);
                }
            }
            return picked > 0
                ? "捡到: " + string.Join(", ", names.Take(5))
                : "没捡到(背包满或物品有归属保护)";
        }

        private static CharacterData FindPlayerCharacter(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return null;
            return GameDataGateway.CharacterDataTable[name] as CharacterData;
        }

        private static PlayerObject FindOnlinePlayer(string name)
        {
            var character = FindPlayerCharacter(name);
            return character?.ActiveConnection?.Player;
        }
    }
}
