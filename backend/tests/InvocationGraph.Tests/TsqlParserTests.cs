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
        Assert.Contains(result.Edges, e => e.Callee.Name == "dbo.OtherProc" && e.Callee.Type == SqlObjectType.StoredProcedure);
        Assert.Contains(result.Edges, e => e.Callee.Name == "[schema].[Another]" && e.Callee.Type == SqlObjectType.StoredProcedure);
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
                   Multiple lines
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
                DECLARE @str NVARCHAR(100);
                DECLARE @m DECIMAL(18,6);
                SET @Id = [dbo].[udf_Scalar](@Id);
                SELECT * FROM [dbo].[tvf_GetStuff](@Id) AS g;
                EXEC [dbo].[RunThis];
                EXECUTE dbo.AnotherProc;
                EXEC [dbo].[Proc_Complex];
                SELECT 'EXEC dbo.FakeProc; dbo.FakeFunc(1)';
            END";

        var result = TsqlParser.Parse(text);

        Assert.NotNull(result);
        Assert.Equal("[dbo].[Proc_Complex]", result!.Definition.Name);
        Assert.Equal(SqlObjectType.StoredProcedure, result.Definition.Type);
        Assert.Equal(4, result.Edges.Count);

        Assert.All(result.Edges, e =>
        {
            Assert.Equal(result.Definition.Name, e.Caller.Name);
            Assert.Equal(result.Definition.Type, e.Caller.Type);
        });

        Assert.Contains(result.Edges, e => e.Callee.Name == "[dbo].[RunThis]" && e.Callee.Type == SqlObjectType.StoredProcedure);
        Assert.Contains(result.Edges, e => e.Callee.Name == "dbo.AnotherProc" && e.Callee.Type == SqlObjectType.StoredProcedure);
        Assert.Contains(result.Edges, e => e.Callee.Name == "[dbo].[tvf_GetStuff]" && e.Callee.Type == SqlObjectType.UserFunction);
        Assert.Contains(result.Edges, e => e.Callee.Name == "[dbo].[udf_Scalar]" && e.Callee.Type == SqlObjectType.UserFunction);

        Assert.DoesNotContain(result.Edges, e => string.Equals(e.Callee.Name, result.Definition.Name, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Parse_WhenFromReferencesSingleTable_ShouldCaptureTableEdge()
    {
        var text = @"
            CREATE PROCEDURE dbo.Proc
            AS
            BEGIN
                SELECT * FROM dbo.Customers;
            END";

        var result = TsqlParser.Parse(text);

        Assert.NotNull(result);
        Assert.Equal("dbo.Proc", result!.Definition.Name);
        Assert.Equal(SqlObjectType.StoredProcedure, result.Definition.Type);
        var edge = Assert.Single(result.Edges);
        Assert.Equal("dbo.Customers", edge.Callee.Name);
        Assert.Equal(SqlObjectType.Table, edge.Callee.Type);
    }

    [Fact]
    public void Parse_WhenFromAndJoinTablesWithQuotedIdentifiers_ShouldCaptureBothEdges()
    {
        var text = @"
            CREATE VIEW [dbo].[vOrders]
            AS
            SELECT o.Id
            FROM [dbo].[Orders] AS o
            JOIN [dbo].[OrderLines] ol ON ol.OrderId = o.Id;";

        var result = TsqlParser.Parse(text);

        Assert.NotNull(result);
        Assert.Equal("[dbo].[vOrders]", result!.Definition.Name);
        Assert.Equal(SqlObjectType.View, result.Definition.Type);
        Assert.Equal(2, result.Edges.Count);
        Assert.Contains(result.Edges, e => e.Callee.Name == "[dbo].[Orders]" && e.Callee.Type == SqlObjectType.Table);
        Assert.Contains(result.Edges, e => e.Callee.Name == "[dbo].[OrderLines]" && e.Callee.Type == SqlObjectType.Table);
    }

    [Fact]
    public void Parse_WhenFromHasTvfAndJoinHasTable_ShouldClassifyTvfAndTableCorrectly()
    {
        var text = @"
            CREATE PROCEDURE dbo.Proc
            AS
            BEGIN
                SELECT *
                FROM dbo.tvf_Items(123) it
                JOIN dbo.Products p ON p.Id = it.ProductId;
            END";

        var result = TsqlParser.Parse(text);

        Assert.NotNull(result);
        Assert.Equal("dbo.Proc", result!.Definition.Name);
        Assert.Equal(SqlObjectType.StoredProcedure, result.Definition.Type);
        Assert.Equal(2, result.Edges.Count);
        Assert.Contains(result.Edges, e => e.Callee.Name == "dbo.tvf_Items" && e.Callee.Type == SqlObjectType.UserFunction);
        Assert.Contains(result.Edges, e => e.Callee.Name == "dbo.Products" && e.Callee.Type == SqlObjectType.Table);
    }

    [Fact]
    public void Parse_WhenFromLooksLikeFunctionNameWithoutParens_ShouldTreatAsTable()
    {
        var text = @"
            CREATE PROCEDURE dbo.Proc
            AS
            BEGIN
                SELECT * FROM dbo.MaybeFunc;
            END";

        var result = TsqlParser.Parse(text);

        Assert.NotNull(result);
        Assert.Equal("dbo.Proc", result!.Definition.Name);
        Assert.Equal(SqlObjectType.StoredProcedure, result.Definition.Type);
        var edge = Assert.Single(result.Edges);
        Assert.Equal("dbo.MaybeFunc", edge.Callee.Name);
        Assert.Equal(SqlObjectType.Table, edge.Callee.Type);
    }

    [Fact]
    public void Parse_WhenInsertInto_ShouldCaptureTargetTable()
    {
        var text = @"
            CREATE PROCEDURE dbo.P
            AS
            BEGIN
                INSERT INTO dbo.Target(Col) VALUES (1);
            END";

        var result = TsqlParser.Parse(text);

        Assert.NotNull(result);
        var edge = Assert.Single(result.Edges);
        Assert.Equal("dbo.Target", edge.Callee.Name);
        Assert.Equal(SqlObjectType.Table, edge.Callee.Type);
    }

    [Fact]
    public void Parse_WhenInsertIntoSelectFrom_ShouldCaptureTargetAndSourceTables()
    {
        var text = @"
            CREATE PROCEDURE dbo.P
            AS
            BEGIN
                INSERT INTO [dbo].[Target](Col)
                SELECT Col FROM [dbo].[Source];
            END";

        var result = TsqlParser.Parse(text);

        Assert.NotNull(result);
        Assert.Equal(2, result.Edges.Count);
        Assert.Contains(result.Edges, e => e.Callee.Name == "[dbo].[Target]" && e.Callee.Type == SqlObjectType.Table);
        Assert.Contains(result.Edges, e => e.Callee.Name == "[dbo].[Source]" && e.Callee.Type == SqlObjectType.Table);
    }

    [Fact]
    public void Parse_WhenUpdateQualifiedTable_ShouldCaptureTargetTable()
    {
        var text = @"
            CREATE PROCEDURE dbo.P
            AS
            BEGIN
                UPDATE dbo.Target SET Col = 1;
            END";

        var result = TsqlParser.Parse(text);

        Assert.NotNull(result);
        var edge = Assert.Single(result.Edges);
        Assert.Equal("dbo.Target", edge.Callee.Name);
        Assert.Equal(SqlObjectType.Table, edge.Callee.Type);
    }

    [Fact]
    public void Parse_WhenUpdateUsesAliasWithFromJoin_ShouldCaptureTablesFromFromJoin()
    {
        var text = @"
            CREATE PROCEDURE dbo.P
            AS
            BEGIN
                UPDATE t
                   SET t.Col = 1
                FROM dbo.Target AS t
                JOIN dbo.Source s ON s.Id = t.Id;
            END";

        var result = TsqlParser.Parse(text);

        Assert.NotNull(result);
        Assert.Equal(2, result.Edges.Count);
        Assert.Contains(result.Edges, e => e.Callee.Name == "dbo.Target" && e.Callee.Type == SqlObjectType.Table);
        Assert.Contains(result.Edges, e => e.Callee.Name == "dbo.Source" && e.Callee.Type == SqlObjectType.Table);
    }

    [Fact]
    public void Parse_WhenDeleteFrom_ShouldCaptureTargetTable()
    {
        var text = @"
            CREATE PROCEDURE dbo.P
            AS
            BEGIN
                DELETE FROM [dbo].[Target] WHERE Id = 1;
            END";

        var result = TsqlParser.Parse(text);

        Assert.NotNull(result);
        var edge = Assert.Single(result.Edges);
        Assert.Equal("[dbo].[Target]", edge.Callee.Name);
        Assert.Equal(SqlObjectType.Table, edge.Callee.Type);
    }

    [Fact]
    public void Parse_WhenIudAppearInCommentsOrStrings_ShouldNotCaptureEdges()
    {
        var text = @"
            CREATE PROCEDURE dbo.P
            AS
            BEGIN
                -- INSERT INTO dbo.Nope VALUES (1);
                /* UPDATE dbo.Nope SET Col = 1; */
                SELECT 'DELETE FROM dbo.Nope';
                SELECT 1;
            END";

        var result = TsqlParser.Parse(text);

        Assert.NotNull(result);
        Assert.Empty(result.Edges);
    }

    [Fact]
    public void Parse_WhenInsertIntoWithColumnList_ShouldNotTreatTableAsFunction()
    {
        var text = @"
            CREATE PROCEDURE dbo.P
            AS
            BEGIN
                INSERT INTO dbo.Target(Col) VALUES (1);
            END";

        var result = TsqlParser.Parse(text);

        Assert.NotNull(result);
        var edge = Assert.Single(result.Edges);
        Assert.Equal("dbo.Target", edge.Callee.Name);
        Assert.Equal(SqlObjectType.Table, edge.Callee.Type);
    }

    [Fact]
    public void Parse_WhenInsertValues_ShouldNotTreatValuesAsFunction()
    {
        var text = @"
            CREATE PROCEDURE dbo.P
            AS
            BEGIN
                INSERT INTO [dbo].[T]([C]) VALUES (1);
            END";

        var result = TsqlParser.Parse(text);

        Assert.NotNull(result);
        var edge = Assert.Single(result.Edges);
        Assert.Equal("[dbo].[T]", edge.Callee.Name);
        Assert.Equal(SqlObjectType.Table, edge.Callee.Type);
    }

    [Fact]
    public void Parse_WhenSelfExecUsesDifferentQuoting_ShouldSkipSelfEdge()
    {
        var text = @"
            CREATE PROCEDURE [dbo].[Proc]
            AS
            BEGIN
                EXEC dbo.Proc;
                EXEC [dbo].[Proc];
            END";

        var result = TsqlParser.Parse(text);

        Assert.NotNull(result);
        Assert.Empty(result.Edges);
    }

    [Fact]
    public void Parse_WhenSameTableReferencedWithDifferentStyles_ShouldDeduplicateEdges()
    {
        var text = @"
            CREATE PROCEDURE dbo.P
            AS
            BEGIN
                SELECT 1
                FROM [dbo].[Orders]
                JOIN dbo.[Orders] o ON 1 = 1;
            END";

        var result = TsqlParser.Parse(text);

        Assert.NotNull(result);
        var edge = Assert.Single(result.Edges);
        Assert.Equal(SqlObjectType.Table, edge.Callee.Type);
        Assert.True(edge.Callee.Name == "[dbo].[Orders]" || edge.Callee.Name == "dbo.[Orders]");
    }

    [Fact]
    public void Parse_WhenTvfSeenAndScalarCallUsesDifferentStyle_ShouldNotDoubleCount()
    {
        var text = @"
            CREATE PROCEDURE dbo.P
            AS
            BEGIN
                SELECT * FROM ""dbo"".""tvf_Items""(1);
                DECLARE @x INT = ;
            END";

        var result = TsqlParser.Parse(text);

        Assert.NotNull(result);
        var edge = Assert.Single(result.Edges);
        Assert.Equal(SqlObjectType.UserFunction, edge.Callee.Type);
    }

    [Fact]
    public void Parse_WhenMergeWithSimpleTargetAndSource_ShouldCaptureBothEdges()
    {
        var text = @"
        CREATE PROCEDURE dbo.P
        AS
        BEGIN
            MERGE dbo.Target AS t
            USING dbo.Source AS s
            ON t.Id = s.Id
            WHEN MATCHED THEN UPDATE SET t.Col = s.Col;
        END";

        var result = TsqlParser.Parse(text);

        Assert.NotNull(result);
        Assert.Contains(result.Edges, e => e.Callee.Name == "dbo.Target" && e.Callee.Type == SqlObjectType.Table);
        Assert.Contains(result.Edges, e => e.Callee.Name == "dbo.Source" && e.Callee.Type == SqlObjectType.Table);
        Assert.Equal(2, result.Edges.Count);
    }

    [Fact]
    public void Parse_WhenMergeWithoutAliases_ShouldCaptureBothEdges()
    {
        var text = @"
        CREATE PROCEDURE dbo.P
        AS
        BEGIN
            MERGE [dbo].[Target]
            USING [dbo].[Source]
            ON Target.Id = Source.Id
            WHEN MATCHED THEN UPDATE SET Target.Col = Source.Col;
        END";

        var result = TsqlParser.Parse(text);

        Assert.NotNull(result);
        Assert.Contains(result.Edges, e => e.Callee.Name == "[dbo].[Target]" && e.Callee.Type == SqlObjectType.Table);
        Assert.Contains(result.Edges, e => e.Callee.Name == "[dbo].[Source]" && e.Callee.Type == SqlObjectType.Table);
        Assert.Equal(2, result.Edges.Count);
    }

    [Fact]
    public void Parse_WhenMergeWithQuotedIdentifiers_ShouldCaptureBothEdges()
    {
        var text = @"
        CREATE PROCEDURE dbo.P
        AS
        BEGIN
            MERGE ""dbo"".""Target"" AS t
            USING [dbo].[Source] AS s
            ON t.Id = s.Id
            WHEN MATCHED THEN UPDATE SET t.Col = s.Col;
        END";

        var result = TsqlParser.Parse(text);

        Assert.NotNull(result);
        Assert.Contains(result.Edges, e => e.Callee.Name == "\"dbo\".\"Target\"" && e.Callee.Type == SqlObjectType.Table);
        Assert.Contains(result.Edges, e => e.Callee.Name == "[dbo].[Source]" && e.Callee.Type == SqlObjectType.Table);
        Assert.Equal(2, result.Edges.Count);
    }

    [Fact]
    public void Parse_WhenMergeWithSubquerySource_ShouldCaptureTargetAndRealSource()
    {
        var text = @"
        CREATE PROCEDURE dbo.P
        AS
        BEGIN
            MERGE dbo.Target AS t
            USING (SELECT * FROM dbo.RealSource) AS s
            ON t.Id = s.Id
            WHEN NOT MATCHED THEN INSERT (Col) VALUES (s.Col);
        END";

        var result = TsqlParser.Parse(text);

        Assert.NotNull(result);
        Assert.Contains(result.Edges, e => e.Callee.Name == "dbo.Target" && e.Callee.Type == SqlObjectType.Table);
        Assert.Contains(result.Edges, e => e.Callee.Name == "dbo.RealSource" && e.Callee.Type == SqlObjectType.Table);
        Assert.Equal(2, result.Edges.Count);
    }

    [Fact]
    public void Parse_WhenMergeSelfTarget_ShouldSkipTargetEdge()
    {
        var text = @"
        CREATE PROCEDURE dbo.Target
        AS
        BEGIN
            MERGE dbo.Target AS t
            USING dbo.Source AS s
            ON t.Id = s.Id
            WHEN MATCHED THEN UPDATE SET t.Col = s.Col;
        END";

        var result = TsqlParser.Parse(text);

        Assert.NotNull(result);
        Assert.DoesNotContain(result.Edges, e => e.Callee.Name == "dbo.Target");
        Assert.Contains(result.Edges, e => e.Callee.Name == "dbo.Source" && e.Callee.Type == SqlObjectType.Table);
        Assert.Single(result.Edges);
    }

}
