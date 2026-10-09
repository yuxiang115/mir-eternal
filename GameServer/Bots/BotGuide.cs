using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using GameServer.Maps;
using GameServer.Templates;

namespace GameServer.Bots
{
    /// <summary>
    /// "官方攻略"双源合成:①知识库(research/kb,七职业×七成长段的装备/技能/地图路线,静态文件)
    /// ②本服真实数据(刷怪表/物品表自动聚合)。服务器实测优先,KB 提供玩法建议。
    /// </summary>
    public static class BotGuide
    {
        private static Dictionary<int, string> _cache; // 等级段下限 → 攻略文本
        private static readonly int[] Bands = { 1, 11, 21, 31, 41 };

        public static string ForLevel(int level, GameObjectRace race = GameObjectRace.战士)
        {
            BuildCache();
            var band = Bands.Last(b => level >= b);
            var text = _cache[band];
            var kb = ForKb(level, race);
            return kb != null ? kb + "\n" + text : text;
        }

        // ===================== 知识库(research/kb) =====================

        private static List<KbStage> _kbStages;

        private class KbStage
        {
            public string ClassId;
            public int LevelMin;
            public int LevelMax;
            public string Phase;
            public string Focus;
            public string GearGoal;
        }

        private static string ForKb(int level, GameObjectRace race)
        {
            try
            {
                if (_kbStages == null)
                    LoadKb();
                if (_kbStages == null || _kbStages.Count == 0)
                    return null;

                var classId = RaceToKbClass(race);
                var stage = _kbStages.FirstOrDefault(s => s.ClassId == classId && level >= s.LevelMin && level <= s.LevelMax);
                if (stage == null)
                    return null;

                var sb = new StringBuilder();
                sb.Append("【玩家攻略·").Append(stage.Phase).Append("】").Append(stage.Focus).Append('\n');
                if (!string.IsNullOrEmpty(stage.GearGoal))
                {
                    var gear = stage.GearGoal;
                    var cut = gear.IndexOf("。");
                    sb.Append("装备路线: ").Append(cut > 0 ? gear.Substring(0, cut + 1) : gear).Append('\n');
                }
                sb.Append("(攻略仅供参考,以服务器实际为准)");
                return sb.ToString();
            }
            catch
            {
                return null; // KB 缺失/损坏不影响自动攻略
            }
        }

        private static void LoadKb()
        {
            _kbStages = new List<KbStage>();
            var path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "kb", "data", "class_progression.json");
            if (!System.IO.File.Exists(path))
                path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "GameServer", "Bots", "research", "kb", "data", "class_progression.json");
            if (!System.IO.File.Exists(path))
                return;

            var json = Newtonsoft.Json.Linq.JArray.Parse(System.IO.File.ReadAllText(path));
            foreach (var item in json)
            {
                _kbStages.Add(new KbStage
                {
                    ClassId = (string)item["class_id"] ?? "",
                    LevelMin = (int?)item["level_min"] ?? 0,
                    LevelMax = (int?)item["level_max"] ?? 0,
                    Phase = (string)item["phase"] ?? "",
                    Focus = (string)item["focus"] ?? "",
                    GearGoal = (string)item["gear_goal"] ?? "",
                });
            }
        }

        private static string RaceToKbClass(GameObjectRace race)
        {
            switch (race)
            {
                case GameObjectRace.战士: return "warrior";
                case GameObjectRace.法师: return "mage";
                case GameObjectRace.道士: return "taoist";
                case GameObjectRace.刺客: return "assassin";
                case GameObjectRace.弓手: return "archer";
                case GameObjectRace.龙枪: return "dragoon";
                default: return "warrior";
            }
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
