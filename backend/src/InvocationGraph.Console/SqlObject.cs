namespace InvocationGraph.UI;

public class SqlObject
{
    public string Name { get; }
    public SqlObjectType Type { get; }

    public SqlObject(string name, SqlObjectType type)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException(
                "Name cannot be null or whitespace.", nameof(name));

        Name = name;
        Type = type;
    }
}
