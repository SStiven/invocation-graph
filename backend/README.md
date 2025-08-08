# T-SQL Dependency Parser

This project provides a lightweight T-SQL parser that analyzes SQL scripts and extracts object definitions and dependencies between them.  
It’s designed to help build an invocation graph showing how stored procedures, functions, views, and tables reference each other.

## Overview
The parser reads a T-SQL file or script, identifies the primary object being defined (e.g., a stored procedure, view, table, function, or trigger), and scans for any invocations or references to other database objects.  
It produces a `ParsedFile` object that contains:

- **Definition** → The primary object being created (name + type).  
- **Edges** → A list of `InvocationEdge` instances, each representing a call or reference from the definition to another object.

## Supported Object Types
- Table
- View
- StoredProcedure
- Trigger
- UserFunction (scalar or table-valued)

## How It Works

### Preprocessing
- Removes comments (`--` and `/* ... */`) and string literals to avoid false matches.
- Strips parameterized type definitions (e.g., `DECIMAL(10,2)` → `DECIMAL`) to prevent mistaken function matches.
- Removes column lists in `INSERT INTO` statements so they aren’t misinterpreted as function calls.

### Definition Extraction
- Detects the primary `CREATE` statement using a regex (`CREATE PROCEDURE|TABLE|VIEW|FUNCTION|TRIGGER`) and extracts:
  - Object type
  - Fully qualified name (including schema if present)

### Dependency Detection
- **Stored Procedures** → `EXEC` / `EXECUTE` statements without parentheses.
- **Table-Valued Functions (TVFs)** → Found in `FROM ...()` clauses.
- **Scalar Functions** → Identified by `<name>(...)` calls that are not in a `FROM` clause.
- **Tables** → Found in `FROM` and `JOIN` clauses without parentheses, as well as targets of `INSERT`, `UPDATE`, and `DELETE` statements.
- Ignores known keywords that look like functions (e.g., `VALUES`).
- Skips self-references using a canonical name comparison that normalizes:
  - Quoting styles (`dbo.Table` vs `[dbo].[Table]` vs `"dbo"."Table"`)
  - Case differences

### Deduplication
- Ensures that multiple references to the same logical object (even with different quoting or casing) are recorded only once.
- Deduplication key includes:
  - Caller name
  - Callee type
  - Callee name (canonicalized)

## Output Model

### ParsedFile
- `Definition`: `SqlObject` (Name, Type)
- `Edges`: List of `InvocationEdge`

### InvocationEdge
- `Caller`: `SqlObject`
- `Callee`: `SqlObject`

### SqlObject
- `Name`: Fully qualified name as it appears in SQL (original form preserved)
- `Type`: `SqlObjectType` enum (Table, View, StoredProcedure, Trigger, UserFunction)

## Key Features
- Robust SQL object name handling — works with various quoting and schema formats.
- Accurate self-call detection — avoids false edges when an object calls itself.
- Noise filtering — skips references inside comments, strings, type declarations, and known keywords.
- Supports mixed T-SQL formatting — works with different styles and casing.

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
END
```

### Output
**Definition**: `[dbo].[OrderSummary]` (StoredProcedure)  

**Edges**:
- `dbo.UpdateStatistics` (StoredProcedure)
- `[dbo].[GetOrders]` (UserFunction)
- `dbo.Customers` (Table)
