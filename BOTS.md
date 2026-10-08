# LLM 陪玩机器人插件

在服务端内嵌由 LLM 驱动的"机器人玩家":它们是地图上真实可见的角色,能聊天、跟随、打怪、喝药、自动复活,由大模型做高层决策(去哪、打谁、说什么),内置反射层做低层操作(走位、喝药、还击),LLM 不可用时机器人退化为纯反射模式继续陪玩。

## 快速开始

1. 编译运行 GameServer,首次启动会在程序目录生成 `BotConfig.json`(默认模板)。
2. 编辑 `BotConfig.json`,填入 LLM 配置(OpenAI 兼容接口,智谱/DeepSeek/OpenAI/Ollama 均可):

```json
{
  "Enabled": true,
  "Llm": {
    "BaseUrl": "https://open.bigmodel.cn/api/paas/v4",
    "ApiKey": "你的API Key",
    "Model": "glm-4.6"
  },
  "Bots": [
    {
      "Name": "陪玩小蜜",
      "Race": 0,
      "Gender": 1,
      "Level": 35,
      "MapId": 142,
      "AutoStart": true,
      "Persona": "你是传奇大陆的一名热心陪玩,性格活泼、爱聊天。"
    }
  ]
}
```

3. 重启服务器,`AutoStart: true` 的机器人自动上线;也可以游戏内聊天框输入命令召唤。

## 游戏内命令(@bot,普通玩家可用)

| 命令 | 作用 |
|---|---|
| `@bot summon 陪玩小蜜` | 召唤机器人到自己身边并跟随你(名字可省略,取配置第一个) |
| `@bot dismiss 陪玩小蜜` | 让机器人下线(名字省略 = 全部下线) |
| `@bot list` | 查看在线机器人状态(等级/血量/地图/坐标) |

机器人对话方式:
- **附近喊话**:在它 15 格内说话并带上它的名字,它会回应;
- **私聊**:直接私聊它,它会私聊回复。

## 配置说明(BotConfig.json)

| 字段 | 说明 |
|---|---|
| `Enabled` | 插件总开关 |
| `Llm.BaseUrl / ApiKey / Model / Temperature / TimeoutSeconds / MaxTokens` | OpenAI 兼容接口参数。Ollama 本地模型填 `http://localhost:11434/v1`,ApiKey 随意填非空 |
| `ThinkIntervalMs` | LLM 思考间隔,默认 5000(被私聊/点名会立即触发) |
| `MaxHistoryTurns` / `MaxChatMemory` | 对话历史与聊天记忆条数(控制 token 消耗) |
| `Reflex.*` | 反射层参数:自动喝血线/蓝线、跟随距离、动作频率、追击上限、药水物品编号、上线赠药数量 |
| `Bots[]` | 机器人定义:`Name` 名字(全局唯一,勿与真实玩家重名)、`Race` 职业(0战士 1法师 2刺客 3弓手 4道士 5龙枪)、`Gender` 性别(1男 2女)、`Hair/HairColor/Face` 外观、`Level` 初始等级(仅首次创建生效,之后随打怪成长)、`MapId` 默认地图、`AutoStart` 随服启动、`Persona` 人设提示词 |

## 架构与线程模型

```
LLM API(线程池,纯 IO)              主循环线程(唯一游戏状态修改者)
      ↑ BotSnapshot(不可变观察)              ↑ ConcurrentQueue<Action>(仿 GMCommand 模式)
BotBrain.ThinkAsync() ──tool_calls──→ BotManager.ActionQueue ──→ PlayerObject.OnWalk / UseSkill / 玩家发送广播
      ↑ 聊天记忆/意图状态                     反射层:喝药/跟随走位/自动还击/自动复活(不经过 LLM)
```

- **机器人 = 真实 PlayerObject + BotConnection(哑连接)**。`BotConnection` 覆写发包为 no-op,因此服务端所有 `ActiveConnection.SendPacket` 调用对机器人天然安全;机器人不进 `NetworkServiceGateway.Connections`,不会被网络层处理。
- **并发安全**:游戏逻辑单线程(MainProcess.MainLoop),仓库无锁设计。LLM 调用完全异步,完成后把动作投入 `ConcurrentQueue`,由主循环逐个执行 —— 与现有 GMCommand 队列同模式。BotManager.Process 自带异常兜底,任何机器人错误都不会打挂主循环(主循环异常=停服)。
- **数据持久化**:机器人角色挂在保留账号 `BOT_<名字>` 下,随服务端 Data.db 每 60 秒正常存盘,等级/背包跨重启保留。`@bot dismiss` 正常走玩家下线流程。

## 暴露给 LLM 的工具(function calling)

`say`(附近喊话)、`whisper`(私聊)、`follow_player`(持续跟随)、`goto_player`(走近后停下)、`move_to`(走到坐标)、`attack`(攻击怪物)、`use_potion`(喝药)、`stop_follow`、`stop`(取消全部意图)。

每次思考时 LLM 收到一份观察快照(JSON):自身状态、视野内玩家/怪物(含距离方位)、最近聊天、当前意图进度、背包摘要,然后通过工具调用表达决策;只回文本不调工具时,若正被人搭话则自动转为喊话。

## 二次开发入口

所有代码在 `GameServer/Bots/`:`BotManager`(生命周期/队列)、`BotBrain`(思考循环/反射层)、`BotToolCatalog`(工具定义与执行)、`BotSnapshot`(观察构建)、`LlmClient`(OpenAI 兼容客户端)、`BotConfig`(配置)、`BotConnection`(哑连接)。加新工具只需在 `BotToolCatalog.Definitions` 加定义、`Execute` 加分支。

对现有代码的侵入仅 3 处(约 19 行):`SConnection.cs`(去 sealed + 发包虚方法 + 受保护构造)、`MainProcess.cs`(主循环接入 Initialize/Process)、`PlayerObject.cs`(附近/私聊两个聊天挂钩)。
