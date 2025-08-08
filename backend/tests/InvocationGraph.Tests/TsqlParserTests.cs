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

    [Fact]
    public void Parse_WithUnion_TopLevelSelect_ShouldCaptureBothTables()
    {
        var text = @"
                CREATE VIEW dbo.V1
                AS
                SELECT c1 FROM dbo.TableA
                UNION
                SELECT c1 FROM dbo.TableB;
            ";

        var result = TsqlParser.Parse(text);

        Assert.NotNull(result);
        Assert.Equal("dbo.V1", result.Definition.Name);
        Assert.Equal(SqlObjectType.View, result.Definition.Type);
        Assert.Contains(result.Edges, e => e.Callee.Name == "dbo.TableA" && e.Callee.Type == SqlObjectType.Table);
        Assert.Contains(result.Edges, e => e.Callee.Name == "dbo.TableB" && e.Callee.Type == SqlObjectType.Table);
        Assert.Equal(2, result.Edges.Count);
    }

    [Fact]
    public void Parse_WithUnionAll_TvfInFrom_ShouldCaptureBothFunctionsAsUserFunction()
    {
        var text = @"
                CREATE VIEW dbo.V2
                AS
                SELECT * FROM dbo.Foo()
                UNION ALL
                SELECT * FROM [dbo].[Bar]();
            ";

        var result = TsqlParser.Parse(text);

        Assert.NotNull(result);
        Assert.Equal("dbo.V2", result.Definition.Name);
        Assert.Equal(SqlObjectType.View, result.Definition.Type);
        Assert.Contains(result.Edges, e => e.Callee.Name == "dbo.Foo" && e.Callee.Type == SqlObjectType.UserFunction);
        Assert.Contains(result.Edges, e => e.Callee.Name == "[dbo].[Bar]" && e.Callee.Type == SqlObjectType.UserFunction);
        Assert.Equal(2, result.Edges.Count);
    }

    [Fact]
    public void Parse_WithUnion_SubqueryInFrom_ShouldCaptureInnerTables()
    {
        var text = @"
                CREATE VIEW dbo.V3
                AS
                SELECT u.*
                FROM (
                    SELECT * FROM dbo.Users
                    UNION
                    SELECT * FROM [dbo].[ArchivedUsers]
                ) u
                JOIN dbo.Roles r ON r.Id = u.RoleId;
            ";

        var result = TsqlParser.Parse(text);

        Assert.NotNull(result);
        var names = result.Edges.Select(e => e.Callee.Name).ToHashSet();
        Assert.Contains("dbo.Users", names);
        Assert.Contains("[dbo].[ArchivedUsers]", names);
        Assert.Contains("dbo.Roles", names);
        Assert.Equal(3, names.Count);
    }

    [Fact]
    public void Parse_InsertInto_WithUnionSelect_ShouldCaptureTargetAndSources()
    {
        var text = @"
                CREATE PROCEDURE dbo.Populate
                AS
                BEGIN
                    INSERT INTO dbo.Target (Id, Name)
                    SELECT Id, Name FROM dbo.Source1
                    UNION
                    SELECT Id, Name FROM dbo.Source2;
                END
            ";

        var result = TsqlParser.Parse(text);

        Assert.NotNull(result);
        Assert.Contains(result.Edges, e => e.Callee.Name == "dbo.Target" && e.Callee.Type == SqlObjectType.Table);
        Assert.Contains(result.Edges, e => e.Callee.Name == "dbo.Source1" && e.Callee.Type == SqlObjectType.Table);
        Assert.Contains(result.Edges, e => e.Callee.Name == "dbo.Source2" && e.Callee.Type == SqlObjectType.Table);
        Assert.Equal(3, result.Edges.Count);
    }

    [Fact]
    public void Parse_Union_OneBranchWithoutFrom_ShouldCaptureOnlyExistingFromTables()
    {
        var text = @"
                CREATE VIEW dbo.V4
                AS
                SELECT 1 AS A
                UNION ALL
                SELECT A FROM dbo.HasFrom;
            ";

        var result = TsqlParser.Parse(text);

        Assert.NotNull(result);
        Assert.Single(result.Edges);
        Assert.Contains(result.Edges, e => e.Callee.Name == "dbo.HasFrom" && e.Callee.Type == SqlObjectType.Table);
    }

    [Fact]
    public void Parse_Merge_UsingSubqueryWithUnion_ShouldCaptureTargetAndUnionSources_NoDupes()
    {
        var text = @"
                CREATE PROCEDURE dbo.MergeProc
                AS
                BEGIN
                    MERGE dbo.Target AS t
                    USING (
                        SELECT * FROM dbo.S1
                        UNION
                        SELECT * FROM dbo.S2
                    ) AS s
                    ON t.Id = s.Id
                    WHEN MATCHED THEN UPDATE SET t.Col = s.Col;
                END
            ";

        var result = TsqlParser.Parse(text);

        Assert.NotNull(result);
        Assert.Contains(result.Edges, e => e.Callee.Name == "dbo.Target" && e.Callee.Type == SqlObjectType.Table);
        Assert.Contains(result.Edges, e => e.Callee.Name == "dbo.S1" && e.Callee.Type == SqlObjectType.Table);
        Assert.Contains(result.Edges, e => e.Callee.Name == "dbo.S2" && e.Callee.Type == SqlObjectType.Table);

        var keys = result.Edges
            .Select(e => (e.Callee.Name.ToLowerInvariant(), e.Callee.Type))
            .ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    [Fact]
    public void Parse_Union_DuplicateSameTableBothBranches_ShouldDeduplicate()
    {
        var text = @"
                CREATE VIEW dbo.V5
                AS
                SELECT * FROM dbo.Dup
                UNION
                SELECT * FROM dbo.Dup;
            ";

        var result = TsqlParser.Parse(text);

        Assert.NotNull(result);
        Assert.Single(result.Edges);
        Assert.Contains(result.Edges, e => e.Callee.Name == "dbo.Dup" && e.Callee.Type == SqlObjectType.Table);
    }

    [Fact]
    public void Parse_CteWithUnion_ShouldCaptureBothSources()
    {
        var text = @"
                CREATE VIEW dbo.V6
                AS
                WITH C AS (
                    SELECT * FROM dbo.A
                    UNION ALL
                    SELECT * FROM dbo.B
                )
                SELECT * FROM C;
            ";

        var result = TsqlParser.Parse(text);

        Assert.NotNull(result);
        Assert.Contains(result.Edges, e => e.Callee.Name == "dbo.A" && e.Callee.Type == SqlObjectType.Table);
        Assert.Contains(result.Edges, e => e.Callee.Name == "dbo.B" && e.Callee.Type == SqlObjectType.Table);
        Assert.Equal(2, result.Edges.Count);
    }
}
