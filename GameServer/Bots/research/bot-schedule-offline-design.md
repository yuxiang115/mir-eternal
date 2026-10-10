# Bot 作息与上下线系统设计(v1.3 · 已搁置 SHELVED)

> ⚠️ **2026-10-09 用户拍板搁置**:人有作息是人的限制,agent 不应模仿人类限制;进游戏要看到所有人都在且忙碌;沉浸感 > 峰谷电费。替代方向见《legend-gameplay-loop-design.md》(真玩家闭环)。本文档保留供回收工程件(SessionEpoch/事件驱动反思/AFK 唤醒预算/社交仪式表)。实施计划(schedule-p0-implementation-plan.md)一并搁置,其中 GPT 评审的四个 P0 工程修正(SessionEpoch 旧引用漏洞/AFK 唤醒顺序/稳定哈希种子/边界即时检查)在回收时一并带上。

---


> **版本**:v1.3 — 2026-10-09 晚(经 GPT 三轮评审:8.5/10,五个关键修改全部采纳;含 ZCode 源码验证结论)
> **评审轨迹**:v1.0(峰时80%下线)→ v1.1(用户抓谷时空场→在线三态)→ v1.2(GPT抓峰窗Tag写反/星期错位/AfkActivity)→ v1.2.1(上下线机制+闹钟之辩)→ **v1.3(GPT深度评审:正交状态/持久化LifeSchedule/Closing前置/事件驱动AFK/技术上线≠社交回归)**
> **配套背景**:`bot-architecture.md` v4.0
> **职责终稿(GPT 定调,本文档遵循)**:**闹钟归程序,生活计划归 Agent,最终执行权归游戏服务器。**

**固定实施目标**:
> 谷价最大化在线人数,稳态 12/12;Active 与 AFK 按人设和实际经历转换。峰价常态限 2 人(有真人在线时扩到 3);AFK 可自动成长也可城里休息;不同角色不同活法。

---

## 1. 背景与动机(浓缩)

1. **涌现已就绪**:Chat.log 里水晶之恋自发表演"预告下线→道别→给理由→约明天→穿帮圆场"全套,只缺真机制
2. **成本**:DeepSeek 工作日 UTC 01:00-04:00 与 06:00-10:00 峰价×2(中国法定节假日除外);换算美中:周日~四晚 20:00-23:00 + 周一~五凌晨 01:00-05:00;周五六晚为谷价,**周日晚是峰价**(UTC 星期错位)
3. **成本模型**:峰时限员(2/0)保底省 ~30%;计入 AFK 事件驱动节流(谷时≈4 Active+8 近零)理论上限 ~64%;以 `_cost.md` 每日遥测实测为准

## 2. 状态模型:三个正交维度(v1.3 重构,替代三态混用)

| 维度 | 取值 | 说明 |
|---|---|---|
| **Presence 是否在世界** | Online / Offline | Offline=无 PlayerObject,零成本 |
| **Control 在线控制态** | Active / AFK / Closing | Closing 只在峰前准备期出现 |
| **Activity 离席活动** | Grind / Town / Idle | 仅 Control=AFK 时有意义 |

- `Online+AFK+Grind`=挂机练级;`Online+Active`=键盘前;`Offline`=不存在
- 不再有"Closing 算 Active 还是 AFK"的含糊

**持久化状态分四类(GPT 框架)**:

| 状态 | 持久化 | 内容 |
|---|---|---|
| DesiredState(期望) | 可重算 | 时钟+峰谷+人设+配额 → 应在哪个状态 |
| **LifeIntent(生活意图)** | **必须持久** | PreferredNextActiveAt/Reason、AfkDefault、CurrentAfkActivity、NextCommitmentId、ScheduleRevision |
| TransitionRecord(转换) | 幂等可重放 | 防重复 Spawn/退队/告别(见 §8 源码事实:Spawn/Dismiss 已幂等) |
| **SessionEpoch(会话代次)** | **必须可靠** | 每次 Spawn 自增;LLM 回执行动作前校验,旧会话请求作废 |

## 3. 总体设计:三层控制(不变)

```
第1层 成本硬闸(程序): 峰谷日历(UTC×时区×节假日) + 配额(晚间2/真人扩3,凌晨0-1)
第2层 人设作息(程序执行): Active窗口表(±15min浮动) + AFK安排
第3层 LLM(方式归agent): 告别词/挂机安排/回归处理/今日计划 —— 全部即兴,时机全归程序
```

## 4. 时间模型

### 4.1 峰谷日历(修正版)

```json
"PeakGuard": {
  "Windows": [
    { "DaysOfWeekUtc": "Mon-Fri", "UtcStart": "01:00", "UtcEnd": "04:00", "MaxOnline": 2, "Tag": "CT晚间" },
    { "DaysOfWeekUtc": "Mon-Fri", "UtcStart": "06:00", "UtcEnd": "10:00", "MaxOnline": 0, "Tag": "CT凌晨" }
  ],
  "HumanOnlineExtraSlot": 1,
  "ChineseHolidays": ["2026-10-01", "..."],
  "ClosingLeadMinutes": 15,
  "LoginStaggerMinutes": [5, 15],
  "ServerRestartStaggerMinutes": [2, 8]
}
```

- **配额修正(GPT)**:峰时常态 **2 人**(满足 ≥80% 下线的硬指标);**真人在线时 +1 席**(检测真人连接数>0 即扩)
- 节假日按**北京日期**(UTC+8)查表;DST 由 TimeZoneInfo 自动处理
- **Closing 是峰前准备期,不是峰后宽限(v1.3 关键修正)**:

```
19:45(T-15)  ScheduleService 定下留守名单;其余进入 Closing,收尾观察注入
19:55(T-5)   收尾最后阶段:禁止开启新的长期活动(新挂机/新组队)
20:00(T-0)   多余角色必须完成 Dismiss;此后在线数任何时刻 ≤ 配额
(凌晨峰同理:00:45 开始收尾,01:00 强制归 0)
```

### 4.2 人设 Active 窗口(人设职业微调版)

工作日(CT):上班族(红缨/乱世佳人/白娘子/纵横四海)05:00-08:00+12:00-13:00;自由职业(夜色无声)白天常在;职业代练(毒玫瑰)10:00-19:00;机动档(射雕英雄/赵子龙)17:00-20:00;学生党(水晶之恋/精灵之吻/魔法小王子)15:00-19:30;夜猫(血饮狂刀)23:00-01:00。周末按周末表。**谷时任意时刻 ≥1 人 Active(13:00-15:00 由代练/自由职业覆盖),在线恒 12**。

## 5. 生命周期

### 5.1 转换总表 + 社交仪式(技术上线 ≠ 社交回归,v1.3 采纳)

| 转换 | LLM 仪式 |
|---|---|
| Offline→AFK(峰后错峰回归) | **不需要**——恢复已有 AFK 活动,静默上线 |
| Offline→Active(约定时刻/窗口开始) | **视上下文**:有未读/有人在场才打招呼,不强制 |
| Active→AFK(下班/睡觉) | 一次微决策:`set_afk_activity` 安排号,不道别(人还"活着",只是离开键盘) |
| AFK→Active(回来) | 观察给挂机战报+未读;说不说话 agent 定 |
| →Closing→Offline(仅峰前硬闸) | 组队中/交谈中才郑重道别;孤身一人静默下 |
| AFK→Offline(峰前硬闸点名) | 通常直接下,不发表感言 |

**深夜短窗特例**(23:00-00:45 CT 谷价尾巴):全员错峰回归但**不强制打招呼**,00:45 收尾也**不强制道别**——两次仪式全免,否则每晚刷两轮"我回来啦/我睡了"就是机械穿帮。

### 5.2 下线实现(源码已验证)

1. T-15:ScheduleService 定留守名单(承诺优先级见 §7),其余进 Closing
2. Closing 观察:"[要下线了] 收尾:道别(仅在有观众时)、卖垃圾、托付、约明天记 make_plan"
3. T-0 强制:主线程 → `BotManager.Dismiss()`(**已验证**:Alive=false → Memory.Save → `Player.Disconnect()` 真实掉线路径 → 移除 brain;对不在线者幂等返回)
4. 记 LastLogoutAt/Reason 入 LifeIntent;依赖 Disconnect 原生清理队伍关系(P0 集成测试验证一项)

### 5.3 上线实现(源码已验证)

1. 触发:Active 窗口+浮动 / 峰窗结束+错峰(5-15min)/ **有约定者提前于约定时刻上线**(不等错峰)
2. `BotManager.Spawn()`(**已验证幂等**:在线则拒绝重复生成)→ 新 BotBrain 实例 + SessionEpoch 自增
3. 加载 Memory + LifeIntent → 首轮观察 = 状态衔接(昨下线时间/理由/未读/挂机战报视转换类型)→ 立即首思,推"今天干什么"

## 6. AFK:事件驱动,无周期性 LLM(v1.3 重构,替代"间隔×4~6")

**Voyager 式分工:agent 决定活动,反射层持续执行,LLM 只在必要时回来。**

- 转挂机时一次微决策:新工具 **`set_afk_activity(mode)`**(Q8 结论:显式工具,不从自然语言猜)——Grind/Town/Idle;Town=寻路走去安全区(不是传送);未及时选择→PersonaCard.AfkDefault 兜底;选择经游戏规则验证
- **零周期思考**:AFK 期间不问"要不要继续打怪"。唤醒只有五个条件:

| 唤醒条件 | 说明 |
|---|---|
| 资源耗尽 | 药尽/背包满(反射层检测,自动降级 Idle 并唤醒处理) |
| 活动失败 | 挂机连续无进展/挪窝连环失败/卡死 |
| 死亡 | 无条件(反思,防掉装备) |
| 社交紧急 | 分级:普通聊天→只进 SocialInbox;被攻击→反射层自防;重大危险/熟人连呼/组队邀请→短暂唤醒 2~5min,**普通事件 ≤3 次/天,生存危险不限** |
| 程序健康巡检 | **确定性检查**(不调 LLM):每 10min 验证 Activity 前置条件仍成立;异常才唤醒 |

- 回 Active 时:挂机战报(击杀/捡获/用度)+"[你回来了] 未读 N 条"

## 7. 承诺即闹钟 v2(GPT 补全)

- **持久化 Commitment Store**:make_plan 本就落盘 BotMemory(跨重启);**ScheduleService 对离线 bot 直接 `BotMemory.Load(name)` 读盘**(无需 BotBrain 存活)——已验证可行
- **T-15 预占位**:19:45 规划留守名单时,扫描未来 4h 内到期的承诺,有承诺者进留守席(赴约不迟到)
- **社交证据分级,反滥占**:承诺排序 = 与真人的约定 > 与 bot 的双方确认约定 > 单方计划;单方计划不占峰时席位;每 bot 峰时承诺席位 ≤1
- **自然语言告别词不解析成日程(GPT 否决 P1 方案,接受)**:"明天老地方见"只进记忆;影响日程的唯一通道是 make_plan/LifeSchedule

## 8. 数据模型

```
LifeSchedule(BotMemory 内新节,持久):
  Routine(Active窗口) / PreferredNextActiveAt+Reason(下次想回来)
  AfkDefault / CurrentAfkActivity / NextCommitmentId / ScheduleRevision
Lifecycle(BotManager 运行时+落盘):
  Presence / SessionEpoch(每次Spawn自增) / LastLogoutAt+Reason
```

**源码事实(ZCode 已验证,GPT 点名的检查项)**:
- `Spawn()` 幂等 ✓(FindBrain 在线即拒);`Dismiss()` 幂等 ✓ 且走 `Player.Disconnect()` 真实路径
- 旧 LLM 回执隔离:ActionQueue 闭包检查 `brain.Alive && brain.Player != null`,新会话=新 BotBrain 实例,旧闭包引用旧对象——**结构性安全**;SessionEpoch 是把该保障形式化+可测试
- 待集成测试:Disconnect 是否完整清理队伍(真人掉线路径应已处理)

## 9. LLM 集成(观察块)

```
[状态] 键盘前(19:40 后转挂机) / 挂机中(人不在,回来时叫你)     ← 常驻
[转挂机前] 安排你的号:set_afk_activity(练级/回城/原地);该卖的卖   ← Active→AFK 前
[要下线了](峰前硬闸) 组队/交谈中→道别托付;独处→不用说什么        ← Closing
[你回来了/你刚上线] 挂机战报+未读+昨天下线衔接;要不要打招呼你自己看着办  ← 上线首轮
```

提示词规则 15 改述:"你在线时长由现实生活决定,观察里的状态提示就是你的节奏;转挂机是安排号不是告别;下线才需要和在场的人说一声。"

## 10. 边界情况

| 情况 | 处理 |
|---|---|
| 谷时重启 | 全员立即 Spawn 落位,但按 ServerRestartStagger 2-8min 错峰,防 12 人同秒出现 |
| 峰时重启 | 只拉留守席+有承诺者,其余排队 |
| Closing 中收到私聊 | 宽限内可回;已 Offline 无响应(真人亦然) |
| 承诺落在峰时但配额满 | 承诺优先级排序;挤不进=爽约,上线后道歉改约(社交后果即故事) |
| 深夜短窗(23:00-00:45) | 全员回归但双免仪式(§5.1) |
| 节假日(北京) | 硬闸解除,作息照常 |
| 服务器在 AFK 中重启 | DesiredState 重算落位;LifeIntent 从盘恢复(挂机安排延续) |

## 11. 分阶段实施

**P0(GPT §八决策清单,全部采纳)**:

| 项目 | 决策 |
|---|---|
| 峰谷日历 | UTC 计算,Closing 前置 T-15,配额晚间2/真人+1、凌晨0 |
| Offline 生命周期 | 复用 Spawn/Dismiss(幂等已验证),集成测试队伍清理 |
| SessionEpoch | 每次 Spawn 自增,LLM 回执行前校验 |
| LifeSchedule | 持久化意图+实际约定(ScheduleService 可离线读) |
| AFK 活动选择 | `set_afk_activity` 工具+人设兜底+规则验证 |
| AFK LLM | 事件驱动(五条件),程序健康巡检不调 LLM |
| 承诺优先席位 | T-15 预占位,真人约定>双方确认>单方,每bot≤1 |
| 社交仪式 | 有需要才告别,双免深夜短窗 |
| 谷价覆盖 | 稳态 12/12,错峰 5-15min,重启错峰 2-8min |
| 成本遥测 | `_cost.md` 每日峰/谷 token×单价汇总 |
| ~~自然语言影响日程~~ | **不做**(GPT 否决,接受) |

**P1**:双方确认承诺协议;社会网络快照分析(关系网是否真实成形);SOTOPIA 式社交可信度盲评
**P2**:离线私聊信箱;点卡经济(可选)

## 12. 验收(GPT 七维表,替换"道别率≥80%")

| 维度 | 测试目标 |
|---|---|
| 峰价控制 | 任何时刻在线 ≤ 配额(2/真人3/凌晨0);Closing 从 T-15 开始 |
| 谷价在线 | 错峰后稳态 12/12;重启错峰无同秒≥3人 |
| 生命周期安全 | 无重复 Spawn;无旧会话工具执行(SessionEpoch);无重复退队 |
| 社交连续性 | 已确认约定在可执行条件下被兑现;无法兑现有合理处理 |
| AFK 真实性 | Grind/Town/Idle 选择符合角色经历(连死三次的冲级狂该选 Town) |
| 有意义的告别 | 离队/交谈中合理通知;独处不发言;深夜短窗双免 |
| 自主生活 | 不同角色表现出有原因的活动与作息差异 |

后四项以 7-14 天行为日志+人工盲评为准,不看计数。

## 13. 已决问题存档

- Q6 AFK 打断:分级中断,普通≤3次/天,生存不限 ✓
- Q7 空窗:自由职业+代练人设覆盖 ✓
- Q8 AFK 活动:agent 优先+人设兜底+显式工具 `set_afk_activity` ✓
- Q9 错峰:可接受,约定者提前上线不受错峰限制 ✓
- 告别语→日程:不做,通道只有 make_plan ✓
- 峰时人数:常态 2(80% 下线硬指标),真人在线扩 3 ✓

## 14. 参考(按价值排序,GPT 推荐+本项目已有)

1. Generative Agents(Park 2023)——生活规划/记忆影响次日行为:https://arxiv.org/abs/2304.03442
2. AI Town(a16z)——**generation number 防过期调度(SessionEpoch 的出处)**、引擎/异步任务分离:https://github.com/a16z-infra/ai-town(重点读 ARCHITECTURE.md、convex/agent/)
3. Concordia(DeepMind)——agent 表达意图、环境层验证执行:https://github.com/google-deepmind/concordia
4. Voyager——agent 定技能、执行器持续跑、反馈闭环(AFK 的形状):https://arxiv.org/abs/2305.16291
5. SOTOPIA(ICLR24)——社交可信度评测(P1 盲评):https://arxiv.org/abs/2310.11667
6. AgentSociety(清华)——社会模拟(P2 参考):https://arxiv.org/abs/2502.08691
