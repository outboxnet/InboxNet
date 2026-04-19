using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using InboxNet.Inbox.EntityFrameworkCore.Stores;
using InboxNet.Inbox.Extensions;
using InboxNet.Inbox.Interfaces;

namespace InboxNet.Inbox.EntityFrameworkCore.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the EF Core–backed SQL Server stores and publisher for the inbox. A dedicated
    /// <see cref="InboxDbContext"/> owns the inbox tables — receive endpoints use it directly
    /// and do not need to share a transaction with the caller's domain DbContext, since
    /// inbox ingestion is the atomic unit.
    /// </summary>
    public static IInboxNetBuilder UseSqlServer(
        this IInboxNetBuilder builder,
        string connectionString,
        Action<EfCoreInboxSqlServerOptions>? configure = null)
    {
        var sqlOptions = new EfCoreInboxSqlServerOptions();
        configure?.Invoke(sqlOptions);

        builder.Services.AddDbContext<InboxDbContext>(options =>
        {
            options.UseSqlServer(connectionString, sql =>
            {
                if (sqlOptions.MigrationsAssembly is not null)
                    sql.MigrationsAssembly(sqlOptions.MigrationsAssembly);
            });
        });

        builder.Services.AddScoped<IInboxStore, EfCoreInboxStore>();
        builder.Services.AddScoped<IInboxHandlerAttemptStore, EfCoreInboxHandlerAttemptStore>();
        builder.Services.AddScoped<IInboxPublisher, EfCoreInboxPublisher>();

        return builder;
    }
}

public class EfCoreInboxSqlServerOptions
{
    public string? MigrationsAssembly { get; set; }
}
