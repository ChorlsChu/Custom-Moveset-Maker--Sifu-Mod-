# Sifu AI Attack Tickets & Combo Start Entries

Research doc for the SifuMovesetEditor "New Moveset" pipeline (Phase 1.3).
All data extracted from `pakchunk0-WindowsNoEditor.pak` via the tools in
`tools\` (see Appendix for reproduction commands).

Date: 2026-10-03.

---

## 1. Runtime mechanism (as reconstructed)

```
 Behavior Tree / AI Action (requestor)
        │  requests ticket by enum value (BP_AIAttackTIcketNames::NewEnumeratorN)
        ▼
 AttackTicketConfig  (DB\AI\Tickets\Attack\*.uasset, class AIAttackTicketData)
        │  behavior (Passive), combat-roles mask, count, per-difficulty
        │  cooldowns, joker behavior, min-distance, always-available flag,
        │  optional m_FallbackTicket (cascades to another ticket enum)
        ▼
 ArchetypeDB  (DB\AI\Archetypes\**\BP_*_ArchetypeDB*.uasset)
        │  m_TicketNameEnumValue   = the archetype's own evaluation ticket (enum)
        │  m_AttackTicketEvaluationFactorsOverride = Map<enum,float>
        │     (0 pairs everywhere, m_bActivateAttackTicketFactorsOverride=False
        │      in all 212 ArchetypeDBs scanned)
        ▼
 Combo file start entries  (m_ComboStartNodesContainer)
           key = same ticket enum + optional condition row
           (difficulty / BP_IsTargetKnockedDownComboCondition / etc.)
           → target node index inside this combo file
```

Key consequences for modding:

- A combo entry is reached **only** if its start-entry enum equals a ticket the
  brain actually requests. Changing a label changes nothing; changing the enum
  value changes routing.
- `m_FallbackTicket` is the cascade: if a ticket cannot run, the fallback enum's
  start entries are used instead. Only 2 of 50 ticket assets define it
  (see §4) — both fall back to enum 69 `FallbackPostDefense`, which has
  start entries in **46** combo files (the game's universal "try something
  else after defense" entry).
- Per-archetype factor weighting exists in data but is **inert** in vanilla
  (flag False, 0 pairs) — safe to ignore for v1.

## 2. Combo start entry anatomy

Parsed by `AnimationParser.ParseStartEntries` (Core\AnimationParser.cs:1924)
and the forensics tool (`--entries` mode). Fields:

| Field | Meaning |
|---|---|
| `m_ParentNodeName` | Human label (inside `m_ComboTransitions`). Hand-authored per file; used only for display/matching in our editor. Sometimes carries a designer suffix like ` Phase Node = Phase1. MC Domination = ALL` (strip it — base label is stable). |
| `m_EnumState.m_Value` | The ticket enum key: `BP_AIAttackTIicketNames::NewEnumeratorN` — **authoritative**. |
| `m_PhaseName` | Optional phase bucket (`Phase1_Defense`, `Fajar_Phase1_Offensive`, …). |
| `m_ComboTransitions.m_Transitions[]` | Rows: `m_eInputTransition` key + `m_ConditionInstance` (null = unconditional) + `m_TargetNodes` map. Keys: `255` = unconditional (single target), `0..N` = condition outcome → target node index. |
| `m_ConditionInstance` | Typically `ComboAIConditionGameDifficulty` (keys = difficulty) or boss-specific conditions (e.g. `BP_IsTargetKnockedDownComboCondition` for Sean). |

Condition keys observed per entry (Sean P2 example):
`Sean Dodge [enum19] → {0:0, 1:1}` via difficulty; `Sean Idle [enum20] → {0:36,1:39,2:45}`
via knocked-down condition; `FallbackPostDefense [69] → 24` (dash node).

## 3. Ticket asset anatomy (AIAttackTicketData)

Single export `AIAttackTicketData`, `m_Config` = `AttackTicketConfig`, 17 fields:

| Field | Notes |
|---|---|
| `m_eBehavior` | `FAttackTicketBehavior::Passive` in every ticket scanned. **Absent in 16/50 tickets** (engine default applies). |
| `m_eCombatRolesMask` | int bitfield, observed values: 4, 6, 12, 14, 15 (bit meanings not decoded; direct/indirect/positional roles suggested by names). |
| `m_iCount` | 1 typical; `AttackTicket_PostDefense`/`OverrideIndirectOpponent` = 10. |
| `m_Cooldowns[]` | 4 × `TimerAndOffset` (m_fTime, m_fOffset), e.g. 2.5/2.0/1.5/1.5 s. |
| `m_iCountPerDifficulty[]` | 3 × int (one per difficulty). |
| `m_CooldownsPerDifficulty[]` | 3 × `AITicketCooldowns` (each containing cooldowns per slot). |
| `m_bUseMinDistanceToPosition`, `m_fMinDistanceToPosition`, `m_fMoveToTimeoutValue` | Positional gating (50 / 250 / 400 / 500 / 1600 uu observed). |
| `m_eBehaviorAgainstJoker` | e.g. `TryCancelCurrentAttacksOnly` (BigGuyPressure, MCDodgeClose, PostDefense). |
| `m_bTicketIsAlwaysAvailable` | True only in `AttackTicket_FengjieOnDizzy`, `AttackTicket_RagingBull`, `AttackTicket_PostDefense`. |
| `m_FallbackTicket` | `SCAITicketEnum` → `NewEnumeratorN`. **Only defined in 2 tickets**, both → enum 69. |

## 4. Ticket asset catalog (50 assets)

`DB\AI\Tickets\Attack\` (+ subfolders `Fengjie\`, `Yang\`). Empty cell = property
absent in serialized data (engine default). `fb` = m_FallbackTicket.

```
asset                                   behavior  roles  cnt  always  joker                        minD    fb
AttackTicket_BigGuyPressure             Passive   6      1            TryCancelCurrentAttacksOnly  50
AttackTicket_DirectOpponnent            -         -      1
AttackTicket_DirectOpponnent_BossVersion-         -      1
AttackTicket_DizzyReaction              Passive   15     1
AttackTicket_FajarRegularAttacks        -         6      1
AttackTicket_FajarVanish                Passive   6      1
AttackTicket_FajarWallJump              Passive   6      1
AttackTicket_FajarWallJumpGoto          Passive   6      1
AttackTicket_FajarWallJumpGotoH         Passive   6      1
AttackTicket_FajarWallJumpHorizontal    Passive   6      1
AttackTicket_FengjieLongRangeAttacks    Passive   6      1            True                         500
AttackTicket_FengjieOnDizzy             Passive   15     1            True
AttackTicket_FengjieOnPushback          Passive   15     1
AttackTicket_FengjieOnWhiff             Passive   15     1
AttackTicket_FengjieShortRangeAttacks   -         6      1
AttackTicket_FetchWeaponReaction_A      Passive   12     1                                         1600
AttackTicket_FetchWeaponReaction_B      Passive   12     1                                         1600
AttackTicket_FetchWeaponReaction_C      Passive   12     1                                         1600
AttackTicket_IndirectOpponent           -         4      1                                         250
AttackTicket_IndirectOpponent_BossVersion-        12     1                                         250
AttackTicket_KurokiChangePhase          Passive   6      1
AttackTicket_KurokiDelayGapCloser       Passive   6      1
AttackTicket_KurokiInstantAttack        Passive   6      1
AttackTicket_KurokiInstantAttackBis     Passive   6      1
AttackTicket_KurokiRegularAttacks       -         15     1
AttackTicket_MCCorneringME              Passive   14     1
AttackTicket_MCDodgeClose               Passive   12     1            TryCancelCurrentAttacksOnly           69
AttackTicket_MCStructureBroken          Passive   -      1
AttackTicket_OnMCKnockedDown            Passive   15     1
AttackTicket_OverrideIndirectOpponent   -         4      10                                        500
AttackTicket_PostDefense                Passive   -      10           TryCancelCurrentAttacksOnly           69
AttackTicket_RagingBull                 Passive   -      -            True
AttackTicket_RagingBullInstant          Passive   6      1
AttackTicket_SeanBurstActive            -         6      1
AttackTicket_SeanExplosionBrasero       Passive   6      1
AttackTicket_SeanStepBack               -         6      1
AttackTicket_ShiroizuDefault            -         6      1
AttackTicket_ThrowFromGroundOpportunity -         6      1
AttackTicket_TraversalAttack            Passive   6      1
AttackTicket_WhiffReaction              -         15     1
AttackTicket_YangAvoidReaction          Passive   15     1
AttackTicket_YangDefenseAttacks         -         15     1                                         400
AttackTicket_YangDefenseCloseReaction   Passive   15     1
AttackTicket_YangDeflectReaction        Passive   15     1
AttackTicket_YangOffenseAttacks         -         15     1
AttackTicket_YangPhase2Attacks          -         15     1
AttackTicket_YangPhase3Attacks          -         15     1
Sean_Avoid_Ticket                       Passive   6      1
Sean_Idle_Ticket                        -         6      1
Seand_Dodge_Ticket                      Passive   6      1
```

Two tickets live outside the folder: `DB\AI\Archetypes\FireDisciple\Attacks\WallJump\AttackTicket_FireDiscipleWallJumpGoto`,
`DB\AI\Archetypes\Yang\WallJump\AttackTicket_YangWallJumpGoto`.

## 5. Ticket enum index → label (51 enums, reconstructed)

The enum asset `BP_AIAttackTIcketNames` (package
`/Game/Blueprints/AI/BP_AIAttackTIcketNames`) is **cooked out of the pak** —
display names cannot be read directly. They were reconstructed by harvesting
`m_ParentNodeName` labels of every start entry across all of `DB\AI`
(1,073 entries from combo files; 7,979 lines incl. ContextualDefense etc.).
Result: **one base label per enum, zero conflicts** across the whole DB —
so labels are reliable identifiers.

Provenance: consistent entry labels, not the enum asset itself. Designer
suffixes (` Phase Node = …`) stripped.

```
enum  label                                     entries
0     Direct Opponent Regular Attacks            39
1     Indirect Opponent Interruption Attacks     33
2     MC Dodge Close                             47
3     MC Cornering me                             7
5     Idle                                        7
6     Dodge                                      44
7     Grab                                        7
8     Rush                                        8
9     KnockedDown                                14
10    Parry                                      45
12    Counter                                     6
13    AttackAsDefense                            14
14    Traversal Attack                            5
15    Raging Bull                                 3
16    Fajar Wall Jump                             3
19    Sean Dodge                                  5
20    Sean Idle                                   3
23    Avoid                                      43
24    SeanDash                                    4
29    Fajar Regular Attacks                       2
30    Custom Combo 1                             31
31    Custom Combo 2                             26
32    Custom Combo 3                              1
34    Custom Combo 5                              3
35    Guard                                       8
41    Fengjie LongRange Attacks                   2
45    KurokiDelayGapCloser                        2
46    Sean Avoid                                  5
47    SeanBurstActive                             1
48    Fengjie ShortRange Attacks                  2
49    ThrowFromGroundOpportunity                  3
52    FireDisciple WallJump GoTo                  3
53    Fengjie OnDizzy                             3
54    On MC Knocked down                         13
55    Fengjie OnPushback                          3
56    Yang Offense Attacks                        2
57    Yang Defense Attacks                        1
58    Dizzy Reaction                              8
60    Yang Defense Close Reaction                 1
64    MC Structure Broken                         7
65    Whiff Reaction                              7
68    KurokiInstantAttackBis                      2
69    FallbackPostDefense                        46
71    Yang WallJump GoTo                          2
72    Yang Ticket Phase 2                         1
73    Yang Ticket Phase 3                         1
74    Fajar Wall Jump Horizontal                  2
76    Fengjie On Whiff                            3
78    Big Guy Raging Bull Instant                 3
83    Direct Opponent Boss Version                8
84    Indirect Opponent Boss Version              8
```

Enum numbers with **no** start entry anywhere in `DB\AI` (never routed to a
combo; may be legacy, engine-side-only, or used by data not under DB\AI):
4, 11, 17, 18, 21, 22, 25–28, 33, 36–40, 42–44, 50, 51, 59, 61–63, 66, 67, 70,
75, 77, 79–82, 85+ (observed max = 84; `NewEnumeratorN` numbering is dense-ish
but has gaps).

## 6. Ticket asset ↔ enum mapping (inferred)

No data field stores a ticket asset's own enum. The bridge is naming
convention — treat as inference, high confidence where names match 1:1:

| enum | label | ticket asset (inferred) | confidence |
|---|---|---|---|
| 0 | Direct Opponent Regular Attacks | AttackTicket_DirectOpponnent | name |
| 1 | Indirect Opponent Interruption Attacks | AttackTicket_IndirectOpponent | name |
| 2 | MC Dodge Close | AttackTicket_MCDodgeClose | name |
| 3 | MC Cornering me | AttackTicket_MCCorneringME | name |
| 14 | Traversal Attack | AttackTicket_TraversalAttack | name |
| 15 | Raging Bull | AttackTicket_RagingBull | name |
| 16 | Fajar Wall Jump | AttackTicket_FajarWallJump | name |
| 19 | Sean Dodge | Seand_Dodge_Ticket | name |
| 20 | Sean Idle | Sean_Idle_Ticket | name |
| 23 | Avoid | (no asset — engine default) | none |
| 29 | Fajar Regular Attacks | AttackTicket_FajarRegularAttacks | name |
| 41/48 | Fengjie Long/ShortRange Attacks | AttackTicket_FengjieLong/ShortRangeAttacks | name |
| 45 | KurokiDelayGapCloser | AttackTicket_KurokiDelayGapCloser | name |
| 46 | Sean Avoid | Sean_Avoid_Ticket | name |
| 49 | ThrowFromGroundOpportunity | AttackTicket_ThrowFromGroundOpportunity | name |
| 52 | FireDisciple WallJump GoTo | AttackTicket_FireDiscipleWallJumpGoto (custom folder) | name |
| 53/55/76 | Fengjie OnDizzy/OnPushback/On Whiff | AttackTicket_FengjieOnDizzy/OnPushback/OnWhiff | name |
| 54 | On MC Knocked down | AttackTicket_OnMCKnockedDown | name |
| 56/57/60 | Yang Offense/Defense/DefenseClose | AttackTicket_Yang*Reaction/Attacks | name |
| 64 | MC Structure Broken | AttackTicket_MCStructureBroken | name |
| 65 | Whiff Reaction | AttackTicket_WhiffReaction | name |
| 68 | KurokiInstantAttackBis | AttackTicket_KurokiInstantAttackBis | name |
| 69 | FallbackPostDefense | AttackTicket_PostDefense | **fallback-proven?** no — PostDefense *falls back to* 69; 69 likely has no asset (engine default) |
| 71 | Yang WallJump GoTo | AttackTicket_YangWallJumpGoto (custom folder) | name |
| 72/73 | Yang Ticket Phase 2/3 | AttackTicket_YangPhase2/3Attacks | name |
| 78 | Big Guy Raging Bull Instant | AttackTicket_RagingBullInstant | name |
| 83/84 | Direct/Indirect Opponent Boss Version | AttackTicket_*_BossVersion | name |
| 5/6/7/8/9/10/12/13/30/31/32/34/35/19… | generic entry labels | no asset → defaults | none |

Note the direction of the only provable link: `AttackTicket_PostDefense.m_FallbackTicket = enum69`
and `AttackTicket_MCDodgeClose.m_FallbackTicket = enum69` — i.e. enum69 is a
**target**, not necessarily PostDefense's own key.

## 7. ArchetypeDB evaluation tickets (m_TicketNameEnumValue)

45 of 212 ArchetypeDBs carry a concrete ticket enum; the rest leave it unset
(default/null). `m_AttackTicketEvaluationFactorsOverride` = 0 pairs and
`m_bActivateAttackTicketFactorsOverride = False` in **all** DBs.

```
enum  label                          archetypes (sample of N)
0     Direct Opp. Regular Attacks    1   BP_H0_TrainingEntry_ArchetypeDB
14    Traversal Attack               1   BP_FlashKick_Base_ArchetypeDB
15    Raging Bull                    3   BP_BigGuy_Base_ArchetypeDB_H3_RagingBull, BP_BigGuy_H4_ArchetypeDB, …
16    Fajar Wall Jump                1   BP_Fajar_Phase2_ArchetypeDB
24    SeanDash                       1   BP_Sean_H0_ArchetypeDB_NoAttack
41    Fengjie LongRange Attacks      6   BP_Fengjie_Phase1/2_LongRange_ArchetypeDB(+Arena variants)
45    KurokiDelayGapCloser           1   BP_Kuroki_P1_ArchetypeDB_Arena_RedRoom08
49    ThrowFromGroundOpportunity     2   BP_FlashKick_ArchetypeDB_Adv_Master, …DanceFloor…
52    FireDisciple WallJump GoTo     1   BP_FireDisciple_ArchetypeDB_Base_Master
53    Fengjie OnDizzy                4   BP_Fengjie_Phase1_ArchetypeDB_Arena_RedRoom08, BP_Fengjie_Test_*
54    On MC Knocked down             3   BP_Sean_P1_ArchetypeDB, BP_Sean_P3_ArchetypeDB, BP_Sean_Transition_ArchetypeDB_
58    Dizzy Reaction                11   BP_Sifu_P1/P2_ArchetypeDB(+all Arena variants)
60    Yang Defense Close Reaction    1   BP_Yang_P1_Defense_ArchetypeDB
65    Whiff Reaction                 1   BP_Yang_P1_ArchetypeDB
68    KurokiInstantAttackBis         1   BP_Kuroki_P1_ArchetypeDB_Melee
71    Yang WallJump GoTo             4   BP_Yang_P2_ArchetypeDB(+Arena variants)
74    Fajar Wall Jump Horizontal     1   BP_Fajar_Phase1_Offensive_ArchetypeDB
76    Fengjie On Whiff               1   BP_Fengjie_Phase1_ArchetypeDB
78    Big Guy Raging Bull Instant    1   BP_BigGuy_ArenaThePit_RagingBull_ArchetypeDB_Adv
```

## 8. Requestors (who asks for tickets)

ASCII scan of `Blueprints\AI` + `DB\AI` (55 files matched ticket-asset or enum
strings). Two request styles:

**By enum** (`BP_AIAttackTIcketNames` string present — these set specific enum
values):

```
Blueprints\AI\Actions\Defense\CounterCombo\BP_AiLaunchCounterCombo_Parry.uasset
Blueprints\AI\Actions\Defense\CounterCombo\BP_AiLaunchCounterCombo_Dodge.uasset
Blueprints\AI\Actions\Defense\CounterCombo\BP_AiLaunchCounterCombo_Avoid.uasset
Blueprints\AI\Actions\GroundGame\BP_Action_GetUpAttack.uasset
Blueprints\AI\Actions\Attacks\Vanish\BP_AiAction_Vanish.uasset
Blueprints\AI\Bosses\Fajar\BP_Fajar_Base.uasset
Blueprints\AI\Bosses\Fajar\BP_BossSituation_Fajar.uasset
Blueprints\AI\Bosses\Yang\BP_AiAction_YangAnim.uasset
DB\AI\Archetypes\BP_MasterArchetype.uasset
```

The `CounterCombo` trio is the parry/dodge/avoid counter system — it explains
why generic `Parry`/`Dodge`/`Avoid` enums (10/6/23) have start entries in
nearly every enemy combo but no ticket asset.

**By asset reference**: only self-references (each ticket references its own
object name), plus `BP_AiCondition_HasNoAttackTicket`, `BP_AiAction_Vanish`
(return value), `BP_Action_GetUpAttack`. Behavior trees under
`Blueprints\AI\BehaviorTree` reference **no** ticket assets — BT-side ticket
request values are stored as bare enum bytes without type strings and were not
attributed in this pass (open question, §10).

## 9. The four in-combo behavior mechanisms (Phase 1.1 conclusion)

The "boss dodge-back" question — Sean, Kuroki and Yang all dodge mid-combo via
the **same mechanic**: action nodes embedded in the transition chain, not
special transition flags. Four distinct mechanisms coexist:

1. **Action nodes** — `m_AIAttackAction.m_DefaultClassObject` points to a
   per-boss `BP_AiAction*_C` class (e.g. Sean `BP_AiActionSean_Dash_S_C`,
   `ShortDash_S_C`; Yang `BP_AIAction_DizzyCounter_C`,
   `BP_AiAction_WallJumpAttack_Yang_C`). No attacks, no anims of their own.
   Sean P2 has 14 action nodes (dashes). Chain example:
   `Tornado(9) -[key0]→ 10 (dash leaf)`, `9 -[key1]→ 11 (dash → 12 →
   redirect 2 EagleSlaughter)`.
2. **Redirect nodes** — `m_NodeRedirect` int (-1 = none), usually
   `[label-row → real attack]` pairs (Sean node 1 `EagleSlaughter` → 2).
3. **Event nodes** — `m_Event` ObjectProperty → phase-switch assets:
   `BP_AiComboEvent_GotoPhase_*` (Kuroki `ComboEventAIGotoPhase`,
   Yang P1 `BP_AiComboEvent_GotoPhase_YangDefensive/Offensive_C`).
4. **Brain-level reactive dodge** — BehaviorTree + `BP_DodgeAnimRequest_*`
   (`Blueprints\AI\BehaviorTree`, boss BPs). Outside combo data.

Editor consequence (Phase 2): the app currently classifies action nodes as
conduits (no attacks, no root/redirect → `AnimationParser:1562`) and **removes
them from the graph** — dash steps are invisible in the editor. `m_bSkip=True`
is the engine's explicit conduit flag but is narrower than the app's
no-anim heuristic. Fix: keep action nodes, render an action badge; optionally
show `m_ParentNodeName` when `m_Name` = "None".

Sean P2 start entries (7) for reference:

```
'Sean Dodge'           enum19 cond=difficulty  {0:0,  1:1}
'Sean Idle'            enum20 cond=kicked-down {0:36, 1:39, 2:45}
'Sean Avoid'           enum46                 {0:0,  1:1}
'On MC Knocked down'   enum54 cond=…          {0:38, 1:37}
'Parry'                enum10                 → 46
'MC Dodge Close'       enum2                  {0:24}
'FallbackPostDefense'  enum69                 → 24 (dash node)
```

Node 46 (`FallbackPostDefense` parent label) is the hub: 12 out-transitions
incl. self-loop. All `m_Name` fields are "None" in Sean — labels live in
`m_ComboTransitions.m_ParentNodeName`.

## 10. Caveats & open questions

- **Enum display names**: reconstructed from entry labels (§5), not from the
  enum asset (cooked out). If a label was ever wrong in one file it would have
  shown as a conflict — there were none.
- **Ticket asset ↔ enum** is naming-convention inference (§6), never a stored
  link. Only fallback chains are stored.
- **Roles mask bits** (4/6/12/14/15) not decoded.
- **BT requestor values**: brains request tickets as bare bytes; which brain
  requests which enum was not fully attributed (would need BT node property
  decoding with enum context).
- **Missing enum numbers** (§5 list) have no start entries; some may be used
  only by brains/tickets.
- `m_TicketNameEnumValue` is per-archetype (one value), while brains request
  many tickets — the relationship between the two is not fully pinned down
  (likely "the archetype's ambient/default evaluation ticket").
- The `m_ParentNodeName` label sometimes contains control characters
  (one Yang entry starts with `\n`) — sanitize before display.

## 11. Condition-row forensics (Phase B design input, 2026-10-04)

Ran via `tools/cond_probe --rows` (7 trees: FD/Sean P2/MainChar/Yang P1/Kuroki P1/Fajar P2/Grunt)
and `--classes` (427 of 458 combo files loaded; GameContent SHA256 == pristine pak for Sean P2,
so the data is vanilla). Raw: `--deep` dumps confirm the CUE4Parse-side payload gap below.

- **Row schema (confirmed)**: one row = ONE `m_ConditionInstance` + `m_TargetNodes`
  `{outcomeValue -> nodeIndex}`. Multi-outcome is common: key sets seen =
  `{255}`, `{0}`, `{0,1}`, `{0,1,2}`, `{0,1,2,3}`, `{0,1,2,3,4}`, `{0,1,2,3,4,5}`
  (Kuroki `node[47]` routes 6 outcomes; `{0}`-only rows have NO 255 fallback).
  Keys are condition return values, not booleans — polarity **verified in-game
  2026-10-12** (user test: `LastAttackHit`, `IsTargetKnockedDown`): key 0 = True,
  key 1 = False (UE Blueprint branch pin order). UI labels follow this.
- **Instance sharing is real** (validates export-keyed param edits): MainChar `exp12/13`
  shared by nodes 30+32, Yang `exp55` by nodes 52+118, Fajar `exp25` by 4 rows across
  nodes 54/64/65/68, `exp70` by 3 rows (nodes 8/18/27).
- **"Choose among several" in vanilla = multiple rows with probabilities**, not
  multi-node outcomes: conditional and unconditional rows mix in one struct with
  `m_fProbability` summing to 100 (Fajar node[27] `p=40` conditional + sibling rows;
  Sean node[3] `p=80` + `p=20`, both conditional, no unconditional sibling).
  **Engine order (roll-before-gate vs gate-before-roll) and behavior when every row's
  condition misses remain unknown** — do not invent probability shapes that vanilla
  doesn't already use.
- **`m_PerConditionValueToNodeMap` (on every condition export — `perCondMap` count ==
  instance count for all 21 classes)**: outer map `{GuidStruct -> conditionStruct}`
  (1-3 pairs); inner `m_Map` = **byte condition-value -> byte small-int (values 0..3
  only)**. It is NOT the row routing (rows whose `m_TargetNodes` go to nodes 31/32 have
  inner maps `0->0, 1->1`). Semantics TBD; byte values are almost always `< nodeCount`
  so a cross-file clone passes `ComboWriteValidator` **without clearing** (update to the
  earlier "must clear" assumption). CUE4Parse leaves `FPropertyTag.Tag` null for this
  property (`Size` 85-235 bytes) — dump it with UAssetAPI (`_combo_forensics --deep`),
  not the CUE4Parse probe.
- **Condition class catalog (seed for the add-condition picker)** — 21 classes, top:
  `ComboAIConditionMCDomination` 88 instances/26 files, `ComboTransitionConditionTargetDistance`
  52/16, `ComboTransitionConditionAILastAttackGuardType` 45/19, `ComboTransitionConditionLastAttackHit`
  44/27, `BP_IsTargetKnockedDownComboCondition_C` 36/25, `BP_AICondition_FullLifeCheck_C` 30/10,
  `ComboAIConditionGroup` 22/10, `ComboTransitionConditionLastAttackGuarded`, `...LastAttackWhiff`,
  `ComboAIConditionGameDifficulty`, `BP_TargetPlaysOrder_C`, `ComboAIConditionCanPerformAction`,
  `ComboAIConditionEnemyDistance`, `ComboTransitionConditionAINavMesh`, `BP_AICondition_BackObstacleCheck_C`,
  `BP_ComboTransition_CheatComboCheck_C` (MainChar only), `BP_AiCondition_Behavior_C`,
  `ComboAIConditionIsCombatRole`, `ComboAIConditionIsInPhaseScenario`, `BP_IsNotEasyMod_C`,
  `BP_AICondition_FengjieDistanceCheck_C`. Full list w/ sample files: `cond_probe --classes`.
- **Vanilla export names already use `_Copy_<guid>` (chains up to 4 deep)** — a cross-file
  condition clone using `AssetCloner`'s `_Copy_<guid>` naming matches vanilla conventions.
- Unresolved class refs: 0 — `ResolvedObject.Class` resolves cleanly for every instance
  (remember `ResolvedObject.Object` is `Lazy<UObject>` — must take `.Value`).

## Appendix: reproduction

```powershell
# condition-row forensics (Phase B): sharing/key-shapes/percond map + class inventory
dotnet run --project tools\cond_probe -- --rows [Game/DB/... paths...]
dotnet run --project tools\cond_probe -- --classes

# from SifuMovesetEditor\
$FC = "C:\Users\Charles\Downloads\Sifu Modding\Unreal Pak Extracter and Creator\extractedPaks\pakchunk0-WindowsNoEditor\Sifu\Content"

# start entries of all combo files (label, enum, condition, targets)
dotnet run --project tools\_combo_forensics -- --entries --all "$FC\DB\AI\Archetypes"

# start entries of every asset under DB\AI (incl. ContextualDefense)
dotnet run --project tools\_combo_forensics -- --entries --allany "$FC\DB\AI"

# full node dump of one combo (12-node schema, transitions, actions)
dotnet run --project tools\_combo_forensics -- --full <combo.uasset>

# deep dump of any asset type (tickets, ArchetypeDBs)
dotnet run --project tools\_combo_forensics -- --deep <file.uasset> [...]

# ArchetypeDB combo-pointer inspection/patching (Phase 3.3 base)
dotnet run --project tools\repoint_combo -- inspect <archetypeDB.uasset>

# tools\_combo_forensics modes: --full | --deep | --entries | --all <dir> | --allany <dir>
```
