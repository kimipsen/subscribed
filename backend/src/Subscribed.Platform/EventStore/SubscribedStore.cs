using JasperFx;
using JasperFx.Events;
using Marten;
using Subscribed.Platform.Database;
using Subscribed.Platform.DataProtection;
using Weasel.Core;

namespace Subscribed.Platform.EventStore;

/// <summary>A module contributes its event types, projections and documents to the store.</summary>
public interface IEventStoreModule
{
    void Configure(StoreOptions options);
}

/// <summary>
/// The only place that configures Marten. Every store (app role at runtime, migrator for the
/// admin CLI, tests) is built here, so the schema the migrator creates is the schema the app uses.
/// </summary>
public static class SubscribedStore
{
    public const string DataProtectionKeyAlias = "dp_key";

    public static DocumentStore Create(string connectionString, string schema, StoreRole role, IEnumerable<IEventStoreModule> modules)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        DatabaseIdentifiers.Require(schema, "Database:Schema");
        var moduleList = modules.ToList();
        return DocumentStore.For(options => Configure(options, connectionString, schema, role, moduleList));
    }

    public static void Configure(StoreOptions options, string connectionString, string schema, StoreRole role, IReadOnlyList<IEventStoreModule> modules)
    {
        options.Connection(connectionString);
        options.DatabaseSchemaName = schema;
        options.Events.DatabaseSchemaName = schema;

        // The application never issues DDL. Only the migrator (admin db migrate) creates objects.
        options.AutoCreateSchemaObjects = role == StoreRole.Migrator ? AutoCreate.CreateOrUpdate : AutoCreate.None;

        options.Events.StreamIdentity = StreamIdentity.AsString;

        // Rich mode: verified (B-1/E5) to need only SELECT, INSERT on mt_events.
        options.Events.AppendMode = EventAppendMode.Rich;
        options.Events.MetadataConfig.HeadersEnabled = true;

        options.UseSystemTextJsonForSerialization(enumStorage: EnumStorage.AsString, casing: Casing.CamelCase);

        options.Schema.For<DataProtectionKeyDocument>()
            .Identity(x => x.Id)
            .DocumentAlias(DataProtectionKeyAlias);

        foreach (var module in modules)
        {
            module.Configure(options);
        }
    }
}

public enum StoreRole
{
    /// <summary>Least-privilege runtime role: no DDL, exact grants.sql privileges.</summary>
    App,

    /// <summary>Schema owner, used only by admin db migrate/patch and projections rebuild.</summary>
    Migrator,
}
