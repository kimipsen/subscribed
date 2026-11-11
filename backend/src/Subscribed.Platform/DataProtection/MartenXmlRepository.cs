using System.Xml.Linq;
using Marten;
using Microsoft.AspNetCore.DataProtection.Repositories;

namespace Subscribed.Platform.DataProtection;

/// <summary>Data Protection key XML stored as an insert-only document (design D-F).</summary>
public sealed class DataProtectionKeyDocument
{
    public string Id { get; set; } = "";

    public string Xml { get; set; } = "";
}

/// <summary>
/// Keys are only ever inserted (the app role has SELECT, INSERT on mt_doc_dp_key). Data Protection
/// never rewrites a stored element; revocation is a new element.
/// </summary>
public sealed class MartenXmlRepository(IDocumentStore store) : IXmlRepository
{
    public IReadOnlyCollection<XElement> GetAllElements()
    {
        // IXmlRepository is synchronous; Marten 9 supports only asynchronous data access. ASP.NET Core
        // has no synchronization context, so blocking here cannot deadlock. The key ring is cached.
        return Task.Run(async () =>
        {
            await using var session = store.QuerySession();
            var documents = await session.Query<DataProtectionKeyDocument>().ToListAsync().ConfigureAwait(false);
            return (IReadOnlyCollection<XElement>)documents.Select(d => XElement.Parse(d.Xml)).ToList();
        }).GetAwaiter().GetResult();
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        ArgumentNullException.ThrowIfNull(element);
        var document = new DataProtectionKeyDocument
        {
            Id = string.IsNullOrEmpty(friendlyName) ? Guid.NewGuid().ToString("N") : friendlyName,
            Xml = element.ToString(SaveOptions.DisableFormatting),
        };
        Task.Run(async () =>
        {
            await using var session = store.LightweightSession();
            session.Insert(document);
            await session.SaveChangesAsync().ConfigureAwait(false);
        }).GetAwaiter().GetResult();
    }
}
