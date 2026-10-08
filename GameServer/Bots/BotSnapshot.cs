using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using GameServer.Maps;
using GameServer.Templates;

namespace GameServer.Bots
{
    /// <summary>
    /// 机器人对周围世界的一次不可变观察,在主循环线程构建,随后在线程池上交给 LLM。
    /// </summary>
    public class BotSnapshot
    {
        public string SelfName;
        public string Race;
        public int Level;
        public int Hp;
        public int MaxHp;
        public int Mp;
        public int MaxMp;
        public int MapId;
        public string MapName;
        public int X;
        public int Y;
        public bool Dead;

        public string FollowTarget;
        public int FollowTargetDistance;
        public string CombatTarget;
        public int CombatTargetHpPercent;

        public List<SeenObject> Players = new List<SeenObject>();
        public List<SeenObject> Monsters = new List<SeenObject>();
        public List<SeenChat> Chat = new List<SeenChat>();
        public Dictionary<string, int> Inventory = new Dictionary<string, int>();

        public class SeenObject
        {
            public int Id;
            public string Name;
            public int Level;
            public int Distance;
            public string Direction;
        }

        public class SeenChat
        {
            public string From;
            public string Text;
            public bool Whisper;
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
                MapId = player.CurrentMap.MapId,
                MapName = GetMapName(player.CurrentMap.MapId),
                X = player.CurrentPosition.X,
                Y = player.CurrentPosition.Y,
                Dead = player.Died,
            };

            foreach (var neighbor in player.Neighbors)
            {
                if (neighbor == player || neighbor.Died && neighbor is MonsterObject)
                    continue;

                var seen = Describe(player, neighbor);
                if (seen == null) continue;

                if (neighbor is PlayerObject)
                {
                    if (!neighbor.Died) snapshot.Players.Add(seen);
                }
                else if (neighbor is MonsterObject)
                {
                    snapshot.Monsters.Add(seen);
                }
            }

            snapshot.Monsters = snapshot.Monsters.OrderBy(m => m.Distance).Take(15).ToList();
            snapshot.Players = snapshot.Players.OrderBy(p => p.Distance).Take(10).ToList();

            if (brain.FollowTargetId != 0 && MapGatewayProcess.Objects.TryGetValue(brain.FollowTargetId, out var followTarget) && !followTarget.Died)
            {
                snapshot.FollowTarget = followTarget.ObjectName;
                snapshot.FollowTargetDistance = player.GetDistance(followTarget);
            }

            if (brain.CombatTargetId != 0 && MapGatewayProcess.Objects.TryGetValue(brain.CombatTargetId, out var combatTarget) && !combatTarget.Died)
            {
                var maxHp = combatTarget[GameObjectStats.MaxHP];
                snapshot.CombatTarget = combatTarget.ObjectName;
                snapshot.CombatTargetHpPercent = maxHp != 0
                    ? Math.Max(0, combatTarget.CurrentHP * 100 / maxHp)
                    : 100;
            }

            lock (brain.ChatMemory)
            {
                foreach (var line in brain.ChatMemory)
                    snapshot.Chat.Add(new SeenChat { From = line.From, Text = line.Text, Whisper = line.Whisper });
            }

            foreach (var item in player.Backpack.Values)
            {
                if (item == null || item.物品模板 == null) continue;
                var key = item.Name;
                var count = Math.Max(1, item.当前持久.V);
                snapshot.Inventory[key] = snapshot.TryGetValue(key) + count;
            }

            return snapshot;
        }

        private static SeenObject Describe(PlayerObject observer, MapObject obj)
        {
            string name;
            int level;
            if (obj is PlayerObject p)
            {
                name = p.ObjectName;
                level = p.CurrentLevel;
            }
            else if (obj is MonsterObject m)
            {
                name = m.ObjectName;
                level = m.CurrentLevel;
            }
            else
            {
                return null;
            }

            return new SeenObject
            {
                Id = obj.ObjectId,
                Name = name,
                Level = level,
                Distance = observer.GetDistance(obj),
                Direction = DirectionName(observer.CurrentPosition, obj.CurrentPosition),
            };
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

        private int TryGetValue(string key)
        {
            int value;
            return Inventory.TryGetValue(key, out value) ? value : 0;
        }
    }
}
