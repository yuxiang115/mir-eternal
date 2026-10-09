# 游戏 Agent 执行层/社会层调研(2026-10-08,响应外部建议)

背景:外部评审建议引入 HTN/GOAP 规划器、社会模拟框架、事件驱动 runtime。结合我们的实际(4 bot、C# 单进程、已有反射层+意图+事件唤醒)逐项调研与判定。

## 结论先行

**我们已经在正确的架构路线上**(反射层持续执行 + 意图 + 事件唤醒 LLM = 简版 HTN + 事件驱动 runtime,今天已把空轮/无意义唤醒砍掉)。这轮采纳的是:**知识库层(Voyager 思想,一级号实验)** + **context 扩到 200k**;HTN 库和社会模拟框架**暂不引入**,引入条件已写明。

## 逐项判定

### 1. Fluid HTN([repo](https://github.com/ptrefall/fluid-hierarchical-task-network))— 不引入,记录触发条件
- 现状:2019 年作品,基于 GameAI Pro 的 HTN 文章(Horizon Zero Dawn 同款技术),2020 年后基本无维护;有 [GameReadyHtn](https://www.nuget.org/packages/GameReadyHtn) 等替代。
- 我们的 AutoGrind/Follow/Combat/Move 意图 + Commitment 到点唤醒,就是手写的 total-order 任务分解。库能带来的增量(部分规划、分解日志)在 4 个意图种类下没有收益。
- **触发条件**(写死,防止将来拍脑袋):意图种类 >6 且出现前置条件依赖(如"买药→需要金币→需要卖货")时,再评估引入 HTN 或 [ReGoap](https://github.com/luxkun/ReGoap)(GOAP 适合"资源不足自动改目标"这类场景,即老周没金币却喊收银蛇的问题 —— 眼下用 prompt 规则"没钱不许喊收"+check_inventory 已覆盖)。

### 2. MINDcraft([repo](https://github.com/mindcraft-bots/mindcraft))— 已对齐,借鉴两点
- skill library(LLM 调函数而非生成代码)= 我们的 BotToolCatalog,方向一致 ✓。
- **借鉴①**:任务失败原因回喂(它把每步执行结果反馈给 LLM)—— 我们的 ToolResults 已做(含"走不过去,被堵死了")。
- **借鉴②**:self-prompting 子目标链("先要石头工具")—— 对应我们观察尾部 `[该干嘛?]` + 空轮不入历史(只留有效决策),已完成。
- MineCollab 论文([2504.17950](https://arxiv.org/abs/2504.17950)):多 agent 协作瓶颈在沟通/进度共享 —— 对应我们"嘴上组队没真组队"问题,team_invite 已落,组队 FSM 见 §4。

### 3. Voyager([repo](https://github.com/MineDojo/Voyager),[论文 2305.16291](https://arxiv.org/abs/2305.16291))— **本轮采纳其"技能库→知识库"思想**
- Voyager 三机制:自动课程(下一步追求什么)/可复用技能库/执行反馈修正。
- 不抄:让 LLM 生成代码(我们是受约束的工具集,更安全稳定)。
- **抄:自动课程 + 知识积累** → 落地为 BotMemory.Knowledge 层:agent 把游戏发现("羊2级20血""金创药回50血""村北有铁匠")用 remember '知识: …' 记下,低频追加、稳定层注入、上限裁剪。一级实验号就是 Voyager 式的"从零课程":它不知道游戏,靠玩与试错积累知识,知识库就是它的攻略本。

### 4. AI Town conversationMembership 状态机 — 列 P1
- invited/walkingOver/participating 的显式状态 + 邀请/接受/拒绝迁移 —— 用于组队/交易/跟随三件事,防止"嘴上组队"。team_invite 已打通第一环;完整 FSM(邀请→接受→共同行动→解散)排 P1。

### 5. Concordia([repo](https://github.com/google-deepmind/concordia))/ SOTOPIA([repo](https://github.com/sotopia-lab/sotopia))/ Lifelong SOTOPIA([2506.12666](https://arxiv.org/abs/2506.12666))— 理论参考,暂不实施
- 可借鉴:社交要有**社会目标**(合作/竞争/谈判),不是表演口吻;Lifelong SOTOPIA 证实长互动中目标完成度下滑、记忆增强有帮助但不解决跨经历推理 —— **记忆必须服务行动**,不做"聊天摘要仓库"。
- 实施时机:做社交矛盾/谈判/传闻系统时(P2-P3)。

### 6. Fluid Behavior Tree — 不引入
- 反射层已经是朴素行为树(优先级:死亡>喝药>战斗>挂机>跟随>移动>捡拾>闲逛),跨帧状态就是意图字段。库的 Unity 依赖得不偿失。

## 本轮落地(实验设计)

**一级白手起家实验号**:Level 1、无启动装备、无启动资金(只有保命药),人格="开小号重玩的网吧常客,对这服不熟,靠自己摸索"。验证:
1. 从零练级:AutoGrind 能否从 1 级羊起步存活成长;
2. 知识库:它玩着玩着记下多少条真实有效的游戏知识(羊的等级/血量/掉落、地图方位、NPC 功能);
3. 知识复用:知识条目随稳定层注入,行为是否随知识变化(比如知道"羊不掉钱"后转向)。
4. context 扩到 200k(MaxContextTokens=200000,压缩阈值 120000),给知识库与更长历史留空间。

**评估口径**(对照外部建议):无人干预 30 分钟 —— LLM 调用次数、击杀数/经验、知识条目数、无效动作占比;真人盲测后置。
