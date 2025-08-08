using InvocationGraph.UI;
using System.Text.RegularExpressions;
using System.Linq;

public static class TsqlParser
{
    private const string IdentifierPattern =
        @"(?:\[[^\]]+\]|""[^""]+""|[A-Za-z_][\w]*)";

    private static readonly HashSet<string> IgnoredScalarNames =
        new(StringComparer.OrdinalIgnoreCase) { "VALUES", "USING", "INSERT", "AS", "OVER", "FROM" };

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
        $@"(?<!\bFROM\s+)(?<![A-Za-z0-9_\]\.""])(?!\b(?:FROM|AS|OVER|USING|INSERT|SELECT|WHERE|UNION|JOIN|ON|GROUP|ORDER)\b)(?<name>{IdentifierPattern}(?:\s*\.\s*{IdentifierPattern})*)\s*\(",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex BlockCommentRegex = new(
        @"/\*.*?\*/", RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex LineCommentRegex = new(
        @"--.*?$", RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex StringRegex = new(
        @"'([^']|'')*'", RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex DataTypeWithParensRegex = new(
        @"\b(?:NVARCHAR|VARCHAR|NCHAR|CHAR|VARBINARY|BINARY|DECIMAL|NUMERIC|FLOAT|REAL|TIME|DATETIME2|DATETIMEOFFSET)\s*\([^)]*\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex FromJoinObjectRegex = new(
        $@"\b(?:FROM|JOIN)\s+(?<name>(?>{IdentifierPattern}(?:\s*\.\s*{IdentifierPattern})*))\s*(?!\s*\()",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex FromJoinAliasRegex = new(
        $@"\b(?:FROM|JOIN)\s+(?:{IdentifierPattern}(?:\s*\.\s*{IdentifierPattern})*)\s+(?:AS\s+)?(?<alias>{IdentifierPattern})\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SelectQualifierRegex = new(
        $@"\bSELECT\b[\s\S]*?\b(?<q>{IdentifierPattern})\s*\.",
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

    private static readonly Regex UsingSubqueryRegex = new(
        @"\bUSING\s*\((?<subquery>.*?)\)\s*(?:AS\s+)?(?<alias>\w+)?",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex WithCteHeadRegex = new(
        $@"\bWITH\s+(?<name>{IdentifierPattern})\s+AS\s*\(",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex WithCteTailRegex = new(
        $@",\s*(?<name>{IdentifierPattern})\s+AS\s*\(",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex FromJoinDerivedOpenRegex = new(
        @"\b(?:FROM|JOIN)\s*\(",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static ParsedFile? Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var clean = Preprocess(text);
        var cteNames = GetCteNames(clean);
        var derivedAliases = GetDerivedTableAliases(clean);
        var fromJoinAliases = GetFromJoinAliases(clean);
        var selectQualifiers = GetSelectQualifiers(clean);

        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in cteNames) excluded.Add(CanonicalizeName(n));
        foreach (var n in derivedAliases) excluded.Add(CanonicalizeName(n));
        foreach (var n in fromJoinAliases) excluded.Add(CanonicalizeName(n));
        foreach (var n in selectQualifiers) excluded.Add(CanonicalizeName(n));

        var defMatch = CreateRegex.Match(clean);
        if (!defMatch.Success) return null;

        var definition = ExtractDefinitionFromMatch(defMatch);
        var edges = new List<InvocationEdge>();
        var tvfNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mergeUsingSpans = LocateMergeUsingSubqueryBoundaries(clean);

        edges.AddRange(GetStoredProcedureCalls(clean, definition));
        edges.AddRange(GetTableValuedFunctionCalls(clean, definition, tvfNames));
        edges.AddRange(GetTableReferencesFromFromJoinClausesExcludingMergeSubqueries(clean, definition, tvfNames, mergeUsingSpans, excluded));
        edges.AddRange(GetInsertTargetTables(clean, definition));
        edges.AddRange(GetUpdateTargetTables(clean, definition));
        edges.AddRange(GetDeleteTargetTables(clean, definition));
        edges.AddRange(GetScalarFunctionCalls(clean, definition, tvfNames));
        edges.AddRange(GetMergeTargetTables(clean, definition));
        edges.AddRange(GetMergeSourceTablesAndSubqueryTables(clean, definition, tvfNames, excluded));

        edges = edges.Where(e => !excluded.Contains(CanonicalizeName(e.Callee.Name))).ToList();

        return new ParsedFile(definition, RemoveDuplicateEdges(edges));
    }

    private static string Preprocess(string text)
    {
        var clean = RemoveCommentsAndStringLiterals(text);
        clean = RemoveParameterizedTypeDeclarations(clean);
        clean = RemoveInsertIntoColumnLists(clean);
        clean = RemoveMergeInsertColumnLists(clean);
        return clean;
    }

    private static SqlObject ExtractDefinitionFromMatch(Match defMatch)
    {
        var defType = ParseType(defMatch.Groups[1].Value);
        var defName = defMatch.Groups["name"].Value;
        return new SqlObject(defName, defType);
    }

    private static IEnumerable<InvocationEdge> GetStoredProcedureCalls(string sql, SqlObject definition)
    {
        foreach (Match m in ExecRegex.Matches(sql))
        {
            var name = m.Groups["name"].Value;
            if (IsSelfInvocation(definition, name)) continue;
            yield return new InvocationEdge(definition, new SqlObject(name, SqlObjectType.StoredProcedure));
        }
    }

    private static IEnumerable<InvocationEdge> GetTableValuedFunctionCalls(string sql, SqlObject definition, HashSet<string> tvfNames)
    {
        foreach (Match m in TableValuedFuncRegex.Matches(sql))
        {
            var name = m.Groups["name"].Value;
            if (IsSelfInvocation(definition, name)) continue;
            tvfNames.Add(name);
            yield return new InvocationEdge(definition, new SqlObject(name, SqlObjectType.UserFunction));
        }
    }

    private static IEnumerable<InvocationEdge> GetTableReferencesFromFromJoinClausesExcludingMergeSubqueries(
        string sql, SqlObject definition, HashSet<string> tvfNames, List<(int start, int end)> mergeUsingSpans, HashSet<string> excludedCanonicalNames)
    {
        foreach (Match m in FromJoinObjectRegex.Matches(sql))
        {
            if (IsIndexWithinAnySpan(m.Index, mergeUsingSpans)) continue;
            var name = m.Groups["name"].Value;
            if (excludedCanonicalNames.Contains(CanonicalizeName(name))) continue;
            if (!ShouldAddTableEdge(definition, name, tvfNames)) continue;
            yield return new InvocationEdge(definition, new SqlObject(name, SqlObjectType.Table));
        }
    }

    private static IEnumerable<InvocationEdge> GetInsertTargetTables(string sql, SqlObject definition)
    {
        foreach (Match m in InsertIntoRegex.Matches(sql))
        {
            var name = m.Groups["name"].Value;
            if (IsSelfInvocation(definition, name)) continue;
            yield return new InvocationEdge(definition, new SqlObject(name, SqlObjectType.Table));
        }
    }

    private static IEnumerable<InvocationEdge> GetUpdateTargetTables(string sql, SqlObject definition)
    {
        foreach (Match m in UpdateTargetQualifiedRegex.Matches(sql))
        {
            var name = m.Groups["name"].Value;
            if (IsSelfInvocation(definition, name)) continue;
            yield return new InvocationEdge(definition, new SqlObject(name, SqlObjectType.Table));
        }
    }

    private static IEnumerable<InvocationEdge> GetDeleteTargetTables(string sql, SqlObject definition)
    {
        foreach (Match m in DeleteFromRegex.Matches(sql))
        {
            var name = m.Groups["name"].Value;
            if (IsSelfInvocation(definition, name)) continue;
            yield return new InvocationEdge(definition, new SqlObject(name, SqlObjectType.Table));
        }
    }

    private static IEnumerable<InvocationEdge> GetScalarFunctionCalls(string sql, SqlObject definition, HashSet<string> tvfNames)
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

    private static IEnumerable<InvocationEdge> GetMergeTargetTables(string sql, SqlObject definition)
    {
        foreach (Match m in MergeTargetRegex.Matches(sql))
        {
            var name = m.Groups["name"].Value;
            if (IsSelfInvocation(definition, name)) continue;
            yield return new InvocationEdge(definition, new SqlObject(name, SqlObjectType.Table));
        }
    }

    private static IEnumerable<InvocationEdge> GetMergeSourceTablesAndSubqueryTables(
        string sql, SqlObject definition, HashSet<string> tvfNames, HashSet<string> excludedCanonicalNames)
    {
        foreach (Match m in MergeUsingRegex.Matches(sql))
        {
            var name = m.Groups["name"].Value;
            if (excludedCanonicalNames.Contains(CanonicalizeName(name))) continue;
            if (ShouldAddTableEdge(definition, name, tvfNames))
                yield return new InvocationEdge(definition, new SqlObject(name, SqlObjectType.Table));
        }
        foreach (Match m in UsingSubqueryRegex.Matches(sql))
        {
            var subquery = m.Groups["subquery"].Value;
            foreach (Match fm in FromJoinObjectRegex.Matches(subquery))
            {
                var subName = fm.Groups["name"].Value;
                if (excludedCanonicalNames.Contains(CanonicalizeName(subName))) continue;
                if (ShouldAddTableEdge(definition, subName, tvfNames))
                    yield return new InvocationEdge(definition, new SqlObject(subName, SqlObjectType.Table));
            }
        }
    }

    private static bool ShouldAddTableEdge(SqlObject definition, string candidateName, HashSet<string> tvfNames)
    {
        if (IsSelfInvocation(definition, candidateName)) return false;
        if (tvfNames.Contains(candidateName)) return false;
        return true;
    }

    private static List<InvocationEdge> RemoveDuplicateEdges(IEnumerable<InvocationEdge> edges)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unique = new List<InvocationEdge>();
        foreach (var e in edges)
        {
            var key = MakeEdgeKey(e);
            if (seen.Add(key)) unique.Add(e);
        }
        return unique;
    }

    private static string MakeEdgeKey(InvocationEdge e) =>
        $"{CanonicalizeName(e.Caller.Name)}|{e.Callee.Type}|{CanonicalizeName(e.Callee.Name)}";

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

    private static string RemoveCommentsAndStringLiterals(string sql)
    {
        var noBlocks = BlockCommentRegex.Replace(sql, "");
        var noLines = LineCommentRegex.Replace(noBlocks, "");
        return StringRegex.Replace(noLines, "");
    }

    private static string RemoveParameterizedTypeDeclarations(string sql) =>
        DataTypeWithParensRegex.Replace(sql, m => Regex.Replace(m.Value, @"\s*\([^)]*\)", ""));

    private static string RemoveInsertIntoColumnLists(string sql) =>
        InsertColumnListRegex.Replace(sql, "$1");

    private static string RemoveMergeInsertColumnLists(string sql) =>
        MergeInsertColumnListRegex.Replace(sql, "$1");

    private static List<(int start, int end)> LocateMergeUsingSubqueryBoundaries(string sql)
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

    private static bool IsIndexWithinAnySpan(int index, List<(int start, int end)> spans) =>
        spans.Any(s => index >= s.start && index <= s.end);

    private static HashSet<string> GetCteNames(string text)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in WithCteHeadRegex.Matches(text))
            names.Add(m.Groups["name"].Value);
        foreach (Match m in WithCteTailRegex.Matches(text))
            names.Add(m.Groups["name"].Value);
        return names;
    }

    private static int FindMatchingParen(string s, int openIndex)
    {
        int depth = 0;
        for (int i = openIndex; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '(') depth++;
            else if (c == ')')
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return -1;
    }

    private static HashSet<string> GetDerivedTableAliases(string text)
    {
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in FromJoinDerivedOpenRegex.Matches(text))
        {
            int open = text.IndexOf('(', m.Index);
            if (open < 0) continue;
            int close = FindMatchingParen(text, open);
            if (close < 0) continue;
            var tail = text.AsSpan(close + 1);
            var aliasMatch = Regex.Match(tail.ToString(),
                $@"^\s*(?:AS\s+)?(?<alias>{IdentifierPattern})\b",
                RegexOptions.IgnoreCase);
            if (aliasMatch.Success)
            {
                var alias = aliasMatch.Groups["alias"].Value;
                if (!string.IsNullOrWhiteSpace(alias))
                    aliases.Add(alias);
            }
        }
        return aliases;
    }

    private static HashSet<string> GetFromJoinAliases(string text)
    {
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in FromJoinAliasRegex.Matches(text))
        {
            var a = m.Groups["alias"].Value;
            if (!string.IsNullOrWhiteSpace(a)) aliases.Add(a);
        }
        return aliases;
    }

    private static HashSet<string> GetSelectQualifiers(string text)
    {
        var qs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in SelectQualifierRegex.Matches(text))
        {
            var q = m.Groups["q"].Value;
            if (!string.IsNullOrWhiteSpace(q)) qs.Add(q);
        }
        return qs;
    }
}
