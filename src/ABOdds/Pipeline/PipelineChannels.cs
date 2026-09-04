using System.Threading.Channels;
using ABOdds.Configuration;
using Microsoft.Extensions.Options;

namespace ABOdds.Pipeline;

public sealed class PipelineChannels
{
    public PipelineChannels(IOptions<PipelineOptions> options)
    {
        var capacity = options.Value.ChannelCapacity;
        FairValueBatches = CreateChannel(capacity);
        EvBatches = CreateChannel(capacity);
        AlertBatches = CreateChannel(capacity);
        AlertOutbox = CreateChannel(capacity);
    }

    public Channel<Guid> FairValueBatches { get; }
    public Channel<Guid> EvBatches { get; }
    public Channel<Guid> AlertBatches { get; }
    public Channel<Guid> AlertOutbox { get; }

    private static Channel<Guid> CreateChannel(int capacity) =>
        Channel.CreateBounded<Guid>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
}
