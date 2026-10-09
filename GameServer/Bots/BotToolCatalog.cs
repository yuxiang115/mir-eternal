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
                Tool("remember", "把重要的事写进你的长期记忆,服务器重启也记得。记玩家用'关于玩家名: 印象';游戏门道用'知识: ...'(怪的数值/掉落/地图/物价,越摸越懂);近况用'近况: ...';大事用'里程碑: ...'。",
                    Param("text", "string", "要记住的内容")),
                Tool("make_plan", "登记一个带时间的约定/计划,到点系统会提醒你去兑现(重启也不忘)。答应玩家'明晚8点'这类事时必须登记。时间写法:'20:00'、'明晚8点'、'10-09 20:00'、'2小时后'。",
                    Param("what", "string", "约定内容,如'和123213组队打祖玛'"),
                    Param("when", "string", "什么时候,如'明晚8点'"),
                    Param("player", "string", "跟谁约的(玩家名,可空)", required: false)),
                Tool("plan_done", "一条约定办完了/取消了,销掉它(描述写一部分就行)。",
                    Param("what", "string", "约定的内容(一部分即可)")),
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
                Tool("update_relation", "更新你对某个玩家的关系认知:是朋友还是仇人,好感多少。被坑了记仇,受过恩记情 —— 这决定你以后怎么对他。",
                    Param("player", "string", "玩家名"),
                    Param("relation", "string", "路人/熟人/朋友/兄弟/仇人"),
                    Param("affinity", "integer", "好感 -100~+100", required: false),
                    Param("note", "string", "为什么(一句话)", required: false)),
                Tool("check_guide", "查官方攻略:自己这等级该去哪张图练、打什么怪、穿什么武器(本服真实数据生成的)。不知道该干嘛/觉得练得慢/想换图时先查这个。",
                    Param("level", "integer", "查哪个等级段的(不填=自己当前等级)", required: false)),
                Tool("set_goal", "给自己定一个长期目标(没有截止日的那种,如'冲40级''攒钱买裁决''交三个朋友')。定了会一直记着,做事围着它转。",
                    Param("text", "string", "目标")),
                Tool("drop_goal", "放弃一个目标(达成了或不想追了)。描述写一部分就行。",
                    Param("text", "string", "目标内容(一部分)")),
                Tool("team_accept", "接受刚收到的组队邀请(谁邀请的就跟他一队)。"),
                Tool("team_reject", "拒绝组队邀请(不想跟就说一声,别晾着)。"),
                Tool("leave_team", "退出当前队伍。"),
                Tool("use_potion", "立刻喝一瓶药。",
                    Param("kind", "string", "hp=生命药,mp=魔法药", required: false)),
                Tool("stop_follow", "停止跟随。"),
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
                        brain.AutoGrind = args["start"] == null || args["start"].Value<bool>();
                        if (brain.AutoGrind)
                        {
                            brain.FollowTargetId = 0;
                            brain.MoveTarget = null;
                        }
                        return brain.AutoGrind ? "开始自动练级" : "停止自动练级";
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
                        if (brain.Memory.Goals.Contains(goal))
                            return "已经定了这个";
                        brain.Memory.Goals.Add(goal);
                        brain.MemoryDirty = true;
                        BotLogger.Log(brain.Definition.Name, "memory", "定目标: " + goal);
                        return "目标已立: " + goal;
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
            if (sinceSay.TotalSeconds < 15 && !brain.IsReplying)
                return "刚说过话,歇会儿(" + (int)(15 - sinceSay.TotalSeconds) + "秒)";
            brain.LastSayTime = MainProcess.CurrentTime;

            var payload = new MemoryStream();
            var writer = new BinaryWriter(payload);
            writer.Write(NearbyChannel);
            writer.Write((byte)0);
            writer.Write(Encoding.UTF8.GetBytes(text + "\0"));
            brain.Player.玩家发送广播(payload.ToArray());
            MainProcess.AddChatLog("[附近][" + brain.Definition.Name + "]: ", Encoding.UTF8.GetBytes(text));
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
            return "已施放 " + skillId + " → " + target.ObjectName;
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
