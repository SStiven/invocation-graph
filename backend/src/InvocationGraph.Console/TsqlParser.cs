using InvocationGraph.UI;
using System.Text.RegularExpressions;

public static class TsqlParser
{
    private const string IdentifierPattern =
        @"(?:\[[^\]]+\]|""[^""]+""|[A-Za-z_][\w]*)";

    private static readonly HashSet<string> IgnoredScalarNames =
        new(StringComparer.OrdinalIgnoreCase) { "VALUES", "USING", "INSERT" };

    private static readonly Regex CreateRegex = new(
        $@"\bCREATE\s+(PROCEDURE|TABLE|VIEW|FUNCTION|TRIGGER)\s+(?<name>{IdentifierPattern}(?:\s*\.\s*{IdentifierPattern})*)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ExecRegex = new(
        $@"\bEXEC(?:UTE)?\s+(?<name>{IdentifierPattern}(?:\s*\.\s*{IdentifierPattern})*)(?!\s*\()",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TableValuedFuncRegex = new(
        $@"\bFROM\s+(?<name>{IdentifierPattern}(?:\s*\.\s*{IdentifierPattern})*)\s*\(",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ScalarFuncRegex = new(
        $@"(?<!\bFROM\s+)(?<![A-Za-z0-9_\]\.""])(?<name>{IdentifierPattern}(?:\s*\.\s*{IdentifierPattern})*)\s*\(",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex BlockCommentRegex = new(@"/\*.*?\*/",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex LineCommentRegex = new(@"--.*?$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex StringRegex = new(@"'([^']|'')*'",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex DataTypeWithParensRegex = new(
        @"\b(?:NVARCHAR|VARCHAR|NCHAR|CHAR|VARBINARY|BINARY|DECIMAL|NUMERIC|FLOAT|REAL|TIME|DATETIME2|DATETIMEOFFSET)\s*\([^)]*\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex FromJoinObjectRegex = new(
        $@"\b(?:FROM|JOIN)\s+(?<name>(?>{IdentifierPattern}(?:\s*\.\s*{IdentifierPattern})*))\s*(?!\s*\()",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex InsertIntoRegex = new(
        $@"\bINSERT\s+INTO\s+(?<name>{IdentifierPattern}(?:\s*\.\s*{IdentifierPattern})*)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex UpdateTargetQualifiedRegex = new(
        $@"\bUPDATE\s+(?:TOP\s*\(\s*\d+\s*\)\s*)?(?<name>{IdentifierPattern}(?:\s*\.\s*{IdentifierPattern})+)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DeleteFromRegex = new(
        $@"\bDELETE\s+FROM\s+(?<name>{IdentifierPattern}(?:\s*\.\s*{IdentifierPattern})*)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex InsertColumnListRegex = new(
        $@"(\bINSERT\s+INTO\s+{IdentifierPattern}(?:\s*\.\s*{IdentifierPattern})*)\s*\([^)]*\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex MergeTargetRegex = new(
        $@"\bMERGE\s+(?<name>{IdentifierPattern}(?:\s*\.\s*{IdentifierPattern})*)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex MergeUsingRegex = new(
        $@"\bUSING\s+(?<name>{IdentifierPattern}(?:\s*\.\s*{IdentifierPattern})*)(?!\s*\()",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex MergeInsertColumnListRegex = new(
        @"(\bWHEN\s+(?:NOT\s+)?MATCHED(?:\s+BY\s+(?:TARGET|SOURCE))?\s+THEN\s+INSERT)\s*\([^)]*\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string StripMergeInsertColumnLists(string sql) =>
    MergeInsertColumnListRegex.Replace(sql, "$1");

    public static ParsedFile? Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var clean = StripCommentsAndStrings(text);
        clean = StripTypeParameterLists(clean);
        clean = StripInsertColumnLists(clean);
        clean = StripInsertColumnLists(clean);
        clean = StripMergeInsertColumnLists(clean);

        var defMatch = CreateRegex.Match(clean);
        if (!defMatch.Success) return null;

        var defType = ParseType(defMatch.Groups[1].Value);
        var defName = defMatch.Groups["name"].Value;
        var definition = new SqlObject(defName, defType);

        var edges = new List<InvocationEdge>();
        var tvfNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var mergeUsingSpans = FindUsingSubqueryRanges(clean);

        edges.AddRange(GetExecEdges(clean, definition));
        edges.AddRange(GetTvfEdges(clean, definition, tvfNames));
        edges.AddRange(GetFromJoinTableEdges(clean, definition, tvfNames, mergeUsingSpans)); // changed
        edges.AddRange(GetInsertIntoEdges(clean, definition));
        edges.AddRange(GetUpdateTargetEdges(clean, definition));
        edges.AddRange(GetDeleteFromEdges(clean, definition));
        edges.AddRange(GetScalarFunctionEdges(clean, definition, tvfNames));
        edges.AddRange(GetMergeTargetEdges(clean, definition));
        edges.AddRange(GetMergeUsingEdges(clean, definition, tvfNames));

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

    private static IEnumerable<InvocationEdge> GetFromJoinTableEdges(
        string sql,
        SqlObject definition,
        HashSet<string> tvfNames,
        List<(int start, int end)> mergeUsingSpans)
    {
        foreach (Match m in FromJoinObjectRegex.Matches(sql))
        {
            if (IsInsideAny(m.Index, mergeUsingSpans)) continue;

            var name = m.Groups["name"].Value;
            if (IsSelfInvocation(definition, name)) continue;
            if (tvfNames.Contains(name)) continue;

            yield return new InvocationEdge(definition, new SqlObject(name, SqlObjectType.Table));
        }
    }

    private static IEnumerable<InvocationEdge> GetInsertIntoEdges(string sql, SqlObject definition)
    {
        foreach (Match m in InsertIntoRegex.Matches(sql))
        {
            var name = m.Groups["name"].Value;
            if (IsSelfInvocation(definition, name)) continue;
            yield return new InvocationEdge(definition, new SqlObject(name, SqlObjectType.Table));
        }
    }

    private static IEnumerable<InvocationEdge> GetUpdateTargetEdges(string sql, SqlObject definition)
    {
        foreach (Match m in UpdateTargetQualifiedRegex.Matches(sql))
        {
            var name = m.Groups["name"].Value;
            if (IsSelfInvocation(definition, name)) continue;
            yield return new InvocationEdge(definition, new SqlObject(name, SqlObjectType.Table));
        }
    }

    private static IEnumerable<InvocationEdge> GetDeleteFromEdges(string sql, SqlObject definition)
    {
        foreach (Match m in DeleteFromRegex.Matches(sql))
        {
            var name = m.Groups["name"].Value;
            if (IsSelfInvocation(definition, name)) continue;
            yield return new InvocationEdge(definition, new SqlObject(name, SqlObjectType.Table));
        }
    }

    private static IEnumerable<InvocationEdge> GetScalarFunctionEdges(string sql, SqlObject definition, HashSet<string> tvfNames)
    {
        foreach (Match m in ScalarFuncRegex.Matches(sql))
        {
            var name = m.Groups["name"].Value;
            if (IsSelfInvocation(definition, name)) continue;
            if (tvfNames.Contains(name)) continue;
            if (IgnoredScalarNames.Contains(name)) continue;

            yield return new InvocationEdge(definition, new SqlObject(name, SqlObjectType.UserFunction));
        }
    }

    private static List<InvocationEdge> DedupeEdges(IEnumerable<InvocationEdge> edges)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unique = new List<InvocationEdge>();

        foreach (var e in edges)
        {
            var callerKey = CanonicalizeName(e.Caller.Name);
            var calleeKey = CanonicalizeName(e.Callee.Name);
            var key = $"{callerKey}|{e.Callee.Type}|{calleeKey}";

            if (seen.Add(key))
                unique.Add(e);
        }

        return unique;
    }

    private static bool IsSelfInvocation(SqlObject definition, string candidateName) =>
        string.Equals(
            CanonicalizeName(definition.Name),
            CanonicalizeName(candidateName),
            StringComparison.OrdinalIgnoreCase);

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
        return StringRegex.Replace(noLines, "");
    }

    private static string StripTypeParameterLists(string sql) =>
        DataTypeWithParensRegex.Replace(sql, m => Regex.Replace(m.Value, @"\s*\([^)]*\)", ""));

    private static string StripInsertColumnLists(string sql) =>
        InsertColumnListRegex.Replace(sql, "$1");

    private static string CanonicalizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;

        static string TrimQuotes(string part)
        {
            part = part.Trim();
            if (part.Length >= 2)
            {
                if (part[0] == '[' && part[^1] == ']') return part[1..^1];
                if (part[0] == '"' && part[^1] == '"') return part[1..^1];
            }
            return part;
        }

        var parts = name.Split('.', StringSplitOptions.RemoveEmptyEntries)
                        .Select(TrimQuotes)
                        .Select(p => p.Trim())
                        .Where(p => p.Length > 0);

        return string.Join(".", parts);
    }

    private static IEnumerable<InvocationEdge> GetMergeTargetEdges(string sql, SqlObject definition)
    {
        foreach (Match m in MergeTargetRegex.Matches(sql))
        {
            var name = m.Groups["name"].Value;
            if (IsSelfInvocation(definition, name)) continue;
            yield return new InvocationEdge(definition, new SqlObject(name, SqlObjectType.Table));
        }
    }

    private static IEnumerable<InvocationEdge> GetMergeUsingEdges(string sql, SqlObject definition, HashSet<string> tvfNames)
    {
        foreach (Match m in MergeUsingRegex.Matches(sql))
        {
            var name = m.Groups["name"].Value;
            if (IsSelfInvocation(definition, name)) continue;
            if (tvfNames.Contains(name)) continue;
            yield return new InvocationEdge(definition, new SqlObject(name, SqlObjectType.Table));
        }

        foreach (Match m in Regex.Matches(sql, @"\bUSING\s*\((?<subquery>.*?)\)\s+AS", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            var subquery = m.Groups["subquery"].Value;
            foreach (Match fm in FromJoinObjectRegex.Matches(subquery))
            {
                var subName = fm.Groups["name"].Value;
                if (IsSelfInvocation(definition, subName)) continue;
                if (tvfNames.Contains(subName)) continue;
                yield return new InvocationEdge(definition, new SqlObject(subName, SqlObjectType.Table));
            }
        }
    }

    private static List<(int start, int end)> FindUsingSubqueryRanges(string sql)
    {
        var spans = new List<(int start, int end)>();
        var usingOpenRx = new Regex(@"\bUSING\s*\(", RegexOptions.IgnoreCase);

        foreach (Match m in usingOpenRx.Matches(sql))
        {
            int open = m.Index + m.Value.LastIndexOf('(');
            int depth = 1;

            for (int i = open + 1; i < sql.Length; i++)
            {
                char c = sql[i];
                if (c == '(') depth++;
                else if (c == ')')
                {
                    depth--;
                    if (depth == 0)
                    {
                        spans.Add((open, i));
                        break;
                    }
                }
            }
        }

        return spans;
    }

    private static bool IsInsideAny(int index, List<(int start, int end)> spans)
        => spans.Any(s => index >= s.start && index <= s.end);
}
