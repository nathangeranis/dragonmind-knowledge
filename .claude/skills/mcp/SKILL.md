---
name: mcp
description: |
  Explains the third-party knowledge-base MCP server this repo's .mcp.json wires up for coding agents.
  Use when: starting the agents profile, troubleshooting an MCP connection error, reading or editing
  .mcp.json, or deciding whether something belongs in the knowledge base versus a code comment.
allowed-tools: Read, Edit, Glob, Grep, Bash
---

# MCP Skill

`.mcp.json` wires Claude Code to one MCP server, which is **not built by this project**:
[`mbcrawfo/KnowledgeBaseServer`](https://github.com/mbcrawfo/KnowledgeBaseServer) (MIT licensed), a
small SQLite-backed memory store exposed over MCP. It has nothing to do with the pgvector/Apache AGE
Knowledge context this repo implements — the naming overlap is coincidental. It's included here as a
worked example of wiring an external MCP server for agent sessions, alongside the code it was used to
build.

## What `.mcp.json` runs

```json
{
  "mcpServers": {
    "knowledgebase": {
      "command": "docker",
      "args": ["exec", "--interactive", "--env", "DATABASE_PATH=/db/dragonmind.db",
                "knowledgebase-mcp", "dotnet", "/app/KnowledgeBaseServer.dll"]
    }
  }
}
```

Claude Code runs this `docker exec` once per session to start the server's stdio process inside the
already-running `knowledgebase-mcp` container — it does not start the container itself.

## Starting the container

```bash
docker compose --profile agents up -d
```

`knowledgebase-mcp` (profile `agents` in `docker-compose.yml`) is an idle container — its entrypoint is
`sleep infinity` — that exists only to give `docker exec` somewhere to run the server binary, with a
named volume (`knowledgebase:`) so the SQLite file at `DATABASE_PATH` survives restarts. It is separate
from the `db` service that backs the Knowledge context itself; nothing in `src/` talks to it.

## Tools it exposes

Once connected, five tools appear as `mcp__knowledgebase__*`:

| Tool | Purpose |
|------|---------|
| `SearchMemory` | Full-text search over stored memories |
| `CreateMemory` | Write a new memory to a topic |
| `ConnectMemories` | Link a memory to related ones (e.g. a correction to the one it replaces) |
| `GetMemoryById` | Fetch one memory by id |
| `GetTopics` | List the topics that exist |

## Search-before-write discipline

`.claude/CLAUDE.md` covers what earns a memory a place in this store, which topics are writable, and
the importance scale — read it before writing to the knowledge base. In short: `SearchMemory` the target
topic first, and extend or correct an existing memory rather than adding a second one that says the same
thing.

## Related Skills

- See the **docker** skill for the `agents` compose profile and the rest of the stack
