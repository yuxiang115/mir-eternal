using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using GameServer.Data;
using GameServer.Maps;
using GameServer.Templates;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GameServer.Bots
{
    /// <summary>
    /// 单个机器人的"大脑":保存意图状态与聊天记忆,调度 LLM 思考,执行反射层动作。
    /// 约定:除 ThinkAsync 外所有成员只能在主循环线程调用;ThinkAsync 只读取快照与对话历史,
    /// 产生的动作通过 BotManager.ActionQueue 投回主线程。
    /// </summary>
    public class BotBrain
    {
        private static readonly Dictionary<ushort, int> SkillRangeCache = new Dictionary<ushort, int>();

        public readonly BotDefinition Definition;
        public PlayerObject Player;
        public bool Alive;

        // ---- 意图状态(主线程读写) ----
        public int FollowTargetId;
        public bool FollowStopsOnArrival;
        public int CombatTargetId;
        public Point? MoveTarget;

        // ---- 聊天记忆(主线程写,快照读取) ----
        public readonly Queue<BotSnapshot.SeenChat> ChatMemory = new Queue<BotSnapshot.SeenChat>();

        // ---- 思考调度 ----
        private volatile bool _thinking;
        private DateTime _nextThinkTime;
        private DateTime _llmPausedUntil;
        private int _llmFailures;
        private readonly List<LlmMessage> _conversation = new List<LlmMessage>();
        private string _systemPrompt;

        // ---- 反射层 ----
        private DateTime _lastReflexTime;
        private DateTime _diedSince;
        private byte _actionCounter;

        public BotBrain(BotDefinition definition, PlayerObject player)
        {
            Definition = definition;
            Player = player;
            Alive = true;
            _nextThinkTime = MainProcess.CurrentTime.AddSeconds(2.0);
            _systemPrompt = BuildSystemPrompt();
        }

        private string BuildSystemPrompt()
        {
            return
                "你是网游《热血传奇》私服里的一名真实陪玩玩家,角色名\"" + Definition.Name + "\"。你通过调用工具与世界互动。\n" +
                "决策规则:\n" +
                "1. 每次收到【观察】(JSON)后决定下一步。没事可做就调用 stop 或不调用任何工具,不要自言自语刷屏。\n" +
                "2. 玩家对你说话(观察里 Chat 有 Whisper=true 或提到你的名字)时,优先用 say 或 whisper 回应,语气自然简短。\n" +
                "3. 陪你身边的玩家打怪:附近有怪物且没有被清理时可以 attack;跟随玩家用 follow_player。\n" +
                "4. 你的生命(Hp)过低时系统会自动喝药,不必专门处理;死亡会自动复活。\n" +
                "5. 说话内容必须是简短口语化中文,每次一两句话。\n" +
                "人设:" + Definition.Persona;
        }

        // ===================== 主线程接口 =====================

        public void RecordChat(string from, string text, bool whisper)
        {
            if (!Alive || whisper && from == Definition.Name)
                return;

            lock (ChatMemory)
            {
                ChatMemory.Enqueue(new BotSnapshot.SeenChat { From = from, Text = text, Whisper = whisper });
                while (ChatMemory.Count > BotManager.Config.MaxChatMemory)
                    ChatMemory.Dequeue();
            }

            // 被私聊或被点名时立即思考,缩短响应延迟
            if (whisper || text != null && text.Contains(Definition.Name))
                _nextThinkTime = MainProcess.CurrentTime;
        }

        /// <summary>每主循环 tick 调用:调度一次 LLM 思考(如到期且空闲)。</summary>
        public void ScheduleThink()
        {
            if (!Alive || Player == null || !BotManager.Config.Enabled)
                return;

            var config = BotManager.Config.Llm;
            if (!LlmClient.IsConfigured(config) || _thinking)
                return;

            if (MainProcess.CurrentTime < _nextThinkTime || MainProcess.CurrentTime < _llmPausedUntil)
                return;

            BotSnapshot snapshot;
            try
            {
                snapshot = BotSnapshot.Build(Player, this);
            }
            catch (Exception ex)
            {
                MainProcess.AddSystemLog("[Bot] " + Definition.Name + " 构建观察失败: " + ex.Message);
                _nextThinkTime = MainProcess.CurrentTime.AddSeconds(10.0);
                return;
            }

            _thinking = true;
            Task.Run(() => ThinkAsync(snapshot));
        }

        private async Task ThinkAsync(BotSnapshot snapshot)
        {
            var calls = new List<LlmToolCall>();
            try
            {
                var config = BotManager.Config;
                var llm = config.Llm;
                var observation = "[Observation] " + JObject.FromObject(snapshot).ToString(Formatting.None);

                _conversation.Add(new LlmMessage("user", observation));
                try
                {
                    var result = await LlmClient.ChatAsync(llm, _conversation, BotToolCatalog.Definitions);
                    _llmFailures = 0;

                    var wasAddressed = snapshot.Chat.Count > 0 && snapshot.Chat.Any(c =>
                        c.Whisper || !string.IsNullOrEmpty(c.Text) && c.Text.Contains(Definition.Name));

                    // 模型只回了文本没调工具:若正被人搭话,把文本当作喊话说出去
                    if (result.ToolCalls.Count == 0 && !string.IsNullOrWhiteSpace(result.Content) && wasAddressed)
                    {
                        result.ToolCalls.Add(new LlmToolCall
                        {
                            Name = "say",
                            Arguments = new JObject { ["text"] = result.Content.Trim() },
                        });
                    }

                    var actionSummary = result.ToolCalls.Count == 0
                        ? "(无动作)"
                        : string.Join("; ", result.ToolCalls.Select(c => c.Name + "(" + c.Arguments.ToString(Formatting.None) + ")"));

                    _conversation.Add(new LlmMessage("assistant", (result.Content ?? "") + "\n[动作] " + actionSummary));
                    calls = result.ToolCalls;
                }
                catch (Exception ex)
                {
                    _conversation.RemoveAt(_conversation.Count - 1); // 回滚观察,避免污染历史
                    _llmFailures++;
                    if (_llmFailures >= 3)
                    {
                        _llmPausedUntil = MainProcess.CurrentTime.AddMinutes(1.0);
                        MainProcess.AddSystemLog("[Bot] " + Definition.Name + " LLM 连续失败,暂停思考1分钟: " + ex.Message);
                    }
                }

                TrimHistory();

                if (calls.Count > 0)
                {
                    var brain = this;
                    BotManager.EnqueueAction(() =>
                    {
                        if (!brain.Alive || brain.Player == null) return;
                        foreach (var call in calls)
                        {
                            var reply = BotToolCatalog.Execute(brain, call.Name, call.Arguments);
                            if (reply != null && (reply.StartsWith("工具执行失败") || reply.StartsWith("未知工具")))
                                MainProcess.AddSystemLog("[Bot] " + brain.Definition.Name + " " + reply);
                        }
                    });
                }
            }
            finally
            {
                _thinking = false;
                _nextThinkTime = MainProcess.CurrentTime.AddMilliseconds(BotManager.Config.ThinkIntervalMs);
            }
        }

        private void TrimHistory()
        {
            // 保留 system + 最近 N 轮 user/assistant
            var maxMessages = 1 + BotManager.Config.MaxHistoryTurns * 2;
            if (_conversation.Count <= maxMessages)
                return;

            var kept = new List<LlmMessage> { _conversation[0] };
            kept.AddRange(_conversation.Skip(_conversation.Count - (maxMessages - 1)));
            _conversation.Clear();
            _conversation.AddRange(kept);
        }

        // ===================== 反射层(全部主线程) =====================

        /// <summary>每主循环 tick 调用:自动喝药/跟随/打怪/移动/复活,不经过 LLM。</summary>
        public void ProcessReflex()
        {
            var player = Player;
            if (!Alive || player == null)
                return;

            try
            {
                ReflexDead(player);
                if (player.Died)
                    return;

                if (MainProcess.CurrentTime < _lastReflexTime)
                    return;
                _lastReflexTime = MainProcess.CurrentTime.AddMilliseconds(BotManager.Config.Reflex.ActIntervalMs);

                ReflexPotion(player);
                if (!ReflexCombat(player))
                    ReflexFollow(player);
                ReflexMove(player);
            }
            catch (Exception ex)
            {
                MainProcess.AddSystemLog("[Bot] " + Definition.Name + " 反射层异常(已忽略): " + ex.Message);
                _lastReflexTime = MainProcess.CurrentTime.AddSeconds(2.0);
            }
        }

        private void ReflexDead(PlayerObject player)
        {
            if (player.Died)
            {
                if (_diedSince == default(DateTime))
                {
                    _diedSince = MainProcess.CurrentTime;
                    _nextThinkTime = MainProcess.CurrentTime; // 死亡触发思考
                }
                else if (MainProcess.CurrentTime > _diedSince.AddSeconds(6.0))
                {
                    player.玩家请求复活();
                    _diedSince = default(DateTime);
                }
            }
            else
            {
                _diedSince = default(DateTime);
            }
        }

        private void ReflexPotion(PlayerObject player)
        {
            var maxHp = player[GameObjectStats.MaxHP];
            var maxMp = player[GameObjectStats.MaxMP];
            var reflex = BotManager.Config.Reflex;

            if (maxHp > 0 && player.CurrentHP * 100 / maxHp < reflex.AutoPotionHpPercent)
                DrinkPotion("hp");
            else if (maxMp > 0 && player.CurrentMP * 100 / maxMp < reflex.AutoPotionMpPercent)
                DrinkPotion("mp");
        }

        public bool DrinkPotion(string kind)
        {
            var player = Player;
            if (player == null || player.Died)
                return false;

            var ids = kind == "mp"
                ? BotManager.Config.Reflex.MpPotionIds
                : BotManager.Config.Reflex.HpPotionIds;

            foreach (var id in ids)
            {
                ItemData item;
                if (player.查找背包物品(id, out item))
                {
                    player.UseItem(1, item.物品位置.V);
                    return true;
                }
            }
            return false;
        }

        private bool ReflexCombat(PlayerObject player)
        {
            if (CombatTargetId == 0)
                return false;

            MapObject target;
            if (!MapGatewayProcess.Objects.TryGetValue(CombatTargetId, out target) || target.Died)
            {
                CombatTargetId = 0;
                _nextThinkTime = MainProcess.CurrentTime; // 目标消失,让 LLM 决定下一步
                return false;
            }

            if (target.CurrentMap != player.CurrentMap)
            {
                CombatTargetId = 0;
                return false;
            }

            var distance = player.GetDistance(target);
            if (distance > BotManager.Config.Reflex.ChaseMaxDistance)
            {
                CombatTargetId = 0;
                return false;
            }

            var skillId = GetAttackSkillId(player);
            var range = GetSkillRange(skillId);
            if (distance <= Math.Max(1, range))
            {
                _actionCounter = (byte)(_actionCounter + 1);
                player.UseSkill(skillId, _actionCounter, target.ObjectId, target.CurrentPosition);
            }
            else
            {
                StepToward(player, target.CurrentPosition);
            }
            return true;
        }

        private void ReflexFollow(PlayerObject player)
        {
            if (FollowTargetId == 0)
                return;

            MapObject master;
            if (!MapGatewayProcess.Objects.TryGetValue(FollowTargetId, out master) || master.Died)
                return; // 主人暂时不在视野/下线,原地等待而不是立刻放弃

            if (master.CurrentMap != player.CurrentMap)
            {
                FollowTargetId = 0;
                _nextThinkTime = MainProcess.CurrentTime;
                return;
            }

            var distance = player.GetDistance(master);
            var followDistance = BotManager.Config.Reflex.FollowDistance;

            if (distance <= followDistance)
            {
                if (FollowStopsOnArrival)
                    FollowTargetId = 0;
                return;
            }

            if (distance > 40)
            {
                FollowTargetId = 0; // 距离过远放弃跟随
                return;
            }

            StepToward(player, master.CurrentPosition);
        }

        private void ReflexMove(PlayerObject player)
        {
            if (MoveTarget == null)
                return;

            var target = MoveTarget.Value;
            var distance = player.GetDistance(target);
            if (distance <= 0)
            {
                MoveTarget = null;
                return;
            }
            StepToward(player, target);
        }

        /// <summary>朝目标走一步;正前方受阻时按怪物 AI 的方式向两侧绕行。</summary>
        private void StepToward(PlayerObject player, Point target)
        {
            if (!player.CanMove())
                return;

            var direction = ComputingClass.GetDirection(player.CurrentPosition, target);
            for (var i = 0; i < 8; i++)
            {
                var front = ComputingClass.前方坐标(player.CurrentPosition, direction, 1);
                if (player.CurrentMap.CanPass(front))
                {
                    player.OnWalk(front);
                    return;
                }
                direction = ComputingClass.TurnAround(direction, MainProcess.RandomNumber.Next(2) == 0 ? -1 : 1);
            }
        }

        // ===================== 技能辅助 =====================

        public static ushort GetAttackSkillId(PlayerObject player)
        {
            // 各职业初始普攻铭文 -> 真实技能Id
            var starterInscription = GetRaceStarterInscription(player.CharRole);
            if (starterInscription != 0
                && InscriptionSkill.DataSheet.TryGetValue(starterInscription, out var inscription)
                && player.MainSkills表.ContainsKey(inscription.SkillId))
            {
                return inscription.SkillId;
            }

            var first = player.MainSkills表.Keys.FirstOrDefault();
            return first;
        }

        private static ushort GetRaceStarterInscription(GameObjectRace race)
        {
            switch (race)
            {
                case GameObjectRace.战士: return 10300;
                case GameObjectRace.法师: return 25300;
                case GameObjectRace.刺客: return 15300;
                case GameObjectRace.弓手: return 20400;
                case GameObjectRace.道士: return 30000;
                case GameObjectRace.龙枪: return 12000;
                default: return 0;
            }
        }

        public static int GetSkillRange(ushort skillId)
        {
            int cached;
            if (SkillRangeCache.TryGetValue(skillId, out cached))
                return cached;

            var range = 1;
            foreach (var template in GameSkills.DataSheet.Values)
            {
                if (template.OwnSkillId == skillId && template.MaxDistance > range)
                    range = template.MaxDistance;
            }
            SkillRangeCache[skillId] = range;
            return range;
        }
    }
}
