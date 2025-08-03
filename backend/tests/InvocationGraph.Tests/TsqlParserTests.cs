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
}
