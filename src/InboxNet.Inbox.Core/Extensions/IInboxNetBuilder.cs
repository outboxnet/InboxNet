using Microsoft.Extensions.DependencyInjection;

namespace InboxNet.Inbox.Extensions;

public interface IInboxNetBuilder
{
    IServiceCollection Services { get; }
}
