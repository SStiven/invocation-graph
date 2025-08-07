using InvocationGraph.UI;

namespace InvocationGraph.Tests;

public class TsqlParserTests
{
    [Fact]
    public void Parse_WhenProcedureCreated_ShouldReturnDefinitionWithNoEdges()
    {
        var text = "CREATE PROCEDURE [dbo].[MyProcedure]";
        var result = TsqlParser.Parse(text);

        Assert.NotNull(result);
        Assert.Equal("[dbo].[MyProcedure]", result.Definition.Name);
        Assert.Equal(SqlObjectType.StoredProcedure, result.Definition.Type);
        Assert.Empty(result.Edges);
    }

    [Fact]
    public void Parse_WhenTableCreated_ShouldReturnDefinitionWithNoEdges()
    {
        var text = "CREATE TABLE dbo.TableName";
        var result = TsqlParser.Parse(text);

        Assert.NotNull(result);
        Assert.Equal("dbo.TableName", result.Definition.Name);
        Assert.Equal(SqlObjectType.Table, result.Definition.Type);
        Assert.Empty(result.Edges);
    }

    [Fact]
    public void Parse_WhenViewCreated_ShouldReturnDefinitionWithNoEdges()
    {
        var text = "CREATE VIEW [dbo].MyView";
        var result = TsqlParser.Parse(text);

        Assert.NotNull(result);
        Assert.Equal("[dbo].MyView", result.Definition.Name);
        Assert.Equal(SqlObjectType.View, result.Definition.Type);
        Assert.Empty(result.Edges);
    }

    [Fact]
    public void Parse_WhenNoCreateStatementPresent_ShouldReturnNull()
    {
        var text = "SELECT * FROM MyDatabase";
        var result = TsqlParser.Parse(text);

        Assert.Null(result);
    }

    [Fact]
    public void Parse_WhenExecCallsPresent_ShouldCaptureInvocationEdges()
    {
        var text = @"
                CREATE PROCEDURE [dbo].[Proc]
                AS
                BEGIN
                    EXEC dbo.OtherProc;
                    EXECUTE [schema].[Another];
                END";

        var result = TsqlParser.Parse(text);

        Assert.NotNull(result);
        Assert.Equal("[dbo].[Proc]", result.Definition.Name);
        Assert.Equal(SqlObjectType.StoredProcedure, result.Definition.Type);

        Assert.Equal(2, result.Edges.Count);
        Assert.Contains(result.Edges, e =>
            e.Callee.Name == "dbo.OtherProc" &&
            e.Callee.Type == SqlObjectType.StoredProcedure);
        Assert.Contains(result.Edges, e =>
            e.Callee.Name == "[schema].[Another]" &&
            e.Callee.Type == SqlObjectType.StoredProcedure);
    }

    [Fact]
    public void Parse_WhenScalarUdfCallsPresent_ShouldCaptureInvocationEdges()
    {
        var text = @"
                CREATE FUNCTION dbo.MyFunc()
                RETURNS INT
                AS
                BEGIN
                    RETURN dbo.OtherFunc();
                END";

        var result = TsqlParser.Parse(text);

        Assert.NotNull(result);
        Assert.Equal("dbo.MyFunc", result.Definition.Name);
        Assert.Equal(SqlObjectType.UserFunction, result.Definition.Type);

        var edge = Assert.Single(result.Edges);
        Assert.Equal("dbo.OtherFunc", edge.Callee.Name);
        Assert.Equal(SqlObjectType.UserFunction, edge.Callee.Type);
    }

    [Fact]
    public void Parse_WhenCallsInsideComments_ShouldIgnoreExecutions()
    {
        var text = @"
                CREATE PROCEDURE dbo.Proc
                AS
                BEGIN
                    -- EXEC dbo.CommentedProc
                    /* EXEC dbo.BlockCommentProc; */
                    EXEC dbo.RealProc;
                END";

        var result = TsqlParser.Parse(text);

        Assert.NotNull(result);
        var edge = Assert.Single(result.Edges);
        Assert.Equal("dbo.RealProc", edge.Callee.Name);
    }

    [Fact]
    public void Parse_WhenCallsInsideStringLiterals_ShouldIgnoreExecutions()
    {
        var text = @"
                CREATE PROCEDURE dbo.Proc
                AS
                BEGIN
                    SELECT 'EXEC dbo.FakeProc';
                    EXEC dbo.RealProc;
                END";

        var result = TsqlParser.Parse(text);

        Assert.NotNull(result);
        var edge = Assert.Single(result.Edges);
        Assert.Equal("dbo.RealProc", edge.Callee.Name);
    }

    [Fact]
    public void Parse_WhenTableValuedFunctionInvoked_ShouldCaptureInvocationEdge()
    {
        var text = @"
                CREATE PROCEDURE dbo.Proc
                AS
                BEGIN
                    SELECT * FROM dbo.MyTvf(123);
                END";

        var result = TsqlParser.Parse(text);

        Assert.NotNull(result);
        var edge = Assert.Single(result.Edges);
        Assert.Equal("dbo.MyTvf", edge.Callee.Name);
        Assert.Equal(SqlObjectType.UserFunction, edge.Callee.Type);
    }

    [Fact]
    public void Parse_WhenScalarFunctionInvoked_ShouldCaptureInvocationEdge()
    {
        var text = @"
                CREATE FUNCTION dbo.MyFunc()
                RETURNS INT
                AS
                BEGIN
                    DECLARE @x INT = dbo.OtherFunc(1);
                    RETURN @x;
                END";

        var result = TsqlParser.Parse(text);

        Assert.NotNull(result);
        var edge = Assert.Single(result.Edges);
        Assert.Equal("dbo.OtherFunc", edge.Callee.Name);
        Assert.Equal(SqlObjectType.UserFunction, edge.Callee.Type);
    }

    [Fact]
    public void Parse_WhenQuotedIdentifiersUsed_ShouldCaptureInvocationEdge()
    {
        var text = @"
                CREATE VIEW [schema].[MyView]
                AS
                SELECT * FROM ""schema"".""fn_Test""();";

        var result = TsqlParser.Parse(text);

        Assert.NotNull(result);
        Assert.Equal("[schema].[MyView]", result.Definition.Name);

        var edge = Assert.Single(result.Edges);
        Assert.Equal("\"schema\".\"fn_Test\"", edge.Callee.Name);
        Assert.Equal(SqlObjectType.UserFunction, edge.Callee.Type);
    }

    [Fact]
    public void Parse_WhenCallsInsideMultiLineBlockComments_ShouldIgnoreExecutions()
    {
        var text = @"
                CREATE PROCEDURE dbo.Proc
                AS
                BEGIN
                    /*
                       EXEC dbo.NotReal;
                       The call above spans
                       multiple lines inside a comment.
                    */
                    EXEC dbo.RealProc;
                END";

        var result = TsqlParser.Parse(text);

        Assert.NotNull(result);
        var edge = Assert.Single(result.Edges);
        Assert.Equal("dbo.RealProc", edge.Callee.Name);
    }

    [Fact]
    public void Parse_WhenFunctionRecurses_ShouldSkipSelfEdge()
    {
        var text = @"
                CREATE FUNCTION dbo.Recursive()
                RETURNS INT
                AS
                BEGIN
                    RETURN dbo.Recursive();
                END";

        var result = TsqlParser.Parse(text);

        Assert.NotNull(result);
        Assert.Empty(result.Edges);
    }

    [Fact]
    public void Parse_WhenVariableDeclaredWithParameterizedType_ShouldNotTreatTypeAsFunction()
    {
        var text = @"
        CREATE PROCEDURE dbo.Proc
        AS
        BEGIN
            DECLARE @n DECIMAL(10,2) = 0;
            DECLARE @s NVARCHAR(50);
            SELECT 1;
        END";

        var result = TsqlParser.Parse(text);

        Assert.NotNull(result);
        Assert.Equal("dbo.Proc", result.Definition.Name);
        Assert.Equal(SqlObjectType.StoredProcedure, result.Definition.Type);
        Assert.Empty(result.Edges);
    }

    [Fact]
    public void Parse_WhenProcHasParamsWithParameterizedTypes_ShouldNotTreatTypesAsFunctions()
    {
        var text = @"
        CREATE PROCEDURE dbo.Proc
            @Id INT,
            @Name NVARCHAR(50),
            @Amount DECIMAL(10,2)
        AS
        BEGIN
            SELECT 1;
        END";

        var result = TsqlParser.Parse(text);

        Assert.NotNull(result);
        Assert.Equal("dbo.Proc", result.Definition.Name);
        Assert.Equal(SqlObjectType.StoredProcedure, result.Definition.Type);
        Assert.Empty(result.Edges);
    }

    [Fact]
    public void Parse_ComplexScript_AllSignalsCaptured_NoiseIgnored_SelfSkipped()
    {
        var text = @"
        CREATE PROCEDURE [dbo].[Proc_Complex]
            @Id INT,
            @Name NVARCHAR(50),
            @Amount DECIMAL(10,2)
        AS
        BEGIN
            -- Noise in comments: EXEC dbo.ShouldNotAppear; dbo.NopeFunc(1); NVARCHAR(77)
            /* Also: DECIMAL(9,9) and EXEC [dbo].[NopeInBlock] */

            DECLARE @str NVARCHAR(100);
            DECLARE @m DECIMAL(18,6);

            -- Real scalar function call
            SET @Id = [dbo].[udf_Scalar](@Id);

            -- TVF in FROM
            SELECT *
            FROM [dbo].[tvf_GetStuff](@Id) AS g;

            -- Stored proc calls
            EXEC [dbo].[RunThis];
            EXECUTE dbo.AnotherProc;

            -- Self invocation (must be ignored)
            EXEC [dbo].[Proc_Complex];

            -- Noise in strings
            SELECT 'EXEC dbo.FakeProc; dbo.FakeFunc(1)';
        END";

        var result = TsqlParser.Parse(text);

        // Definition checks
        Assert.NotNull(result);
        Assert.Equal("[dbo].[Proc_Complex]", result!.Definition.Name);
        Assert.Equal(SqlObjectType.StoredProcedure, result.Definition.Type);

        // Expect exactly these four edges:
        // - Stored procs: [dbo].[RunThis], dbo.AnotherProc
        // - Functions:    [dbo].[tvf_GetStuff] (TVF), [dbo].[udf_Scalar] (scalar)
        Assert.Equal(4, result.Edges.Count);

        // Every edge must originate from the same caller (the definition)
        Assert.All(result.Edges, e =>
        {
            Assert.Equal(result.Definition.Name, e.Caller.Name);
            Assert.Equal(result.Definition.Type, e.Caller.Type);
        });

        // Stored procedure edges (EXEC / EXECUTE)
        Assert.Contains(result.Edges, e =>
            e.Callee.Name == "[dbo].[RunThis]" &&
            e.Callee.Type == SqlObjectType.StoredProcedure);

        Assert.Contains(result.Edges, e =>
            e.Callee.Name == "dbo.AnotherProc" &&
            e.Callee.Type == SqlObjectType.StoredProcedure);

        // Function edges (TVF + scalar)
        Assert.Contains(result.Edges, e =>
            e.Callee.Name == "[dbo].[tvf_GetStuff]" &&
            e.Callee.Type == SqlObjectType.UserFunction);

        Assert.Contains(result.Edges, e =>
            e.Callee.Name == "[dbo].[udf_Scalar]" &&
            e.Callee.Type == SqlObjectType.UserFunction);

        // No self edge
        Assert.DoesNotContain(result.Edges, e =>
            string.Equals(e.Callee.Name, result.Definition.Name, StringComparison.OrdinalIgnoreCase));

        // Guardrails: parameterized types and noise should NOT appear as edges
        Assert.DoesNotContain(result.Edges, e =>
            e.Callee.Name.Equals("DECIMAL", StringComparison.OrdinalIgnoreCase) ||
            e.Callee.Name.Equals("NVARCHAR", StringComparison.OrdinalIgnoreCase) ||
            e.Callee.Name.Equals("dbo.FakeProc", StringComparison.OrdinalIgnoreCase) ||
            e.Callee.Name.Equals("dbo.FakeFunc", StringComparison.OrdinalIgnoreCase) ||
            e.Callee.Name.Equals("[dbo].[NopeInBlock]", StringComparison.OrdinalIgnoreCase));
    }

}
