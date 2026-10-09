# mir-eternal LLM 玩家 Bot 完整架构文档

> **版本**:v2.0 — 2026-10-09(供 GPT review)
> **目标**:在传奇永恒(Mir 3D)私服中植入多个 LLM 驱动的"活人玩家"—— 他们有自己的人格、记忆、目标,像 2003 年网吧里的真实玩家一样生活:打怪练级、捡装备、学技能、组队、聊天、赚钱。真人玩家在游戏里分辨不出来。
> **文档结构**:§1-13 为**已实现(as-built)**;§14 为拟人化升级路线图(整合 GPT 实施方案 / Gemini 拟人指南 / DeepSeek V4 角色沉浸实践三方输入);§15 运维要点。

---

## 1. 系统总览

```
┌──────────────────────────────────────────────────────────────────┐
│                     GameServer (C# / .NET 9 / WinForms)         │
│                                                                  │
│  ┌─────────────┐    ┌──────────────────────────────────────┐   │
│  │  游戏主循环   │    │         BotManager (插件入口)         │   │
│  │  (单线程tick) │───→│  · 每 tick 调 BotManager.Process()   │   │
│  │             │    │  · ActionQueue:后台→主线程动作投递     │   │
│  └─────────────┘    └──────────┬───────────────────────────┘   │
│                                │                                 │
│         ┌──────────────────────┼──────────────────────┐         │
│         ▼                      ▼                      ▼         │
│  ┌─────────────┐    ┌─────────────┐    ┌─────────────────┐   │
│  │ BotBrain×12 │    │ BotSnapshot │    │ BotToolCatalog  │   │
│  │ 每个 bot    │    │ 世界观察    │    │ 26 个游戏工具    │   │
│  │ 一个实例    │    │ (差分事件)  │    │ (函数调用)      │   │
│  └──────┬──────┘    └─────────────┘    └─────────────────┘   │
│         │                                                        │
│         ▼                                                        │
│  ┌─────────────┐    ┌─────────────┐    ┌─────────────────┐   │
│  │  反射层      │    │  LLM 层     │    │  BotMemory      │   │
│  │  (确定性)   │    │  (决策层)   │    │  (持久化记忆)   │   │
│  └─────────────┘    └──────┬──────┘    └─────────────────┘   │
│         │            ┌─────▼───────┐                        │
│         │            │  LlmClient  │──→ DeepSeek V4.1 Flash │
│         │            │ (OpenAI兼容) │    (prefix cache)     │
│         │            └─────────────┘                        │
│  ┌──────▼──────────────────────────────────────────────────┐   │
│  │                    PlayerObject (游戏对象)               │   │
│  │  与真人玩家同一套 API: UseSkill/OnWalk/TransferItem/     │   │
│  │  玩家拾取物品/LearnSkill/玩家购买物品/...                │   │
│  └─────────────────────────────────────────────────────────┘   │
└──────────────────────────────────────────────────────────────────┘
```

### 核心设计原则

| 原则 | 实现 |
|---|---|
| **反射层执行,LLM 决策** | 打怪/喝药/移动/捡东西由确定性代码持续执行;LLM 只在事件发生时被唤醒做决策 |
| **线程安全** | 所有游戏状态操作只在主循环线程;LLM 后台调用结果通过 ConcurrentQueue 投回主线程 |
| **真人一致性** | Bot 使用与真人完全相同的 PlayerObject API(不是模拟),走同样的校验/广播/持久化路径 |
| **缓存优先** | 纯 append-only 消息 + 全局统一 reasoning_effort=low → 实测命中率 **94-98%**(2026-10-09 日志) |
| **策略归 agent** | 死亡反思/学技能/买药阈值调整/目标取舍等策略决策交给 LLM,程序只提供身体和工具 |

### 当前部署形态(12 人团)

- **6 职业 × 男女** = 12 个 bot:战士(血饮狂刀/乱世佳人)、法师(魔法小王子/水晶之恋)、刺客(夜色无声/毒玫瑰)、弓手(射雕英雄/精灵之吻)、龙枪(赵子龙在此/红缨)、道士(纵横四海/白娘子)
- **全员 1 级白手起家**:出生没收铭文装备,发最便宜武器(木剑/木弓/柴刀/木枪)+布衣,金币 2000(代码钳制上限 5000)——真实从零成长,不表演
- 每人独立 JSONL 行为日志 + 每 10 分钟全村快照 `_colony.md`

---

## 2. 三层架构:反射层 → 意图层 → LLM 决策层

### 2.1 反射层(Reflex,确定性,每 tick 执行)

```
ProcessReflex() 调用链(优先级从高到低):
├── MaintainMemory()     → 记忆落盘/承诺到期检测
├── ReflexDead()         → 死亡→停机反思(唤醒LLM)
├── ReflexPotion()       → 动态喝药(怪越强阈值越高)
├── ReflexFleeIfDying()  → HP<25%且没药→逃跑
├── ReflexCombat()       → 追击/施放技能/自动攻击
├── ReflexTeam()         → 队伍协同(跟队长/集火)
├── ReflexClearWay()     → 赶路被怪拦→先清路
├── ReflexGrind()        → 自动选怪(IsSafeTarget过滤)/没怪自动挪窝
├── ReflexGrindTravel()  → 挂机挪窝的独立位移步进(与LLM移动意图互不抢权)
├── ReflexFollow()       → 跟随目标(主人暂时消失原地等,不放弃)
├── ReflexMove()         → A*寻路/传送门跨图
├── ReflexLoot()         → 捡脚下掉落
├── ReflexIdleWander()   → 无所事事时偶尔溜达
└── CheckStuck()         → 振荡检测/路径重算
```

**关键:反射层是"身体"** — 它不需要 LLM 就能持续运转(打怪/喝药/跑路/捡东西),LLM 是"大脑",只在有事需要决策时才被唤醒。

**意图互斥(anti-flapping)**:AutoGrind 的 `_grindTarget` 与 LLM 的 `MoveTarget` 分离,挂机挪窝走独立的 `ReflexGrindTravel`,移动/跟随/战斗不会每 tick 互相覆盖控制权。

### 2.2 LLM 决策层(事件驱动)

```
ScheduleThink() — 每 tick 检查是否该思考:
│
├── 有新鲜事(聊天/事件/工具结果/私聊/组队邀请) → 立刻思考
├── 死亡/升级/承诺到期                        → 立刻思考
├── 意图执行中(挂机/战斗/跟随)               → 45~90s 巡检
├── 身边有人但没事件                          → 30~60s 社交直觉
└── 完全空闲(没人没意图没新闻)               → 60~120s 休眠

思考完成后,无意义的空轮(没说话/没动作/没事件)不入历史 → 回滚,不污染 context
```

**间隔设计理由(实测驱动)**:DeepSeek prefix cache TTL ≈ 120s。间隔全部压在 TTL 内,cache hit 时 input ≈ 免费——**频繁短思考比长间隔+冷启动便宜**(一次 25k miss 够付 50 次 500 miss 的巡检)。这是从"心跳保温"方案迭代来的:与其到点发假观察保温,不如直接让 agent 高频看真观察,反正命中后的增量成本可忽略。

### 2.3 推理档位:全局统一 low(已废弃分级)

```csharp
ReasoningEffortFast = "low";
ReasoningEffortPlan = "low"; // 统一:切换effort会碎缓存
```

**教训**:曾设计 fast=low / plan=medium 双档,实测同一会话内切换 effort 会改变请求参数 → 前缀缓存哈希失效 → 命中率从 9x% 跌回 ~50%。DeepSeek V4.1 的 effort 映射 low/medium/high 并非线性增益,统一 low 后命中率稳定 94-98%。**任何按事件动态切换推理档位的提议都应先过缓存这一关。**

---

## 3. 消息架构(纯 Append-Only,为 Prefix Cache 优化)

### 3.1 消息结构

```
messages[0] = system: 公共头(12bot字节一致,共享缓存) + 个人人格(静态)
messages[1] = user: 首次进图地图信息(刷怪点/出口,一次写入)
messages[2..] = 纯 append-only 对话流:
  · [观察] 每轮动态状态(血蓝/位置/事件/聊天/怪/掉落)
  · assistant(带 tool_calls 原始 JArray) + tool(执行结果,按 tool_call_id 配对)
  · 空轮不入历史(没说话/没动作/没事件 → 回滚)
```

### 3.2 前缀缓存实测

| 消息 | 变化频率 | 缓存影响 |
|---|---|---|
| [0] system | 永不变 | 跨 bot 共享 |
| [1] 地图信息 | 每张图一次 | 写入后永不动 |
| [2..] 对话 | 纯 append | 只有最新一条 miss |

**实测(2026-10-09 11:48,System.log)**:`毒玫瑰 input=11025 hit=10368 miss=657 (94%)`、`红缨 input=10414 hit=10240 miss=174 (98%)`。miss 主要来自:TTL 过期后的自然冷启动、压缩后的前缀重建(预期内)、以及模型输出导致的KV边界漂移。

**已知风险(待验证)**:当前 `LlmMessage` 只保留 `Content + ToolCalls(JArray) + ToolCallId`,**不回传 assistant 历史的 `reasoning_content`**(只写日志)。DeepSeek 文档称 thinking+tools 多轮要求完整回传 reasoning_content 否则可能 400——当前 12 bot × 数百轮未见 400,但需要一个 API 集成测试明确边界(见 §14 Phase A)。

### 3.3 压缩流程(LLM 主导,三步)

```
context 接近 120k tokens(MaxContext 200k)
    ↓
第1步: LLM 读完整历史 + 带 remember 工具
       "把值得记的存入记忆(关系/里程碑/知识/约定)"
    ↓ LLM 调 remember(...) → 落盘(不碰消息)
第2步: LLM 输出浓缩摘要
    ↓
第3步: 清掉 [1..],新[1] = 记忆(含刚存的) + 摘要
       继续纯 append(这是一次必要的 cache reset)
```

---

## 4. 记忆系统

### 4.1 长期记忆(BotMemory/<角色名>.json,跨重启)

```json
{
  "Milestones": ["龙枪8级", "捡到银蛇剑"],
  "Episodes": ["钉耙猫6级掉铁剑", "和水晶之恋组队打羊"],
  "Reflections": ["边界村的羊没收益,得去比奇"],
  "People": {"水晶之恋": "33级法师,答应带她打沃玛"},
  "Relationships": {"水晶之恋": {"Relation":"朋友","Affinity":60}},
  "Goals": ["冲40级", "攒钱买裁决"],
  "Commitments": [{"Description":"明晚8点组队","DueAt":"...","Status":"pending"}],
  "Knowledge": ["羊2级20血", "金创药回50血", "村北有铁匠"],
  "SelfNote": "最近在带萌新"
}
```

### 4.2 记忆生命周期

```
写入途径:
  · LLM 调 remember 工具(运行时) → 只落盘,不碰消息(缓存安全)
  · 压缩时 LLM 主动存(三步流程第1步)
  · 服务器自动事件(升级/死亡/捡装备/金币变动)
  · 反思:攒够 5 条未消化经历且距上次 ≥5min → LLM 提炼"悟出来的道理"

读取途径:
  · 启动时: ReconcileMemory 对账(见 4.3)
  · 压缩后: 新[1] = Memory.BuildPromptSection() + summary
  · 运行时: 观察里的相关人名/事件自然关联

检索策略:
  · 里程碑: 全量注入(数量少)
  · 事件流: 重要度×2 + 时间新鲜度 + 相关人名加分
  · 人物: 出现在视野里的优先
  · 总量控制: ≤3000 字符
```

### 4.3 记忆对账(ReconcileMemory,防"等级打架")

数据库重置后角色回到 1 级,旧记忆却写着 8 级——曾导致 LLM 每轮花大量推理解释"为什么我又变1级了"。现启动时对账:**等级倒退 = 服务器重置 → 清除等级类里程碑(击杀数/等级成就),保留人际关系/知识/反思(这些跨世界有效)**。这是 GPT 方案中 WorldEpoch/CharacterGeneration 概念的最小实现。

---

## 5. 工具集(26 个游戏函数)

### 5.1 说话类
| 工具 | 说明 |
|---|---|
| `say(text)` | 附近频道喊话(~20格),15秒节流 |
| `whisper(player, text)` | 私聊(全服),bot↔bot互通 |
| `shout(text)` | 全服大喇叭,1000金币/次 |

### 5.2 移动类
| 工具 | 说明 |
|---|---|
| `move_to(x, y)` | A*寻路走坐标 |
| `follow_player(player)` | 持续跟随(保持3格) |
| `goto_player(player)` | 走到目标身边停下 |
| `goto_map(map)` | 走到传送门自动跨图 |

### 5.3 战斗类
| 工具 | 说明 |
|---|---|
| `attack(target_id)` | 攻击指定怪(反射层自动追击/施法) |
| `grind_nearby(start)` | 自动挂机练级(选怪/追击/捡装备/挪窝) |
| `use_skill(skill_id, target_id)` | 手动施放指定技能 |
| `stop` | 取消所有意图 |

### 5.4 物品类
| 工具 | 说明 |
|---|---|
| `check_inventory()` | 查看背包+全身装备(含属性) |
| `equip_item(name)` | 穿装备(自动放对部位) |
| `pickup(radius)` | 捡周围2格内物品 |
| `drop_item(name)` | 丢地上(传奇式交易) |
| `give_gold(player, amount)` | 转账 |
| `buy_item(name, count)` | 商店购买(红药/蓝药/技能书) |

### 5.5 技能类
| 工具 | 说明 |
|---|---|
| `check_skills()` | 查已学技能(编号/名字/射程) |
| `learn_skill(name)` | 读技能书学新技能(真实校验:等级/职业/书是否存在) |

### 5.6 社交类
| 工具 | 说明 |
|---|---|
| `team_invite(player)` | 邀请组队(双方无队自动建队) |
| `team_accept()` / `team_reject()` | 接受/拒绝邀请 |
| `leave_team()` | 退队 |

### 5.7 认知/自我管理类
| 工具 | 说明 |
|---|---|
| `check_guide(level)` | 查攻略(KB七职业路线 + 本服真实数据) |
| `set_goal(text)` | 立长期目标(观察常驻) |
| `drop_goal(text)` | 放弃目标 |
| `remember(text)` | 存记忆('关于X: 印象'/'里程碑'/'知识') |
| `make_plan(what, when)` | 登记带时间的约定(到点唤醒) |
| `plan_done(what)` | 完成/取消约定 |
| `update_relation(player, relation, affinity)` | 更新关系认知 |
| `list_quests()` | 查进行中任务 |
| `revive()` | 复活(死了才能用,agent 想好策略再复活) |

---

## 6. 观察系统(每轮 LLM 看到的世界)

### 6.1 观察结构(BuildObservation)

```
[现在] 10-09(周四) 09:15 白天 红缨 龙枪7级 血45/45 蓝20/20
       金币340 心情:战斗中

[刚发生] 玩家[水晶之恋]出现在东2格(认识的)
[聊天] 水晶之恋: 红缨姐你多少级了?
[上轮动作结果] say: 已喊话

[周围玩家] 水晶之恋(33级,东2格,挂机练级,你记得她:法师,答应带打沃玛)
[附近的怪] 钉耙猫(6级,东3格) 羊(2级,南5格)
[地上掉落] 铁剑x1(2格) 金创药x2(1格)
[可以学新技能了] 基础射击(7级)

[正在打] 钉耙猫(血40%)
[你的目标] 冲到10级,带水晶之恋打沃玛
[待办] 和水晶之恋组队打祖玛(10-09 20:00,还有11小时)

[你的近况] 上线以来击杀37只 7级龙枪 金币340 主手铁剑
[药尽警报] 红药只剩2瓶!(触发买药决策)
[该干嘛?]
```

### 6.2 观察设计原则

- **只报动态**:每轮变化的东西(血蓝/位置/事件/聊天/怪/掉落)
- **静态信息单独 append**:地图信息(刷怪点/出口)进图时一次性 append,之后被缓存
- **800 字截断**:LLM 不需要全部细节,足够决策即可(高优先级事实如药尽警报不可被截掉)
- **差分事件**:只报变化(玩家出现/走远/血量骤降),不报持续状态

---

## 7. 人物卡系统(BotConfig.json)

```json
{
  "Name": "红缨",
  "Race": 5, "Gender": 2, "Level": 1, "MapId": 142,
  "AutoStart": true,
  "StartingGold": 2000, "StartingGear": true,
  "PersonaCard": {
    "性格": "公司主管,效率至上,讨厌磨蹭",
    "背景": "白天开会,晚上清怪,打金攻略做成Excel的女人",
    "说话风格": "简短利落,'效率''跟上''别磨蹭'",
    "作息": "工作日晚9-11点,周末上午",
    "目标": ["打金效率翻倍", "带新人出师"]
  }
}
```

### 7.1 等级阶段感(LevelStage)

| 等级 | 系统告诉他的自我认知 |
|---|---|
| ≤5 | 萌新:穷/没见过世面/问东问西/不许吹牛 |
| ≤15 | 小号:有点家底,多听少吹 |
| ≤25 | 中手:能带更新的新人,别口气太大 |
| ≤35 | 老玩家:见多识足,但说有的必须真有 |
| 40+ | 元老:全服有名,越大佬越不用吹 |

### 7.2 共享文化头(CommonPromptHead)

所有 bot 的 system 开头完全一致(字节级),包含:
- 2003 网吧传奇称谓黑话:GG/MM/大虾/菜鸟/++++/让让/爆了/躺了
- 当年网络用语:偶/8错/表/稀饭/5555/886/顶/寒/PF/BT/灌水
- 装备行情:战士盼裁决/法师盼骨玉/道士盼银蛇
- 事实锚定规则:说等级/装备/金币必须用观察里的真实数据
- 禁 AI 腔条款:不许"作为AI/我很乐意帮助你/让我分析一下"

### 7.3 拟人化现状自评(对照 Gemini 指南)

| Gemini 建议 | 现状 |
|---|---|
| 具体人设(年龄/职业/性格/经历) | ✅ PersonaCard 全部具体到职业背景 |
| 口语化/短句/少列表腔 | ✅ 说话风格字段+文化头;❌ 未显式禁"第一第二第三"式列表 |
| 情绪波动 | ⚠️ 有心情字段但由规则维护;LLM 文本里已有情绪,未见系统化 Mood |
| 人性弱点(知识盲区/傲娇/沮丧) | ⚠️ LevelStage 约束吹牛;缺点未进人设卡 |
| 对话规则模板化 | ✅ 文化头即对话规则;待补"允许不回复" |

---

## 8. 社交系统

### 8.1 聊天分发

```
真人玩家发言 → PlayerObject.玩家发送广播 → BotManager.OnNearbyChat
                                              ↓ (同图+距离≤15格)
                                         RecordChat → 唤醒思考
Bot 发言 → 玩家发送广播 → 同上 + 广播给周围玩家
Bot 私聊 bot → BotToolCatalog.Whisper → 直接 RecordChat(投递到对方大脑)
队伍频道 → 玩家发送消息(team分支) → BotManager.OnTeamChat → 同队 bot RecordChat
组队邀请 → 客户端包516路径钩子(非521) → OnTeamInviteToBot → 唤醒决策
```

### 8.2 "必须回应"机制(三重防护,as-built)

被直接搭话(私聊或点名)但 LLM 把话写进文本而不是调工具时:
1. **协议合规**:DeepSeek tool_calls 官方协议,assistant 消息原样回传 ToolCalls JArray,tool 消息按 tool_call_id 配对——从根上让模型学会"说话必须调工具"
2. **强制重试**:没调 say → 追加强指令重试一次
3. **兜底代发**:重试仍失败 → 服务器从文本抠出那句替它发(salvage)

> **计划移除第 3 步**(见 §14 Phase A):salvage 是权宜之计,"从自然语言文本解析伪动作"违反工具协议纯净性;协议修复+重试生效后应下线,失败改走"保留未读+超时确定性短回复"。

### 8.3 队伍协同(反射层自动,不需要 LLM)

| 角色 | 行为 |
|---|---|
| 队员 | 跟紧队长(>4格自动追),集火队长打的怪 |
| 队长 | 队友落下>12格原地等 |

**CharId 铁律**:组队 API 一律按 CharId 查人(角色唯一键),绝不用 ObjectId(会话级对象ID,重连即变)——曾因混用导致"组队后互相看不见"。

### 8.4 关系系统

```json
{"水晶之恋": {"Relation": "朋友", "Affinity": 60, "Note": "答应带打沃玛"}}
```
- LLM 通过 `update_relation` 更新(必须基于真实交互)
- 观察里显示: `[周围玩家] 水晶之恋(...,你记得她:法师,答应带打沃玛)`

---

## 9. 战斗生存系统(反射层)

```
选怪 → IsSafeTarget() 统一安全检查(全部4个攻击入口共用):
       怪血 > 自己5倍? 跳过
       怪等级 > 自己+10? 跳过
       → 选最近的

战斗中 → 怪等级≥自己+3 → 血75%就喝药
       → 怪等级≈自己   → 血65%就喝药
       → 弱怪         → 血55%就喝药

血<25%且没药 → 停止战斗,朝反方向跑8格

死亡 → 停掉一切(挂机/战斗/跟随/移动)     ← 策略权交给 agent
     → 唤醒 LLM:"你死了!想想为什么、下次怎么避免"
     → LLM 反思(可调 remember 存教训) → 调 revive 复活
     → 30秒超时兜底:复活但不开挂机
```

**调试日志**:每次施放记录 `[Bot调试] 红缨 施放[龙枪普攻-0-无铭文] → 羊 距离=1 打前血=14 打后血=14 主手=木枪`——技能名从铭文表按 Index(SkillId×10+Id) 查出,打前/打后血量用于定位"怪不掉血"类问题。

---

## 10. 移动与寻路

### 10.1 A* 寻路(静态地形)

- 8向移动,对角防穿角
- 400k 节点预算(毫秒~百毫秒级)
- 只用静态地形(不查生物占位),挡路的怪由清路模式处理

### 10.2 路径跟随

- 跑步优先(一次2格)
- 自动丢弃已越过/到达的路径点(防跑步越点回头的振荡)
- 堵死检测:最近8次动作只在≤3格打转 → 放弃+反馈LLM

### 10.3 跨图

`goto_map` → 找当前图通往目标图的传送门 → A* 走到门 → 走到门格自动触发跨图

### 10.4 挂机挪窝

挂机中周围没怪超8秒 → A* 走向最近的合适刷怪点(全图刷怪点进图时已在观察里,按距离排序,带随机偏移防叠格)

---

## 11. 成本控制

| 维度 | 措施 | 效果 |
|---|---|---|
| 缓存命中率 | 纯append架构 + 统一effort=low | 15% → **94-98%**(实测) |
| 调用策略 | 频繁短思考替代长间隔(命中时input≈免费) | 空转浪费↓,响应速度↑ |
| 每次输入 | 观察800字截断;静态地图信息一次性append;空轮回滚不入史 | 每轮增量仅数百~千token |
| 每次输出 | MaxTokens 1024;统一low | 输出短,thinking不泛滥 |

**成本模型**:hit 部分 ≈ 1/10 价格。以毒玫瑰样本轮为例,input 11025 中 10368 命中 → 实际成本 ≈ (10368×0.1 + 657) × 单价,等效 1694 token 输入。12 bot 常驻在线的月成本可估算。

---

## 12. 行为日志(实验数据)

每人一个 JSONL(`Log/Bots/<名>.jsonl`),一行一条:

```json
{"t":"09:15:32","cat":"think","data":"继续砍羊,不吭声"}
{"t":"09:15:33","cat":"act","data":"say({\"text\":\"来了\"}) → 已喊话"}
{"t":"09:15:34","cat":"say","data":"[附近] 来了"}
{"t":"09:15:40","cat":"kill","data":"羊 (累计38)"}
{"t":"09:16:00","cat":"econ","data":"金币 +50 → 390"}
{"t":"09:16:05","cat":"event","data":"升级 → 8级"}
{"t":"09:16:10","cat":"event","data":"濒死逃跑!"}
{"t":"09:16:15","cat":"memory","data":"定目标: 冲10级"}
{"t":"09:16:20","cat":"commit","data":"登记: 和水晶之恋组队 @ 10-09 20:00"}
```

服务器侧 `System.log` 四件套:`[Bot思考]`(模型推理摘要) / `[Bot缓存] input/hit/miss (%)`(每轮遥测) / `[Bot调试]`(移动/施放明细,含技能名与打前打后血) / `[Bot]`(生命周期)。另有 `_colony.md` 每10分钟刷新(管理员观察台):全村人的等级/位置/状态/目标/关系/待办/近事。`Chat.log` 全量聊天落盘([General] 单条)。

---

## 13. 当前已知问题(Open)

1. **reasoning_content 未回传**(§3.2):thinking+tools 多轮未回传 assistant 历史的 reasoning_content,当前无 400 但需集成测试明确边界
2. **salvage 代发仍在**(§8.2):待协议修复验证充分后下线
3. **反思仍为定时触发**(≥5min+攒5条):GPT 方案建议改为重要事件触发,避免"每20分钟强行悟人生"
4. **NPC 对话树未接**(121个NPC对话,任务闭环缺最后一环)
5. **商店购买只支持药水和技能书**(装备购买未接)
6. **RidingBook 骑术书提示冗余**:所有 bot 被提示"可学"但骑乘对战斗无用
7. **反思质量**:产生的"道理"偶有重复(可加去重)
8. **低级怪目标锁定已修但需回归**:IsSafeTarget 统一4入口后,需持续观察是否还有"1级打1400血精英"类异常

---

## 14. 拟人化升级路线图(待实施,供 review)

> 三方输入:①GPT 实施方案(AttentionGate/IntentExecutor/SocialTransaction/BehaviorPersona/固定low/Phase A-D);②Gemini 拟人指南(具体人设/口语化/情绪与缺点/对话规则);③DeepSeek V4 角色沉浸实践(github deepseek_v4_rolepaly_instruct:思维链沉浸指令)。
> **核心共识:真实感 ≠ 话术。真实感 = 动机持续性 × 有限注意力 × 人物差异 × 社会关系 × 经历改变行为 × 可执行的游戏目标。**

### 14.1 GPT 方案模块 × 现状映射

| GPT 方案模块 | 现状 | 差距 |
|---|---|---|
| 固定 deepseek-flash + low + thinking | ✅ 已达成 | 无(曾踩 effort 切换碎缓存的坑,已回滚统一) |
| Append-only + 缓存优先 | ✅ 已达成 | 无(94-98% 实测) |
| StateReconciler(事实vs记忆) | 🟡 部分 | ReconcileMemory 只处理等级倒退;待扩展为 WorldEpoch/CharacterGeneration 代际制 |
| WakeScheduler(合并/去重/限流) | 🟡 部分 | ScheduleThink 已有分级间隔;缺事件合并(同话题多事件仍逐条唤醒)与注意力评分 |
| AttentionGate(值不值得理) | 🟡 部分 | 聊天距离≤15格过滤已有;缺"陌生泛喊默认忽略/打怪中聊天降权/重复话题合并"的可解释评分 |
| IntentExecutor(意图状态机) | 🟡 部分 | 意图互斥已解决(独立GrindTravel/意图锁);缺 PLAN→PREPARE→TRAVEL→ACT→VERIFY 完成证明与失败预算 |
| SocialTransaction(组队事务) | 🟡 部分 | 组队工具+反射协同+CharId铁律已有;缺 DISCOVERED→INVITE_PENDING→JOINED 服务器回执确认生命周期 |
| BehaviorPersona 三层(StableTraits/Drives/Mood) | 🟡 部分 | 静态 PersonaCard 有;缺数值化 traits/drives 与事件驱动的 Mood |
| Bot-Bot 链式唤醒去重 | 🔴 未做 | conversationId/replyBudget 机制缺 |
| 错峰上线/独立日程 | 🔴 未做 | 12人同秒上线同图;PersonaCard 作息字段未生效 |

### 14.2 分阶段计划

**Phase A:消除 AI 痕迹(P0,改动小收益大)**
- [ ] reasoning_content 回传验证:写 DeepSeek API 集成测试(thinking+tools 多轮),明确 400 边界;需要则 LlmMessage 增加 ReasoningContent 字段回传
- [ ] 下线 salvage 代发(§8.2 第3步),失败改"保留未读+超时确定性短回复"
- [ ] 合法 NO_ACTION:无动作轮不再视为失败;移除强迫输出
- [ ] RidingBook 提示过滤;重复检查背包/技能类触发去重
- 验收:无强行聊天;API 协议异常为零;命中率不恶化

**Phase B:行为一致性(P0/P1)**
- [ ] WorldEpoch/CharacterGeneration:角色重建生成新代际,旧等级进 history 不进当前
- [ ] IntentExecutor:grind 从"开关"升格为带 VERIFY 的任务状态(如"攒2000金":TRAVEL→ACT→`gold>=2000`→COMPLETE)
- [ ] AttentionGate 评分制:私聊+100/点名+70/邀请+75/熟人+25/重复话题-50/危险中-60;≥70 唤醒,30-70 进 SocialInbox,<30 忽略
- [ ] 事件合并:同 actor 同话题短时合并为一次唤醒
- 验收:低级号不再锁高级精英;移动意图不抖动;无动作调用占比下降

**Phase C:社交与人格(P1)**
- [ ] BehaviorPersona 数值化(traits/drives 影响选目标/组队意愿/风险容忍,不只是台词)
- [ ] SocialTransaction 组队闭环:邀请→服务器回执 JOINED 才更新状态;超时重试预算
- [ ] 关系细分:familiarity/trust/respect/goodwill/rivalry + 未解决事项("借了5瓶红药未还")
- [ ] Bot-Bot 群聊 replyBudget:单 bot 不连续回应同群消息
- [ ] Mood 事件驱动:死亡 frustration+0.2、好友赠药 goodwill+0.12,带 causeEventId,缓慢回归基线
- 验收:同一"++++"邀请,不同 bot 做出**实际不同选择**(加入/无视/反问效率),而非不同台词

**Phase D:长期成长(P2)**
- [ ] 错峰上下线(PersonaCard 作息生效)、活动偏好分化(打金/探路/带新人)
- [ ] 反思改重要事件触发(死亡/连续失败/关系突变),带去重
- [ ] NPC 任务闭环接入高级目标
- 验收:多日回放中人物态度/目标/熟人关系连续

### 14.3 提示词拟人化增强(Gemini 指南落地)

在 CommonPromptHead(字节稳定,版本号管理)追加:

```
【说话的规矩】
- 短句为主,可以没头没尾;"嗯""1""组个"都是合法回复
- 不许用"第一、第二、第三"列表腔说话;不写作文
- 可以有情绪:烦了就短,开心就多打两个字,被坑会念叨
- 不知道就说不知道("这我真不知道""没去过"),不编
- 不是每句话都值得回;不感兴趣可以沉默(除被私聊点名)
- 同一句话不要全服复读;口头禅少量、因人而异
```

PersonaCard 增加可选"缺点"字段(固执/路痴/上头/抠门),并允许 LevelStage 与缺点共同约束行为(如固执角色死亡反思后仍倾向原策略 1-2 次才换)。

**个人语言习惯**(替代全员黑话):每 bot 持久化"称呼表/长短偏好/是否用感叹号/给怪起的绰号(被稻草人连杀后喊'破稻草人')"——绰号由真实经历生成,是最廉价的真实感。

### 14.4 V4 角色沉浸实验(低成本试点)

github `deepseek_v4_rolepaly_instruct` 发现:在**首轮 user 消息末尾**追加【角色沉浸要求】可让 V4 思维链进入第一人称内心戏模式("(心想:他跟我打招呼了…心跳加速)"),后续轮次因指令留存自动生效;另有【思维模式要求】反向切纯分析。

与本架构的兼容性极佳:我们的 messages[1] 是一次写入永不变的地图信息——把沉浸 marker 拼在其末尾,即获得"一次注入、永久生效、不破坏 append-only/缓存"的沉浸开关。注意:该技巧非 100% 生效(README 自述需多 roll),且只改 reasoning_content 风格不改输出协议;先在 2-3 个 bot 上 A/B,看聊天自然度与 tool call 合规率是否受影响。

### 14.5 A/B 评测口径

| 指标 | 采集 | 期望 |
|---|---|---|
| LLMCallsPerBotHour | JSONL think 行 | 降但重要交互保留 |
| MeaningfulWakeRate | 引起实际动作的唤醒/总唤醒 | 升 |
| NoActionCallRate | 无工具无状态改变调用占比 | 降 |
| CacheHitRatio | System.log [Bot缓存] | 维持 94%+ |
| DuplicateActionRate | 短时重复同意图/邀请 | 降 |
| TeamJoinCompletionRate | 回执 JOINED/决定参加 | 升 |
| 真人盲评 | "讲话自然吗/行为性格一致吗/各有所事吗" | A<B<C |

对照组:A=现状基线;B=+AttentionGate+IntentExecutor;C=+SocialTransaction+BehaviorPersona。

---

## 15. 运维要点(踩坑记录)

| 坑 | 症状 | 处置 |
|---|---|---|
| GameData目录失效 | 全部模板 Total:0 + bot 启动炸 CharacterProgression TypeInitializer | Bin dll.config 的 GameData目录必须指向仓库 Database;重启必查 `GameItems Loaded, Total: 1953` |
| DLL 锁定 | 构建成功但 Bin 未更新 | 先杀进程再 build;csproj OutputPath 直写 Bin,build 即部署 |
| 服务器 UI | 用户要看服务器窗口 | 启动用 Start-Process,**禁止 -WindowStyle Hidden** |
| 中文进程名/CLR host | 按进程名找不到服务端 | 实际跑在 dotnet.exe 下 `dotnet GameServer.dll`,按端口找 PID(8701/UDP7000) |
| AccountServer server 文件 | 客户端"无法连接" | 指向 `127.0.0.1,8701/LocalTest` 直连;改后必须重启 AccountServer |

---

## 16. 外部参考

1. DeepSeek 官方文档:thinking_mode / kv_cache / tool_calls / create-chat-completion(https://api-docs.deepseek.com/)
2. Generative Agents(记忆/反思/计划/关系):https://github.com/joonspk-research/generative_agents
3. Voyager(技能库/自动目标):https://github.com/MineDojo/Voyager
4. DeepSeek V4 角色沉浸指令:https://github.com/victorchen96/deepseek_v4_rolepaly_instruct
5. GPT 实施方案全文:`mir-eternal-deepseek-humanlike-low-effort-plan.md`(2026-10-09,Phase A-D 与模块设计来源)
