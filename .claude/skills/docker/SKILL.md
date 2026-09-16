---
name: docker
description: |
  Configures the local Docker Compose stack: Postgres with pgvector and Apache AGE, the containerized
  test run, and the opt-in agents profile.
  Use when: starting or resetting the local database, running the full test suite in one command,
  diagnosing why an integration test can't reach Postgres, or changing docker-compose.yml or either Dockerfile.
allowed-tools: Read, Edit, Write, Glob, Grep, Bash
---

# Docker Skill

Everything runs from one `docker-compose.yml` with three services, gated by two profiles:

| Service | Profile | What it is |
|---------|---------|------------|
| `db` | *(default — no profile)* | Postgres 16 with pgvector and Apache AGE, on `127.0.0.1:5455` |
| `tests` | `test` | Builds the solution and runs the full suite against `db` |
| `knowledgebase-mcp` | `agents` | Third-party MCP server container (see the **mcp** skill) |

## The Postgres image

`docker/postgres/Dockerfile` starts from `apache/age:release_PG16_1.6.0` (Postgres 16 with the AGE graph
extension already built in) and adds pgvector as an apt package on top:

```dockerfile
FROM apache/age:release_PG16_1.6.0
USER root
RUN apt-get update && apt-get install -y --no-install-recommends postgresql-16-pgvector
USER postgres
```

The container is started with `shared_preload_libraries=age` — AGE (unlike pgvector) must be preloaded
at server start, not just `CREATE EXTENSION`'d. The `vector` and `age` extensions themselves, and the
`knowledge_graph` graph, are created by the EF Core `InitialCreate` migration the first time the app or
the test suite runs against a fresh database — the image just makes both extensions available to load.

## Why trust auth

```yaml
POSTGRES_HOST_AUTH_METHOD: trust
ports:
  - "127.0.0.1:${KNOWLEDGE_DB_PORT:-5455}:5432"
```

No password exists anywhere in this repo. That's safe specifically because the port is bound to
`127.0.0.1` — nothing outside the host machine can reach it — and the container holds no data anyone
needs to protect: it's a disposable local database you can `down -v` and rebuild in seconds. Don't lift
this pattern into a compose file that binds `0.0.0.0` or ships real data.

## Quick Start

### One-command test run (build image, start Postgres, run everything, exit)

```bash
docker compose --profile test up --build --exit-code-from tests
```

This is what CI runs. `tests` waits on `db`'s healthcheck, then builds and runs
`Dragonmind.Knowledge.sln` in Release inside `docker/tests/Dockerfile`
(`mcr.microsoft.com/dotnet/sdk:10.0`), with `KNOWLEDGE_TEST_CONNECTION` pointed at the `db` service.

### Dev loop (keep Postgres running, iterate with the local SDK)

```bash
docker compose up -d --wait db
dotnet test tests/Dragonmind.Knowledge.IntegrationTests
```

### Start the agents profile

```bash
docker compose --profile agents up -d
```

See the **mcp** skill for what this container is and how Claude Code reaches it.

### Reset everything

```bash
docker compose down -v
```

Drops the `pgdata` volume along with it — the next `up` starts from a clean database and the migration
recreates the schema from scratch. Safe to run any time; there is nothing in this stack worth keeping.

## Related Skills

- See the **postgresql** and **pgvector** skills for what's inside the database once it's running
- See the **apache-age** skill for the graph the `age` extension backs
- See the **entity-framework** skill for the migration that creates the schema
- See the **mcp** skill for the `agents` profile's container
