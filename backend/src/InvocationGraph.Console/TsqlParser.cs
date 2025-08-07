using InvocationGraph.UI;
using System.Text.RegularExpressions;

public static class TsqlParser
{
    private const string IdentifierPattern =
        @"(?:\[[^\]]+\]|""[^""]+""|[A-Za-z_][\w]*)";

    private static readonly Regex CreateRegex = new Regex(
        $@"\bCREATE\s+(PROCEDURE|TABLE|VIEW|FUNCTION|TRIGGER)\s+" +
        $@"(?<name>{IdentifierPattern}(?:\s*\.\s*{IdentifierPattern})*)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ExecRegex = new Regex(
        $@"\bEXEC(?:UTE)?\s+(?<name>{IdentifierPattern}(?:\s*\.\s*{IdentifierPattern})*)(?!\s*\()",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TableValuedFuncRegex = new Regex(
        $@"\bFROM\s+(?<name>{IdentifierPattern}(?:\s*\.\s*{IdentifierPattern})*)\s*\(",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ScalarFuncRegex = new Regex(
        $@"(?<!\bFROM\s+)(?<![A-Za-z0-9_\]\.""])" +
        $@"(?<name>{IdentifierPattern}(?:\s*\.\s*{IdentifierPattern})*)\s*\(",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex BlockCommentRegex = new Regex(
        @"/\*.*?\*/", RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex LineCommentRegex = new Regex(
        @"--.*?$", RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex StringRegex = new Regex(
        @"'([^']|'')*'", RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex DataTypeWithParensRegex = new Regex(
        @"\b(?:NVARCHAR|VARCHAR|NCHAR|CHAR|VARBINARY|BINARY|DECIMAL|NUMERIC|FLOAT|REAL|TIME|DATETIME2|DATETIMEOFFSET)\s*\([^)]*\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static ParsedFile? Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var clean = StripCommentsAndStrings(text);
        clean = StripTypeParameterLists(clean);

        var defMatch = CreateRegex.Match(clean);
        if (!defMatch.Success) return null;

        var defType = ParseType(defMatch.Groups[1].Value);
        var defName = defMatch.Groups["name"].Value;
        var definition = new SqlObject(defName, defType);

        var edges = new List<InvocationEdge>();

        edges.AddRange(GetExecEdges(clean, definition));
        var tvfNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        edges.AddRange(GetTvfEdges(clean, definition, tvfNames));
        edges.AddRange(GetScalarFunctionEdges(clean, definition, tvfNames));

        return new ParsedFile(definition, DedupeEdges(edges));
    }

    private static IEnumerable<InvocationEdge> GetExecEdges(string sql, SqlObject definition)
    {
        foreach (Match m in ExecRegex.Matches(sql))
        {
            var name = m.Groups["name"].Value;
            if (IsSelfInvocation(definition, name)) continue;
            yield return new InvocationEdge(definition, new SqlObject(name, SqlObjectType.StoredProcedure));
        }
    }

    private static IEnumerable<InvocationEdge> GetTvfEdges(string sql, SqlObject definition, HashSet<string> tvfNames)
    {
        foreach (Match m in TableValuedFuncRegex.Matches(sql))
        {
            var name = m.Groups["name"].Value;
            if (IsSelfInvocation(definition, name)) continue;
            tvfNames.Add(name);
            yield return new InvocationEdge(definition, new SqlObject(name, SqlObjectType.UserFunction));
        }
    }

    private static IEnumerable<InvocationEdge> GetScalarFunctionEdges(string sql, SqlObject definition, HashSet<string> tvfNames)
    {
        foreach (Match m in ScalarFuncRegex.Matches(sql))
        {
            var name = m.Groups["name"].Value;
            if (IsSelfInvocation(definition, name) || tvfNames.Contains(name)) continue;
            yield return new InvocationEdge(definition, new SqlObject(name, SqlObjectType.UserFunction));
        }
    }

    private static List<InvocationEdge> DedupeEdges(IEnumerable<InvocationEdge> edges)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unique = new List<InvocationEdge>();
        foreach (var e in edges)
        {
            var key = $"{e.Caller.Name}|{e.Callee.Type}|{e.Callee.Name}";
            if (seen.Add(key)) unique.Add(e);
        }
        return unique;
    }

    private static bool IsSelfInvocation(SqlObject definition, string candidateName)
    {
        return string.Equals(
            definition.Name,
            candidateName,
            StringComparison.OrdinalIgnoreCase
        );
    }

    private static SqlObjectType ParseType(string token) =>
        token.ToUpperInvariant() switch
        {
            "PROCEDURE" => SqlObjectType.StoredProcedure,
            "TABLE" => SqlObjectType.Table,
            "VIEW" => SqlObjectType.View,
            "FUNCTION" => SqlObjectType.UserFunction,
            "TRIGGER" => SqlObjectType.Trigger,
            _ => throw new ArgumentException($"Unknown SQL object type '{token}'.", nameof(token))
        };

    private static string StripCommentsAndStrings(string sql)
    {
        var noBlocks = BlockCommentRegex.Replace(sql, "");
        var noLines = LineCommentRegex.Replace(noBlocks, "");
        var noLiterals = StringRegex.Replace(noLines, "");
        return noLiterals;
    }

    private static string StripTypeParameterLists(string sql)
    {
        return DataTypeWithParensRegex.Replace(sql, m =>
            Regex.Replace(m.Value, @"\s*\([^)]*\)", ""));
    }
}