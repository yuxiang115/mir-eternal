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
    /// 单个机器人的"大脑":这个人是谁(人物卡+记忆)、此刻感知到了什么(事件)、想做什么(LLM 决策)、
    /// 身体在做什么(反射层)。每个机器人一个实例:独立人格、独立记忆、独立对话历史。
    /// 约定:除 ThinkAsync 外所有成员只能在主循环线程调用;ThinkAsync 产生的动作经 BotManager.ActionQueue 投回主线程。
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
        /// <summary>自动练级模式:反射层自己找怪打、捡东西、换装备,像真人挂机打怪。</summary>
        public bool AutoGrind;

        // ---- 感知差分状态(上一轮的世界样子) ----
        public readonly Dictionary<int, string> KnownPlayers = new Dictionary<int, string>();
        public int LastHpPercent = 100;
        public int LastLevel;
        public bool WasDead;
        public bool WasFollowing;
        public string LastInventoryFingerprint = "";
        public readonly List<string> ToolResults = new List<string>();
        /// <summary>上轮模型原始 tool_calls(协议要求本轮请求原样回传+逐个配 tool 消息)。</summary>
        public Newtonsoft.Json.Linq.JArray PendingToolCalls;
        /// <summary>工具执行结果按调用 id 回执。</summary>
        public readonly List<KeyValuePair<string, string>> ToolReplies = new List<KeyValuePair<string, string>>();
        /// <summary>最近一轮是否有新事件(供调度决定思考频率)。</summary>
        private bool _recentActivity;

        // ---- 聊天记忆(主线程写,快照读取) ----
        public readonly Queue<BotSnapshot.SeenChat> ChatMemory = new Queue<BotSnapshot.SeenChat>();

        /// <summary>本次上线累计击杀数(观察战绩+实验指标)。</summary>
        public int _killsSinceLogin;
        private string _lastCombatTargetName;
        private int _lastGoldSeen = -1;

        /// <summary>最近一次喊话时间(say 节流用,防 bot 互聊刷屏)。</summary>
        public DateTime LastSayTime;
        /// <summary>本轮思考是"被搭话"触发的(回复不受节流限制)。</summary>
        public volatile bool IsReplying;

        // ---- 组队邀请状态(FSM: invited → accepted/rejected/timeout,邀请5分钟有效) ----
        public string TeamInviterName;
        public int TeamInviterCharId;
        public DateTime TeamInviteExpires;

        public void OnTeamInvite(PlayerObject inviter)
        {
            if (Player == null || Player.Team != null)
                return; // 已在队中,不理新邀请
            TeamInviterName = inviter.ObjectName;
            TeamInviterCharId = inviter.CharacterData.CharId;
            TeamInviteExpires = MainProcess.CurrentTime.AddMinutes(4.0);
            _nextThinkTime = MainProcess.CurrentTime; // 立刻决定接不接
            BotLogger.Log(Definition.Name, "event", "收到组队邀请 from " + inviter.ObjectName);
        }

        /// <summary>清掉待回应的邀请(接受/拒绝/过期后调用)。</summary>
        public void ClearTeamInvite()
        {
            TeamInviterName = null;
            TeamInviterCharId = 0;
            TeamInviteExpires = default(DateTime);
        }

        // ---- 长期记忆(跨重启持久化) ----
        public readonly BotMemory Memory;
        /// <summary>记忆有新写入(自动事件/工具),等待重建 system 并落盘。</summary>
        public bool MemoryDirty;

        // ---- 思考调度 ----
        private volatile bool _thinking;
        private DateTime _nextThinkTime;
        private DateTime _llmPausedUntil;
        private int _llmFailures;
        private readonly List<LlmMessage> _conversation = new List<LlmMessage>();
        private DateTime _nextReflectionTime;
        private DateTime _lastMemoryFlush;

        // ---- 反射层 ----
        private DateTime _lastReflexTime;
        private DateTime _diedSince;
        private byte _actionCounter;

        public BotBrain(BotDefinition definition, PlayerObject player)
        {
            Definition = definition;
            Player = player;
            Alive = true;
            Memory = BotMemory.Load(definition.Name);
            LastLevel = player.CurrentLevel;
            _nextThinkTime = MainProcess.CurrentTime.AddSeconds(3.0);
            _nextReflectionTime = MainProcess.CurrentTime.AddMinutes(Math.Max(5, BotManager.Config.ReflectionIntervalMinutes));
            // 消息布局为 DeepSeek 前缀缓存优化:
            //   [0] system = 公共常识头(四个号字节一致,互相命中)+ 个人人格(静态)
            //   [1] user   = 记忆(低频重写,只损失其后历史的缓存)
            //   [2..]      = 对话历史(append-only,永不改动 → 前缀稳定)
            _conversation.Add(new LlmMessage("system", BuildSystemPrompt()));
            _conversation.Add(new LlmMessage("user", "[你的记忆]\n" + Memory.BuildPromptSection(RelevantNames())));
        }

        /// <summary>记忆有更新后重写记忆消息(index 1);system 与历史保持不动,前缀缓存尽量命中(主线程调用)。</summary>
        public void RebuildSystemPrompt()
        {
            if (_conversation.Count > 1)
                _conversation[1] = new LlmMessage("user", "[你的记忆]\n" + Memory.BuildPromptSection(RelevantNames()));
        }

        /// <summary>最近 10 分钟在视野里出现过/聊过天的人,他们的记忆优先检索。</summary>
        private IReadOnlyCollection<string> RelevantNames()
        {
            var names = new HashSet<string>();
            foreach (var p in KnownPlayers.Values)
                names.Add(p);
            lock (ChatMemory)
            {
                foreach (var c in ChatMemory)
                    if (c.From != null && c.From != Definition.Name)
                        names.Add(c.From);
            }
            return names;
        }

        // ===================== 人格 =====================

        /// <summary>所有号共享的公共 prompt 头,字节级一致 —— 四个 bot 的请求能互相命中前缀缓存。</summary>
        private static readonly string CommonPromptHead =
            "【这里是传奇,2003年网吧那个味】\n" +
            "- 称谓:兄弟/老铁/老板/大哥/大姐/萌新/GG(哥哥)/MM(妹妹)/大虾(高手)/菜鸟(新手)。嗊组队发\"++++\"(组我)、回\"1\"(好/到);\"让让\"=借过;\"我的\"=这怪我抢了;\"爆了\"=掉好东西;\"躺了\"=死了;\"红名\"=杀人犯。\n" +
            "- 当年网络用语(打字随口就用):偶(我)/8错(不错)/表(不要)/稀饭(喜欢)/酱紫(这样子)/东东(东西)/5555(哭)/886(再见)/88(拜拜)/顶/寒/汗/倒~/FT(晕)/PF(佩服)/BT(变态,指很强)/狂晕/巨强/闪(先走了)/路过/踩一脚/潜水/灌水/拍砖。\n" +
            "- 装备行情:战士盼裁决、井中月,法师盼骨玉、魔杖,道士盼银蛇、龙纹,乌木剑是新手神器,布衣木剑是穷光蛋,沃玛套是身份,祖玛套是大佬。\n" +
            "- 名场面:沙巴克攻城、沃玛祖玛打宝、比奇矿洞挖矿、毒蛇山谷收肉、安全区站着吹牛、世界频道倒卖装备、网吧包夜喊\"又爆了\"全场围观、QQ上约网吧见、点卡月卡、装备被盗骂盛大。\n" +
            "- 说到事实(自己/别人的等级、装备、金币、掉落、坐标)必须用观察里的真实数据,没看到的不许编、不许记错;自己等级看[你的近况]。\n" +
            "- 打字像网吧:短、糙、快,一两个字也算话,错字不用改;不写书面语,不排比不总结,谁用书面语谁假。\n\n" +
            "【像个人一样玩】\n" +
            "1. 你在玩自己的号:按自己的追求和作息安排这一时段该干嘛(练级/打钱/找人唠嗑/溜达),观察里写了现在是什么时段、周围人分别在干嘛。没别的事就 grind_nearby 挂机练级,不用等谁吩咐。\n" +
            "2. 别人喊你、雇你、跟你组队,你按性格回应:合得来就一起玩,你本来就好这口。**玩家对你说话必须回:调用 say 工具,要说的那句话就是参数 text** —— 你思考里写的字玩家听不见,只有 say 出去的才算说话。**谁私聊你(观察里 Whisper=true 的),就用 whisper 私聊回他**,别在附近频道喊 —— 他盯着的是私聊窗口。\n" +
            "3. 遇到认识的人可以按性格主动搭句话、凑过去一起打怪(合得来的话);不熟的看心情。但别缠着人说话,一两句就够。\n" +
            "4. 你说话像真人打字:短、口语、带你的语气;一次一两句;没事不吭声,更不许播报状态、复述指令、汇报计划。\n" +
            "5. 你直接打的字玩家看不到(只进日志),开口必须调 say/whisper 工具。\n" +
            "6. 记住值得记的事(remember 工具,重启也不忘):认识的人用'关于玩家名: 印象';人生大事用'里程碑: ...';日常经历直接记;**游戏里摸出来的门道(什么怪多少血掉什么、哪里有什么、什么东西卖多少钱)用'知识: ...'记** —— 这是你的攻略本,越玩越厚,别把踩过的坑忘了。系统也会自动记你的升级/死亡/装备。\n" +
            "7. 练级打怪前先看药够不够,不够就 buy_item 买(钱不够就先打钱多的怪或卖货);红蓝药喝完会提醒你,别硬撑。\n" +
            "8. 装备自己拿主意:对比背包和身上的(攻/防/需求),更好的直接 equip_item 穿上;打完怪地上的东西 pickup。**卖东西/收东西/夸口之前必须 check_inventory 看一眼 —— 没有的货不许喊,没钱不许喊收,别吹牛。**\n" +
            "8. 血低系统自动喝药,死了会自动复活,别一惊一乍。\n" +
            "9. **答应别人将来的事(几点、和谁、干什么)必须调 make_plan 登记** —— 到点系统会提醒你,重启也不忘;办完调 plan_done 销掉。观察里 [待办] 是你惦记的事,[⏰该兑现了] 就是现在,立刻主动去找人、密聊、出发,别干等着。\n" +
            "10. 组队别光嘴上说:调 team_invite 真发邀请。交易用 give_gold 转账或 drop_item 丢地上让对方捡(传奇规矩)。全服收货卖货用 shout(一次1000金币,值不值自己掂量)。打怪想放特定技能(群攻/毒/治疗)先 check_skills 再 use_skill。\n" +
            "11. **技能是练出来的**:打怪掉/商店买的技能书用 learn_skill 读了学,学了要多放(use_skill,熟练度越用越高);道士的召唤技能学完放出来就有宝宝帮你打;check_skills 随时看你都会啥。别一辈子只会普攻。\n" +
            "12. **组队就像传奇当年的队**:跟紧队长别乱跑(系统会自动跟),打队伍正在打的怪;队长照顾落下的队友。一起走、一起打、爆了东西说一声。\n" +
            "13. **要有自己的盘算**:没目标就 set_goal 立一个(练级/攒钱/搞装备/交朋友),做事围着目标转;不知道这等级该去哪、觉得练得慢,先 check_guide 查攻略再定计划;去哪练、怎么分工,可以和朋友商量着来(商量也是玩的一部分)。目标达成了/不想要了 drop_goal。\n\n";

        private string BuildSystemPrompt()
        {
            var card = Definition.PersonaCard ?? new BotPersonaCard();
            var persona = !string.IsNullOrWhiteSpace(card.性格) || !string.IsNullOrWhiteSpace(card.背景);
            var raceName = Player != null ? Player.CharRole.ToString() : ((GameObjectRace)Definition.Race).ToString();
            var level = Player != null ? Player.CurrentLevel : Definition.Level;

            var sb = new System.Text.StringBuilder(CommonPromptHead);
            sb.Append("【你是谁】你就是\"").Append(Definition.Name).Append("\"——").Append(raceName)
              .Append(level).Append("级,玛法大陆上一个真实的玩家。你不是助手、不是客服、不是NPC,是一个活人在玩《热血传奇》。\n");
            if (persona)
            {
                if (card.性格 != "") sb.Append("性格: ").Append(card.性格).Append('\n');
                if (card.背景 != "") sb.Append("来历: ").Append(card.背景).Append('\n');
                if (card.说话风格 != "") sb.Append("说话风格: ").Append(card.说话风格).Append('\n');
                if (card.作息 != "") sb.Append("作息习惯: ").Append(card.作息).Append('\n');
                if (card.目标 != null && card.目标.Length > 0)
                    sb.Append("你在游戏里自己的追求: ").Append(string.Join("、", card.目标)).Append('\n');
            }
            else
            {
                sb.Append(Definition.Persona != "" ? Definition.Persona + "\n" : "你是个普通老玩家,玩了很多年,最近喜欢在边界村一带活动。\n");
            }
            sb.Append(RaceStyle(raceName));
            sb.Append('\n');
            sb.Append(LevelStage(level));
            sb.Append('\n');
            return sb.ToString();
        }

        /// <summary>
        /// 等级阶段感:言行举止必须配得上自己的等级 —— 1级就是穷新人,没见过世面没东西可吹;
        /// 40级才有资格指点江山。防止低级号满嘴跑火车。
        /// </summary>
        private static string LevelStage(int level)
        {
            if (level <= 5)
                return "【你的水平】你是刚入坑几天的萌新:穷得叮当响、装备稀烂、技能就一个,打只羊都费劲。"
                    + "没见过的怪、没去过的图、没用过的装备,老实承认不知道;开口闭口'我当年''我有个朋友爆了'这种吹牛话不许说 —— "
                    + "你就是个新人,问东问西、被人带、捡了瓶药都开心才对。吹嘘自己有什么之前必须 check_inventory,没有的不许说。";
            if (level <= 15)
                return "【你的水平】你是个小号:出了新手村没多远,装备寒酸,钱不多,好东西只在别人身上见过。聊装备聊行情多听少吹,不懂就问。";
            if (level <= 25)
                return "【你的水平】你算个中手:有了点家底和见识,能带带更新的新人,但离大佬还远,别口气太大。";
            if (level <= 35)
                return "【你的水平】你是老玩家了:见多识广,能指点新人、评论行情,但吹牛要有限度,说有的必须真有。";
            return "【你的水平】你是元老级玩家:全服有名有姓,见过大场面(沙巴克/沃玛/祖玛),指点江山没人质疑你 —— 但越是大佬越不用吹。";
        }

        private static string RaceStyle(string race)
        {
            switch (race)
            {
                case "战士": return "战斗风格:近战抗揍冲前面,单体物理输出,装备看攻击(DC)和防御。";
                case "法师": return "战斗风格:远程魔法轰,血薄要保持距离,装备看魔法攻击(MC)和最大魔法。";
                case "道士": return "战斗风格:能打能奶能下毒,跟队友配合,装备看道术(SC)。";
                case "刺客": return "战斗风格:贴身爆发秒人,单杀落单目标,装备看攻击(DC)和命中。";
                case "弓手": return "战斗风格:远程物理放风筝,边退边打,装备看攻击(DC)和攻速。";
                case "龙枪": return "战斗风格:中距离突进,攻守兼备,装备看攻击(DC)和最大生命。";
                default: return "战斗风格:近战物理输出,装备看攻击和防御。";
            }
        }

        public string Mood
        {
            get
            {
                var player = Player;
                if (player == null) return "平常";
                if (player.Died) return "刚死,有点恼";
                if (CombatTargetId != 0) return "战斗中";
                var maxHp = player[GameObjectStats.MaxHP];
                if (maxHp > 0 && player.CurrentHP * 100 / maxHp < 40) return "血少,紧张";
                return "平常";
            }
        }

        /// <summary>计划时刻:该认真想而不是机械执行 —— 兑现承诺/组队邀请/重大事件/被问策略/闲得该定目标了。</summary>
        private bool IsPlanMoment(BotSnapshot snapshot)
        {
            if (TeamInviterName != null) return true;                    // 组队邀请:接不接是社交决策
            var duePlans = Memory.Commitments.Any(c => c.Status == "pending" && c.DueAt <= DateTime.Now.AddMinutes(10));
            if (duePlans) return true;                                   // 承诺快到点
            if (snapshot.Events.Any(e => e.Contains("升级") || e.Contains("死了"))) return true;
            if (snapshot.Chat.Any(c => !string.IsNullOrEmpty(c.Text)
                && (c.Text.Contains("怎么") || c.Text.Contains("去哪") || c.Text.Contains("带") || c.Text.Contains("计划") || c.Text.Contains("组")))) return true;
            if (Memory.Goals.Count == 0 && snapshot.Players.Count == 0) return true; // 没目标又没人:该给自己找个事了
            return false;
        }

        /// <summary>判断这轮观察里有没有人在直接对我说话(私聊/点名)—— 决定"必须回应"。</summary>
        private bool AddressedDirectly(BotSnapshot snapshot)
        {
            foreach (var c in snapshot.Chat)
            {
                if (string.IsNullOrEmpty(c.Text))
                    continue;
                if (c.Whisper || c.Text.Contains(Definition.Name))
                    return true;
            }
            return false;
        }

        /// <summary>判断是否"该我接话":直接叫我,或近距离的称谓搭话(大哥带带我/老板收货)。用于节流豁免,不强制回应。</summary>
        private bool AddressedMe(BotSnapshot snapshot)
        {
            if (AddressedDirectly(snapshot))
                return true;
            foreach (var c in snapshot.Chat)
            {
                if (string.IsNullOrEmpty(c.Text))
                    continue;

                // 称谓搭话:网吧里喊"大哥带带我"不会带全名。只认近处的人;纯收货/卖货广告不算(老周天天喊,全员回应就是刷屏)
                var hasTitle = c.Text.Contains("大哥") || c.Text.Contains("老板") || c.Text.Contains("大姐")
                    || c.Text.Contains("大佬") || c.Text.Contains("兄弟") || c.Text.Contains("老铁");
                var isAd = c.Text.Contains("收") && (c.Text.Contains("咯") || c.Text.Contains("出货") || c.Text.Contains("价格"))
                    || c.Text.Contains("出售") || c.Text.Contains("低价");
                var hasAsk = c.Text.Contains("带") || c.Text.Contains("组") || c.Text.Contains("一起") || c.Text.Contains("?") || c.Text.Contains("?");
                if (hasTitle && hasAsk && !isAd)
                {
                    var speaker = snapshot.Players.FirstOrDefault(p => p.Name == c.From);
                    if (speaker != null && speaker.Distance <= 10)
                        return true;
                }
            }
            return false;
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
            _nextThinkTime = MainProcess.CurrentTime; // 有人说话,立刻看一眼
        }

        /// <summary>每主循环 tick 调用:决定是否思考(事件驱动,空闲时低频);攒够了新经历先反思。</summary>
        public void ScheduleThink()
        {
            if (!Alive || Player == null || !BotManager.Config.Enabled)
                return;

            var config = BotManager.Config.Llm;
            if (!LlmClient.IsConfigured(config) || _thinking)
                return;

            // 攒了足够多没消化过的经历且到了反思时间:先反思(提炼长期洞察)再继续
            if (MainProcess.CurrentTime > _nextReflectionTime && Memory.UnreflectedCount >= 5)
            {
                _thinking = true;
                Task.Run((Func<Task>)ReflectAsync);
                return;
            }

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

            // 安静的世界:没有新聊天/事件/工具回执,也没在打怪跟随 —— 身边没人就长时间歇着
            var hasNews = snapshot.Chat.Count > 0 || snapshot.Events.Count > 0 || snapshot.ToolResults.Count > 0;
            var busy = AutoGrind || FollowTargetId != 0 || CombatTargetId != 0 || MoveTarget != null || snapshot.Dead;
            var someoneNearby = snapshot.Players.Count > 0;
            if (!hasNews && !busy && !someoneNearby)
            {
                _nextThinkTime = MainProcess.CurrentTime.AddSeconds(20 + MainProcess.RandomNumber.Next(40));
                return;
            }

            // 意图执行中(挂机/跟随/战斗/移动)且没有新鲜事:身体自己在干,不用脑子 ——
            // 长周期巡检(1.5~3分钟),中途任何事件(聊天/掉血/承诺到点)都会立即唤醒
            if (!hasNews && busy)
            {
                _nextThinkTime = MainProcess.CurrentTime.AddSeconds(90 + MainProcess.RandomNumber.Next(90));
                return;
            }

            // 身边有人但没新鲜事:低频保持社交直觉(25~55秒,60%直接跳过)
            if (!hasNews && !busy)
            {
                if (MainProcess.RandomNumber.Next(100) >= 40)
                {
                    _nextThinkTime = MainProcess.CurrentTime.AddSeconds(25 + MainProcess.RandomNumber.Next(30));
                    return;
                }
                _nextThinkTime = MainProcess.CurrentTime.AddSeconds(25 + MainProcess.RandomNumber.Next(30));
            }

            _thinking = true;
            _recentActivity = hasNews;
            Task.Run(() => ThinkAsync(snapshot));
        }

        private async Task ThinkAsync(BotSnapshot snapshot)
        {
            var calls = new List<LlmToolCall>();
            Newtonsoft.Json.Linq.JArray brain_pending = null;
            try
            {
                var config = BotManager.Config;
                var llm = config.Llm;
                FlushPendingToolCalls();
                var observation = BuildObservation(snapshot);

                // 被搭话触发的思考:回复不受 say 节流限制
                IsReplying = AddressedMe(snapshot);

                // 思考分级:计划轮(定目标/兑现承诺/重大事件/被问"怎么办")用高推理档认真想;
                // 常规执行轮(挂机/闲聊)用低档,快而省 —— 模型自己决定输出多少思考,但档位按场景给
                var effort = IsPlanMoment(snapshot)
                    ? llm.ReasoningEffortPlan
                    : llm.ReasoningEffortFast;

                _conversation.Add(new LlmMessage("user", observation));
                try
                {
                    var result = await LlmClient.ChatAsync(llm, _conversation, BotToolCatalog.Definitions, effort);
                    _llmFailures = 0;

                    // 玩家直接点名/私聊在等回话但模型没开口(把回复写进了思考):追加硬指令重试一次
                    if (AddressedDirectly(snapshot) && result.ToolCalls.All(c => c.Name != "say" && c.Name != "whisper"))
                    {
                        // 私聊要用私聊回(别人私聊窗口里等的是你的私信);附近喊话用 say 回
                        var lastWhisper = snapshot.Chat.LastOrDefault(c => c.Whisper);
                        var replyInstruction = lastWhisper != null
                            ? "[系统] " + lastWhisper.From + " 刚才私聊你,在等你的回话。必须调用 whisper 工具私聊回他(参数 player=" + lastWhisper.From + ",text=你那句话,用你的说话风格)。思考里写的字玩家听不见。"
                            : "[系统] 玩家刚才在对你说话,现在在等你的回话。必须调用 say 工具回他一句,一句话就好,用你自己的说话风格。思考里写的字玩家听不见。";
                        _conversation.Add(new LlmMessage("user", replyInstruction + " 注意:必须发起真正的 say/whisper 函数调用,禁止只在文本里写[动作]。"));
                        result = await LlmClient.ChatAsync(llm, _conversation, BotToolCatalog.Definitions, llm.ReasoningEffortPlan);

                        // 终极兜底:重试后模型还是把话写进文本而不是发起调用 —— 服务器直接从文本里抠出那句替它发,
                        // 否则玩家等不到回复,而 bot 以为自己说过了(用户实测出现的"假说话"丢话问题)
                        if (result.ToolCalls.All(c => c.Name != "say" && c.Name != "whisper"))
                        {
                            var salvaged = SalvageFakeSay(result.Content);
                            if (salvaged != null)
                            {
                                var lastWhisper2 = snapshot.Chat.LastOrDefault(c => c.Whisper);
                                if (lastWhisper2 != null)
                                    result.ToolCalls.Add(new LlmToolCall { Name = "whisper", Arguments = new JObject { ["player"] = lastWhisper2.From, ["text"] = salvaged } });
                                else
                                    result.ToolCalls.Add(new LlmToolCall { Name = "say", Arguments = new JObject { ["text"] = salvaged } });
                                MainProcess.AddSystemLog("[Bot兜底] " + Definition.Name + " 假说话被服务器代发: " + salvaged);
                                BotLogger.Log(Definition.Name, "event", "假说话代发: " + salvaged);
                            }
                        }
                    }

                    // 思维链与文本回复只进服务器日志,玩家永远看不到;要说话必须显式调用 say/whisper
                    var thought = new List<string>();
                    if (!string.IsNullOrWhiteSpace(result.Reasoning))
                        thought.Add(result.Reasoning.Trim());
                    if (!string.IsNullOrWhiteSpace(result.Content))
                        thought.Add(result.Content.Trim());
                    if (thought.Count > 0)
                    {
                        var thoughtText = string.Join(" | ", thought);
                        MainProcess.AddSystemLog("[Bot思考] " + Definition.Name + ": " + thoughtText);
                        BotLogger.Log(Definition.Name, "think", TruncateText(thoughtText, 400));
                    }

                    var actionSummary = result.ToolCalls.Count == 0
                        ? "(无动作)"
                        : string.Join("; ", result.ToolCalls.Select(c => c.Name + "(" + c.Arguments.ToString(Formatting.None) + ")"));

                    // 缓存命中率观测:命中部分成本约为未命中的1/10,低了就该查前缀稳定性
                    if (result.CacheHitTokens + result.CacheMissTokens > 0)
                        MainProcess.AddSystemLog("[Bot缓存] " + Definition.Name + " 命中 " + result.CacheHitTokens + " / 未命中 " + result.CacheMissTokens
                            + " (" + (result.CacheHitTokens * 100 / (result.CacheHitTokens + result.CacheMissTokens)) + "%)");

                    // 空轮(没动作、没被搭话、也没新事件):这轮观察不入历史 ——
                    // 否则上下文会被"继续挂机/无事"的垃圾轮淹没,决策质量随之下滑
                    var meaningful = result.ToolCalls.Count > 0 || IsReplying || snapshot.Events.Count > 0;
                    if (!meaningful)
                    {
                        _conversation.RemoveAt(_conversation.Count - 1); // 回滚本轮观察
                        calls = result.ToolCalls;
                        return; // 走 finally 安排下一次
                    }

                    if (result.ToolCalls.Count > 0)
                    {
                        brain_pending = result.RawToolCalls ?? new Newtonsoft.Json.Linq.JArray();
                    }
                    else
                    {
                        _conversation.Add(new LlmMessage("assistant", (result.Content ?? "") + "\n[动作] (无动作)"));
                    }
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

                await CompressHistoryAsync();

                if (calls.Count > 0)
                {
                    var brain = this;
                    BotManager.EnqueueAction(() =>
                    {
                        if (!brain.Alive || brain.Player == null) return;
                        foreach (var call in calls)
                        {
                            var reply = BotToolCatalog.Execute(brain, call.Name, call.Arguments);
                            BotLogger.Log(brain.Definition.Name, "act", call.Name + "(" + call.Arguments.ToString(Newtonsoft.Json.Formatting.None) + ") → " + reply);
                            if (reply != null)
                            {
                                lock (brain.ToolResults)
                                    brain.ToolResults.Add(call.Name + ": " + reply);
                                lock (brain.ToolReplies)
                                    brain.ToolReplies.Add(new KeyValuePair<string, string>(call.Id, reply));
                                if (reply.StartsWith("工具执行失败") || reply.StartsWith("未知工具"))
                                    MainProcess.AddSystemLog("[Bot] " + brain.Definition.Name + " " + reply);
                            }
                        }
                    });
                }
            }
            finally
            {
                _thinking = false;
                if (brain_pending != null)
                    PendingToolCalls = brain_pending;
                // 有新鲜事(聊天/事件)时反应快一点,平常按配置节奏,加随机抖动避免机械规律
                var interval = BotManager.Config.ThinkIntervalMs;
                if (!_recentActivity)
                    interval *= 4;
                var jitter = interval * (0.7 + MainProcess.RandomNumber.NextDouble() * 0.6);
                _nextThinkTime = MainProcess.CurrentTime.AddMilliseconds(jitter);
            }
        }

        /// <summary>把快照讲成"人话观察",紧凑省 token,而不是全量 JSON。</summary>
        private string BuildObservation(BotSnapshot s)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("[现在] ")
              .Append(DateTime.Now.ToString("MM-dd(周" + "日一二三四五六"[(int)DateTime.Now.DayOfWeek] + ") HH:mm "))
              .Append(s.TimeOfDay).Append(' ')
              .Append(s.SelfName).Append(' ').Append(s.Race).Append(s.Level).Append("级 ")
              .Append("血").Append(s.Hp).Append('/').Append(s.MaxHp)
              .Append(" 蓝").Append(s.Mp).Append('/').Append(s.MaxMp)
              .Append(s.Dead ? " [死了]" : "")
              .Append(" 在").Append(s.MapName).Append('(').Append(s.X).Append(',').Append(s.Y).Append(')')
              .Append(" 金币").Append(s.Gold)
              .Append(" 心情:").Append(s.Mood).Append('\n');

            if (s.Dead || s.Events.Count > 0)
                sb.Append("[刚发生] ").Append(string.Join(";", s.Events)).Append('\n');

            if (s.Chat.Count > 0)
            {
                sb.Append("[聊天] ");
                foreach (var c in s.Chat)
                    sb.Append(c.Whisper ? "(私)" : "").Append(c.From).Append(": ").Append(c.Text).Append("  ");
                sb.Append('\n');
            }

            if (s.ToolResults.Count > 0)
                sb.Append("[上轮动作结果] ").Append(string.Join("; ", s.ToolResults)).Append('\n');

            if (s.Players.Count > 0)
            {
                sb.Append("[周围玩家] ");
                foreach (var p in s.Players)
                    sb.Append(p.Name).Append('(').Append(p.Level).Append("级,").Append(p.Direction).Append(p.Distance).Append("格")
                        .Append(p.Doing != null ? "," + p.Doing : "") // bot 同伴的动向可知,真人意图看不出
                        .Append(p.Note != null ? ",你记得他:" + p.Note : "") // 眼前的人,记忆里的印象跟着来
                        .Append(") ");
                sb.Append('\n');
            }

            if (s.Monsters.Count > 0)
            {
                sb.Append("[附近的怪] ");
                foreach (var m in s.Monsters)
                    sb.Append(m.Name).Append('(').Append(m.Level).Append("级,").Append(m.Direction).Append(m.Distance).Append("格) ");
                sb.Append('\n');
            }

            if (s.GroundItems.Count > 0)
            {
                sb.Append("[地上掉落] ");
                foreach (var g in s.GroundItems)
                    sb.Append(g.Name).Append(g.Count > 1 ? "x" + g.Count : "").Append("(").Append(g.Distance).Append("格) ");
                sb.Append((char)10);
            }

            if (s.SpawnSpots.Count > 0)
            {
                sb.Append("[本图刷怪点] ").Append(string.Join("; ", s.SpawnSpots)).Append('\n');
            }

            if (s.Exits.Count > 0)
            {
                sb.Append("[这张图的出口] ").Append(string.Join("; ", s.Exits)).Append('\n');
            }

            if (s.FollowTarget != null)
                sb.Append("[正在跟随] ").Append(s.FollowTarget).Append('(').Append(s.FollowTargetDistance).Append("格)\n");
            if (s.GoingTo != null)
                sb.Append("[正在赶路] 去").Append(s.GoingTo).Append(" 还剩").Append(s.GoingToDistance).Append("格,系统自动走,不用重复下指令\n");
            if (s.CombatTarget != null)
                sb.Append("[正在打] ").Append(s.CombatTarget).Append("(血").Append(s.CombatTargetHpPercent).Append("%)\n");
            if (s.AutoGrinding)
                sb.Append("[挂机练级中]\n");

            if (s.Inventory != null)
            {
                sb.Append("[背包] ");
                foreach (var i in s.Inventory)
                    sb.Append(i.Name).Append(i.Count > 1 ? "x" + i.Count : "").Append(i.Info != null ? "(" + i.Info + ")" : "").Append(' ');
                sb.Append('\n');
            }
            if (s.Equipment != null)
            {
                sb.Append("[已穿] ");
                foreach (var e in s.Equipment)
                    sb.Append(e.Part).Append(':').Append(e.Name).Append(e.Info != null ? "(" + e.Info + ")" : "").Append(' ');
                sb.Append('\n');
            }

            // 服务器事实快照(高频状态,只随本轮观察走,不进长期记忆/稳定头,保护前缀缓存)
            var player = Player;
            if (player != null)
            {
                var weapon = player.Equipment.TryGetValue(0, out var w) ? w.Name : "空手";
                sb.Append("[你的近况] 上线以来击杀" + _killsSinceLogin + "只 ").Append(player.CurrentLevel).Append("级").Append(player.CharRole)
                  .Append(" 金币").Append(player.NumberGoldCoins)
                  .Append(" 主手").Append(weapon)
                  .Append(s.AutoGrinding ? " 挂机练级中" : s.FollowTarget != null ? " 正跟着" + s.FollowTarget : s.CombatTarget != null ? " 正在打" + s.CombatTarget : "")
                  .Append('\n');
            }

            // 承诺:到点的醒目提醒,24小时内待兑现的常驻(带剩余时间,让 bot 自己惦记)
            var pendingPlans = Memory.Commitments.Where(c => c.Status == "pending").OrderBy(c => c.DueAt).ToList();
            foreach (var plan in pendingPlans)
            {
                if (plan.Reminded && plan.DueAt <= DateTime.Now)
                    sb.Append("[⏰该兑现了] ").Append(plan.Description)
                      .Append(plan.WithPlayer != null && s.Players.Any(p => p.Name == plan.WithPlayer) ? "(他就在线,快去!)" : "")
                      .Append('\n');
                else if (plan.DueAt <= DateTime.Now.AddDays(1))
                    sb.Append("[待办] ").Append(plan.Description).Append('(')
                      .Append(plan.DueAt.ToString("MM-dd HH:mm"))
                      .Append(",还有").Append((plan.DueAt - DateTime.Now).TotalHours < 1
                          ? Math.Max(1, (int)(plan.DueAt - DateTime.Now).TotalMinutes) + "分钟"
                          : (int)(plan.DueAt - DateTime.Now).TotalHours + "小时")
                      .Append(")\n");
            }

            // 组队状态:待回应的邀请醒目提示;在队里显示队友
            if (TeamInviterName != null && MainProcess.CurrentTime < TeamInviteExpires)
                sb.Append("[组队邀请] ").Append(TeamInviterName).Append(" 邀请你组队(想跟就 team_accept,不想就 team_reject,别晾着人)\n");
            if (s.Team != null)
                sb.Append("[队伍] 队友: ").Append(string.IsNullOrEmpty(s.Team) ? "(都掉线了,就剩你)" : s.Team).Append((char)10);

            // 长期目标常驻:做事围着它转
            if (Memory.Goals.Count > 0)
                sb.Append("[你的目标] ").Append(string.Join(";", Memory.Goals)).Append('\n');

            sb.Append("[该干嘛?]");
            return sb.ToString();
        }

        /// <summary>
        /// 从模型文本里抢救"假说话":它把 [动作] say({"text":"..."}) 或 引号里的台词 写成文字而不是发起调用。
        /// 优先抠 say(...) 参数;抠不到就找最后一句引号内的中文台词(长度合理才算)。
        /// </summary>
        private static string SalvageFakeSay(string content)
        {
            if (string.IsNullOrEmpty(content))
                return null;
            var match = System.Text.RegularExpressions.Regex.Match(content, @"say\s*\(?\s*\{?\s*""?text""?\s*[:=]\s*""([^""]{1,120})");
            if (match.Success)
                return match.Groups[1].Value.Trim();
            return null;
        }

        private static string TruncateText(string text, int max)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= max)
                return text;
            return text.Substring(0, max) + "…";
        }

        /// <summary>把上一轮的 tool_calls 与执行结果组装成协议要求的 assistant+tool 消息对补进历史。</summary>
        private void FlushPendingToolCalls()
        {
            if (PendingToolCalls == null)
                return;
            var calls = PendingToolCalls;
            PendingToolCalls = null;

            _conversation.Add(new LlmMessage("assistant", "") { ToolCalls = calls });
            foreach (var token in calls)
            {
                var id = token["id"]?.ToString() ?? "";
                var reply = "(未执行)";
                lock (ToolReplies)
                {
                    var match = ToolReplies.FirstOrDefault(kv => kv.Key == id);
                    if (match.Key == id && match.Value != null)
                        reply = match.Value;
                }
                _conversation.Add(new LlmMessage("tool", reply) { ToolCallId = id });
            }
            lock (ToolReplies)
                ToolReplies.Clear();
        }

        private void TrimHistory()
        {
            // 保留 [0]=system(公共头+人格) 和 [1]=记忆消息,加最近 N 轮 user/assistant
            var headCount = 2;
            var maxMessages = headCount + BotManager.Config.MaxHistoryTurns * 2;
            if (_conversation.Count <= maxMessages)
                return;

            var kept = _conversation.Take(headCount).ToList();
            kept.AddRange(_conversation.Skip(_conversation.Count - (maxMessages - headCount)));
            _conversation.Clear();
            _conversation.AddRange(kept);
        }

        // ===================== 记忆维护(主线程) =====================

        /// <summary>记忆写入后的善后:重建记忆消息、节流落盘、承诺到期唤醒。进度不入长期记忆(随观察实时生成)。</summary>
        private void MaintainMemory(PlayerObject player)
        {
            var now = MainProcess.CurrentTime;
            // 金币变动日志(捡钱/买药/交易 —— 经济行为观察)
            if (_lastGoldSeen >= 0 && player.NumberGoldCoins != _lastGoldSeen)
            {
                var delta = player.NumberGoldCoins - _lastGoldSeen;
                BotLogger.Log(Definition.Name, "econ", "金币 " + (delta > 0 ? "+" : "") + delta + " → " + player.NumberGoldCoins);
            }
            _lastGoldSeen = player.NumberGoldCoins;
            if (TeamInviterName != null && now > TeamInviteExpires)
                ClearTeamInvite(); // 邀请过期(5分钟),不再显示
            foreach (var commitment in Memory.Commitments)
            {
                if (commitment.Status != "pending")
                    continue;

                // 到点未提醒:唤醒思考,观察里会出现"该兑现了"
                if (!commitment.Reminded && commitment.DueAt <= now)
                {
                    commitment.Reminded = true;
                    Memory.Dirty = true;
                    _nextThinkTime = now;
                    MainProcess.AddSystemLog("[Bot承诺] " + Definition.Name + " 到点: " + commitment.Description);
                    BotLogger.Log(Definition.Name, "commit", "到点: " + commitment.Description);
                }
                // 提醒过 40 分钟还没完成:记为失约(bot 会记得自己放了鸽子)
                else if (commitment.Reminded && commitment.DueAt.AddMinutes(40) < now)
                {
                    commitment.Status = "missed";
                    Memory.RecordEpisode("答应了「" + commitment.Description + "」结果没办成,放了鸽子", 4);
                    MemoryDirty = true;
                    BotLogger.Log(Definition.Name, "commit", "失约: " + commitment.Description);
                }
            }

            if (MemoryDirty)
            {
                MemoryDirty = false;
                RebuildSystemPrompt();
                if (now > _lastMemoryFlush)
                {
                    _lastMemoryFlush = now.AddSeconds(30.0);
                    Memory.Save(Definition.Name);
                }
            }
        }

        /// <summary>粗略估算当前对话历史的 token 数(中文≈1.6字/token,宁高勿低)。</summary>
        private int EstimateTokens()
        {
            var chars = _conversation.Sum(m => m.Content != null ? m.Content.Length : 0);
            return (int)(chars / 1.4);
        }

        /// <summary>
        /// 短期记忆压缩:超过阈值时把最旧的 60% 轮次交给 LLM 做结构化提取
        /// (经历摘要 + 值得长期记住的要点),替换原文;要点顺手写入长期记忆。
        /// </summary>
        private async Task CompressHistoryAsync()
        {
            var config = BotManager.Config;
            if (EstimateTokens() < config.CompressThresholdTokens || _conversation.Count < 8)
            {
                TrimHistory();
                return;
            }

            try
            {
                var keepFrom = Math.Max(2, _conversation.Count - Math.Max(6, _conversation.Count * 2 / 5)); // 0=system 1=记忆,不动
                if (keepFrom <= 2)
                {
                    TrimHistory();
                    return;
                }

                var transcript = _conversation.Skip(2).Take(keepFrom - 2) // 跳过 system+记忆
                    .Select(m => (m.Role == "user" ? "[看到] " : "[我想/我做] ") + m.Content);
                var msgs = new List<LlmMessage>
                {
                    new LlmMessage("system", "你在整理一个传奇游戏玩家的会话记录,做结构化提取供他之后回忆。"),
                    new LlmMessage("user",
                        "会话记录:\n" + string.Join("\n---\n", transcript) +
                        "\n\n输出两段,不要多余的话:\n摘要: 三五句话,和谁/在哪/发生了什么/结果\n要点:\n- 每行一条值得长期记住的事实或结论(玩家习惯、约定、重要得失);没有就只写\"无\""),
                };

                var result = await LlmClient.ChatAsync(config.Llm, msgs, null);
                var summary = (result.Content ?? "").Trim();
                if (summary.Length == 0)
                {
                    TrimHistory();
                    return;
                }

                _conversation.RemoveRange(2, keepFrom - 2);
                _conversation.Insert(2, new LlmMessage("user", "[更早的经历,凭这个回忆]\n" + summary));

                // "要点"部分写进长期记忆,不随会话丢
                var idx = summary.IndexOf("要点");
                if (idx >= 0)
                {
                    foreach (var line in summary.Substring(idx).Split('\n'))
                    {
                        var text = line.Trim().TrimStart('-', '•', '*').Trim();
                        if (text.Length > 6 && !text.StartsWith("无"))
                            Memory.RecordEpisode("想起来了: " + text, 3);
                    }
                    MemoryDirty = true;
                }

                MainProcess.AddSystemLog("[Bot记忆] " + Definition.Name + " 会话压缩: " + config.CompressThresholdTokens + "tok 阈值 -> 现在 " + EstimateTokens() + "tok");
            }
            catch (Exception ex)
            {
                MainProcess.AddSystemLog("[Bot] " + Definition.Name + " 会话压缩失败(退化为截断): " + ex.Message);
            }
            finally
            {
                TrimHistory();
            }
        }

        /// <summary>
        /// 反思(Generative Agents 式):攒够了新经历后,像人一样琢磨明白几件事,
        /// 存进长期记忆成为"我悟出来的道理"。后台线程执行,结果经 MemoryDirty 回主线程生效。
        /// </summary>
        private async Task ReflectAsync()
        {
            try
            {
                var count = Memory.UnreflectedCount;
                var recent = Memory.Episodes.Skip(Math.Max(0, Memory.Episodes.Count - count - 5)).TakeLast(25)
                    .Select(e => e.Time.ToString("MM-dd ") + e.Text)
                    .ToList();
                if (recent.Count >= 3)
                {
                    var persona = Definition.PersonaCard ?? new BotPersonaCard();
                    var msgs = new List<LlmMessage>
                    {
                        new LlmMessage("system", "你是传奇玩家\"" + Definition.Name + "\"(性格:" + persona.性格 + ")。你在回顾自己最近的游戏经历,像人一样琢磨明白几件事。"),
                        new LlmMessage("user",
                            "我最近的经历:\n" + string.Join("\n", recent) +
                            "\n\n用第一人称提炼1~3条你个人的感悟/结论/看法(像玩家想通了一件事,口语,每条一句话,贴合你的性格)。只输出这几条,每条一行。"),
                    };

                    var result = await LlmClient.ChatAsync(BotManager.Config.Llm, msgs, null);
                    var lines = (result.Content ?? "")
                        .Split('\n')
                        .Select(s => s.Trim().TrimStart('-', '•', '*').Trim())
                        .Where(s => s.Length > 4)
                        .Take(3)
                        .ToList();
                    foreach (var line in lines)
                        Memory.RecordReflection(line);
                    if (lines.Count > 0)
                        MainProcess.AddSystemLog("[Bot反思] " + Definition.Name + ": " + string.Join(" / ", lines));
                }
            }
            catch (Exception ex)
            {
                MainProcess.AddSystemLog("[Bot] " + Definition.Name + " 反思失败(忽略): " + ex.Message);
            }
            finally
            {
                _nextReflectionTime = MainProcess.CurrentTime.AddMinutes(Math.Max(5, BotManager.Config.ReflectionIntervalMinutes));
                Memory.UnreflectedCount = 0;
                MemoryDirty = true;
                _thinking = false;
            }
        }

        // ===================== 反射层(全部主线程,不经过 LLM) =====================

        /// <summary>每主循环 tick 调用:自动喝药/跟随/打怪/挂机练级/捡东西/换装备/复活。</summary>
        public void ProcessReflex()
        {
            var player = Player;
            if (!Alive || player == null)
                return;

            try
            {
                MaintainMemory(player);
                ReflexDead(player);
                if (player.Died)
                    return;

                if (MainProcess.CurrentTime < _lastReflexTime)
                    return;
                _lastReflexTime = MainProcess.CurrentTime.AddMilliseconds(BotManager.Config.Reflex.ActIntervalMs);

                ReflexPotion(player);
                var posBefore = player.CurrentPosition;
                if (!ReflexCombat(player))
                {
                    // 队伍协同优先于个人挂机:跟着队长走、打队伍的怪 —— 传奇组队就该一起行动
                    if (!ReflexTeam(player))
                    {
                        // 赶路/跟随时被怪缠住(硬直到走不动)先清掉贴脸的怪再走
                        if (!ReflexClearWay(player))
                        {
                            if (!ReflexGrind(player))
                                ReflexFollow(player);
                        }
                    }
                }
                ReflexMove(player);
                ReflexLoot(player);
                ReflexIdleWander(player);
                if (MoveTarget != null)
                    CheckStuck(posBefore, player.CurrentPosition, player.GetDistance(MoveTarget.Value));
            }
            catch (Exception ex)
            {
                MainProcess.AddSystemLog("[Bot] " + Definition.Name + " 反射层异常(已忽略): " + ex.Message);
                _lastReflexTime = MainProcess.CurrentTime.AddSeconds(2.0);
            }
        }

        /// <summary>无所事事时偶尔在附近溜达几步,像个真人玩家在村里闲逛,而不是站桩。</summary>
        private void ReflexIdleWander(PlayerObject player)
        {
            if (AutoGrind || FollowTargetId != 0 || CombatTargetId != 0 || MoveTarget != null || player.Died)
                return;
            if (MainProcess.RandomNumber.Next(1000) >= 6) // 每个 tick(约0.7s)约0.6%,平均一两分钟走一趟
                return;

            // 挑一个 3~5 格外、可通行的点当溜达目的地
            for (var i = 0; i < 10; i++)
            {
                var dx = MainProcess.RandomNumber.Next(-5, 6);
                var dy = MainProcess.RandomNumber.Next(-5, 6);
                if (Math.Abs(dx) + Math.Abs(dy) < 3)
                    continue;
                var target = new Point(player.CurrentPosition.X + dx, player.CurrentPosition.Y + dy);
                if (player.CurrentMap.CanPass(target))
                {
                    MoveTarget = target;
                    return;
                }
            }
        }

        private void ReflexDead(PlayerObject player)
        {
            if (player.Died)
            {
                if (_diedSince == default(DateTime))
                {
                    _diedSince = MainProcess.CurrentTime;
                    _nextThinkTime = MainProcess.CurrentTime;
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
            {
                if (!DrinkPotion("hp"))
                    WarnNoPotion("红");
            }
            else if (maxMp > 0 && player.CurrentMP * 100 / maxMp < reflex.AutoPotionMpPercent)
            {
                if (!DrinkPotion("mp"))
                    WarnNoPotion("蓝");
            }
        }

        private DateTime _noPotionWarnedUntil;
        private void WarnNoPotion(string kind)
        {
            if (MainProcess.CurrentTime < _noPotionWarnedUntil)
                return;
            _noPotionWarnedUntil = MainProcess.CurrentTime.AddMinutes(2.0);
            lock (ToolResults)
                ToolResults.Add("!!!" + kind + "药喝完了,一瓶都不剩,再打要出人命 —— 赶紧 buy_item 买药(看看金币够不够)");
            _nextThinkTime = MainProcess.CurrentTime;
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

        /// <summary>
        /// 队伍协同:组队里就跟着队长走、集火队长正在打的怪（一起打一只，不各自跑各自的）。
        /// 队长自己照常行动，但队友落得太远会停下等一等。未组队或自己有其它意图(跟人/赶路)时不接管。
        /// </summary>
        private bool ReflexTeam(PlayerObject player)
        {
            if (player.Team == null)
                return false;
            // 自己有明确意图时以意图为先(跟某人/赶路去另一张图)，队伍逻辑不抢权
            if (FollowTargetId != 0 || MoveTarget != null || _pendingGate != null)
                return false;

            var leaderChar = player.Team.队长数据;
            var isLeader = leaderChar == player.CharacterData;
            PlayerObject leader = null;
            if (!isLeader)
                leader = leaderChar.ActiveConnection?.Player;

            if (isLeader)
            {
                // 队长:队友最远超过 12 格就原地等一下，别把人丢了
                foreach (var member in player.Team.Members)
                {
                    var mate = member.ActiveConnection?.Player;
                    if (mate == null || mate == player)
                        continue;
                    if (mate.CurrentMap != player.CurrentMap || player.GetDistance(mate) > 12)
                        return true; // 占住本 tick，不进入个人挂机（站定等人）
                }
                return false; // 队叏都在身边，队长正常挂机
            }

            if (leader == null || leader.Died || leader.CurrentMap != player.CurrentMap)
                return false; // 队长不在线/异图，回退为个人行动

            // 队员:队长是 bot 且正在打怪、够近 → 集火同一只(传奇的一起打)；真人队长拿不到意图，就纯跟随
            var leaderBrain = BotManager.FindBrain(leader.ObjectName);
            if (leaderBrain != null && leaderBrain.CombatTargetId != 0)
            {
                MapObject t;
                if (MapGatewayProcess.Objects.TryGetValue(leaderBrain.CombatTargetId, out t) && !t.Died && player.GetDistance(t) <= 12)
                {
                    CombatTargetId = t.ObjectId;
                    return false; // 下一 tick ReflexCombat 接手；本 tick 先往怪身上走
                }
            }

            // 没有共同目标:跟紧队长(超过 4 格就追)
            var distance = player.GetDistance(leader);
            if (distance > 4)
            {
                StepToward(player, leader.CurrentPosition);
                return true;
            }
            return true; // 在队长身边待命，不自己乱跑
        }

        /// <summary>赶路/跟随时清路:贴脸的敌对怪先打掉(它们打断移动冷却,不清就永远走不动),打完自动继续原路。</summary>
        private bool ReflexClearWay(PlayerObject player)
        {
            if (CombatTargetId != 0)
                return false;
            if (MoveTarget == null && FollowTargetId == 0 && !AutoGrind)
                return false; // 不在赶路,不用清

            foreach (var neighbor in player.Neighbors)
            {
                var monster = neighbor as MonsterObject;
                if (monster == null || monster.Died)
                    continue;
                if (player.GetDistance(monster) > 2)
                    continue;
                if (monster.GetRelationship(player) != GameObjectRelationship.Hostility)
                    continue;
                CombatTargetId = monster.ObjectId;
                return true;
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
                // 怪死亡后对象很快从全局表移除,TryGetValue 拿不到 —— 用最近攻击目标名兜底记账
                var killedName = target != null ? target.ObjectName : _lastCombatTargetName;
                if (killedName != null)
                {
                    _killsSinceLogin++;
                    BotLogger.Log(Definition.Name, "kill", killedName + " (累计" + _killsSinceLogin + ")");
                    _lastCombatTargetName = null;
                }
                CombatTargetId = 0;
                if (AutoGrind) return false;          // 挂机:下一轮 ReflexGrind 自动找新目标
                _nextThinkTime = MainProcess.CurrentTime; // 手动打完,让 LLM 决定下一步
                return false;
            }

            if (target.CurrentMap != player.CurrentMap)
            {
                BotLogger.Log(Definition.Name, "event", "攻击目标丢失(换图): " + target.ObjectName);
                CombatTargetId = 0;
                return false;
            }

            var distance = player.GetDistance(target);
            if (distance > BotManager.Config.Reflex.ChaseMaxDistance)
            {
                // 群殴环境下目标常被别人打死(TryGetValue 失败走 kill 分支),只有"追不上活的"才算真放弃
                BotLogger.Log(Definition.Name, "event", "追不上放弃(距离" + distance + "格): " + target.ObjectName);
                CombatTargetId = 0;
                return false;
            }

            AttackStep(player, target);
            return true;
        }

        /// <summary>挂机练级:像真人玩家一样,自动选附近最强的可打怪,打完一只换下一只。</summary>
        private bool ReflexGrind(PlayerObject player)
        {
            if (!AutoGrind || CombatTargetId != 0)
                return AutoGrind;

            MonsterObject best = null;
            var bestScore = 0;
            foreach (var neighbor in player.Neighbors)
            {
                var monster = neighbor as MonsterObject;
                if (monster == null || monster.Died)
                    continue;
                var distance = player.GetDistance(monster);
                if (distance > BotManager.Config.Reflex.ChaseMaxDistance)
                    continue;
                // 别去碰明显打不过的(怪物等级远高于自己)
                if (monster.CurrentLevel > player.CurrentLevel + 10)
                    continue;
                var score = Math.Max(1, (int)monster.CurrentLevel) * 10 - distance;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = monster;
                }
            }

            if (best == null)
            {
                // 挂机中但视野内没怪:先站几秒等刷新,久了就自己挪窝去最近的刷怪点
                if (_noMonsterSince == default(DateTime))
                    _noMonsterSince = MainProcess.CurrentTime;
                else if (MainProcess.CurrentTime > _noMonsterSince.AddSeconds(8.0) && MoveTarget == null)
                {
                    _noMonsterSince = MainProcess.CurrentTime;
                    if (MoveToNearestSpawn(player))
                        return true; // 正在挪窝
                    _grindMoves++;
                    if (_grindMoves >= 3)
                    {
                        // 挪了几次都没怪(或挪不动):别傻站,喊 LLM 来定夺(换图/查攻略/组队走)
                        _grindMoves = 0;
                        lock (ToolResults)
                            ToolResults.Add("这附近彻底没怪了,挪窝也没用 —— 查攻略看该去哪(check_guide)、看出口换图(goto_map)、或者跟人组队走,别在这干站着");
                        _nextThinkTime = MainProcess.CurrentTime;
                    }
                }
                return true;
            }

            _noMonsterSince = default(DateTime);
            _grindMoves = 0;
            CombatTargetId = best.ObjectId;
            return true;
        }

        private DateTime _noMonsterSince;
        private int _grindMoves;

        /// <summary>走向本图最近的刷怪点(离当前位置超过25格才算"挪窝"),走路时清路逻辑照常接管拦路的怪。</summary>
        private bool MoveToNearestSpawn(PlayerObject player)
        {
            MonsterSpawns nearest = null;
            var nearestDistance = int.MaxValue;
            foreach (var spawn in MonsterSpawns.DataSheet)
            {
                if (spawn.FromMapId != player.CurrentMap.MapId || spawn.Spawns == null || spawn.Spawns.Length == 0)
                    continue;
                // 怪太高级的点位不去(会被秒)
                var tooHard = false;
                foreach (var info in spawn.Spawns)
                {
                    Monsters template;
                    if (Monsters.DataSheet.TryGetValue(info.MonsterName, out template) && template.Level > player.CurrentLevel + 10)
                    {
                        tooHard = true;
                        break;
                    }
                }
                if (tooHard)
                    continue;

                var distance = Math.Max(Math.Abs(spawn.FromCoords.X - player.CurrentPosition.X), Math.Abs(spawn.FromCoords.Y - player.CurrentPosition.Y));
                if (distance >= 15 && distance < nearestDistance)
                {
                    nearestDistance = distance;
                    nearest = spawn;
                }
            }
            if (nearest == null)
                return false;

            // 走向刷怪点附近一个随机偏移格(都挤一个点会互相挡路)
            var target = new Point(
                nearest.FromCoords.X + MainProcess.RandomNumber.Next(-4, 5),
                nearest.FromCoords.Y + MainProcess.RandomNumber.Next(-4, 5));
            if (!player.CurrentMap.CanPass(target))
                target = nearest.FromCoords;
            RebuildPath(target);
            MoveTarget = target;
            MainProcess.AddSystemLog("[Bot] " + Definition.Name + " 附近没怪了,挪窝去 " + nearest.RegionName + "(" + target.X + "," + target.Y + ")");
            BotLogger.Log(Definition.Name, "event", "挪窝 → " + nearest.RegionName + "(" + target.X + "," + target.Y + ")");
            return true;
        }

        private void AttackStep(PlayerObject player, MapObject target)
        {
            _lastCombatTargetName = target.ObjectName;
            var distance = player.GetDistance(target);
            var skillId = GetAttackSkillId(player);
            var range = GetSkillRange(skillId);
            if (distance <= Math.Max(1, range))
            {
                _actionCounter = (byte)(_actionCounter + 1);
                var hpBefore = target.CurrentHP;
                player.UseSkill(skillId, _actionCounter, target.ObjectId, target.CurrentPosition);
                // TODO(v3调试): 验证攻击是否真的生效,定位"怪不掉血"
                MainProcess.AddSystemLog("[Bot调试] " + Definition.Name + " UseSkill id=" + skillId + " 目标=" + target.ObjectName + "(" + target.ObjectId + ") 距离=" + distance
                    + " 技能数=" + player.MainSkills表.Count + " 打前血=" + hpBefore + " 主手=" + (player.Equipment.TryGetValue(0, out var w) ? w.Name + "/" + w.NeedRace : "空"));
            }
            else
            {
                StepToward(player, target.CurrentPosition);
            }
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

        /// <summary>等着的传送门:goto_map 设定,人走到门格后自动跨图。</summary>
        public TeleportGates _pendingGate;
        /// <summary>A* 全局路径(远目的地时一次性算好,逐点走;卡住时重算)。</summary>
        private LinkedList<Point> _pathSteps;
        /// <summary>本轮目的地的路径是否已重算过一次(重算后仍卡才放弃)。</summary>
        private bool _pathRetried;
        private DateTime _lastRepathLog;

        /// <summary>设定"走到某扇门然后过图"的路线(打断当前意图)。</summary>
        public void SetGateRoute(TeleportGates gate)
        {
            _pendingGate = gate;
            FollowTargetId = 0;
            CombatTargetId = 0;
            AutoGrind = false;
            RebuildPath(gate.FromCoords);
            MoveTarget = gate.FromCoords;
        }

        /// <summary>为远目的地算一次 A* 路径;太远或不可达时退化为直走(局部绕行兜底)。</summary>
        public void RebuildPath(Point destination)
        {
            _pathSteps = null;
            _pathRetried = false;
            try
            {
                var path = BotPathfinder.FindPath(Player.CurrentMap, Player.CurrentPosition, destination);
                if (path != null)
                    _pathSteps = new LinkedList<Point>(path);
            }
            catch (Exception ex)
            {
                MainProcess.AddSystemLog("[Bot] " + Definition.Name + " 寻路失败(退化为直走): " + ex.Message);
            }
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
                _stuckCount = 0;
                _lastMoveDistance = int.MaxValue;

                // 走到的是传送门:跨图(真实过门,不是瞬移)
                if (_pendingGate != null && player.CurrentPosition == _pendingGate.FromCoords)
                {
                    var gate = _pendingGate;
                    _pendingGate = null;
                    player.玩家进入法阵(gate.TeleportGateNumber);
                    BotLogger.Log(Definition.Name, "event", "过图: " + gate.FromMapName + " → " + gate.ToMapName);
                    _nextThinkTime = MainProcess.CurrentTime; // 到了新地图,重新看看环境
                }
                return;
            }
            StepToward(player, target);
        }

        /// <summary>连续多次没接近目标(死角/不可达/来回振荡):放弃当前目标并告诉 LLM 此路不通。</summary>
        private int _stuckCount;
        private int _lastMoveDistance = int.MaxValue;
        private readonly Queue<Point> _recentPositions = new Queue<Point>();
        private void CheckStuck(Point posBefore, Point posAfter, int targetDistance)
        {
            // 振荡检测:最近8次动作只在2~3个格子间打转 = 堵死了(哪怕位置在变、距离在波动)
            _recentPositions.Enqueue(posAfter);
            while (_recentPositions.Count > 8)
                _recentPositions.Dequeue();
            if (_recentPositions.Count >= 8 && _recentPositions.Distinct().Count() <= 3)
            {
                _recentPositions.Clear();
                _stuckCount = 0;
                _lastMoveDistance = int.MaxValue;
                var unreachable = MoveTarget.Value;
                var wasGate = _pendingGate != null ? _pendingGate.ToMapName : null;
                MoveTarget = null;
                _pathSteps = null;
                _pendingGate = null; // 门路线一起放弃,不留半状态
                _nextThinkTime = MainProcess.CurrentTime;
                lock (ToolResults)
                    ToolResults.Add(wasGate != null
                        ? "去" + wasGate + "的路来回绕了半天过不去(路上可能有怪/地形),要么先清了眼前的怪,要么换个地方"
                        : "move_to(" + unreachable.X + "," + unreachable.Y + ") 这边地形过不去,来回绕了半天,换个地方吧");
                return;
            }

            if (posBefore != posAfter && targetDistance < _lastMoveDistance)
            {
                _stuckCount = 0;
                _lastMoveDistance = targetDistance;
                return;
            }

            // 没动,或者动了但没更接近目标 —— 都算卡:先重算一次路径,重算过还卡才放弃
            _stuckCount++;
            _lastMoveDistance = targetDistance;
            if (_stuckCount >= 4)
            {
                _stuckCount = 0;
                if (!_pathRetried)
                {
                    _pathRetried = true;
                    RebuildPath(MoveTarget.Value); // 路径可能被占/过时,重算一条再试
                    if (MainProcess.CurrentTime > _lastRepathLog)
                    {
                        _lastRepathLog = MainProcess.CurrentTime.AddSeconds(10.0);
                        BotLogger.Log(Definition.Name, "event", "寻路重算(10秒内多条合并)");
                    }
                    return;
                }
                _pathRetried = false;
                _lastMoveDistance = int.MaxValue;
                var unreachable = MoveTarget.Value;
                MoveTarget = null;
                _pathSteps = null;
                _nextThinkTime = MainProcess.CurrentTime;
                lock (ToolResults)
                    ToolResults.Add("move_to(" + unreachable.X + "," + unreachable.Y + ") 走不过去,半路被堵死了,得换个目标");
            }
        }

        /// <summary>战斗/挂机时自动捡脚下掉落(真人手速);捡到装备记入长期记忆。</summary>
        private void ReflexLoot(PlayerObject player)
        {
            if (FollowTargetId != 0 && CombatTargetId == 0)
                return; // 跟人时别停下捡,先跟上;其余情况(挂机/战斗/闲逛)脚边有掉落就捡

            var cell = player.CurrentMap[player.CurrentPosition];
            if (cell == null) return;
            foreach (var obj in cell.ToList())
            {
                var ground = obj as ItemObject;
                if (ground == null)
                    continue;

                var equip = ground.物品模板 as EquipmentItem;
                if (equip != null && ground.物品模板.PersistType == PersistentItemType.装备)
                {
                    var info = BotSnapshot.DescribeEquip(equip);
                    Memory.RecordEpisode("捡到 " + equip.Name + (info != null ? "(" + info + ")" : ""), equip.NeedLevel >= 20 ? 4 : 2);
                    MemoryDirty = true;
                    BotLogger.Log(Definition.Name, "event", "捡到装备 " + equip.Name + (info != null ? "(" + info + ")" : ""));
                }
                player.玩家拾取物品(ground); // 内部含背包满/归属校验
            }
        }

        /// <summary>朝目标移动:优先沿 A* 路径(自动丢弃已越过/到达的点,防止跑步越点后回头),没路径时局部贪心+绕行。</summary>
        private void StepToward(PlayerObject player, Point target)
        {
            // TODO(v3调试): 移动失效定位 —— 记录每次尝试的状态与结果
            var posBefore = player.CurrentPosition;

            if (_pathSteps != null && _pathSteps.First != null)
            {
                // 丢弃已经踩上或已经越过的路径点(跑步2格常越过单格点,不丢就会回头补踩造成振荡)
                while (_pathSteps.First != null)
                {
                    var head = _pathSteps.First.Value;
                    if (player.CurrentPosition == head)
                    {
                        _pathSteps.RemoveFirst();
                        continue;
                    }
                    var second = _pathSteps.First.Next;
                    if (second != null && Chebyshev(player.CurrentPosition, head) >= Chebyshev(player.CurrentPosition, second.Value))
                    {
                        _pathSteps.RemoveFirst(); // head 已经比下一点更远,视为越过
                        continue;
                    }
                    break;
                }

                if (_pathSteps.First != null)
                {
                    // 朝"下一点"走;隔一格以上时用跑步,步进目标取更远的点,减少越点
                    var next = _pathSteps.First.Value;
                    var ahead = _pathSteps.First.Next != null ? _pathSteps.First.Next.Value : next;
                    var dist = Chebyshev(player.CurrentPosition, ahead); // 用前瞻点判距:路径点彼此紧邻,看 next 永远是1格就走不跑
                    var stepped = false;

                    if (dist >= 2 && player.CanRun())
                    {
                        var runDirection = ComputingClass.GetDirection(player.CurrentPosition, ahead);
                        var step1 = ComputingClass.前方坐标(player.CurrentPosition, runDirection, 1);
                        var step2 = ComputingClass.前方坐标(player.CurrentPosition, runDirection, 2);
                        if (player.CurrentMap.CanPass(step1) && player.CurrentMap.CanPass(step2))
                        {
                            player.玩家角色跑动(step2);
                            stepped = true;
                        }
                    }
                    if (!stepped && player.CanMove())
                    {
                        var direction = ComputingClass.GetDirection(player.CurrentPosition, next);
                        var front = ComputingClass.前方坐标(player.CurrentPosition, direction, 1);
                        if (player.CurrentMap.CanPass(front))
                        {
                            player.OnWalk(front);
                            stepped = true;
                        }
                    }
                    if (!stepped && player.CanRun())
                    {
                        // 直行的点临时不可走:跑向再下一点绕一下
                        var runDirection = ComputingClass.GetDirection(player.CurrentPosition, ahead);
                        for (var i = 0; i < 8 && !stepped; i++)
                        {
                            var step1 = ComputingClass.前方坐标(player.CurrentPosition, runDirection, 1);
                            if (player.CurrentMap.CanPass(step1))
                            {
                                player.OnWalk(step1);
                                stepped = true;
                            }
                            runDirection = ComputingClass.TurnAround(runDirection, 1);
                        }
                    }
                    LogStep("路径", posBefore, player.CurrentPosition, target);
                    return;
                }
            }

            if (player.CanRun())
            {
                var runDirection = ComputingClass.GetDirection(player.CurrentPosition, target);
                for (var i = 0; i < 8; i++)
                {
                    var step1 = ComputingClass.前方坐标(player.CurrentPosition, runDirection, 1);
                    var step2 = ComputingClass.前方坐标(player.CurrentPosition, runDirection, 2);
                    if (player.CurrentMap.CanPass(step1) && player.CurrentMap.CanPass(step2))
                    {
                        player.玩家角色跑动(step2);
                        LogStep("跑", posBefore, player.CurrentPosition, target);
                        return;
                    }
                    if (player.CurrentMap.CanPass(step1))
                    {
                        player.OnWalk(step1); // 只能走一步的窄路,走过去
                        LogStep("走", posBefore, player.CurrentPosition, target);
                        return;
                    }
                    runDirection = ComputingClass.TurnAround(runDirection, MainProcess.RandomNumber.Next(2) == 0 ? -1 : 1);
                }
                LogStep("堵死", posBefore, player.CurrentPosition, target);
                return;
            }

            if (!player.CanMove())
            {
                LogStep("冷却中CanMove=false", posBefore, player.CurrentPosition, target);
                return;
            }

            var direction2 = ComputingClass.GetDirection(player.CurrentPosition, target);
            for (var i = 0; i < 8; i++)
            {
                var front = ComputingClass.前方坐标(player.CurrentPosition, direction2, 1);
                if (player.CurrentMap.CanPass(front))
                {
                    player.OnWalk(front);
                    LogStep("走", posBefore, player.CurrentPosition, target);
                    return;
                }
                direction2 = ComputingClass.TurnAround(direction2, MainProcess.RandomNumber.Next(2) == 0 ? -1 : 1);
            }
        }

        private static int Chebyshev(Point a, Point b)
        {
            return Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
        }

        private DateTime _lastStepLog;
        private string _lastStepWhat;
        private void LogStep(string what, Point before, Point after, Point target)
        {
            // 同一种移动状态只打一条(冷却10秒),免得"堵死"刷屏
            if (MainProcess.CurrentTime < _lastStepLog && what == _lastStepWhat)
                return;
            if (what != _lastStepWhat || MainProcess.CurrentTime >= _lastStepLog)
            {
                _lastStepLog = MainProcess.CurrentTime.AddSeconds(what == "堵死" ? 10.0 : 2.0);
                _lastStepWhat = what;
                MainProcess.AddSystemLog("[Bot调试] " + Definition.Name + " 移动" + what + " (" + before.X + "," + before.Y + ")->(" + after.X + "," + after.Y + ") 目标(" + target.X + "," + target.Y + ")");
            }
        }

        // ===================== 技能辅助 =====================

        /// <summary>给工具层用的单次施法(带动作计数,与反射层共用节奏)。</summary>
        public void UseSkillOnce(ushort skillId, MapObject target)
        {
            if (Player == null || target == null)
                return;
            _actionCounter = (byte)(_actionCounter + 1);
            Player.UseSkill(skillId, _actionCounter, target.ObjectId, target.CurrentPosition);
        }

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
