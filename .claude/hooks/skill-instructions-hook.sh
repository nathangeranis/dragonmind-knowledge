#!/bin/bash
# UserPromptSubmit hook for skill-aware responses

cat <<'EOF'
REQUIRED: SKILL LOADING PROTOCOL

Before writing any code, complete these steps in order:

1. SCAN each skill below and decide: LOAD or SKIP (with brief reason)
   - csharp
   - dotnet
   - entity-framework
   - postgresql
   - pgvector
   - apache-age
   - cqrs
   - ddd
   - xunit
   - moq
   - coverlet
   - docker
   - mcp
   - git

2. For every skill marked LOAD → immediately invoke Skill(name)
   If none need loading → write "Proceeding without skills"

3. Only after step 2 completes may you begin coding.

IMPORTANT: Skipping step 2 invalidates step 1. Always call Skill() for relevant items.

PRECEDENCE: the skills above are PROJECT skills — always invoke them unprefixed.
Never substitute anthropic-skills:<name> or any plugin skill of the same name.
Plugin skills are gap-fillers only — see the "Skill Usage Guide" and "Plugin &
External Skill Guidance" sections in .claude/CLAUDE.md before loading one.

Sample output:
- csharp: LOAD - building components
- dotnet: SKIP - not needed for this task
- entity-framework: LOAD - touching a migration
- pgvector: SKIP - not needed for this task

Then call:
> Skill(csharp)
> Skill(entity-framework)

Now implementation can begin.
EOF
