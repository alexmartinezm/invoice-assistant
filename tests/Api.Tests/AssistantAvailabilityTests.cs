using Api.Assistant;

namespace Api.Tests;

/// <summary>
/// What the chat says when it cannot answer. This is the only diagnostic most deployments get —
/// the app starts fine without a provider — so a message that points at the wrong variable costs
/// somebody a deploy.
/// </summary>
public class AssistantAvailabilityTests
{
    [Fact]
    public void Openai_credentials_under_the_default_provider_are_named_as_the_mismatch_they_are()
    {
        // The provider defaults to azure-openai, so this is what "paste your key into the panel and
        // deploy" produces. Telling this user to go and set AZURE_OPENAI_ENDPOINT sends them after
        // an Azure account they never wanted.
        var availability = ChatClientRegistration.DescribeAvailability(new AiOptions
        {
            Provider = "azure-openai",
            OpenAiApiKey = "sk-test",
            ChatModel = "gpt-4.1-mini-2025-04-14",
        });

        Assert.False(availability.IsConfigured);
        Assert.Contains("AI_PROVIDER=openai-compatible", availability.Reason);
    }

    [Fact]
    public void The_mismatch_is_reported_in_the_other_direction_too()
    {
        var availability = ChatClientRegistration.DescribeAvailability(new AiOptions
        {
            Provider = "openai-compatible",
            AzureEndpoint = "https://example.openai.azure.com/",
            AzureApiKey = "key",
            AzureChatDeployment = "gpt-4.1-mini",
        });

        Assert.False(availability.IsConfigured);
        Assert.Contains("AI_PROVIDER=azure-openai", availability.Reason);
    }

    [Fact]
    public void With_nothing_set_the_message_names_the_variables_the_chosen_provider_wants()
    {
        var azure = ChatClientRegistration.DescribeAvailability(new AiOptions { Provider = "azure-openai" });
        var openAi = ChatClientRegistration.DescribeAvailability(new AiOptions { Provider = "openai-compatible" });

        // No mismatch to report, so neither message should send anyone to change AI_PROVIDER.
        Assert.Contains("AZURE_OPENAI_ENDPOINT", azure.Reason);
        Assert.DoesNotContain("AI_PROVIDER=", azure.Reason);

        Assert.Contains("OPENAI_API_KEY", openAi.Reason);
        Assert.DoesNotContain("AI_PROVIDER=", openAi.Reason);
    }

    [Fact]
    public void A_half_configured_azure_setup_is_told_which_half_is_missing()
    {
        var availability = ChatClientRegistration.DescribeAvailability(new AiOptions
        {
            Provider = "azure-openai",
            AzureEndpoint = "https://example.openai.azure.com/",
            AzureApiKey = "key",
        });

        Assert.False(availability.IsConfigured);
        Assert.Contains("AZURE_OPENAI_CHAT_DEPLOYMENT", availability.Reason);
    }

    [Theory]
    [InlineData("azure-openai")]
    [InlineData("openai-compatible")]
    public void A_fully_configured_provider_is_available(string provider)
    {
        var availability = ChatClientRegistration.DescribeAvailability(new AiOptions
        {
            Provider = provider,
            AzureEndpoint = "https://example.openai.azure.com/",
            AzureApiKey = "key",
            AzureChatDeployment = "gpt-4.1-mini",
            OpenAiApiKey = "sk-test",
            ChatModel = "gpt-4.1-mini-2025-04-14",
        });

        Assert.True(availability.IsConfigured);
        Assert.Equal(string.Empty, availability.Reason);
    }

    [Fact]
    public void A_typo_in_the_provider_name_says_so_rather_than_falling_back()
    {
        // Silently defaulting to a provider here would be worse than failing: the user would be
        // billed against whichever key happened to be set.
        var availability = ChatClientRegistration.DescribeAvailability(new AiOptions { Provider = "openai" });

        Assert.False(availability.IsConfigured);
        Assert.Contains("Unknown AI_PROVIDER 'openai'", availability.Reason);
    }
}
