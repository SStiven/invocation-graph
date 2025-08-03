namespace InvocationGraph.UI;

public class InvocationEdge
{
    public SqlObject Caller { get; }
    public SqlObject Callee { get; }

    public InvocationEdge(SqlObject caller, SqlObject callee)
    {
        Caller = caller ?? throw new ArgumentNullException(nameof(caller));
        Callee = callee ?? throw new ArgumentNullException(nameof(callee));
    }
}
