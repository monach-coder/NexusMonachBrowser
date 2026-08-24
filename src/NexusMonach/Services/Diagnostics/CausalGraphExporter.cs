using System.Text;
using System.Xml.Linq;

namespace NexusMonach.Services.Diagnostics;

/// <summary>
/// Экспорт причинного графа в стандартные форматы: Mermaid (текст в issue),
/// DOT (Graphviz) и GraphML (Gephi и любые графовые анализаторы).
/// </summary>
public static class CausalGraphExporter
{
    /// <summary>Mermaid-диаграмма: читается GitHub/GitLab прямо в Markdown.</summary>
    public static string ToMermaid(CausalGraph graph)
    {
        var builder = new StringBuilder("graph TD\n");
        foreach (var node in graph.Nodes)
            builder.Append("    ").Append(node.Id)
                .Append("[\"").Append(EscapeMermaid(TitleWithTime(node))).Append("\"]\n");
        foreach (var edge in graph.Edges)
            builder.Append("    ").Append(edge.FromId)
                .Append(" -->|").Append(EscapeMermaid(edge.Relation))
                .Append('|').Append(edge.ToId).Append('\n');
        var root = graph.Nodes.FirstOrDefault(n => n.Id == graph.RootCauseNodeId);
        if (root is not null && graph.Nodes.Count > 1)
            builder.Append("    style ").Append(root.Id).Append(" stroke:#FF6B6B,stroke-width:3px\n");
        return builder.ToString();
    }

    /// <summary>DOT-файл для Graphviz: dot -Tsvg crash.dot -o crash.svg.</summary>
    public static string ToDot(CausalGraph graph)
    {
        var builder = new StringBuilder("digraph CrashCausal {\n    rankdir=TD;\n");
        foreach (var node in graph.Nodes)
            builder.Append("    \"").Append(node.Id).Append("\" [label=\"")
                .Append(EscapeDot(TitleWithTime(node))).Append("\"];\n");
        foreach (var edge in graph.Edges)
            builder.Append("    \"").Append(edge.FromId).Append("\" -> \"").Append(edge.ToId)
                .Append("\" [label=\"").Append(EscapeDot(edge.Relation))
                .Append("\", fontsize=9];\n");
        builder.Append("}\n");
        return builder.ToString();
    }

    /// <summary>GraphML: XML-формат для Gephi, yEd, networkx и других инструментов.</summary>
    public static string ToGraphML(CausalGraph graph)
    {
        var ns = XNamespace.Get("http://graphml.graphdrawing.org/xmlns");
        var root = new XElement(ns + "graphml",
            new XAttribute(XNamespace.Xmlns + "xsi", "http://www.w3.org/2001/XMLSchema-instance"));
        root.Add(new XElement(ns + "key",
            new XAttribute("id", "d0"), new XAttribute("for", "node"),
            new XAttribute("attr.name", "label"), new XAttribute("attr.type", "string")));
        root.Add(new XElement(ns + "key",
            new XAttribute("id", "d1"), new XAttribute("for", "edge"),
            new XAttribute("attr.name", "relation"), new XAttribute("attr.type", "string")));
        var graphElement = new XElement(ns + "graph",
            new XAttribute("id", "crash-causal"), new XAttribute("edgedefault", "directed"));
        foreach (var node in graph.Nodes)
            graphElement.Add(new XElement(ns + "node", new XAttribute("id", node.Id),
                new XElement(ns + "data", new XAttribute("key", "d0"), TitleWithTime(node))));
        foreach (var edge in graph.Edges)
            graphElement.Add(new XElement(ns + "edge",
                new XAttribute("source", edge.FromId), new XAttribute("target", edge.ToId),
                new XElement(ns + "data", new XAttribute("key", "d1"),
                    $"{edge.Relation} (+{edge.LagMs} мс)")));
        root.Add(graphElement);
        return root.ToString();
    }

    private static string TitleWithTime(CausalNode node) =>
        $"{node.TimestampUtc:HH:mm:ss} {node.Title}";

    private static string EscapeMermaid(string value) =>
        value.Replace("\"", "&quot;", StringComparison.Ordinal);

    private static string EscapeDot(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
}
