using System.Xml.Linq;
using NexusMonach.Services.Diagnostics;
using Xunit;

namespace NexusMonach.Security.Tests;

public sealed class CausalGraphTests
{
    private static readonly DateTimeOffset Base = DateTimeOffset.UtcNow;

    private static CausalGraphBuilder.CrashContext RenderFailureContext(
        IReadOnlyList<SystemEventRecord>? systemEvents = null)
    {
        var breadcrumbs = new List<(DateTimeOffset, string, string)>
        {
            (Base.AddSeconds(-120), "startup", "main-window-ready"),
            (Base.AddSeconds(-31), "webview2", "process-RenderProcessUnresponsive"),
            (Base.AddSeconds(-25), "wpf", "dispatcher-unhandled")
        };
        return new CausalGraphBuilder.CrashContext(
            "System.Runtime.InteropServices.COMException",
            "UCEERR_RENDERTHREADFAILURE (0x88980406)",
            "wpf", "dispatcher-unhandled", breadcrumbs,
            systemEvents ?? []);
    }

    [Fact]
    public void WebView2RendererHang_IsCausalParentOfRenderFailure()
    {
        var graph = CausalGraphBuilder.Build(RenderFailureContext());

        var crash = graph.Nodes.Single(n => n.Id == "crash");
        Assert.Contains("UCEERR_RENDERTHREADFAILURE", crash.Title, StringComparison.Ordinal);

        // Зависание рендерера связано с отказом прямым ребром «вызвало».
        var hang = graph.Nodes.Single(n => n.Title.Contains("RenderProcessUnresponsive"));
        Assert.Contains(graph.Edges, e =>
            e.FromId == hang.Id && e.ToId == "crash" && e.Relation == CausalGraphBuilder.RelationCaused);

        // Корневая причина — самый ранний узел цепочки, а не сам отказ.
        Assert.NotEqual("crash", graph.RootCauseNodeId);
    }

    [Fact]
    public void DwmCrash_IsRootCauseOfCompositionFailure()
    {
        var breadcrumbs = new List<(DateTimeOffset, string, string)>
        {
            (Base.AddSeconds(-30), "startup", "main-window-ready")
        };
        var context = new CausalGraphBuilder.CrashContext(
            "System.Runtime.InteropServices.COMException",
            "Композиция рабочего стола отключена (0x80263001)",
            "wpf", "dispatcher-unhandled", breadcrumbs,
            [new SystemEventRecord(SystemEventReader.KindDwmCrash,
                "Крах процесса: dwm.exe", Base.AddSeconds(-40), "код c0000409")]);

        var graph = CausalGraphBuilder.Build(context);

        var root = graph.Nodes.Single(n => n.Id == graph.RootCauseNodeId);
        Assert.Contains("dwm.exe", root.Title, StringComparison.Ordinal);
        Assert.Contains(graph.Edges, e =>
            e.FromId == root.Id && e.ToId == "crash" && e.Relation == CausalGraphBuilder.RelationCaused);
    }

    [Fact]
    public void DisplayDriverReset_IsRootCauseOfRenderFailure()
    {
        var context = RenderFailureContext(
        [
            new SystemEventRecord(SystemEventReader.KindDisplayDriverReset,
                "Сброс видеоадаптера: nvlddmkm", Base.AddSeconds(-60), "восстановлен")
        ]);

        var graph = CausalGraphBuilder.Build(context);

        var root = graph.Nodes.Single(n => n.Id == graph.RootCauseNodeId);
        Assert.Contains("Сброс видеоадаптера", root.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void UnrelatedSystemEvent_IsCorrelatedNotCausal()
    {
        var context = RenderFailureContext(
        [
            new SystemEventRecord(SystemEventReader.KindAppCrash,
                "Крах процесса: somegame.exe", Base.AddSeconds(-50), "код c0000005")
        ]);

        var graph = CausalGraphBuilder.Build(context);

        var game = graph.Nodes.Single(n => n.Title.Contains("somegame"));
        var edge = graph.Edges.Single(e => e.FromId == game.Id && e.ToId == "crash");
        Assert.Equal(CausalGraphBuilder.RelationCorrelated, edge.Relation);
    }

    [Fact]
    public void EmptyBreadcrumbs_StillProducesValidGraph()
    {
        var context = new CausalGraphBuilder.CrashContext(
            "System.Exception", "тест", "tests", "unit", [], []);

        var graph = CausalGraphBuilder.Build(context);

        Assert.Contains(graph.Nodes, n => n.Id == "crash");
        Assert.Empty(graph.Edges);
    }

    [Fact]
    public void MermaidExport_IsWellFormedAndMarksRoot()
    {
        var graph = CausalGraphBuilder.Build(RenderFailureContext());
        var mermaid = CausalGraphExporter.ToMermaid(graph);

        Assert.StartsWith("graph TD", mermaid);
        Assert.Contains("-->", mermaid);
        Assert.Contains("вызвало", mermaid);
        Assert.Contains("stroke:#FF6B6B", mermaid); // корневая причина подсвечена
    }

    [Fact]
    public void MermaidExport_EscapesQuotesInLabels()
    {
        var graph = CausalGraphBuilder.Build(new CausalGraphBuilder.CrashContext(
            "System.Exception", "сообщение с \"кавычками\"", "wpf", "test",
            [(Base, "wpf", "stage")], []));
        var mermaid = CausalGraphExporter.ToMermaid(graph);

        Assert.DoesNotContain("[\"сообщение", mermaid); // кавычка экранирована
        Assert.Contains("&quot;", mermaid);
    }

    [Fact]
    public void DotExport_ProducesDigraph()
    {
        var graph = CausalGraphBuilder.Build(RenderFailureContext());
        var dot = CausalGraphExporter.ToDot(graph);

        Assert.StartsWith("digraph CrashCausal", dot);
        Assert.Contains(" -> ", dot);
        Assert.EndsWith("}\n", dot);
    }

    [Fact]
    public void GraphMLExport_IsValidXml()
    {
        var graph = CausalGraphBuilder.Build(RenderFailureContext());
        var graphml = CausalGraphExporter.ToGraphML(graph);

        var xml = XDocument.Parse(graphml);
        var ns = xml.Root!.Name.Namespace;
        Assert.Equal("graphml", xml.Root.Name.LocalName);
        Assert.NotEmpty(xml.Root.Descendants(ns + "node"));
        Assert.NotEmpty(xml.Root.Descendants(ns + "edge"));
    }
}
