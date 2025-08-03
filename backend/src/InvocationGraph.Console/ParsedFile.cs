namespace InvocationGraph.UI;

public class ParsedFile
{
    public SqlObject Definition { get; }
    public List<InvocationEdge> Edges { get; }

    public ParsedFile(SqlObject definition, List<InvocationEdge> edges)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        Edges = edges ?? throw new ArgumentNullException(nameof(edges));
    }
}
