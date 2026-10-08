using System.Linq;
using GameServer.Bots;
using GameServer.Maps;
using Models.Enums;

namespace GameServer.PlayerCommands
{
    /// <summary>
    /// 陪玩机器人管理命令:
    /// @bot summon [名字]  召唤机器人到自己身边(不填名字则取配置里的第一个)
    /// @bot dismiss [名字] 让机器人下线(不填名字则全部下线)
    /// @bot list            查看机器人列表与状态
    /// </summary>
    public class PlayerCommandBot : PlayerCommand
    {
        public override GameMasterLevel RequiredGMLevel => GameMasterLevel.Player;

        [Field(Position = 0)]
        public string Action;

        [Field(Position = 1, IsOptional = true)]
        public string Name;

        public override void Execute()
        {
            if (!BotManager.Enabled)
            {
                Player.SendMessage("机器人插件未启用 (BotConfig.json)");
                return;
            }

            var action = (Action ?? "").ToLowerInvariant();
            switch (action)
            {
                case "summon":
                case "召唤":
                {
                    var definition = FindDefinition(Name);
                    if (definition == null)
                    {
                        Player.SendMessage("机器人 [" + (Name ?? "?") + "] 不在配置文件里");
                        return;
                    }
                    Player.SendMessage(BotManager.Spawn(definition, Player));
                    return;
                }
                case "dismiss":
                case "遣散":
                {
                    if (string.IsNullOrEmpty(Name))
                    {
                        var count = 0;
                        foreach (var def in BotManager.Config.BotDefinitions.ToArray())
                        {
                            if (BotManager.FindBrain(def.Name) != null)
                            {
                                BotManager.Dismiss(def.Name);
                                count++;
                            }
                        }
                        Player.SendMessage(count > 0 ? "已让 " + count + " 个机器人下线" : "当前没有在线机器人");
                        return;
                    }
                    Player.SendMessage(BotManager.Dismiss(Name));
                    return;
                }
                case "list":
                case "status":
                case "列表":
                case "状态":
                {
                    Player.SendMessage(BotManager.Status());
                    return;
                }
                default:
                {
                    Player.SendMessage("用法: @bot summon [名字] | @bot dismiss [名字] | @bot list");
                    return;
                }
            }
        }

        private BotDefinition FindDefinition(string name)
        {
            var definitions = BotManager.Config.BotDefinitions;
            if (definitions == null || definitions.Length == 0)
                return null;

            if (!string.IsNullOrEmpty(name))
                return definitions.FirstOrDefault(d => d.Name == name)
                    ?? definitions.FirstOrDefault(d => d.Name.Contains(name));

            return definitions.FirstOrDefault(d => d.AutoStart) ?? definitions[0];
        }
    }
}
