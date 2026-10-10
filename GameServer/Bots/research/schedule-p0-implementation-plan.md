# 作息系统 P0 实施方案(供 GPT+用户 review)

> **依据**:`bot-schedule-offline-design.md` v1.3(设计冻结)+ `gpt-references-deep-dive-2026-10-09.md`(研究增量)
> **性质**:实现级方案——文件级改动清单、六步上线路径、影子验证、风险清单。审完即可开写。
> **范围**:v1.3 P0 决策清单全项;研究新增 P1 项(反思事件驱动等)作为紧随其后的 P0.5 批次,不在本方案内展开。

---

## 一、代码架构

### 新文件:`GameServer/Bots/BotScheduleService.cs`(预计 ~350 行)

```
BotScheduleService(静态,主线程每分钟 tick,由 BotManager.Process 尾部驱动)
├── 日历层
│   ├── IsPeakNow(now) → PeakWindow? / null
│   │   · UTC 窗口 × TimeZoneInfo("Central Standard Time") 换算(DST 自动)
│   │   · 星期按 UTC 日历日判(CT 周日晚峰/周五六晚谷的错位由此自然正确)
│   │   · 节假日:北京日期(UTC+8)查 ChineseHolidays 表 → 全天非峰
│   └── SecondsToPeakBoundary() → 距下一个峰开始/结束的秒数(Closing T-15 与错峰用)
├── 期望态层(纯函数,无副作用)
│   ├── DesiredControl(bot, now) → Active/AFK
│   │   · PersonaCard.Routine 窗口 + 当日浮动(seed=hash(bot名,日期),±15min 高斯)
│   └── DesiredPresence(bot, now) → Online/Offline
│       · 非峰 → Online(全员);峰 → 配额内 Online(留守名单),配额外 Offline
├── 配额层(峰时)
│   ├── 留守名单 = 承诺优先(T-15 前扫未来4h Commitments:真人>双方>单方,每bot≤1)
│   │             > 夜猫倾向排序,取 MaxOnline(2)+真人在线?1:0
│   └── 真人检测:在线 PlayerObject 中非 bot 连接数 >0
└── 转换层(幂等,带 TransitionRecord)
    ├── 需要下线:先注入 Closing 观察(T-15/T-5 两档提示)→ T-0 仍在线则强制 Dismiss
    ├── 需要上线:错峰延迟(峰后 5-15min/bot 随机;重启 2-8min;有承诺者免错峰按约提前)
    └── Active↔AFK:只改 Control 态+注入转换观察,不动 Presence
```

### 状态落地(BotBrain 运行时 + BotMemory 持久)

```
BotBrain 新字段:
  FocusControl Control      // Active / AFK / Closing
  AfkActivity? Activity     // Grind / Town / Idle(仅 AFK)
  int SessionEpoch          // Spawn 时从 LifeIntent 读取并 +1;LLM 回执行前校验
  int AfkWakeBudget         // 当日普通事件唤醒剩余(初始3)
BotMemory 新节 LifeIntent(持久 JSON):
  Routine(周中/周末 Active 窗口) / AfkDefault / CurrentAfkActivity
  LastLogoutAt / LastLogoutReason / PreferredNextActiveAt+Reason
  ScheduleRevision / SessionEpoch(计数器)
TransitionRecord = LifeIntent 内的布尔/时间戳(如 LastClosingNoticedAt),转换前查重防重复注入观察
```

### 关键机制实现点

**1. AFK 零周期思考**:ScheduleThink 开头 `if (Control == AFK) return;`(除五个唤醒条件)。唤醒通道全部走既有钩子:
- 死亡:ReflexDead 已唤醒 ✓(补:AFK 下也要唤醒)
- 药尽:ReflexPotion 的 WarnNoPotion 处加"AFK 则降级 Activity=Idle 并消耗 1 次预算唤醒"
- 活动失败:挪窝连环失败已有停挂机路径 → AFK 下改为唤醒
- 社交:RecordChat 处按 Control 分诊——AFK 时普通聊天只入 ChatMemory 队列不唤醒;**私聊/点名消耗 AfkWakeBudget(≤3/天)唤醒**;组队邀请唤醒(重要社交);被攻击不唤醒(反射自防)
- 健康巡检:ScheduleService 每 10min 对 AFK bot 做确定性检查(药>0?位置在变?Activity=Grind 则怪区正常?)——异常才置唤醒标志,不调 LLM

**2. SessionEpoch**:ThinkAsync 的 EnqueueAction 闭包捕获 `epoch = brain.SessionEpoch`;执行前 `if (brain.SessionEpoch != epoch) return;`。与既有 brain.Alive 检查双保险,使"旧请求作用于新会话"从结构性安全升级为可断言安全。

**3. set_afk_activity 工具**:mode=Grind/Town/Idle;Town=取消战斗意图+寻路走向该图城镇点(坐标源:TeleportGates 同图的村庄门坐标,查不到退化为 Idle 并如实回执);验证后写 LifeIntent.CurrentAfkActivity。转挂机前观察提示该工具;LLM 未及时调用→AfkDefault 兜底。

**4. 观察四块**(BuildObservation 按 Control/Activity/生命周期注入):
```
[状态] 键盘前(19:40后转挂机) / 挂机中·练级(人不在,回来叫你)          ← 常驻
[转挂机前] 10分钟后离开键盘:安排号(set_afk_activity),该卖的卖       ← Active→AFK 前10min
[要下线了] 峰时将至:组队/交谈中→道别托付;独处→静默;别开新活动        ← Closing T-15/T-5
[你回来了/你刚上线] 挂机战报(击杀N/捡获X/金币Δ)+未读摘要+昨日衔接;打不打招呼自己定 ← 转换首轮
```
AFK 未读摘要:AFK 期间 ChatMemory 超容量(MaxChatMemory=30)会截尾——AFK 转换时把队列压缩成一行统计("AFK期间附近聊天42条,提到你3次,私聊2条:红缨问你几点下")+保留最近 5 条原文。

**5. _cost.md 遥测**:LlmClient 结果处按 IsPeakNow 分类累计(per bot: 谷input/谷output/峰input/峰output tokens);每日 00:05(CT)由 ScheduleService 汇总追加一行到 Log/Bots/_cost.md(12 bot + 合计 + 等效人民币,单价表进 PeakGuard.Price 配置)。

**6. 峰时留守者节流**:留守 bot(峰时在线)ThinkInterval ×2(硬闸内天然生效,复用现有 interval 计算)。

---

## 二、文件级改动清单

| 文件 | 改动 | 规模 |
|---|---|---|
| **BotScheduleService.cs**(新) | 日历/期望态/配额/转换四层 + 巡检 + 成本遥测 | ~350 行 |
| BotConfig.cs | PeakGuard 配置节(窗口/节假日/单价/错峰参数) + BotDefinition 增 Routine/AfkDefault/NightOwl | ~60 行 |
| BotBrain.cs | Control/Activity/Epoch/AfkWakeBudget 字段;AFK 门与五唤醒;观察四块;SessionEpoch 校验;反思触发改重要度阈值(P0.5 顺手) | ~150 行改动 |
| BotManager.cs | tick 挂接;真人检测;Spawn/Dismiss 转换调用;Colony 显示三态(键盘前/挂机·X/离线·下次上) | ~80 行 |
| BotToolCatalog.cs | set_afk_activity 工具 | ~40 行 |
| BotMemory.cs | LifeIntent 节读写 | ~50 行 |
| BotConfig.json(Bin) | 12 人设:Routine 表(CT 时间,按 v1.3 §4.2)+ AfkDefault + 夜猫倾向 + 职业微调(夜色无声→自由职业,毒玫瑰→代练) | 数据 |

---

## 三、六步上线路径(每步:构建→重启→日志验证→提交)

**Step 1|影子模式(半天~一天)**:全部日历/期望态/配额逻辑上线,但 `PeakGuard.ShadowMode=true`——**只打日志不执行转换**("[作息影子] 19:45:00 若实弹:血饮狂刀→Closing(峰前15min)……")。验证点:星期错位(今天周四晚 20:00 前应见影子 Closing)、窗口边界、配额名单、节假日/DST 换算单测式抽查。**这一步专治日历 bug——v1.0 的 Tag 写反就是没跑影子的教训**。

**Step 2|生命周期实弹(下线侧)**:影子转正但只开 Closing→Dismiss + T-0 强制;配额=0(即峰时全员下线)最简形态。验证:当晚峰前 15 分钟日志出现收尾观察与道别,20:00 在线归零;重启不再回档(自动存档已兜底)。

**Step 3|上线侧**:错峰登录 + Active 窗口浮动 + AutoStart=谷时全员落位。验证:峰后 5-15 分钟陆续回归、无同秒≥3 人;次日各人设按各自窗口转 Active。

**Step 4|AFK 全家桶**:Control 门 + 五唤醒 + set_afk_activity + AfkDefault + 未读压缩 + 健康巡检。验证:工作日上午全员在线但 AFK 占多数、LLM 调用降为事件驱动(对照 [Bot缓存] 频次)、药尽自动降级、AfkActivity 分布不塌缩。

**Step 5|社交仪式与收尾**:观察四块措辞调教 + 提示词规则 15 + Colony 三态显示 + 深夜短窗双免。验证:孤身下线不发言、组队中道别、上线不强制打招呼。

**Step 6|成本闭环**:_cost.md 遥测上线,跑满 7 天出首份实测周报(对照理论 30-60%)。

**P0.5 批次(紧随,独立小改)**:反思事件驱动(重要度累计>150)+ 对话记忆化(≥4 条往来→第一人称摘要+好恶入 Episodes)——研究文档的两条最高价值项,与作息无耦合。

---

## 四、风险与待 GPT 拍板项

| # | 风险/问题 | 我的预案 |
|---|---|---|
| R1 | Player.Disconnect 队伍/会话清理是否完整(AI Town 的 leave 是显式拆会话的) | Step 2 集成测试:组队中 Dismiss,查队友侧队伍面板与日志;不完整则在 Dismiss 前显式 LeaveTeam |
| R2 | Town 模式安全区坐标无权威来源 | 退化链:同图 TeleportGates 村庄门 → GuidedKB 城镇点 → Idle(如实回执"这图不知道哪儿是城,先原地") |
| R3 | agent 在 Closing 拖延不下线(T-0 兜底会丢告别) | T-0 无条件 Dismiss;告别丢失可接受(真人也有"被网管拔线"型下线) |
| R4 | AFK 未读溢出(ChatMemory 30 条) | 转换时压缩统计+保最近5条(§一.4);不引入持久信箱(P2) |
| R5 | 影子期发现日历换算错(时区/DST/星期) | 影子期存在的原因;错了改日历层,不影响其他层 |
| R6 | 留守者节流×2 后峰时响应变慢(真人在峰时约 bot) | 峰时被私聊/点名仍立即唤醒(新闻通道与间隔无关)✓ 已保证 |
| Q1 | 影子模式跑多久?我建议≥12h(覆盖一个完整峰窗边界);GPT 若认为可短至 2h 请说明 | — |
| Q2 | Step 2 的"配额=0 全下线"最简形态会有一晚没有留守(若当晚有真人)——可接受还是要直接从配额2开始? | 我倾向最简形态(先证下线侧,再放开留守) |
| Q3 | 单价表(谷/峰 × input/output 人民币)由用户填还是我先按 DeepSeek 公开价写死? | 先写死进配置,用户随时改 |

---

## 五、验收挂钩(实现自 v1.3 §12,不重复设计)

每步的验证点即七维验收的工程可测子集;7-14 天盲评(含 _replay 工具)属 P0.5/P1,不在本方案。

---

## 六、一句话给 GPT

方案 = v1.3 决策清单的直译:一个新服务类(日历/期望态/配额/转换四层)+ 六个文件的小改 + 六步上线(影子先行)+ 三个待拍板问题。**没有任何架构新概念——v1.3 评审已把概念定完,这步全是工程。**
