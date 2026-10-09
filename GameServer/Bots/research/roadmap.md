# Roadmap

按外部评审 + 自身实测排的行动清单。状态:✅ 完成 / 🔨 进行中 / ⏳ 待做 / 💤 暂缓。

## P0(缓存与数据完整性)

- ✅ 滑动截断退化为兜底(80 轮),压缩(阈值 120k,总预算 200k)是唯一历史重塑
- ✅ messages[1] 只留稳定层;Progress 改观察尾部实时快照;视野玩家印象随观察携带
- ✅ 空轮不入历史(无动作/无搭话/无新事件的轮次回滚,上下文不被"继续挂机"淹没)
- ✅ 意图执行中不唤醒 LLM(busy 无新闻 → 90~180s 巡检;事件/聊天/承诺到点立即唤醒)
- ✅ 振荡检测(位置环缓冲,8 次动作内只在 ≤3 格打转即判堵死,放弃+反馈)
- ✅ 聊天分类:私聊/点名=必须回;称谓搭话=接话不强制;收货广告不再全员误触发

## P1(活人感核心功能)

- ✅ Goal/Commitment 系统(make_plan/plan_done,到点唤醒,失约自动记为"放鸽子")
- ✅ Knowledge 知识库层(remember '知识: …',越玩越厚的攻略本)
- ✅ 游戏工具面:check_inventory/check_skills/use_skill/drop_item/give_gold/shout(大喇叭1000金)/team_invite
- ✅ 启动套装:中位数平民装备+启动资金(每号一次),含不合规清退(等级/性别/职业/贵重品)
- ✅ 一级白手起家实验号(开荒阿六,无装备无资金,验证从零成长+知识积累)
- ⏳ 组队完整 FSM(AI Town conversationMembership 式:邀请→接受→共同行动→解散)
- ⏳ 玩家邀请 bot 时 bot 自动应答(现在只支持 bot 主动邀请)
- ⏳ People 升级:追加式 beliefs + unresolved;服务器自动事件标 source
- ⏳ Reflection 事件驱动(importance≥4 尽快;低重要度攒批;下线整理)

## P2(打磨)

- ⏳ 清退逻辑补扫装备栏(屠龙若已穿上身漏清 —— 已知遗留)
- ⏳ 交易/给予工具扩展;世界喊话策略(倒爷攒钱大喇叭)
- ⏳ 会话结束 checkpoint;LongMemEval 式记忆回归测试
- 💤 HTN/GOAP 库(触发条件:意图>6种且带前置依赖,见 game-agent-research)
- 💤 Embedding/图数据库

## P3(社会仿真)

- 💤 传闻传播/行会/攻城/社交矛盾(SOTOPIA+Concordia 思想,见 game-agent-research)

## 协议风险登记

- 若未来改用标准 multi-turn tool calling,必须同步处理 DeepSeek reasoning_content 传回。
- CommonPromptHead 改动必须五号同步(跨 bot 缓存命中依赖字节级一致)。
- 若设置 DeepSeek user_id,跨 bot 缓存命中失效,需重测。
