# GitHub 参考笔记(2026-10-08,实地抓取)

不是道听途说,以下内容来自实际抓取的仓库文件。

## MINDcraft(develop 分支,src/agent/)

**命令面(actions.js,503 行,30+ 个 `!` 命令)** —— 对照我们的工具目录(19 个),值得补的:

| 它有我们没有 | 对应我们的设计 |
|---|---|
| `!rememberHere` / `!goToRememberedPlace` | **地点记忆** —— remember '知识: …' 只存文字;应支持"把当前坐标记为'铁匠铺'"与"回到记过的地点"。→ 我们补:`remember_place(name)` + `goto_place(name)` |
| `!goal` / `!endGoal` | 长期目标显式设置(我们的 Commitment 只覆盖带时间的;无截止日的长期目标("冲40级")还没有工具载体,现在靠 prompt 目标字段+AutoGrind) |
| `!searchForBlock` / `!searchForEntity` | 搜索环境(找矿/找怪)—— 我们 ReflexGrind 自动选怪已覆盖找怪;找 NPC/找物没有 |
| `!newAction` | 让 LLM 写新技能代码 —— 我们明确不抄(受约束工具集更稳) |
| `!givePlayer` / `!discard` | = 我们的 give_gold/drop_item ✓ 已有 |
| `!goToBed` / `!consume` / `!equip` | = 作息/喝药/穿装备 ✓ 已有(反射层) |

结构启示:它的 commands 分 `actions.js / queries.js / index.js` —— **"行动"与"查询"分离**。我们 check_inventory/check_skills 是查询 ✓ 同构。

## AI Town(ARCHITECTURE.md 实抓)

1. **决策循环**:Agent.tick 是游戏 tick 一部分;每秒一个 step;**同一时间只允许一个 inProgressOperation**(= 我们每 bot 一个在途 LLM 请求 ✓ 同构)。
2. **同步/异步分离**:游戏状态只能由引擎写;LLM 异步操作不能直写状态,必须提交 input(如 moveTo),下个循环处理 —— = 我们的 ActionQueue(后台线程→主线程)✓ 同构,验证了我们的设计。
3. **对话 FSM**(conversationMembership):`invited → walkingOver → participating`,配 acceptInvite/rejectInvite/leaveConversation + startTyping/finishSendingMessage(打字指示器!)。→ 我们组队/交易该照此做;**打字中提示**是个便宜又强力的拟真点(玩家看到"对方正在输入…")。
4. **记忆**:对话结束→LLM 总结→embedding→向量库;对话前 embed "What you think about X?" 取**最相似 3 条**注入。我们无 embedding,用 相关名+重要度+时间 检索替代 ✓;它的"每条记忆附带对该玩家的观点查询"≈我们的 People 印象随观察携带 ✓。

## Fluid HTN(见 game-agent-research-2026-10-08.md,不引入,触发条件已登记)
