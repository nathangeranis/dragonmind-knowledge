# .NET Project Structure Patterns Reference

## Contents
- Single-Project-Per-Context Convention
- Global Usings Pattern
- Nullable Reference Types
- Centralized Build Settings via Directory.Build.props
- Anti-Pattern: New Project Per Layer
- Anti-Pattern: `.Result` / `.Wait()` Instead of Async

## Single-Project-Per-Context Convention

The bounded context (`Dragonmind.Knowledge`) is **one** `.csproj` containing `Domain/`, `Application/`, `Infrastructure/` as plain directories — not three separate assemblies. This keeps `dotnet build` fast and avoids namespace sprawl for what is, at any given layer, a small number of types.

```
Dragonmind.Knowledge/
├── Domain/            # zero infrastructure dependencies
├── Application/        # commands/queries/handlers
└── Infrastructure/      # EF Core, pgvector, Apache AGE repos
Dragonmind.Knowledge.csproj   # ONE project file
```

**Do:** add new aggregates/handlers as directories inside the existing project.
**Don't:** create `Dragonmind.Knowledge.Domain.csproj` as a separate assembly — that fragments a small context into more moving parts than it needs.

**WARNING:** never add infrastructure NuGet packages (EF Core, Npgsql, HTTP clients, etc.) to `Dragonmind.Core` — Foundation must stay infrastructure-free, since `Dragonmind.Knowledge` depends on it.

## Global Usings Pattern

Each project has its own `GlobalUsings.cs` instead of repeating `using` statements in every file:

```csharp
// Dragonmind.Knowledge/GlobalUsings.cs
global using Dragonmind.Core.Domain;
global using Dragonmind.Core.Application;
global using System.ComponentModel.DataAnnotations;
```

**Why it matters:** when you add a new handler file, you should almost never need a `using Dragonmind.Core.Application;` line at the top — if you find yourself adding one repeatedly across new files in the same project, add it to that project's `GlobalUsings.cs` instead. Don't add usings there that only one file needs; that defeats the purpose and hides real dependencies.

## Nullable Reference Types

Every `.csproj` has `<Nullable>enable</Nullable>`. This is not optional per-file — it's solution-wide.

```csharp
// ✅ CORRECT — explicit about what can be null
public string? Source { get; set; }
public string Content { get; set; } = string.Empty;

// ❌ INCORRECT — suppressing the compiler instead of fixing the model
public string Content { get; set; } = null!;
```

### WARNING: Suppressing nullable warnings with `!`

**The Problem:**

```csharp
// BAD - forces the compiler to trust you, then crashes at runtime instead of compile time
var document = _repository.GetByIdAsync(id).Result!;
document.Content.ToUpperInvariant();
```

**Why This Breaks:**
1. The null-forgiving operator (`!`) doesn't make something non-null — it just tells the compiler to stop warning you, moving the failure from build time to runtime.
2. A `NullReferenceException` several layers deep in a handler is far more expensive to trace than a compiler warning at the call site.
3. It silently defeats the entire reason `<Nullable>enable</Nullable>` is turned on solution-wide — one `!` in a hot path erases that safety net for every caller downstream.

**The Fix:**

```csharp
// GOOD - handle the null case explicitly
var document = await _repository.GetByIdAsync(id, ct);
if (document is null)
{
    return null;
}
```

**When You Might Be Tempted:** during a rushed handler implementation when you "know" the repository will never return null for this call. It will, eventually — a stale cache, a race between a write and a read, a bad migration. Handle it.

## Centralized Build Settings via Directory.Build.props

A single `Directory.Build.props` at the repo root holds settings every project should share, so bumping a shared analyzer or setting requires touching one file instead of every `.csproj`:

```xml
<!-- Directory.Build.props (repo root) -->
<Project>
  <PropertyGroup>
    <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
  </PropertyGroup>
</Project>
```

MSBuild imports the nearest `Directory.Build.props` walking up from each `.csproj` automatically — no explicit `<Import>` is needed in the individual project files.

### Why This Matters

Without it, a shared setting (an analyzer severity, a language version bump) that needs to apply everywhere has to be copy-pasted into every `.csproj`, and a project added later that forgets the copy silently drifts from the rest of the solution. Centralizing it means there's only one place it can go stale.

## Anti-Pattern: New Project Per Layer

### WARNING: Splitting the context into Domain/Application/Infrastructure assemblies

**The Problem:**

```
Dragonmind.Knowledge.Domain.csproj
Dragonmind.Knowledge.Application.csproj
Dragonmind.Knowledge.Infrastructure.csproj
```

**Why This Breaks:**
1. MediatR assembly scanning is configured per-assembly; splitting the context multiplies the registration surface for no benefit since these layers are internal to one context anyway.
2. Cross-project `InternalsVisibleTo` hacks start appearing to let Application see Domain internals — a smell that the split shouldn't have happened in the first place.
3. It adds project-reference and build-graph overhead for a context small enough that one assembly is easier to navigate.

**The Fix:** keep Domain/Application/Infrastructure as directories inside one project.

**When You Might Be Tempted:** copying an older .NET DDD sample that splits by layer instead of by context. Directories-within-one-project is the convention this solution uses — match it.

## Anti-Pattern: `.Result` / `.Wait()` Instead of Async

### WARNING: Blocking on async code

**The Problem:**

```csharp
// BAD - blocks the calling thread waiting on the async operation
public KnowledgeDocumentDto AddDocument(string content)
{
    var embedding = _embeddingService.GenerateEmbeddingAsync(content).Result;
    return _repository.AddAsync(document).Result;
}
```

**Why This Breaks:**
1. Under ASP.NET Core's thread pool, `.Result`/`.Wait()` on an async call can deadlock when there's no captured `SynchronizationContext` to resume on, or exhaust the thread pool under load.
2. Exceptions get wrapped in `AggregateException`, hiding the real stack trace.
3. It defeats the entire point of the async pipeline this handler chain is built around — `IEmbeddingGenerator` and every repository method are `Task`-returning precisely so callers can propagate cancellation and avoid blocking a thread on I/O.

**The Fix:**

```csharp
// GOOD - propagate async all the way up
public async Task<KnowledgeDocumentDto> AddDocumentAsync(string content, CancellationToken ct)
{
    var embedding = await _embeddingService.GenerateEmbeddingAsync(content, ct);
    return await _repository.AddAsync(document, ct);
}
```

**When You Might Be Tempted:** wiring an async service into a synchronous interface method you don't want to change the signature of. Change the signature — `async Task` should propagate to the top of the call chain, not stop partway.
