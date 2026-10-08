using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

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
        public int MaxHistoryTurns = 16;
        public int MaxChatMemory = 30;
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
                        Gender = 1,
                        Hair = 0,
                        HairColor = 0,
                        Face = 0,
                        Level = 35,
                        MapId = 142,
                        AutoStart = true,
                        Persona = "你是传奇大陆的一名热心陪玩,性格活泼、爱聊天,会主动陪同玩家打怪升级。用简短口语化的中文说话,每次不超过两句话。",
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
        /// <summary>LLM 人设系统提示词</summary>
        public string Persona = "你是一名传奇游戏陪玩。";
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
