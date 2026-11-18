using System.Xml.Linq;

namespace NetAI.TestGenerator.Core.Extensions;

/// <summary>Helpers for editing MSBuild project/props XML.</summary>
internal static class ProjectXmlExtensions
{
    /// <summary>
    /// Returns the first <c>ItemGroup</c> that already contains <paramref name="itemName"/> items, or appends a new
    /// <c>ItemGroup</c> to <paramref name="projectRoot"/>.
    /// </summary>
    public static XElement GetOrAddItemGroup(this XElement projectRoot, string itemName)
    {
        var itemGroup = projectRoot.Elements("ItemGroup").FirstOrDefault(g => g.Elements(itemName).Any());
        if (itemGroup is null)
        {
            itemGroup = new XElement("ItemGroup");
            projectRoot.Add(itemGroup);
        }

        return itemGroup;
    }
}
