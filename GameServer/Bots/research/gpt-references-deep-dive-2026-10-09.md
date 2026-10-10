# GPT 六大参考·源码级深度研究(2026-10-09)

> **目的**:GPT 三轮评审引用的 6 个参考,逐个实际精读(论文 + 仓库源码,不是摘要转述),提炼**对本项目的具体启发**。
> **实读清单**:AI Town(ARCHITECTURE.md + inputs.ts + player.ts + memory.ts 四文件源码)、Generative Agents(克隆仓库精读 retrieve.py/reflect.py/plan.py/scratch.py)、Concordia(README + 组件文档结构)、Voyager(README + 论文 2305.16291)、SOTOPIA(论文 ar5iv 全文精读)、AgentSociety(README)。
> **标记**:✓已有(我们已同构)/ v1.3(已在作息设计 P0)/ **P1◆新想法**(本研究新增,建议进路线图)/ ✗不抄(明确拒绝及理由)。

---

## 1. AI Town(a16z)—— 四个源码文件实读

### 硬核发现

1. **generation number**(ARCHITECTURE.md):"monotonically increases",所有调度运行携带期望代号,失效即"fail immediately"——**这是我们 SessionEpoch 的直系出处**,v1.3 已采纳
2. **表所有权三分离**:引擎独占游戏表 / Agent 只写自己的表 / **消息表放引擎外**("The core simulation doesn't need to know about messages")——聊天高频低价值,引擎不背这个锅
3. **Agent 单飞**:"ensure that an agent only is trying to do one thing at a time"——= 我们的 `_thinking` single-flight ✓
4. **join 校验链**(player.ts):token 去重 → 人数上限 → **随机位置尝试 10 次(用 blocked 判断)** → 角色合法性 → 初始化。全部 throw Error 直拒
5. **leave 清理链**(player.ts):先找该玩家**参与的所有会话并逐个 stop**,再删玩家——离开前显式拆会话,不是只删自己
6. **记忆管道**(memory.ts):对话结束 → GPT 第一人称摘要(**"add if you liked or disliked this interaction"**)→ embedding → 入库;**重要性(poignancy)0-9 分,temp 0.0,解析失败默认 5**;**反思触发=最近 100 条记忆重要性总和>500**(自上次反思起)→ 生成 {insight, statementIds} JSON → 洞察也入库成为记忆
7. **检索**:向量召回 overfetch×10 → relevance+importance+recency(`0.99^小时`)三归一加权 → 取 3;**lastAccess 更新有 300s 节流**(防高频touch)
8. **引擎空闲即睡**:"If the engine is idle for a minute and an input comes in, we want to run the engine immediately"——输入驱动唤醒,不空转

### 对本项目

| 发现 | 启发 |
|---|---|
| generation number | v1.3 SessionEpoch ✓(已采纳) |
| leave 先拆会话再删人 | **我们 Dismiss 依赖 Disconnect 原生清理,v1.3 P0 集成测试项实证需要**——AI Town 证明这必须显式做 |
| 反思=重要性累计阈值触发 | **P1◆ 反思事件驱动**:我们的"反思改事件触发"路线图项拿到具体机制——Memory 事件带重要度分,自上次反思累计 >阈值(初始 150,相当于 ~30 个普通事件或 3 次死亡)触发;**替代现在的 20 分钟定时器**(GPT 二轮批评的"每20分钟强行悟人生") |
| 对话摘要带"喜欢/厌恶" | **P1◆ 对话记忆化**:有来有回的对话(≥4 条)结束后,自动第一人称摘要+好恶存 Episodes——现在对话原文躺在上下文里等压缩,压缩时才被动记忆;主动摘要让"上次聊得很开心"变成稳定记忆 |
| lastAccess 节流 / overfetch 重排 | P1◆ 检索微优化:相关名匹配前先按重要度预筛 20 条再重排,注入量减半 |
| 消息表放引擎外 | ✓同构(我们的聊天走 RecordChat 队列不占引擎表) |

---

## 2. Generative Agents(Stanford)—— 仓库克隆精读

### 硬核发现(源码级,非论文转述)

1. **检索打分真实权重**(scratch.py):`recency_w=1, relevance_w=1, importance_w=1` **三权相等**,recency=`0.99^访问序`(不是小时,是最近访问排行),归一化后求和——论文里调参,代码里默认全 1
2. **反思触发**(reflect.py):`importance_ele_n` 条近期事件 poignancy 求和 vs `importance_trigger_max` 阈值——与 AI Town 同一血统(>500/100条)
3. **计划五级分解链**(plan.py):`wake_up_hour → first_daily_plan(粗排+时间点) → hourly_schedule → task_decomp(分钟时长) → 动作定位链(sector→arena→game_object→pronunciatio[头顶表情符号]→event_triple)`——全部 LLM 调用,一层层问
4. **decide_to_talk 先于 talk**(plan.py `generate_decide_to_talk`):**用检索到的记忆决定要不要发起交谈**——社交主动性由记忆驱动,不是随机

### 对本项目

| 发现 | 启发 |
|---|---|
| 三权相等+0.99衰减 | ✓我们的检索同族(重要度×2+新鲜+相关),无需改;教训:**别过度调参**,等权就是论文的出厂默认 |
| 日计划→小时→分钟的分解 | 我们的 DailyPlan≈first_daily_plan 一层。**不抄五级分解**(游戏行为由意图+反射执行,不是时间表木偶;GPT 二轮也警告"不要把人变成日程表")。但 P1◆ 可让 DailyPlan 支持时间段写法("15-17点打羊,晚上组队")——模型自己写,程序不强制 |
| decide_to_talk 用记忆 | P1◆ Bounded Attention 落地素材:唤醒后是否搭话,观察里带上"你记得他:上次帮你挡怪"——**用关系记忆驱动社交主动性**,正好接 SocialHistory 六维 |
| pronunciatio 表情 | ✗不抄(Mir 客户端没有该渲染,纯装饰) |

---

## 3. Concordia(DeepMind)—— README+组件结构

### 硬核发现

1. **Entity=组件容器**:"instructions, memory and action selection"都是可插拔组件,预制件(prefab)实例化
2. **GM 解析意图**:实体自然语言表述行动意图,GM"checking physical plausibility"后转成结果——**agent 提议、环境裁决**
3. **player/GM 是角色不是类**:"assigned player or GM roles",同一次 run_loop 里动态分配

### 对本项目

- **agent 提议/环境裁决**:✓完全同构(我们的工具层=GM:use_skill 真校验、buy_item 查商店、learn_skill 查等级)——三度验证(Concordia/AI Town/我们)说明这是游戏 agent 的标准形态
- **组件化 BotBrain**:P2◆ 现在的 BotBrain 2000+ 行大单体;Concordia 式拆分(感知组件/记忆组件/行动选择组件/意图组件)值得在 LifeDirector 全家桶落地时一并做——**但不在 P0**(重构风险 > 收益)
- ✗不抄它的通用 GM 循环(我们的"GM"就是游戏服务器本体,不需要模拟层)

---

## 4. Voyager(NVIDIA 等)—— README+论文

### 硬核发现

1. **自动课程**:基于当前探索状态(已有物品/已探地图/已完成任务)由 LLM 提议下一个任务,黑盒查询无微调
2. **技能库=可执行代码**,"temporally extended, interpretable, compositional",可加载到新世界复用
3. **迭代提示**:环境反馈+执行报错+自我验证三入 prompt 迭代改进程序

### 对本项目

- **环境反馈闭环**:✓同构且刚强化完——learn_skill 失败现在给出真实原因(等级/职业/没书),毒玫瑰据此更新认知改目标;这就是 Voyager 反馈迭代在受约束工具集上的形态
- **自动课程**:P1◆ 我们的对应物=目标完成后由 agent 自荐下一目标(已涌现:毒玫瑰 drop_goal 后立新目标);可加一粒催化剂:目标达成时观察加"[目标达成了] 下一个追什么,你自己定"——**不建议**做成程序化课程(会回到"12 个挂机程序"的老路)
- **技能=代码**:✗不抄(维持受约束工具集;此前 MINDcraft 调研已决策,本次三读确认)
- **技能可组合复用**:以 Knowledge 形态存在("稻草人掉药""基础射击射程8")✓ 等价物

---

## 5. SOTOPIA(ICLR24)—— 论文全文精读(评测金矿)

### 硬核发现

1. **七维社交评测**:

| 维度 | 分值 | 定义 |
|---|---|---|
| Goal 目标达成 | 0–10 | 社交目标达成度 |
| Believability 可信度 | 0–10 | 自然+与人设一致 |
| Knowledge 信息获取 | 0–10 | 互动中拿到新信息 |
| Secret 秘密保守 | −10–0 | 0=没泄 |
| Relationship 关系 | −5–5 | 维护/增进 |
| Social Rules 规范 | −10–0 | 0=没违规 |
| Financial 经济收益 | −5–5 | 短期+长期 |

2. **GPT-4 judge**:与人类标注者**完全相同的指导语**,temperature 0,输出分数+理由;与人类相关性 Goal r=0.71 最强,Fin 0.62、Rel 0.56;**judge 系统性偏松**(Soc/Sec 给分高于人类);>74% 分数落在人类 1 个标准差内
3. **动作空间五类**:speak / 非言语 / physical action / **none / leave**——**与我们工具集一一对应**(say/move/attack/无动作/下线)!
4. **人类 vs GPT-4 行为差异**(SOTOPIA-hard):人类每轮 **16.8 词 vs GPT-4 45.5 词**;人类谈判**低价开价、更能坚持目标**——**人类更短、更策略**
5. **秘密泄露**:所有模型 Sec/Soc 均为负;定性案例"对话第一句就把秘密说了"

### 对本项目

| 发现 | 启发 |
|---|---|
| 七维+LLM judge 结构 | **P1◆ 盲评 rubric 直接裁剪**:MMO 版五维=目标达成(自己定的目标推进)/可信度(盲评能否认出是谁)/关系(关系网变化有原因)/经济(金币曲线合理)/规范(游戏规则遵守);judge 用 temp 0 + 统一指导语 + **锚定人类样本防偏松**(SOTOPIA 的教训) |
| 人类 16.8 词/轮 | **强证**我们的短句 prompt 规则与 MaxTokens 压制——"话少而准"就是人类基线,不是我们抠门 |
| 低价开价/坚持目标 | P2◆ 经济人格:bot 交易时该有开价策略(压价/抬价因人而异),接 SocialHistory 的 trust 维度 |
| 秘密字段 | **P1◆ PersonaCard 加"秘密"**:每人一个不主动说的事(夜色无声其实怕黑/毒玫瑰代练是被雇的)——被戳到时可以慌、可以岔开;SOTOPIA 证明模型普遍守不住秘密,这恰好是"人味"测试点 |
| action space 含 leave | ✓ 印证 v1.3:下线是一等动作,不是异常 |

---

## 6. AgentSociety(清华)—— README 级

### 硬核发现

- v2:声明式 agent 定义(spec→框架建 workspace)、可插拔环境组件(SimpleSocialSpace)、**JSONL 回放 + DuckDB 实验分析**、多种推理模式路由(CodeGen/ReAct/Plan-Execute)
- v1:Ray 分布式城市级仿真(gRPC 环境)
- 商业目录排除在 Apache 2.0 外

### 对本项目

- **JSONL 回放分析**:P1◆ 我们的 72h 空服实验(拟人度验收)正缺工具——把 Bots/*.jsonl + Chat.log 合成可按时间轴回放的时间线,盲评者看"一天的生活"而不是抽样日志;不必上 DuckDB,一个生成 _replay-YYYYMMDD.md 的脚本即可
- ✗不抄:Ray/城市级/需求层次整套——12 个 bot 的规模用不上,复杂度税大于收益(与 GPT 判断一致)

---

## 7. 交叉综合:六源共识 × 我们的位置

| 共识 | 出处 | 我们 |
|---|---|---|
| agent 提议、环境裁决 | Concordia/AI Town/Voyager | ✓ 工具层=GM,三度验证 |
| 记忆=归一化加权(新/重/相关) | GA/AI Town 同血统 | ✓ 同族;等权就是论文默认,别过度调参 |
| 过期调度用单调代号防 | AI Town | v1.3 SessionEpoch ✓ |
| 一次只飞一个异步操作 | AI Town | ✓ `_thinking` single-flight |
| 反思由重要度累计触发 | GA/AI Town | **P1◆ 现为 20min 定时器,改成阈值触发**(机制照抄:近 N 条事件重要度和>阈值) |
| 离开世界前显式拆社会关系 | AI Town(leave→stop conversations) | v1.3 P0 集成测试项(退队/会话清理) |
| 人类基线=短而策略 | SOTOPIA 实测 16.8词 | ✓ 短句规则+MaxTokens;P2◆ 开价策略 |
| 回放式实验分析 | AgentSociety | P1◆ _replay 日志生成器(72h 实验配套) |

## 8. 本次研究新增行动项汇总(按优先级)

1. **P1◆ 反思改事件驱动**:Memory 事件重要度累计 >150(自上次反思)触发,替代 20min 定时——机制来自 GA/AI Town 血统,一并解决"强行悟人生"批评
2. **P1◆ 对话记忆化**:≥4 条有来有回的对话结束 → 第一人称摘要+"喜欢/厌恶这次交流"存 Episodes(AI Town prompt 原文可用)
3. **P1◆ 盲评 rubric 裁剪 SOTOPIA 五维**(目标/可信/关系/经济/规范),judge temp 0 + 人类锚定防偏松
4. **P1◆ PersonaCard"秘密"字段**:不主动说、被戳到会慌——SOTOPIA 证明这是普遍弱点,反着用就是人味测试点
5. **P1◆ _replay 回放生成器**:72h 空服实验的盲评配套工具
6. **P1◆ Bounded Attention 吸收 decide_to_talk**:搭话意愿由关系记忆驱动(GA 源码模式)
7. **P2◆** Concordia 式组件化 BotBrain;SOTOPIA 式经济开价人格;检索 overfetch 重排微优化
8. **✗明确不抄**:五级日程分解(日程木偶)、技能=代码(三度确认)、Ray 分布式、pronunciatio 表情

> 以上 P1◆ 项已同步建议进 roadmap;v1.3 作息系统的 P0 清单不受影响(本研究全部是增量)。
