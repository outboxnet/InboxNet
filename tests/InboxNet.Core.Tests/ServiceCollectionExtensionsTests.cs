using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using InboxNet.Extensions;
using InboxNet.Interfaces;
using InboxNet.Options;
using Xunit;

namespace InboxNet.Core.Tests;

public class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddInboxNet_RegistersOptions()
    {
        var services = new ServiceCollection();

        services.AddInboxNet(o =>
        {
            o.BatchSize = 100;
            o.SchemaName = "custom";
            o.InstanceId = "test-instance";
        });

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<OutboxOptions>>();

        options.Value.BatchSize.Should().Be(100);
        options.Value.SchemaName.Should().Be("custom");
        options.Value.InstanceId.Should().Be("test-instance");
    }

    [Fact]
    public void AddInboxNet_RegistersSerializer()
    {
        var services = new ServiceCollection();

        services.AddInboxNet();

        var provider = services.BuildServiceProvider();
        var serializer = provider.GetRequiredService<IMessageSerializer>();

        serializer.Should().NotBeNull();
    }

    [Fact]
    public void AddInboxNet_ReturnsBuilder()
    {
        var services = new ServiceCollection();

        var builder = services.AddInboxNet();

        builder.Should().NotBeNull();
        builder.Services.Should().BeSameAs(services);
    }

    [Fact]
    public void AddInboxNet_DefaultOptions_HaveSensibleDefaults()
    {
        var services = new ServiceCollection();

        services.AddInboxNet();

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<OutboxOptions>>();

        options.Value.BatchSize.Should().Be(50);
        options.Value.SchemaName.Should().Be("outbox");
        options.Value.DefaultVisibilityTimeout.Should().Be(TimeSpan.FromMinutes(5));
        options.Value.MaxConcurrentDeliveries.Should().Be(10);
        options.Value.ProcessingMode.Should().Be(ProcessingMode.DirectDelivery);
    }
}
