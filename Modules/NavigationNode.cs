using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace IDVBuff.Modules;

/// <summary>
/// A node in the shell navigation tree. Only nodes with a ModuleId load content.
/// </summary>
public sealed class NavigationNode
{
    public NavigationNode(
        string displayName,
        Symbol icon,
        string? moduleId = null,
        IEnumerable<NavigationNode>? children = null,
        bool isExpanded = false)
    {
        DisplayName = displayName;
        Icon = icon;
        ModuleId = moduleId;
        Children = children?.ToArray() ?? [];
        IsExpanded = isExpanded;
    }

    public string DisplayName { get; }
    public Symbol Icon { get; }
    public string? ModuleId { get; }
    public IReadOnlyList<NavigationNode> Children { get; }
    public bool IsExpanded { get; private set; }

    public void ToggleExpanded()
    {
        if (Children.Count > 0)
            IsExpanded = !IsExpanded;
    }
}

/// <summary>
/// A rendered item in the expandable navigation tree.
/// </summary>
public sealed class NavigationEntry
{
    private readonly List<NavigationEntry> _children = [];

    public NavigationEntry(NavigationNode node, NavigationEntry? parent)
    {
        Node = node;
        Parent = parent;
        parent?._children.Add(this);
    }

    public NavigationNode Node { get; }
    public NavigationEntry? Parent { get; }
    public IReadOnlyList<NavigationEntry> Children => _children;
    public string DisplayName => Node.DisplayName;
    public Symbol Icon => Node.Icon;
    public string? ModuleId => Node.ModuleId;
    public string ExpansionGlyph => "\u203A";
    public Visibility ExpansionVisibility => Node.Children.Count > 0
        ? Visibility.Visible
        : Visibility.Collapsed;
    public Visibility ChildrenVisibility => Node.IsExpanded
        ? Visibility.Visible
        : Visibility.Collapsed;

    public static IReadOnlyList<NavigationEntry> CreateRoots(
        IEnumerable<NavigationNode> nodes)
    {
        var roots = new List<NavigationEntry>();
        foreach (var node in nodes)
            roots.Add(CreateEntryTree(node, parent: null));
        return roots;
    }

    private static NavigationEntry CreateEntryTree(
        NavigationNode node,
        NavigationEntry? parent)
    {
        var entry = new NavigationEntry(node, parent);
        foreach (var child in node.Children)
            CreateEntryTree(child, entry);
        return entry;
    }

}
