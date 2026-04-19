using Microsoft.EntityFrameworkCore;
using InboxNet.Inbox.EntityFrameworkCore.Configurations;
using InboxNet.Inbox.Models;

namespace InboxNet.Inbox.EntityFrameworkCore;

public class InboxDbContext : DbContext
{
    private readonly string _schema;

    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();
    public DbSet<InboxHandlerAttempt> InboxHandlerAttempts => Set<InboxHandlerAttempt>();

    public InboxDbContext(DbContextOptions<InboxDbContext> options, string schema = "inbox")
        : base(options)
    {
        _schema = schema;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(_schema);
        modelBuilder.ApplyConfiguration(new InboxMessageConfiguration());
        modelBuilder.ApplyConfiguration(new InboxHandlerAttemptConfiguration());
    }
}
