using Amazon;
using Amazon.Runtime;

namespace CareFlow.BuildingBlocks.Aws;

public static class AwsClientConfig
{
    /// <summary>Client configuration for <paramref name="region"/>, pointed at LocalStack when <paramref name="serviceUrl"/> is set.</summary>
    public static TConfig Create<TConfig>(string region, string? serviceUrl)
        where TConfig : ClientConfig, new()
    {
        var config = new TConfig { RegionEndpoint = RegionEndpoint.GetBySystemName(region) };
        if (!string.IsNullOrWhiteSpace(serviceUrl))
        {
            config.ServiceURL = serviceUrl;
        }

        return config;
    }
}
