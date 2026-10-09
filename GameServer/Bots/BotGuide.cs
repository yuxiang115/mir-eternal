using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using GameServer.Maps;
using GameServer.Templates;

namespace GameServer.Bots
{
    /// <summary>
    /// 服务器自动生成的"官方攻略"(等级段→推荐地图/怪/装备),给 bot 的 check_guide 工具用。
    /// 数据全部来自 GameMap/MonsterSpawns/Monsters/GameItems,不是编的 —— bot 查到的是这个服的真实生态。
    /// </summary>
    public static class BotGuide
    {
        private static Dictionary<int, string> _cache; // 等级段下限 → 攻略文本
        private static readonly int[] Bands = { 1, 11, 21, 31, 41 };

        public static string ForLevel(int level)
        {
            BuildCache();
            var band = Bands.Last(b => level >= b);
            return _cache[band];
        }

        private static void BuildCache()
        {
            if (_cache != null)
                return;

            // 每张图的怪物集合(名字→等级)
            var mapMonsters = new Dictionary<byte, Dictionary<string, byte>>();
            foreach (var spawn in MonsterSpawns.DataSheet)
            {
                Dictionary<string, byte> monsters;
                if (!mapMonsters.TryGetValue(spawn.FromMapId, out monsters))
                    mapMonsters[spawn.FromMapId] = monsters = new Dictionary<string, byte>();
                foreach (var info in spawn.Spawns)
                {
                    if (info == null || string.IsNullOrEmpty(info.MonsterName))
                        continue;
                    Monsters template;
                    var monsterLevel = Monsters.DataSheet.TryGetValue(info.MonsterName, out template) ? template.Level : (byte)0;
                    monsters[info.MonsterName] = monsterLevel;
                }
            }

            _cache = new Dictionary<int, string>();
            foreach (var band in Bands)
            {
                var lo = band;
                var hi = band + 9;
                var sb = new StringBuilder();
                sb.Append(lo).Append("-").Append(hi).Append("级攻略(本服实测数据):\n");

                // 推荐图:图内怪等级区间覆盖 [lo,hi] 的,按贴合度排序
                var suitable = mapMonsters
                    .Where(m => m.Value.Count > 0)
                    .Select(m => new
                    {
                        MapId = m.Key,
                        Min = m.Value.Values.Min(),
                        Max = m.Value.Values.Max(),
                        Monsters = m.Value,
                    })
                    .Where(x => x.Max >= lo && x.Min <= hi + 4)
                    .OrderBy(x => Math.Abs(((x.Min + x.Max) / 2) - (lo + hi) / 2))
                    .Take(4)
                    .ToList();

                foreach (var map in suitable)
                {
                    GameMap template;
                    var mapName = GameMap.DataSheet.TryGetValue(map.MapId, out template) && !string.IsNullOrEmpty(template.MapName)
                        ? template.MapName
                        : ("#" + map.MapId);
                    var top = map.Monsters.OrderByDescending(m => m.Value).Take(5)
                        .Select(m => m.Key + m.Value + "级");
                    sb.Append("- ").Append(mapName).Append(": ").Append(string.Join("/", top)).Append('\n');
                }
                if (suitable.Count == 0)
                    sb.Append("- 这个等级段没有匹配的野外地图,找人带或越级小心点打\n");

                // 装备建议:该段能穿的中位数武器(便宜靠谱的商店货)
                foreach (var raceName in new[] { "战士", "法师", "道士" })
                {
                    var race = (GameObjectRace)Enum.Parse(typeof(GameObjectRace), raceName);
                    var weapons = GameItems.DataSheet.Values
                        .OfType<EquipmentItem>()
                        .Where(e => e.Type == ItemType.武器 && e.NeedLevel >= lo && e.NeedLevel <= hi
                                    && (e.NeedRace == GameObjectRace.通用 || e.NeedRace == race)
                                    && e.SalePrice <= 30000 && e.SalePrice > 0)
                        .OrderBy(e => e.MaxDC + e.MaxMC + e.MaxSC)
                        .ToList();
                    if (weapons.Count > 0)
                    {
                        var pick = weapons[weapons.Count / 2];
                        sb.Append("- ").Append(raceName).Append("武器: ").Append(pick.Name)
                          .Append("(需").Append(pick.NeedLevel).Append("级");
                        if (pick.MaxDC > 0) sb.Append(" 攻").Append(pick.MinDC).Append("-").Append(pick.MaxDC);
                        if (pick.MaxMC > 0) sb.Append(" 魔").Append(pick.MinMC).Append("-").Append(pick.MaxMC);
                        if (pick.MaxSC > 0) sb.Append(" 道").Append(pick.MinSC).Append("-").Append(pick.MaxSC);
                        sb.Append(")\n");
                    }
                }
                _cache[band] = sb.ToString();
            }
        }
    }
}
