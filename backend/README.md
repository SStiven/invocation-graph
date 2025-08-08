# T-SQL Dependency Parser

This project provides a lightweight **T-SQL parser** that analyzes SQL scripts and extracts **object definitions** and the **dependencies** between them.  
It’s designed to help build an **invocation graph** showing how stored procedures, functions, views, and tables reference each other.

---

## Overview

The parser reads a T-SQL file or script, identifies the **primary object being defined** (e.g., stored procedure, view, table, function, or trigger), and scans for any **invocations or references** to other database objects.

It produces a `ParsedFile` object containing:

- **Definition** → The primary object being created (name + type)
- **Edges** → A list of `InvocationEdge` instances, each representing a call or reference from the definition to another object

---

## Supported Object Types

- **Table**
- **View**
- **StoredProcedure**
- **Trigger**
- **UserFunction** (scalar or table-valued)

---

## How It Works

### 1. Preprocessing

Before any dependency detection, the parser cleans up the SQL to avoid false positives:

- Removes comments (`--` and `/* ... */`)
- Removes string literals (e.g., `'2025-01-01'`)
- Strips parameterized type definitions  
  _Example_: `DECIMAL(10,2)` → `DECIMAL`
- Removes column lists in:
  - `INSERT INTO` statements
  - `MERGE ... WHEN MATCHED/NOT MATCHED ... INSERT(...)` clauses
- Keeps only the essential object references

---

### 2. Definition Extraction

The parser locates the **main `CREATE` statement**:

- Supported: `CREATE PROCEDURE`, `CREATE TABLE`, `CREATE VIEW`, `CREATE FUNCTION`, `CREATE TRIGGER`
- Extracts:
  - **Object type** (mapped to `SqlObjectType` enum)
  - **Fully qualified name** (schema + object)

---

### 3. Dependency Detection

The parser detects different types of dependencies based on context:

| Object Type / Reference             | Detection Rule                                                                                  |
|--------------------------------------|-------------------------------------------------------------------------------------------------|
| **Stored Procedures**                | `EXEC` / `EXECUTE` statements without parentheses                                               |
| **Table-Valued Functions (TVFs)**    | Found in `FROM ...()` clauses                                                                   |
| **Scalar Functions**                 | `<name>(...)` calls not in a `FROM` clause, excluding known keywords (e.g., `VALUES`)           |
| **Tables (queries)**                 | Found in `FROM` / `JOIN` clauses without parentheses                                            |
| **Tables (DML)**                      | Target of `INSERT`, `UPDATE`, and `DELETE` statements                                           |
| **MERGE Target**                      | Extracted from `MERGE <table>`                                                                  |
| **MERGE Source Table**                | Extracted from `USING <table>`                                                                  |
| **MERGE Subquery Source**             | Extracted from `USING (<subquery>) ...` by parsing all `FROM` / `JOIN` inside the subquery       |

The parser **skips self-references** (when the caller object references itself).

---

### 4. Deduplication

- All detected edges are normalized and deduplicated
- Normalization:
  - Removes quoting differences (`dbo.Table` vs `[dbo].[Table]`)
  - Ignores case differences
- Deduplication key = `(Caller Name, Callee Type, Callee Name)`

---

## Output Model

### `ParsedFile`
```csharp
public record ParsedFile(SqlObject Definition, List<InvocationEdge> Edges);
```
- **Definition** → `SqlObject` (Name, Type)
- **Edges** → List of `InvocationEdge`

### `InvocationEdge`
```csharp
public record InvocationEdge(SqlObject Caller, SqlObject Callee);
```
- **Caller** → The defining object
- **Callee** → The referenced object

### `SqlObject`
```csharp
public record SqlObject(string Name, SqlObjectType Type);
```
- **Name** → Fully qualified name as in SQL
- **Type** → `SqlObjectType` enum:
  - `Table`
  - `View`
  - `StoredProcedure`
  - `Trigger`
  - `UserFunction`

---

## Key Features

- **Robust SQL object name handling** — handles various quoting styles and schema formats
- **Accurate self-call detection** — avoids false dependencies when an object calls itself
- **Noise filtering** — ignores comments, strings, type declarations, known function-like keywords
- **MERGE support** — detects both the target and real source tables, including sources inside subqueries
- **Works with mixed T-SQL formatting** — insensitive to casing and whitespace variations

---

## Example

### Input
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

### Output
**Definition**: `[dbo].[OrderSummary]` (StoredProcedure)  

**Edges**:
- `dbo.UpdateStatistics` (StoredProcedure)
- `[dbo].[GetOrders]` (UserFunction)
- `dbo.Customers` (Table)
- `dbo.SalesSummary` (Table) — MERGE target
- `dbo.RecentSales` (Table) — MERGE subquery source

---

## Testing

Unit tests validate:

- Parsing each supported object type
- Ignoring noise (comments, strings, data type params)
- Correctly handling MERGE statements (both target and subquery source detection)
- Deduplication logic