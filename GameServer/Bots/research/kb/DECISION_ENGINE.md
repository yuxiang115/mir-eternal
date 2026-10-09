# Agent 决策引擎：知识库和服务器事实分离

## 状态快照契约
输入最低应有：
```json
{
  "agent_id": "p_101",
  "class_id": "warrior",
  "level": 26,
  "skills_known": ["攻杀剑术", "刺杀剑术"],
  "attrs": {"attack": 33, "magic": 0, "taoism": 0, "spirit": 0, "stab": 0},
  "hp_ratio": 0.82, "mp_ratio": 0.6,
  "gold": 25000,
  "items": [], "equipment": {},
  "available_maps": ["沃玛森林", "比奇矿区", "死亡峡谷"],
  "monster_estimates": [], "market_quotes": {},
  "party": [], "world_events": [], "time_budget_minutes": 12
}
```
数值只是输入格式示例，不是某个真实角色的属性。

## 关键规则
1. `valid_ability = skill ∈ skills_known AND requirements_met`，官方技能只用于解锁候选，不能直接施法。
2. `valid_equipment = item in inventory/market AND all requirement checks passed`。武器要求有 `level`/`attack`/`magic`/`spirit`/`stab`/`taoism` 等类型。
3. `valid_map = map in server.available_maps`，不根据知识库的等级段凭空决定地图已解锁。
4. `estimated_net_gain = expected_gold_from_server_observations - potion_cost - repair_cost - death_risk_cost`；未知则保守地尝试 3–5 次战斗再更新，不臆造收益。
5. `gear_upgrade_value = effective_dps_gain + survival_gain + skill_synergy - total_cost`；以实际服务器数值估算，别用武器名大小排序。
6. `goal_score = progress + economy + social + personality + novelty - risk - travel_cost`；所有权重随人格及事件变化。
7. 全局世界事件（BOSS刷新/公会战/好友召唤）可以打断当前计划，但不能绕过服务器地图、物品或角色权限。

## 内循环建议
- deterministic high-frequency loop：寻路、打怪、拾取、补药、紧急脱离（100ms–2s 级别视服务端）。
- mid-level rule planner：每 30–120 秒计算风险、经济和目标推进；不必须用 LLM。
- LLM strategic planner：5–15 分钟或重大事件调用一次，用职业攻略 + 近期事件做有解释的决策。
- long-term memory：仅保存重要经历；不需要写下每一击。

## 目标选择伪代码
```text
context = queryServerState(agent)
strategy = findByClassAndLevel(class_id, level)
goals = generateCandidates(strategy, context)
for goal in goals:
    if not skill/map/item prerequisites: reject(goal)
    if risk exceeds current acceptable range: reject(goal)
    goal.score = utility(goal, agent.personality, memories, context)
selected = softmax_sample(topK(goals), temperatureByPersonality)
executeLowLevelActions(selected)
if death | equipmentBreak | levelUp | partyInvite | lootRare | expensiveRun:
    reconsiderPlan()
```
建议 softmax 控制在符合事实的候选里做有限随机；不让随机性破坏角色设定、安全和世界状态。

## 优先读取顺序
`server snapshot` → `SERVER_ADAPTER_TEMPLATE` → `classes/{class}.md` → `data/class_progression.json` → `skills/weapons/rings/engraving` → `player memories` → `LLM plan`。
