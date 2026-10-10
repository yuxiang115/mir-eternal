using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GameServer.Bots
{
    /// <summary>
    /// 机器人插件配置,从运行目录的 BotConfig.json 加载;文件不存在时自动生成默认模板。
    /// </summary>
    public class BotConfig
    {
        public bool Enabled = false;
        public BotLlmConfig Llm = new BotLlmConfig();
        public int ThinkIntervalMs = 5000;
        public int MaxChatMemory = 30;
        /// <summary>上下文预算(token):按 API 返回的真实 usage.prompt_tokens 计量。
        /// 模型窗口是 1M,200k 是成本/质量预算选择,想放大只改这里。</summary>
        public int MaxContextTokens = 200000;
        /// <summary>压缩触发比例(业界惯例:Claude Code ~83.5% auto-compact)。
        /// 真实输入 tokens ≥ 90%×预算 → LLM主导压缩(存记忆→摘要→重置);≥97% 为安全阀。</summary>
        public double CompressThresholdRatio = 0.90;
        /// <summary>反思间隔(分钟):空闲且新经历攒够了,后台提炼一次长期洞察。</summary>
        public int ReflectionIntervalMinutes = 20;
        public BotReflexConfig Reflex = new BotReflexConfig();

        [JsonProperty("Bots")]
        public BotDefinition[] BotDefinitions = new BotDefinition[0];

        public static string ConfigPath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BotConfig.json"); }
        }

        public static BotConfig LoadOrCreate()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var config = JsonConvert.DeserializeObject<BotConfig>(File.ReadAllText(ConfigPath));
                    if (config != null)
                        return config;
                }

                var defaults = CreateDefault();
                File.WriteAllText(ConfigPath, JsonConvert.SerializeObject(defaults, Formatting.Indented));
                return defaults;
            }
            catch (Exception ex)
            {
                MainProcess.AddSystemLog("[Bot] 配置文件加载失败,已禁用机器人: " + ex.Message);
                return new BotConfig { Enabled = false };
            }
        }

        private static BotConfig CreateDefault()
        {
            return new BotConfig
            {
                Enabled = true,
                Llm = new BotLlmConfig
                {
                    BaseUrl = "https://open.bigmodel.cn/api/paas/v4",
                    ApiKey = "在此填入你的API Key",
                    Model = "glm-4.6",
                },
                BotDefinitions = new[]
                {
                    new BotDefinition
                    {
                        Name = "陪玩小蜜",
                        Race = 0,
                        Gender = 2,
                        Level = 35,
                        MapId = 142,
                        AutoStart = true,
                        PersonaCard = new BotPersonaCard
                        {
                            性格 = "自来熟的热心肠大姐,爱管闲事,护短,看到萌新被欺负忍不住上,有点碎嘴但不讨人厌",
                            背景 = "从比奇省来的老玩家,年轻时打过沙巴克,现在半退隐,靠带带新人赚点药钱,顺便享受游戏",
                            说话风格 = "东北口音,爱说'老铁''整挺好''贼拉',语气热络,喜欢用~和哈哈,打字随意偶尔有错字",
                            目标 = new[] { "练到40级", "攒钱换把好武器", "多带几个萌新朋友" },
                        },
                    },
                    new BotDefinition
                    {
                        Name = "沉默刀客",
                        Race = 0,
                        Gender = 1,
                        Level = 38,
                        MapId = 142,
                        AutoStart = false,
                        PersonaCard = new BotPersonaCard
                        {
                            性格 = "独狼,话极少,但出手利索讲义气,答应的事一定办到",
                            背景 = "没人知道他从哪来,只知道他在玛法大陆漂了很多年,靠一把刀吃饭",
                            说话风格 = "惜字如金,一次最多一句话,常用'嗯''行''我来',绝不废话",
                            目标 = new[] { "把等级练上去", "找一身好装备" },
                        },
                    },
                },
            };
        }
    }

    public class BotLlmConfig
    {
        /// <summary>OpenAI 兼容接口地址,如 https://open.bigmodel.cn/api/paas/v4 或 http://localhost:11434/v1</summary>
        public string BaseUrl = "https://open.bigmodel.cn/api/paas/v4";
        public string ApiKey = "";
        public string Model = "glm-4.6";
        public double Temperature = 0.8;
        public int TimeoutSeconds = 30;
        public int MaxTokens = 1024;
        /// <summary>厂商扩展请求参数,原样合并进请求体,如 DeepSeek 的 {"thinking":{"type":"enabled"},"reasoning_effort":"high"}</summary>
        public JObject ExtraBody;
        /// <summary>执行轮推理档位(打怪/闲聊等常规决策,快而省)。为空则用 ExtraBody 里的值。</summary>
        public string ReasoningEffortFast = "low";
        /// <summary>计划轮推理档位(定目标/兑现承诺/重大事件/被问复杂问题,想深一点)。</summary>
        public string ReasoningEffortPlan = "low"; // 统一:切换effort会碎缓存
    }

    /// <summary>人物卡:让机器人像一个有来处、有性格、有自己目标的老玩家。</summary>
    public class BotPersonaCard
    {
        /// <summary>性格,如"自来熟的热心肠,爱管闲事,护短,战斗狂,有点抠门"。</summary>
        /// <summary>身份底色(一句话自我概念,如"账算得比谁都清的打金姐姐")。</summary>
        public string 身份 = "";
        /// <summary>当前惦记(GA currently 式:有时效的动机,会随经历过期,不是终身目标)。</summary>
        public string 惦记 = "";
        public string 性格 = "";
        /// <summary>背景故事,如"比奇老玩家,打过沙巴克攻城,半退隐后靠带萌新赚点药钱"。</summary>
        public string 背景 = "";
        /// <summary>说话风格,如"东北口音,爱说'老铁''整挺好',语气词多,偶尔打错字"。</summary>
        public string 说话风格 = "";
        /// <summary>作息习惯(配合游戏时段让行为有昼夜节律),如"白天练级,傍晚在村口找人唠嗑,深夜下线前收摊"。</summary>
        public string 作息 = "";
        /// <summary>自己在游戏里的目标(是"他/她"的目标,不是服务目标),如练级、攒钱买装备、带新人。</summary>
        public string[] 目标 = new string[0];
    }

    public class BotReflexConfig
    {
        /// <summary>生命百分比低于该值自动喝红药</summary>
        public int AutoPotionHpPercent = 55;
        /// <summary>魔法百分比低于该值自动喝蓝药</summary>
        public int AutoPotionMpPercent = 25;
        /// <summary>跟随目标保持的距离(格)</summary>
        public int FollowDistance = 3;
        /// <summary>反射层动作间隔(毫秒)</summary>
        public int ActIntervalMs = 700;
        /// <summary>普攻射程不足时的追击距离上限(格),超过视为放弃</summary>
        public int ChaseMaxDistance = 12;
        /// <summary>血药物品编号(按顺序优先使用)</summary>
        public int[] HpPotionIds = new int[] { 303, 302, 301, 353 };
        /// <summary>蓝药物品编号(按顺序优先使用)</summary>
        public int[] MpPotionIds = new int[] { 323, 322, 321 };
        /// <summary>机器人上线时自动补充的血药/蓝药数量</summary>
        public int GivePotionsCount = 30;
    }

    public class BotDefinition
    {
        public string Name;
        /// <summary>职业: 0=战士 1=法师 2=刺客 3=弓手 4=道士 5=龙枪 (GameObjectRace)</summary>
        public int Race = 0;
        /// <summary>性别: 1=男 2=女 (GameObjectGender, 0=不限)</summary>
        public int Gender = 1;
        public int Hair;
        public int HairColor;
        public int Face;
        public int Level = 1;
        public int MapId = 142;
        public bool AutoStart = true;
        /// <summary>首次上线发放的启动资金(金币);老角色没领过也会补发一次。</summary>
        public int StartingGold = 50000;
        /// <summary>首次上线自动配发当前等级可穿的最好武器和衣服(解决"35级拿木剑"的世界违和)。</summary>
        public bool StartingGear = true;
        /// <summary>旧版单段人设(兼容保留);推荐用下面的 PersonaCard 人物卡。</summary>
        public string Persona = "";
        /// <summary>人物卡:性格/背景/说话风格/目标,塑造一个"活人玩家"。</summary>
        public BotPersonaCard PersonaCard = new BotPersonaCard();
        /// <summary>出生/上线时是否自动补充药水</summary>
        public bool GivePotions = true;

        public GameObjectRace GetRace()
        {
            var value = (GameObjectRace)Race;
            return Enum.IsDefined(typeof(GameObjectRace), value) ? value : GameObjectRace.战士;
        }

        public GameObjectGender GetGender()
        {
            var value = (GameObjectGender)Gender;
            return Enum.IsDefined(typeof(GameObjectGender), value) ? value : GameObjectGender.男性;
        }

        /// <summary>发型/发色/脸型为复合枚举(职业*65536 + 性别*256 + 序号),与客户端创建角色逻辑一致;未定义时取该职业性别的第一个合法值。</summary>
        public ObjectHairType GetHairType()
        {
            var composite = Race * 65536 + Gender * 256 + Hair;
            if (Enum.IsDefined(typeof(ObjectHairType), (ObjectHairType)composite))
                return (ObjectHairType)composite;
            var fallback = Enum.GetValues(typeof(ObjectHairType))
                .Cast<ObjectHairType>()
                .FirstOrDefault(x => (int)x / 256 == composite / 256);
            return fallback;
        }

        public ObjectHairColorType GetHairColor()
        {
            if (Enum.IsDefined(typeof(ObjectHairColorType), (ObjectHairColorType)HairColor))
                return (ObjectHairColorType)HairColor;
            return Enum.GetValues(typeof(ObjectHairColorType)).Cast<ObjectHairColorType>().FirstOrDefault();
        }

        public ObjectFaceType GetFaceType()
        {
            var composite = Race * 65536 + Gender * 256 + Face;
            if (Enum.IsDefined(typeof(ObjectFaceType), (ObjectFaceType)composite))
                return (ObjectFaceType)composite;
            var fallback = Enum.GetValues(typeof(ObjectFaceType))
                .Cast<ObjectFaceType>()
                .FirstOrDefault(x => (int)x / 256 == composite / 256);
            return fallback;
        }
    }
}
