# T‑SQL Dependency Parser (​.NET 8)

A lightweight parser that scans T‑SQL scripts to extract the **primary object definition** (procedure, view, table, function, trigger) and the **dependencies** that object references. The output is a dependency graph: **nodes = SQL objects**, **edges = invocations/references**.

> Built for static analysis workflows: impact analysis, invocation graphs, refactoring prep, and auto‑documentation of database code.

---

## Table of Contents
- [Overview](#overview)
- [Quick Start](#quick-start)
- [How It Works](#how-it-works)
  - [Preprocessing](#preprocessing)
  - [Definition Extraction](#definition-extraction)
  - [Dependency Detection](#dependency-detection)
  - [Deduplication & Self‑calls](#deduplication--self-calls)
- [Data Model](#data-model)
- [Example](#example)
- [Notes & Limitations](#notes--limitations)
- [Testing](#testing)
- [Roadmap](#roadmap)

---

## Overview

Given a single T‑SQL script, the parser locates the **first** `CREATE` statement, treats that as the file’s **definition**, and then finds outbound references to other objects.

**Output** is a `ParsedFile`:
- **Definition** — the created `SqlObject` with name and type.
- **Edges** — deduplicated list of `InvocationEdge` (`Caller` → `Callee`).

The parser does **not** connect to a database; it’s regex‑driven static analysis.

---

## Quick Start

```csharp
using System;
using System.IO;
using TsqlDependencyParser;

var sql = File.ReadAllText("MyProcedure.sql");
var parser = new TsqlParser();

var result = parser.Parse(sql);
if (result is null)
{
    Console.WriteLine("No CREATE statement found.");
    return;
}

Console.WriteLine($"Definition: {result.Definition.Name} ({result.Definition.Type})");
foreach (var e in result.Edges)
{
    Console.WriteLine($"  -> {e.Callee.Name} ({e.Callee.Type})");
}
```

> `Parse` returns `null` if the script contains no supported `CREATE` statement.

---

## How It Works

### Preprocessing

To reduce false positives, the input text is normalized before extraction:

- Remove line and block comments (`--`, `/* ... */`).
- Remove string literals (`'like this'`).
- Collapse parameterized type declarations so numbers don’t look like function calls (e.g., `DECIMAL(10,2)` → `DECIMAL`).
- Remove column lists from `INSERT INTO ... (col1, col2, ...)` and from `MERGE ... INSERT (...)` clauses.
- Normalize quoting for later canonicalization.

### Definition Extraction

Finds the **first** supported `CREATE` statement and extracts the object type/name:

- `CREATE PROCEDURE | TABLE | VIEW | FUNCTION | TRIGGER`
- Name supports `[brackets]`, `"quotes"`, and unquoted forms.
- The parser returns **one** `Definition` per script (the first match).

### Dependency Detection

Specialized extractors scan for references (callee objects) from the definition:

| Pattern / Context                                   | Emitted Callee Type | Examples                                         |
|-----------------------------------------------------|---------------------|--------------------------------------------------|
| `EXEC` / `EXECUTE` (no parentheses)                 | `StoredProcedure`   | `EXEC dbo.DoWork`                                |
| TVFs in `FROM ...()`                                | `UserFunction`      | `FROM dbo.GetData()`                             |
| Objects in `FROM` / `JOIN` (not subqueries)         | `Table`             | `FROM dbo.Customers`                             |
| DML targets: `INSERT INTO`, `DELETE FROM`           | `Table`             | `INSERT INTO dbo.Target`                         |
| DML target: `UPDATE schema.object ...`              | `Table`             | `UPDATE dbo.Target SET ...`                      |
| `MERGE <target>`                                    | `Table`             | `MERGE dbo.Target AS t`                          |
| `MERGE ... USING <table>`                           | `Table`             | `USING dbo.Source`                               |
| `MERGE ... USING (<subquery>)` → tables inside it   | `Table`             | `USING (SELECT * FROM dbo.RealSource) AS s`      |
| Scalar calls `<name>(...)` (outside `FROM`)         | `UserFunction`      | `SELECT dbo.CalcAmount(@x)`, `SELECT GETDATE()`* |

\* See **Notes & Limitations** for how built‑ins are handled.

The `FROM`/`JOIN` scan explicitly **skips** tables that appear inside a `MERGE ... USING (subquery)` to avoid duplicates (they are handled by the MERGE extractor).

### Deduplication & Self‑calls

- **Self‑reference filtering** removes edges where `Caller` and `Callee` are the same logical object.
- **Canonicalization** for deduplication:
  - Case‑insensitive
  - Brackets/quotes removed
  - No schema inference (see Notes)

Deduplication key is `(CallerName, CalleeType, CalleeName)` after canonicalization.

---

## Data Model

```csharp
public enum SqlObjectType
{
    Table,
    View,
    StoredProcedure,
    Trigger,
    UserFunction
}

public record SqlObject(string Name, SqlObjectType Type);

public record InvocationEdge(SqlObject Caller, SqlObject Callee);

public record ParsedFile(SqlObject Definition, List<InvocationEdge> Edges);
```

---

## Example

**Input**

```sql
CREATE PROCEDURE [dbo].[OrderSummary]
AS
BEGIN
    EXEC dbo.UpdateStatistics;

    SELECT *
    FROM [dbo].[GetOrders](GETDATE()) AS o
    JOIN dbo.Customers c ON c.Id = o.CustomerId;

    MERGE dbo.SalesSummary AS t
    USING (SELECT * FROM dbo.RecentSales) AS s
    ON t.Id = s.Id
    WHEN NOT MATCHED THEN
        INSERT (Id, Total) VALUES (s.Id, s.Total);
END
```

**Output (conceptual)**

```
Definition: [dbo].[OrderSummary] (StoredProcedure)

Edges:
- dbo.UpdateStatistics (StoredProcedure)
- [dbo].[GetOrders] (UserFunction)      -- TVF in FROM
- dbo.Customers (Table)                 -- FROM/JOIN
- dbo.SalesSummary (Table)              -- MERGE target
- dbo.RecentSales (Table)               -- MERGE subquery source
```

> Note: `GETDATE()` is inside a `FROM` TVF call, so it is **not** emitted by the scalar‑function extractor.

---

## Notes & Limitations

- **One definition per script** — Only the **first** supported `CREATE` is used; others are ignored. `Parse` returns `null` if none are found.
- **`CREATE` only** — `ALTER` and `CREATE OR ALTER` are **not** recognized as definitions.
- **FROM/JOIN is typed as `Table`** — Without catalog access, the parser can’t distinguish tables vs. views in `FROM`/`JOIN`; it emits `Table` by design.
- **UPDATE target must be qualified** — `UPDATE` targets are detected only when **schema‑qualified** (e.g., `UPDATE dbo.Target ...`). Unqualified `UPDATE Target ...` isn’t captured as a target (tables in a following `FROM/JOIN` still are).
- **APPLY not scanned** — `CROSS/OUTER APPLY` are not currently parsed for table sources; only `FROM` / `JOIN` are recognized.
- **Scalar call handling** — Any `name(...)` outside `FROM` is treated as a `UserFunction`, including built‑ins like `GETDATE()`, except for a small ignore list (e.g., `VALUES`, `USING`, `INSERT`). If you don’t want built‑ins, add a built‑ins filter.
- **No schema inference** — Canonicalization strips quotes and ignores case but **does not** add default schemas. `Orders` ≠ `dbo.Orders` for dedup/self‑call purposes.
- **Regex‑driven** — This is not a full SQL grammar; it’s intentionally pragmatic for fast static scanning.

---

## Testing

xUnit tests cover:
- Definition extraction for each type
- Ignoring comments/strings/type parameters/column lists
- MERGE target + source (including subquery) handling
- Self‑reference filtering and deduplication
- Quoted identifiers, mixed casing, and formatting variations

---

## Roadmap

- Recognize `ALTER` and `CREATE OR ALTER` as definitions
- Distinguish views vs. tables in `FROM`/`JOIN` using optional catalog metadata
- Detect `CROSS/OUTER APPLY` sources
- Expand the ignore list / built‑ins filter for scalar calls
- Support multiple definitions per script (optional mode)
