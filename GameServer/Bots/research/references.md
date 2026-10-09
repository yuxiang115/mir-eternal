# 参考论文与项目(该抄什么、不该抄什么)

## 论文

| 论文 | 借鉴点 | 不抄 |
|---|---|---|
| [Generative Agents (2304.03442)](https://arxiv.org/abs/2304.03442) | Memory Stream+检索打分;Reflection 先生成问题再检索证据;Planning(日程) | 小镇模拟的世界观框架;LLM 打 importance 分(我们用服务器事件规则替代) |
| [MemGPT (2310.08560)](https://arxiv.org/abs/2310.08560) | 分层记忆(core/archival);上下文换入换出;自编辑记忆思想 | OS 式复杂抽象;我们靠"稳定头+append 历史+checkpoint"达到同效 |
| [Reflexion (2303.11366)](https://arxiv.org/abs/2303.11366) | 从失败更新行为(战斗经验:"这种怪要拉开打"),不是写摘要 | 自我重构循环 |
| [LongMemEval (2410.10813)](https://arxiv.org/abs/2410.10813) | 记忆能力评估维度:事实提取/跨会话推理/时间推理/更新/拒答 | 暂缓实施(见 roadmap) |
| [A-MEM (2502.12110)](https://arxiv.org/abs/2502.12110) | 记忆之间建立关联(事件→信念→关系的链) | 动态图组织 |
| [Zep/Graphiti (2501.13956)](https://arxiv.org/abs/2501.13956) | 时间化事实、来源追踪(evidenceIds)、关系演化 | 图数据库基础设施 |
| Don't Break the Cache (2601.06007) | 缓存边界策略的系统评测方法 | 数值不可直接套用 DeepSeek |

## 开源项目

| 项目 | 看什么 | 不引入什么 |
|---|---|---|
| [generative_agents](https://github.com/joonspk-research/generative_agents) | 反思两段式(问题→证据检索→结论)的实现细节 | Python 原型整体移植 |
| [ai-town](https://github.com/a16z-infra/ai-town) | 多角色交谈调度、对话发起频率控制 | Convex/TS 技术栈 |
| [chasm](https://github.com/chasmlol/chasm) | 游戏 action 接口与 AI 层的隔离边界 | 它的运行时 |
| [letta-code (MemGPT)](https://github.com/letta-ai/letta-code) | memory lifecycle(写入→检索→衰减→归档)状态机 | 替换我们的 runtime |
| [mem0](https://github.com/mem0ai/mem0) | 记忆抽取格式、ADD/UPDATE/DELETE 决策 | 基础设施集成 |
| [graphiti](https://github.com/getzep/graphiti) | 时间化事实的 schema 设计 | neo4j |
| [llm-game](https://github.com/UninstallInternet/llm-game) | NPC 自主目标、协商 | — |
| [r.A.I.d](https://github.com/Heretyc/RAID) | 角色独立记忆、秘密、个人经历 | — |

## 我们自己的实测结论(比论文更可信的部分)

1. DeepSeek 前缀缓存按字节前缀匹配,公共 system 头跨 bot 互相命中(实测冷启动后第二个 bot 首跳 72%)。
2. 会变的内容插在历史中间会产生"阻断效应"(每轮固定多 miss),必须放开头(低频)或尾部(每轮新增)。
3. 滑动截断是缓存杀手(96%→44% 瞬间),checkpoint 换代才是对的。
4. thinking 模型把"想说的话"写进思考不调工具是被搭话时哑巴的主因;硬指令重试一次可解。
5. 称谓搭话(大哥/老板)不含名字,必须在"必须回应"判定里特判,否则对话断线。
6. 观察里 CombatTarget 血量采样永远赶不上 2 秒一只的击杀节奏,LLM 会误判"怪不掉血"——观察要带累计战果,不带瞬时血量。
