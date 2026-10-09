# 传奇永恒：玩家型游戏 Agent 成长知识库

版本：2026-10-08 首版研究整理，非私服配置导出。

## 目的
让 NPC/AI 玩家在游戏世界中拥有合理的“职业认知 + 等级目标 + 技能边界 + 装备追求 + 经济行为 + 社交偏好”。支持七职业、各等级段，供 RAG、上下文注入或规则引擎使用。

## 如何接入
1. **每个角色创建时**读取 `AGENT_SYSTEM_PROMPT.md`、`LIVE_PLAYER_BEHAVIOR.md` 和其对应的 `classes/{class_id}.md`。
2. **每次升级/换装**按 `data/class_progression.json` 中 level_min/level_max 命中一个阶段；读取 `data/skills.json`、`data/weapons.json`、`data/rings.json`。
3. **游戏服务端是真相**：行动前查询实时等级、实际已学技能、装备可穿条件、地图怪物、经验、库存、地图是否开放、价格；知识库只提供候选和玩家偏好。
4. **每 5–15 分钟或重大事件时**根据 `DECISION_ENGINE.md` 刷新短期计划，**不需要每个 tick 请求 LLM**。
5. 为角色持久化 `goal`、`preferences`、`relations`、`memory`、`current_plan`，不要所有 Agent 一套最优策略。

## 文件结构
- `classes/*.md`：7 职业成长路线（1–60，47级之后为开放式高阶段，51级后没有无依据的新增技能）。
- `data/skills.json`：官方技能等级（学级≠已学）
- `data/weapons.json`：官方武器名称与已核验穿戴条件，缺失的为 null
- `data/rings.json`：官方已核验戒指小型样本，不是全量装备库
- `data/weapon_fusion.json`：七职业官方合成路径（要校验版本、材料与服务器开放）
- `data/engravings.json`：铭文参考
- `data/level_routes.json`：地图参考建议，**非硬性的准入条件**
- `data/class_progression.json`：7 职业 × 7 成长段的 49 个计划
- `EQUIPMENT_AND_ECONOMY.md`：经济与换装决策
- `LIVE_PLAYER_BEHAVIOR.md`：活人感 / 人格 / 关系 / 社交
- `DECISION_ENGINE.md`：策略决策算法与状态数据契约
- `SERVER_ADAPTER_TEMPLATE.json`：服务器覆写模板（你的真实表填入）
- `examples/decisions.jsonl`：示例决策事件（模拟，非真实服务器执行）
- `SOURCES.md`：官方来源与可靠性等级

## 重要适配说明
- 当前官方职业介绍包含 **战士、法师、道士、刺客、弓手、龙枪、妖师七个职业**，而旧公告仅有六职业，这是版本差异，不要据此禁用妖师。
- 武器、铭文和地图随 经典版/疾速版/绿色版、开放节点与私服修改而变化。本包不会捏造体验值、爆率、刷新时间、怪物血量或私服价格。
- `weapon_fusion.json` 的 input_count=6 指官方的基准需求数量，官方某些版本允许最多 4 件替换材料；不可将它当成服务器实际配方。

## 最小使用范例
输入：职业=法师、等级=26、已学火墙/疾光、魔杖未拥有、金币少、蓝药少、没有组队。
应输出：先低耗练级或卖垃圾补资金；如果能稳定应对蜈蚣/石墓浅层则尝试；先比较魔杖属性、价格与药费；不直接去赤月、不开 31 级才学的魔法盾。

- `data/accessory_fusion.json`：七职业饰品合成 36 条已核对配方；龙枪/妖师无祖玛合成路径。
