---
name: git
description: |
  Manages Git version control and branching strategy for this repository.
  Use when: creating branches, writing commit messages, managing PRs, resolving conflicts,
  reviewing git history, or following the project's branching conventions.
allowed-tools: Read, Edit, Write, Glob, Grep, Bash
---

# Git Skill

This repository uses a trunk-based workflow with short-lived feature branches off `main`. The commit
format is conventional commits (`feat:`, `fix:`, etc.), and PRs require passing tests before merge. The
solution is small by design — two library projects (`src/Dragonmind.Core`, `src/Dragonmind.Knowledge`)
plus two test projects — so most commits touch a single layer; scope your changes accordingly.

## Quick Start

### Create a feature branch

```bash
git checkout main && git pull
git checkout -b feature/graph-traversal-max-depth
```

### Stage and commit with correct format

```bash
git add src/Dragonmind.Knowledge/Infrastructure/Persistence/Repositories/ApacheAgeKnowledgeGraphRepository.cs
git add tests/Dragonmind.Knowledge.UnitTests/

git commit -m "feat: Add depth-limited graph traversal for KnowledgeFact queries

Limits Cypher path length via a maxDepth parameter, so a traversal over a large
graph fails fast instead of timing out.

- Add maxDepth parameter to GetFactsWithinDistanceAsync's Cypher query
- Add unit tests for the depth boundary condition

Co-Authored-By: Claude <noreply@anthropic.com>"
```

### Pre-push validation

```bash
dotnet build Dragonmind.Knowledge.sln
dotnet test tests/Dragonmind.Knowledge.UnitTests
git push -u origin feature/graph-traversal-max-depth
```

## Branch Naming

| Type | Pattern | Example |
|------|---------|---------|
| Feature | `feature/name` | `feature/vector-search-scope-filter` |
| Bug fix | `bugfix/name` | `bugfix/graph-edge-scope-leak` |
| Enhancement | `enhancement/name` | `enhancement/cache-invalidation-retry` |
| Docs | `docs/name` | `docs/update-claude-md-architecture` |
| Claude Code (agent-generated) | `claude/<adjective>-<name>` | `claude/tidy-knowledge-graph-cleanup` |
| Copilot agent (agent-generated) | `copilot/<description>` | `copilot/fix-scope-id-validation` |

The `claude/*` and `copilot/*` patterns are generated automatically by agent sessions — recognize them
when reading history, but do not use them when naming a branch by hand.

## Commit Types

| Type | When | Example |
|------|------|---------|
| `feat` | New functionality | `feat: Add depth-limited graph traversal for KnowledgeFact queries` |
| `fix` | Bug fixes | `fix: Constrain every edge in a graph traversal to its scope` |
| `refactor` | Code restructure (no behavior change) | `refactor: Extract handler registration into AddKnowledgeContext` |
| `test` | Test additions/fixes | `test: Add domain tests for RelationshipTypes.IsAllowed` |
| `chore` | Build/tooling/migration | `chore: Add HNSW index migration for knowledge.documents` |
| `docs` | Documentation updates | `docs: Update CLAUDE.md with the ACL boundary test list` |

## Co-Authored-By Trailer

Commits authored through Claude Code carry a trailer crediting the assistant, as in the example above:
`Co-Authored-By: Claude <noreply@anthropic.com>`. Keep it on any commit Claude Code writes; a
human-authored commit does not need it.

## PR Workflow Checklist

Copy this checklist for every PR:
- [ ] Branch created from latest `main`
- [ ] `dotnet build Dragonmind.Knowledge.sln` passes with zero warnings
- [ ] `dotnet test tests/Dragonmind.Knowledge.UnitTests` passes
- [ ] Handlers NOT registered in DI — MediatR auto-discovers them via assembly scanning
- [ ] No cross-layer shortcuts — the ACL facade (`IKnowledgeContextFacade`) is the only way in
- [ ] Migration created if the EF Core schema changed
- [ ] PR description explains WHY, not what

## Related Skills

See the **dotnet** skill for build/test commands. See the **entity-framework** skill for migration
commits. See the **xunit** skill for test-related commits.
