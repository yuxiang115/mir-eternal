using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using GameServer.Data;
using GameServer.Maps;
using GameServer.Networking;
using GameServer.Templates;

namespace GameServer.Bots
{
    /// <summary>
    /// 机器人插件入口:配置加载、生命周期管理、主线程动作队列、聊天事件分发。
    /// Process() 由主循环每 tick 调用;所有对 PlayerObject 的操作都发生在主循环线程上。
    /// </summary>
    public static class BotManager
    {
        public static BotConfig Config;
        public static bool Enabled { get { return Config != null && Config.Enabled; } }

        /// <summary>LLM/后台任务产生的动作队列,由主循环消费(仿 GMCommand 模式)。</summary>
        public static readonly ConcurrentQueue<Action> ActionQueue = new ConcurrentQueue<Action>();

        /// <summary>在线机器人列表,仅主循环线程访问。</summary>
        private static readonly List<BotBrain> Brains = new List<BotBrain>();
        private static bool _initialized;

        public static void Initialize()
        {
            if (_initialized)
                return;
            _initialized = true;

            Config = BotConfig.LoadOrCreate();
            if (!Config.Enabled)
            {
                MainProcess.AddSystemLog("[Bot] 机器人插件未启用(BotConfig.json Enabled=false)");
                return;
            }

            MainProcess.AddSystemLog("[Bot] 机器人插件已加载,定义数量: " + Config.BotDefinitions.Length);

            if (!LlmClient.IsConfigured(Config.Llm))
                MainProcess.AddSystemLog("[Bot] 警告: LLM 未配置(BotConfig.json Llm 节),机器人只能执行反射层动作,无法对话思考");

            foreach (var definition in Config.BotDefinitions)
            {
                if (!definition.AutoStart)
                    continue;
                try
                {
                    var result = Spawn(definition, null);
                    MainProcess.AddSystemLog("[Bot] " + result);
                }
                catch (Exception ex)
                {
                    MainProcess.AddSystemLog("[Bot] 启动机器人 [" + definition.Name + "] 失败: " + ex.Message);
                }
            }
        }

        /// <summary>主循环每 tick 调用。任何异常都被吞掉,绝不允许机器人把服务器打挂。</summary>
        public static void Process()
        {
            if (!Enabled)
                return;

            try
            {
                while (ActionQueue.TryDequeue(out var action))
                {
                    try
                    {
                        action();
                    }
                    catch (Exception ex)
                    {
                        MainProcess.AddSystemLog("[Bot] 动作执行异常(已忽略): " + ex.Message);
                    }
                }

                for (var i = Brains.Count - 1; i >= 0; i--)
                {
                    var brain = Brains[i];
                    if (!brain.Alive || brain.Player == null)
                    {
                        Brains.RemoveAt(i);
                        continue;
                    }
                    brain.ScheduleThink();
                    brain.ProcessReflex();
                }
            }
            catch (Exception ex)
            {
                MainProcess.AddSystemLog("[Bot] Process 异常(已忽略): " + ex.Message);
            }
        }

        public static void EnqueueAction(Action action)
        {
            ActionQueue.Enqueue(action);
        }

        // ===================== 召唤 / 驱散 =====================

        /// <summary>让机器人上线。summoner 非空时出生在召唤者身边,否则出生在配置地图的复活区。仅主线程调用。</summary>
        public static string Spawn(BotDefinition definition, PlayerObject summoner)
        {
            if (definition == null)
                return "机器人定义不存在";

            var existing = FindBrain(definition.Name);
            if (existing != null && existing.Alive)
            {
                // 已在线:若指定了召唤者则改为跟随
                if (summoner != null)
                {
                    existing.FollowTargetId = summoner.ObjectId;
                    return definition.Name + " 已在线,现在过来跟你";
                }
                return definition.Name + " 已经在线";
            }

            // 名字冲突保护:真实玩家已占用该名字则拒绝(机器人角色固定挂在 BOT_ 前缀账号下)
            var nameOwner = GameDataGateway.CharacterDataTable[definition.Name] as CharacterData;
            if (nameOwner != null && !IsOwnedByBotAccount(nameOwner, definition.Name))
                return "名字 [" + definition.Name + "] 已被真实玩家占用";

            var character = FindOrCreateCharacter(definition);
            if (character == null)
                return "机器人 [" + definition.Name + "] 角色创建失败";

            // 指定出生位置
            var map = ChooseSpawnMap(definition, summoner);
            if (map == null)
                return "地图 " + definition.MapId + " 不存在";

            character.CurrentMap.V = map.MapId;
            character.CurrentCoords.V = ChooseSpawnPoint(map, summoner);
            character.CurrentDir.V = ComputingClass.随机方向();
            if (character.CurrentHP.V == 0)
                character.CurrentHP.V = 1; // 避免构造函数把出生点改到重生地图

            var connection = new BotConnection
            {
                CurrentStage = GameStage.PlayingScene,
                Account = character.Account.V,
            };

            PlayerObject player;
            try
            {
                player = new PlayerObject(character, connection);
            }
            catch (Exception ex)
            {
                MainProcess.AddSystemLog("[Bot] 创建 PlayerObject 失败: " + ex.Message);
                character.ActiveConnection = null;
                return "机器人 [" + definition.Name + "] 上线失败: " + ex.Message;
            }

            connection.Player = player;
            player.玩家进入场景();

            var brain = new BotBrain(definition, player);
            Brains.Add(brain);

            if (summoner != null)
                brain.FollowTargetId = summoner.ObjectId;

            if (definition.GivePotions)
                GivePotions(player);

            return definition.Name + " 已上线(等级 " + player.CurrentLevel + ",地图 " + map.MapId + ")";
        }

        public static string Dismiss(string name)
        {
            var brain = FindBrain(name);
            if (brain == null || !brain.Alive)
                return "机器人 [" + name + "] 不在线";

            try
            {
                brain.Alive = false;
                brain.Player.Disconnect();
                brain.Player = null;
            }
            catch (Exception ex)
            {
                MainProcess.AddSystemLog("[Bot] 下线机器人 [" + name + "] 异常: " + ex.Message);
            }
            Brains.Remove(brain);
            return name + " 已下线";
        }

        public static string Status()
        {
            if (Brains.Count == 0)
                return "当前没有在线机器人 (定义 " + (Config?.BotDefinitions.Length ?? 0) + " 个)";

            return string.Join("; ", Brains.Select(b =>
                b.Definition.Name + "=" + (b.Alive && b.Player != null
                    ? "Lv" + b.Player.CurrentLevel + " HP" + b.Player.CurrentHP + " 地图" + b.Player.CurrentMap.MapId + " 坐标(" + b.Player.CurrentPosition.X + "," + b.Player.CurrentPosition.Y + ")"
                    : "offline")));
        }

        // ===================== 聊天事件入口(由 PlayerObject 挂钩调用,主线程) =====================

        /// <summary>附近频道有人喊话:发给视野内机器人记入聊天记忆。</summary>
        public static void OnNearbyChat(PlayerObject sender, string text)
        {
            if (!Enabled || Brains.Count == 0 || sender == null || string.IsNullOrEmpty(text))
                return;

            foreach (var brain in Brains)
            {
                if (!brain.Alive || brain.Player == null || brain.Player == sender)
                    continue;
                if (brain.Player.CurrentMap != sender.CurrentMap || brain.Player.GetDistance(sender) > 15)
                    continue;
                brain.RecordChat(sender.ObjectName, text, false);
            }
        }

        /// <summary>玩家私聊机器人时由 PlayerObject 挂钩调用。</summary>
        public static void OnWhisperToBot(PlayerObject sender, CharacterData targetCharacter, string text)
        {
            if (!Enabled || sender == null || targetCharacter == null || string.IsNullOrEmpty(text))
                return;

            foreach (var brain in Brains)
            {
                if (brain.Alive && brain.Player != null && brain.Player.CharacterData == targetCharacter)
                {
                    brain.RecordChat(sender.ObjectName, text, true);
                    return;
                }
            }
        }

        public static bool IsBotCharacter(string name)
        {
            var character = GameDataGateway.CharacterDataTable[name] as CharacterData;
            return character != null && IsOwnedByBotAccount(character, name);
        }

        private static bool IsOwnedByBotAccount(CharacterData character, string botName)
        {
            var account = character.Account.V;
            return account != null
                && account.Account.V != null
                && account.Account.V == "BOT_" + botName;
        }

        public static BotBrain FindBrain(string name)
        {
            return Brains.FirstOrDefault(b => string.Equals(b.Definition.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        // ===================== 内部 =====================

        private static CharacterData FindOrCreateCharacter(BotDefinition definition)
        {
            var accountName = "BOT_" + definition.Name;
            var account = GameDataGateway.AccountData表.DataSheet.Values
                .Cast<AccountData>()
                .FirstOrDefault(a => a.Account.V == accountName);

            if (account == null)
                account = new AccountData(accountName);

            var character = account.Characters.FirstOrDefault(c => c.CharName.V == definition.Name);
            if (character != null)
            {
                if (character.ActiveConnection != null)
                    return null; // 数据残留(上次进程未正常下线),避免双开
                return character;
            }

            character = new CharacterData(
                account,
                definition.Name,
                definition.GetRace(),
                definition.GetGender(),
                definition.GetHairType(),
                definition.GetHairColor(),
                definition.GetFaceType());

            // 新角色按配置初始化等级与满状态
            if (definition.Level > 1)
            {
                character.Level.V = (byte)Math.Min(definition.Level, GameServer.Config.MaxLevel);                var stats = CharacterProgression.GetData(character.CharRace.V, character.Level.V);
                if (stats != null)
                {
                    character.CurrentHP.V = stats[GameObjectStats.MaxHP];
                    character.CurrentMP.V = stats[GameObjectStats.MaxMP];
                }
            }
            return character;
        }

        private static MapInstance ChooseSpawnMap(BotDefinition definition, PlayerObject summoner)
        {
            var mapId = summoner != null ? summoner.CurrentMap.MapId : definition.MapId;
            return MapGatewayProcess.GetMapInstance(mapId);
        }

        private static Point ChooseSpawnPoint(MapInstance map, PlayerObject summoner)
        {
            if (summoner != null && summoner.CurrentMap == map)
            {
                // 在召唤者周围找一个可通行的格子
                for (var radius = 1; radius <= 4; radius++)
                {
                    for (var dx = -radius; dx <= radius; dx++)
                    {
                        for (var dy = -radius; dy <= radius; dy++)
                        {
                            var point = new Point(summoner.CurrentPosition.X + dx, summoner.CurrentPosition.Y + dy);
                            if (Math.Abs(dx) + Math.Abs(dy) <= radius + 1 && map.CanPass(point))
                                return point;
                        }
                    }
                }
                return summoner.CurrentPosition;
            }

            var area = map.ResurrectionArea ?? map.传送区域 ?? map.地图区域.FirstOrDefault();
            return area != null ? area.RandomCoords : Point.Empty;
        }

        private static void GivePotions(PlayerObject player)
        {
            var count = Config.Reflex.GivePotionsCount;
            GiveItem(player, Config.Reflex.HpPotionIds[0], count);
            GiveItem(player, Config.Reflex.MpPotionIds[0], count);
        }

        private static void GiveItem(PlayerObject player, int itemId, int count)
        {
            GameItems template;
            if (!GameItems.DataSheet.TryGetValue(itemId, out template))
                return;

            for (var i = 0; i < count; i += 100)
            {
                byte position;
                if (!player.CharacterData.TryGetFreeSpaceAtInventory(out position))
                    return;
                player.GainItem(template, position, Math.Min(100, count - i));
            }
        }
    }
}
