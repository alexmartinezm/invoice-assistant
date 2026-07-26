using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text;
using Api.Assistant.Tools;
using Api.Domain;
using Api.Features.Auth;
using Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Api.Assistant;

public sealed record ChatRequest(Guid? ConversationId, string Message);

/// <summary>
/// One chat turn, start to finish. Read it top to bottom: load the conversation, build the
/// messages, ask the model with the tool catalog attached, stream what comes back, save the turn.
/// </summary>
/// <remarks>
/// Tool calls are executed by <c>Microsoft.Extensions.AI</c>'s function-invoking client, and each
/// tool goes over HTTP to our own API with the caller's token. F2 inserts the policy gate between
/// the model's tool call and its execution; F4 adds usage accounting around the model call.
/// </remarks>
public sealed class ChatOrchestrator(
    IChatClient chatClient,
    ToolCatalog toolCatalog,
    SystemPromptProvider systemPrompt,
    AppDbContext db,
    IClock clock,
    IOptions<AssistantOptions> options,
    ILogger<ChatOrchestrator> logger)
{
    private readonly AssistantOptions _options = options.Value;

    public async IAsyncEnumerable<ChatEvent> RunTurnAsync(
        ChatRequest request,
        ClaimsPrincipal principal,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var turn = AssistantTelemetry.Source.StartActivity("assistant.turn");
        turn?.SetTag("assistant.user_role", principal.Role().ToString());
        turn?.SetTag("assistant.prompt_hash", systemPrompt.Hash);

        // 1. The conversation carries the prompt hash it was started under, so a later regression
        //    can be attributed to the exact prompt that produced these answers.
        var conversation = await LoadOrCreateConversationAsync(request.ConversationId, principal.Id(), cancellationToken);
        turn?.SetTag("assistant.conversation_id", conversation.Id);

        var traceId = (turn ?? Activity.Current)?.TraceId.ToString() ?? string.Empty;
        yield return new ConversationStarted(conversation.Id, traceId);

        // 2. System prompt + a bounded slice of history + what the user just said.
        var userMessage = request.Message.Trim();
        var messages = BuildMessages(conversation, userMessage);

        // 3. The model may call any tool in the catalog; it can reach nothing else.
        var chatOptions = new ChatOptions
        {
            Tools = toolCatalog.ChatTools,
            ToolMode = ChatToolMode.Auto,
            MaxOutputTokens = _options.MaxOutputTokens,
            Temperature = 0f,
        };

        // 4. Stream. Text goes to the user token by token; tool calls surface as activity events
        //    so the UI can say what the assistant is doing instead of freezing.
        var answer = new StringBuilder();
        var toolNamesByCallId = new Dictionary<string, string>(StringComparer.Ordinal);

        await foreach (var update in chatClient.GetStreamingResponseAsync(messages, chatOptions, cancellationToken))
        {
            foreach (var content in update.Contents)
            {
                switch (content)
                {
                    case FunctionCallContent call:
                        toolNamesByCallId[call.CallId] = call.Name;
                        logger.LogInformation("Turn {ConversationId} calls tool {Tool}", conversation.Id, call.Name);
                        yield return new ToolActivity(call.Name, "start", DescribeTool(call.Name));
                        break;

                    case FunctionResultContent result:
                        var toolName = toolNamesByCallId.GetValueOrDefault(result.CallId, "tool");
                        yield return new ToolActivity(toolName, "end", DescribeTool(toolName));
                        break;

                    case TextContent { Text.Length: > 0 } text:
                        answer.Append(text.Text);
                        yield return new TextToken(text.Text);
                        break;
                }
            }
        }

        // 5. Persist the turn so the next one has context.
        await SaveTurnAsync(conversation, userMessage, answer.ToString(), cancellationToken);
        yield return new TurnCompleted(conversation.Id, traceId);
    }

    private async Task<Conversation> LoadOrCreateConversationAsync(
        Guid? conversationId,
        Guid userId,
        CancellationToken cancellationToken)
    {
        if (conversationId is { } id)
        {
            // Scoped by user id: a conversation belongs to whoever started it.
            var existing = await db.Conversations
                .Include(c => c.Messages)
                .SingleOrDefaultAsync(c => c.Id == id && c.UserId == userId, cancellationToken);

            if (existing is not null)
            {
                return existing;
            }
        }

        var conversation = new Conversation
        {
            UserId = userId,
            CreatedAt = clock.UtcNow,
            SystemPromptHash = systemPrompt.Hash,
        };

        db.Conversations.Add(conversation);
        await db.SaveChangesAsync(cancellationToken);
        return conversation;
    }

    private List<ChatMessage> BuildMessages(Conversation conversation, string userMessage)
    {
        List<ChatMessage> messages = [new(ChatRole.System, systemPrompt.Text)];

        messages.AddRange(conversation.Messages
            .OrderBy(m => m.CreatedAt)
            .TakeLast(_options.HistoryMessages)
            .Select(m => new ChatMessage(
                m.Role is MessageRole.User ? ChatRole.User : ChatRole.Assistant,
                m.Content)));

        messages.Add(new ChatMessage(ChatRole.User, userMessage));
        return messages;
    }

    private async Task SaveTurnAsync(
        Conversation conversation,
        string userMessage,
        string answer,
        CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;

        conversation.Messages.Add(new Message
        {
            ConversationId = conversation.Id,
            Role = MessageRole.User,
            Content = userMessage,
            CreatedAt = now,
        });

        conversation.Messages.Add(new Message
        {
            ConversationId = conversation.Id,
            Role = MessageRole.Assistant,
            Content = answer,
            CreatedAt = now.AddMilliseconds(1),
        });

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Fallback label for the activity indicator; the UI may localise it by tool name.</summary>
    private static string DescribeTool(string toolName) => toolName switch
    {
        "list_invoices" => "Checking invoices…",
        "get_invoice" => "Opening the invoice…",
        "search_customers" => "Looking up customers…",
        "get_receivables_summary" => "Totalling receivables…",
        _ => $"Running {toolName}…",
    };
}
