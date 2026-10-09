using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using GameServer.Data;
using GameServer.Maps;
using GameServer.Templates;

namespace GameServer.Bots
{
    /// <summary>
    /// 机器人对世界的一次"人的感知":只报告这会儿新发生的事(Events/Chat)和轻量场景,
    /// 背包/装备只在变化时携带。构建时与 brain 保存的上一轮状态做差分。
    /// </summary>
    public class BotSnapshot
    {
        // ---- 场景(每轮轻量携带) ----
        public string SelfName;
        public string Race;
        public int Level;
        public int Hp;
        public int MaxHp;
        public int Mp;
        public int MaxMp;
        public string MapName;
        /// <summary>游戏时段:清晨/白天/傍晚/深夜 —— 作息行为随它变。</summary>
        public string TimeOfDay;
        public int X;
        public int Y;
        public bool Dead;
        public int Gold;
        public string Mood;

        public string FollowTarget;
        public int FollowTargetDistance;
        /// <summary>正在赶路的目的地描述(如"比奇省的门"或坐标),让 LLM 知道不用重复发移动指令。</summary>
        public string GoingTo;
        public int GoingToDistance;
        public string CombatTarget;
        public int CombatTargetHpPercent;
        public bool AutoGrinding;
        /// <summary>队伍状态:队友名字(逗号分隔),不在队为 null。</summary>
        public string Team;

        public List<SeenObject> Players = new List<SeenObject>();
        public List<SeenObject> Monsters = new List<SeenObject>();
        /// <summary>当前地图的出口(传送门):名字+方向+目的地 —— 想换地图练级就得知道能去哪。</summary>
        public List<string> Exits = new List<string>();
        public List<SeenChat> Chat = new List<SeenChat>();

        /// <summary>这一轮新发生的事(视野/血量/等级/状态变化),没有就是空。</summary>
        public List<string> Events = new List<string>();

        /// <summary>上一轮工具调用的结果回执。</summary>
        public List<string> ToolResults = new List<string>();

        /// <summary>背包/装备详情,只在发生变化或明确需要时携带(null=未变)。</summary>
        public List<ItemDesc> Inventory;
        public List<EquipDesc> Equipment;

        public class SeenObject
        {
            public int Id;
            public string Name;
            public int Level;
            public int Distance;
            public string Direction;
            /// <summary>对方在干嘛(打怪中/挂机练级/跟着人/闲逛) —— 只对其他 bot 可知,真人玩家看不到意图。</summary>
            public string Doing;
            /// <summary>你记忆里对这个人的印象(有才带,随观察走,不进稳定头)。</summary>
            public string Note;
        }

        public class SeenChat
        {
            public string From;
            public string Text;
            public bool Whisper;
        }

        /// <summary>背包物品(可穿的同名装备会逐件列出,重复的显示 Count)。</summary>
        public class ItemDesc
        {
            public string Name;
            public int Count;
            public string Info; // 类型/需求/攻防摘要,装备类才有
        }

        public class EquipDesc
        {
            public string Part;   // 部位名(武器/衣服...)
            public string Name;
            public string Info;
        }

        public static BotSnapshot Build(PlayerObject player, BotBrain brain)
        {
            var snapshot = new BotSnapshot
            {
                SelfName = player.ObjectName,
                Race = player.CharRole.ToString(),
                Level = player.CurrentLevel,
                Hp = player.CurrentHP,
                MaxHp = player[GameObjectStats.MaxHP],
                Mp = player.CurrentMP,
                MaxMp = player[GameObjectStats.MaxMP],
                MapName = GetMapName(player.CurrentMap.MapId),
                TimeOfDay = TimeOfDayName(),
                X = player.CurrentPosition.X,
                Y = player.CurrentPosition.Y,
                Dead = player.Died,
                Gold = player.NumberGoldCoins,
                Mood = brain.Mood,
                AutoGrinding = brain.AutoGrind,
            };

            // ---- 视野内玩家 ----
            var playerIds = new HashSet<int>();
            foreach (var neighbor in player.Neighbors)
            {
                var p = neighbor as PlayerObject;
                if (p == null || p == player || p.Died)
                    continue;
                playerIds.Add(p.ObjectId);
                snapshot.Players.Add(new SeenObject
                {
                    Id = p.ObjectId,
                    Name = p.ObjectName,
                    Level = p.CurrentLevel,
                    Distance = player.GetDistance(p),
                    Direction = DirectionName(player.CurrentPosition, p.CurrentPosition),
                    Doing = DescribeDoing(p),
                    Note = brain.Memory.People.ContainsKey(p.ObjectName) ? brain.Memory.People[p.ObjectName] : null,
                });
            }
            snapshot.Players = snapshot.Players.OrderBy(p => p.Distance).ToList();

            // ---- 视野内怪物(近的几只,让 LLM 看得见猎物) ----
            foreach (var neighbor in player.Neighbors)
            {
                var m = neighbor as MonsterObject;
                if (m == null || m.Died)
                    continue;
                var distance = player.GetDistance(m);
                if (distance > 8)
                    continue;
                snapshot.Monsters.Add(new SeenObject
                {
                    Id = m.ObjectId,
                    Name = m.ObjectName,
                    Level = m.CurrentLevel,
                    Distance = distance,
                    Direction = DirectionName(player.CurrentPosition, m.CurrentPosition),
                });
            }
            snapshot.Monsters = snapshot.Monsters.OrderBy(m => m.Distance).Take(5).ToList();

            // ---- 当前地图出口(哪扇门通往哪张图,用中文图名) ----
            foreach (var gate in TeleportGates.DataSheet)
            {
                if (gate.FromMapId != player.CurrentMap.MapId || gate.ToMapId == gate.FromMapId)
                    continue;
                GameMap toMap;
                var toName = GameMap.DataSheet.TryGetValue(gate.ToMapId, out toMap) && !string.IsNullOrEmpty(toMap.MapName)
                    ? toMap.MapName
                    : gate.ToMapName;
                var distance = Math.Max(Math.Abs(gate.FromCoords.X - player.CurrentPosition.X), Math.Abs(gate.FromCoords.Y - player.CurrentPosition.Y));
                snapshot.Exits.Add("去" + toName + "的门在" + DirectionName(player.CurrentPosition, gate.FromCoords) + "约" + distance + "格(" + gate.FromCoords.X + "," + gate.FromCoords.Y + ")");
            }

            // ---- 与上一轮差分出事件 ----
            foreach (var p in snapshot.Players)
                if (!brain.KnownPlayers.ContainsKey(p.Id))
                {
                    var known = brain.Memory.People.ContainsKey(p.Name) ? "认识的" : "没见过";
                    snapshot.Events.Add("玩家[" + p.Name + "]出现在" + p.Direction + p.Distance + "格(" + known + ")");
                }
            foreach (var entry in brain.KnownPlayers)
                if (!playerIds.Contains(entry.Key))
                    snapshot.Events.Add("视野里的玩家[" + entry.Value + "]走远了");
            brain.KnownPlayers.Clear();
            foreach (var p in snapshot.Players)
                brain.KnownPlayers[p.Id] = p.Name;

            var hpPercent = snapshot.MaxHp > 0 ? snapshot.Hp * 100 / snapshot.MaxHp : 100;
            if (hpPercent < brain.LastHpPercent - 15)
                snapshot.Events.Add("血量掉到" + hpPercent + "%");
            else if (hpPercent > brain.LastHpPercent + 30 && brain.LastHpPercent > 0)
                snapshot.Events.Add("血量恢复到" + hpPercent + "%");
            brain.LastHpPercent = hpPercent;

            if (player.CurrentLevel > brain.LastLevel && brain.LastLevel > 0)
            {
                snapshot.Events.Add("升级了!现在是" + player.CurrentLevel + "级");
                brain.Memory.RecordMilestone("升到 " + player.CurrentLevel + " 级", 5);
                brain.MemoryDirty = true;
                BotLogger.Log(brain.Definition.Name, "event", "升级 → " + player.CurrentLevel + "级");
            }
            brain.LastLevel = player.CurrentLevel;

            if (player.Died && !brain.WasDead)
            {
                snapshot.Events.Add("死了");
                brain.Memory.RecordEpisode("被怪打死了", 3);
                brain.MemoryDirty = true;
                BotLogger.Log(brain.Definition.Name, "event", "死亡 @" + player.CurrentPosition.X + "," + player.CurrentPosition.Y);
            }
            brain.WasDead = player.Died;

            if (brain.FollowTargetId != 0 && MapGatewayProcess.Objects.TryGetValue(brain.FollowTargetId, out var followTarget) && !followTarget.Died)
            {
                snapshot.FollowTarget = followTarget.ObjectName;
                snapshot.FollowTargetDistance = player.GetDistance(followTarget);
                if (snapshot.FollowTargetDistance > 12)
                    snapshot.Events.Add("跟随的[" + snapshot.FollowTarget + "]已经拉开" + snapshot.FollowTargetDistance + "格");
            }
            else if (brain.FollowTargetId == 0 && brain.WasFollowing)
            {
                snapshot.Events.Add("刚才跟着的人不见了");
            }
            brain.WasFollowing = brain.FollowTargetId != 0;

            if (brain.CombatTargetId != 0 && MapGatewayProcess.Objects.TryGetValue(brain.CombatTargetId, out var combatTarget) && !combatTarget.Died)
            {
                var maxHp = combatTarget[GameObjectStats.MaxHP];
                snapshot.CombatTarget = combatTarget.ObjectName;
                snapshot.CombatTargetHpPercent = maxHp != 0
                    ? Math.Max(0, combatTarget.CurrentHP * 100 / maxHp)
                    : 100;
            }

            if (player.Team != null)
                snapshot.Team = string.Join(",", player.Team.Members
                    .Where(m => m.ActiveConnection != null && m.CharName.V != player.ObjectName)
                    .Select(m => m.CharName.V));

            // 赶路状态:LLM 看得到"已经在走",就不会每轮重复发 goto_map/move_to
            if (brain._pendingGate != null)
            {
                snapshot.GoingTo = brain._pendingGate.ToMapName + "的门";
                snapshot.GoingToDistance = player.GetDistance(brain._pendingGate.FromCoords);
            }
            else if (brain.MoveTarget != null)
            {
                snapshot.GoingTo = "(" + brain.MoveTarget.Value.X + "," + brain.MoveTarget.Value.Y + ")";
                snapshot.GoingToDistance = player.GetDistance(brain.MoveTarget.Value);
            }

            // ---- 聊天(本轮未读消息) ----
            lock (brain.ChatMemory)
            {
                foreach (var line in brain.ChatMemory)
                    snapshot.Chat.Add(new SeenChat { From = line.From, Text = line.Text, Whisper = line.Whisper });
                brain.ChatMemory.Clear();
            }

            // ---- 上轮工具回执 ----
            lock (brain.ToolResults)
            {
                snapshot.ToolResults.AddRange(brain.ToolResults);
                brain.ToolResults.Clear();
            }

            // ---- 背包/装备(变化才带全量) ----
            var fingerprint = player.Backpack.Count + "/" + player.Equipment.Count + "/" + player.NumberGoldCoins;
            if (fingerprint != brain.LastInventoryFingerprint)
            {
                snapshot.Inventory = DescribeBackpack(player);
                snapshot.Equipment = DescribeEquipment(player);
                brain.LastInventoryFingerprint = fingerprint;
            }

            return snapshot;
        }

        public static List<ItemDesc> DescribeBackpack(PlayerObject player)
        {
            var list = new List<ItemDesc>();
            foreach (var item in player.Backpack.Values)
            {
                if (item == null || item.物品模板 == null) continue;
                var desc = new ItemDesc { Name = item.物品模板.Name, Count = 1 };
                if (item.物品模板.PersistType == PersistentItemType.堆叠)
                    desc.Count = Math.Max(1, item.当前持久.V);
                var equip = item.物品模板 as EquipmentItem;
                if (equip != null)
                    desc.Info = DescribeEquip(equip);
                var existing = list.LastOrDefault(i => i.Name == desc.Name && i.Info == desc.Info);
                if (existing != null && desc.Count >= 0)
                    existing.Count += desc.Count;
                else
                    list.Add(desc);
            }
            return list;
        }

        public static List<EquipDesc> DescribeEquipment(PlayerObject player)
        {
            var list = new List<EquipDesc>();
            foreach (var pair in player.Equipment)
            {
                var equip = pair.Value;
                if (equip == null || equip.物品模板 == null) continue;
                list.Add(new EquipDesc
                {
                    Part = ((EquipmentWearingParts)pair.Key).ToString(),
                    Name = equip.Name,
                    Info = DescribeEquip(equip.物品模板 as EquipmentItem),
                });
            }
            return list;
        }

        /// <summary>装备属性摘要:需求(等级/职业) + 攻防数值,供对比好坏。</summary>
        public static string DescribeEquip(EquipmentItem equip)
        {
            if (equip == null) return null;
            var parts = new List<string>();
            if (equip.NeedLevel > 0) parts.Add("需" + equip.NeedLevel + "级");
            if (equip.NeedRace != GameObjectRace.通用) parts.Add("限" + equip.NeedRace);
            if (equip.MinDC > 0 || equip.MaxDC > 0) parts.Add("攻" + equip.MinDC + "-" + equip.MaxDC);
            if (equip.MinMC > 0 || equip.MaxMC > 0) parts.Add("魔" + equip.MinMC + "-" + equip.MaxMC);
            if (equip.MinSC > 0 || equip.MaxSC > 0) parts.Add("道" + equip.MinSC + "-" + equip.MaxSC);
            if (equip.MinDef > 0 || equip.MaxDef > 0) parts.Add("防" + equip.MinDef + "-" + equip.MaxDef);
            if (equip.MaxHP > 0) parts.Add("血+" + equip.MaxHP);
            if (equip.MaxMP > 0) parts.Add("蓝+" + equip.MaxMP);
            return parts.Count > 0 ? string.Join(" ", parts) : null;
        }

        private static string TimeOfDayName()
        {
            var hour = DateTime.Now.Hour;
            if (hour >= 5 && hour < 9) return "清晨";
            if (hour >= 9 && hour < 17) return "白天";
            if (hour >= 17 && hour < 22) return "傍晚";
            return "深夜";
        }

        /// <summary>对方玩家在干嘛;对其他 bot 从其意图状态读取,真人玩家显示未知。</summary>
        private static string DescribeDoing(PlayerObject p)
        {
            var brain = BotManager.FindBrain(p.ObjectName);
            if (brain == null)
                return null; // 真人玩家,意图不可知
            if (brain.Player == null)
                return null;
            if (brain.Player.Died) return "躺了";
            if (brain.CombatTargetId != 0) return "打怪中";
            if (brain.AutoGrind) return "挂机练级";
            if (brain.FollowTargetId != 0) return "跟着人";
            if (brain.MoveTarget != null) return "赶路";
            return "闲逛";
        }

        private static string DirectionName(Point from, Point to)
        {
            var direction = ComputingClass.GetDirection(from, to);
            switch (direction)
            {
                case GameDirection.上方: return "北";
                case GameDirection.右上: return "东北";
                case GameDirection.右方: return "东";
                case GameDirection.右下: return "东南";
                case GameDirection.下方: return "南";
                case GameDirection.左下: return "西南";
                case GameDirection.左方: return "西";
                case GameDirection.左上: return "西北";
                default: return direction.ToString();
            }
        }

        private static string GetMapName(int mapId)
        {
            GameMap map;
            return GameMap.DataSheet.TryGetValue((byte)mapId, out map) ? map.MapName : mapId.ToString();
        }
    }
}
