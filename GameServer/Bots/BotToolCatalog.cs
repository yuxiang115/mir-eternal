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
                Tool("say", "在附近频道喊话,只有你周围约20格内的玩家能看到。用简短的中文,不要刷屏。",
                    Param("text", "string", "要说的内容")),
                Tool("whisper", "私聊指定玩家(全服任何距离)。",
                    Param("player", "string", "目标玩家名字"),
                    Param("text", "string", "要说的内容")),
                Tool("follow_player", "持续跟随一名玩家,保持约3格距离。",
                    Param("player", "string", "要跟随的玩家名字")),
                Tool("goto_player", "走到指定玩家身边后停下(一次性接近)。",
                    Param("player", "string", "目标玩家名字")),
                Tool("move_to", "走到当前地图指定坐标。",
                    Param("x", "integer", "目标X坐标"),
                    Param("y", "integer", "目标Y坐标")),
                Tool("attack", "攻击附近的一只怪物,会自动追击并施放技能,直到它死亡。",
                    Param("target_id", "integer", "观察里怪物的Id")),
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
                    case "follow_player":
                        return Follow(brain, args["player"]?.ToString(), arrivalStops: false);
                    case "goto_player":
                        return Follow(brain, args["player"]?.ToString(), arrivalStops: true);
                    case "move_to":
                        return MoveTo(brain, args["x"]?.Value<int?>() ?? 0, args["y"]?.Value<int?>() ?? 0);
                    case "attack":
                        return Attack(brain, args["target_id"]?.Value<int?>() ?? 0);
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

            var payload = new MemoryStream();
            var writer = new BinaryWriter(payload);
            writer.Write(NearbyChannel);
            writer.Write((byte)0);
            writer.Write(Encoding.UTF8.GetBytes(text + "\0"));
            brain.Player.玩家发送广播(payload.ToArray());
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
