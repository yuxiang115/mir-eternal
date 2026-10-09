using System;
using System.Collections.Generic;
using System.Drawing;
using GameServer.Maps;

namespace GameServer.Bots
{
    /// <summary>
    /// 网格 A* 寻路(8向,对角防穿角)。只在设定远目的地时调用一次(几十万节点预算内毫秒~百毫秒级),
    /// 之后反射层沿路径逐点走;走不通时由振荡检测触发重算。
    /// </summary>
    public static class BotPathfinder
    {
        private static readonly int[] Dx = { -1, 0, 1, -1, 1, -1, 0, 1 };
        private static readonly int[] Dy = { -1, -1, -1, 0, 0, 1, 1, 1 };

        public static List<Point> FindPath(MapInstance map, Point from, Point to, int nodeBudget = 400000)
        {
            if (from == to)
                return new List<Point>();
            if (map.IsBlocked(to))
                return null;

            var cameFrom = new Dictionary<Point, Point>();
            var gScore = new Dictionary<Point, int> { [from] = 0 };
            var seq = 0;
            var open = new SortedSet<(int F, int Seq, Point P)>(Comparer<(int, int, Point)>.Create((a, b) =>
                a.Item1 != b.Item1 ? a.Item1.CompareTo(b.Item1) : a.Item2.CompareTo(b.Item2)));
            open.Add((Heuristic(from, to), seq++, from));

            var expanded = 0;
            while (open.Count > 0 && expanded < nodeBudget)
            {
                var current = open.Min;
                open.Remove(current);
                var p = current.P;

                if (p == to)
                    return Reconstruct(cameFrom, p);

                expanded++;
                var gp = gScore[p];
                for (var i = 0; i < 8; i++)
                {
                    var next = new Point(p.X + Dx[i], p.Y + Dy[i]);
                    // 只查静态地形(不查生物占位):羊群/玩家是活动的,挡路靠清路/绕行解决,
                    // 用 CanPass 会在"算路时空、走过去被占"之间反复振荡
                    if (map.IsBlocked(next))
                        continue;
                    // 对角移动要求两个相邻直行格都可通,防止穿墙角
                    if (Dx[i] != 0 && Dy[i] != 0
                        && (map.IsBlocked(new Point(p.X + Dx[i], p.Y)) || map.IsBlocked(new Point(p.X, p.Y + Dy[i]))))
                        continue;

                    var cost = Dx[i] != 0 && Dy[i] != 0 ? 14 : 10;
                    var candidate = gp + cost;
                    int existing;
                    if (gScore.TryGetValue(next, out existing) && existing <= candidate)
                        continue;

                    gScore[next] = candidate;
                    cameFrom[next] = p;
                    open.Add((candidate + Heuristic(next, to), seq++, next));
                }
            }
            return null; // 预算耗尽或不可达
        }

        private static int Heuristic(Point a, Point b)
        {
            var dx = Math.Abs(a.X - b.X);
            var dy = Math.Abs(a.Y - b.Y);
            return (dx + dy) * 10 - Math.Min(dx, dy) * 6; // 对角修正的曼哈顿
        }

        private static List<Point> Reconstruct(Dictionary<Point, Point> cameFrom, Point end)
        {
            var path = new List<Point>();
            var current = end;
            while (cameFrom.ContainsKey(current))
            {
                path.Add(current);
                current = cameFrom[current];
            }
            path.Reverse();
            return path;
        }
    }
}
