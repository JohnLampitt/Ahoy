# Competing Planner Process

A lightweight adversarial design review workflow for Ahoy. When the primary
developer (Claude Code) proposes architecture or implementation, a separate LLM
("competing planner") reviews it blind and provides independent feedback. The
resulting tension surfaces blind spots, validates decisions, and produces better
designs than single-author iteration.

---

## 1. Purpose

A single author — human or LLM — accumulates assumptions. The competing planner
provides:

- **Blind-spot detection**: The competing LLM has no context about why decisions
  were made, so it challenges things the primary author considers settled
- **Design pressure without team overhead**: Equivalent to a peer design review,
  without scheduling or synchronization cost
- **Adversarial validation**: Decisions that survive a competent challenge are
  demonstrably stronger than decisions that were never challenged
- **Documented rationale**: The cycle forces explicit reasoning at every step —
  adopt, push back, or defer, each with a written justification

---

## 2. The Cycle

```
Gap prompt (what we don't know)
        ↓
Competing LLM feedback
        ↓
Integration plan (adopt / push back / defer, with rationale)
        ↓
Implementation
        ↓
Rebuttal prompt (what the competing LLM got wrong or left open)
        ↓
Competing LLM rebuttal
        ↓
(repeat from Integration plan)
```

Each pass through the cycle produces:
- A prompt document in `docs/` (permanent design artifact)
- An integration plan in `.claude/plans/` (implementation roadmap)
- Code changes committed to the feature branch
- A rebuttal prompt if the feedback left threads open

---

## 3. Prompt Conventions

A good gap or rebuttal prompt has all of these:

**Architecture quick-reference** (5–10 lines): the key types, system order, and
constraints. The competing LLM is stateless — give it enough context to reason
correctly without reading the codebase.

**Numbered gaps or threads**: each one is a discrete, answerable design question.
Do not bundle two separate design questions into one gap.

**Per-gap structure:**
1. What is currently implemented (or not implemented)
2. What the competing LLM previously said (for rebuttals)
3. What is unresolved or wrong
4. The specific design question(s) to answer

**Closing instruction**: Explicitly state that vague deferrals are not acceptable.
Use this exact framing:
> "For each thread, either provide a concrete design decision (with rationale) or
> explicitly argue for deliberate deferral with the specific condition that would
> trigger revisiting it."

**What to avoid:**
- Open-ended questions without a design stake ("How should this work?")
- Questions that require reading the full codebase to answer
- Mixing implementation questions ("which file?") with design questions ("should
  this mechanic exist?") — ask design first, implementation follows from the decision

---

## 4. Integration Rules

After receiving competing LLM feedback, Claude Code classifies each point:

| Decision | When to use | Documentation required |
|---|---|---|
| **Adopt** | The feedback is correct, or better than the current plan | Note in plan: "adopted without change" |
| **Adopt with modification** | The direction is right but the specific proposal conflicts with existing architecture | Note what was changed and why |
| **Push back** | The proposal is wrong for a specific, articulable reason tied to design goals or architecture constraints | Write the counterargument explicitly |
| **Defer** | The proposal is reasonable but outside the current scope or requires a prerequisite that doesn't exist yet | State the condition that will un-defer it |

Push-backs and deferrals must be logged in the integration plan. They are not
rejections — they are explicit decisions. An undocumented deferral becomes
technical debt; a documented deferral becomes a future prompt.

---

## 5. Rebuttal Triggers

Write a rebuttal prompt when the competing LLM feedback has one or more of:

- **Unanswered deferral**: a question from a prior round was not addressed in the
  new round, and the deferral reason was not stated
- **Shallow rationale**: an answer was given but with no supporting reasoning
  (e.g. "this is realistic" without explaining why realism is the priority here)
- **Missing data model**: a mechanic was proposed but the types, events, or state
  changes needed to implement it were not specified
- **Tension with existing architecture**: the proposal conflicts with a design
  decision already made (and documented in an SDD or prior plan), and the conflict
  was not acknowledged
- **"Yes, and" without substance**: the competing LLM agreed with the existing
  approach without adding new insight — this is not useful feedback

A rebuttal prompt is a full prompt document (same conventions as above), framed
as "here is what you said, here is why it is insufficient, answer again."

---

## 6. File Naming Convention

All prompt documents live in `docs/` and follow this pattern:

```
LLM-DesignGapsPrompt{N}-{Topic}.md
```

Where:
- `{N}` is the sequential prompt number (1, 2, 3, ...)
- `{Topic}` is a short CamelCase label for the focus area

Examples from this project:
- `LLM-DesignGapsPrompt1-KnowledgeAndQuests.md` — initial 7 gaps, knowledge/quest
- `LLM-DesignGapsPrompt2-KnowledgeAndQuests.md` — 6 further gaps from SDD analysis
- `LLM-DesignGapsPrompt3-Rebuttal.md` — Part 3 rebuttal of 7 unresolved threads

Integration plans live in `.claude/plans/` with short random names (auto-generated
by Claude Code's plan mode). The plan filename is not meaningful; the content is.

---

## 7. What This Process Is Not

- **Not a replacement for design documents.** SDDs in `docs/` are the authoritative
  spec. The competing planner prompts are inputs to the design process, not outputs.
  Decisions made via this process are recorded in the SDDs.

- **Not a way to avoid making decisions.** The competing planner is used to make
  better decisions faster, not to defer them indefinitely. If the cycle produces
  a third round of deferrals on the same question, that is a signal to make a
  decision and move on, even if imperfect.

- **Not symmetrical.** The competing LLM does not have persistent memory, does not
  read the full codebase, and is operating on a prompt written by Claude Code. Its
  feedback is valuable input, not equal vote. Claude Code integrates, challenges,
  or defers based on first-hand knowledge of the architecture.
