using System;
using System.ClientModel;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using WhisperDrop.Models;

namespace WhisperDrop.State;

public sealed class OpenAiTranscriptEnhancementAgentFactory : ITranscriptEnhancementAgentFactory
{
    private const string ConnectionResultSchema = """
{"type":"object","properties":{"ok":{"type":"boolean"}},"required":["ok"],"additionalProperties":false}
""";

    public ITranscriptEnhancementAgent Create(AiPostProcessingOptions options)
    {
        var endpoint = options.GetValidatedEndpoint();
        var apiKey = string.IsNullOrWhiteSpace(options.ApiKey) ? "ollama" : options.ApiKey;

        var chatClient = new ChatClient(
            model: options.Model,
            credential: new ApiKeyCredential(apiKey),
            options: new OpenAIClientOptions
            {
                Endpoint = endpoint,
                NetworkTimeout = TimeSpan.FromMinutes(10)
            });

        var agent = chatClient.AsAIAgent(new ChatClientAgentOptions
        {
            Name = "TranscriptCorrectionAgent",
            Description = "Corrects ASR transcripts and returns the requested JSON object.",
            ChatOptions = new ChatOptions
            {
                Instructions = TranscriptEnhancementPromptBuilder.SystemInstructions,
                Temperature = 0,
                ResponseFormat = Microsoft.Extensions.AI.ChatResponseFormat.Json
            }
        });

        return new OpenAiTranscriptEnhancementAgent(agent);
    }

    private sealed class OpenAiTranscriptEnhancementAgent(AIAgent agent) : ITranscriptEnhancementAgent
    {
        public async Task<TranscriptChunkResult> CorrectAsync(
            TranscriptChunk chunk,
            string? detectedLanguage,
            string userInstructions,
            string? previousResponseError,
            CancellationToken cancellationToken)
        {
            try
            {
                var response = await CompleteJsonAsync(
                    TranscriptEnhancementPromptBuilder.BuildUserPrompt(
                        chunk,
                        detectedLanguage,
                        userInstructions,
                        previousResponseError),
                    cancellationToken).ConfigureAwait(false);
                return JsonSerializer.Deserialize<TranscriptChunkResult>(response, JsonOptions)
                    ?? throw new TranscriptEnhancementException("The AI model returned an empty structured response.");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new TranscriptEnhancementException(
                    "AI post-processing request failed.",
                    exception);
            }
        }

        public async Task TestConnectionAsync(CancellationToken cancellationToken)
        {
            try
            {
                var response = await CompleteJsonAsync(
                    $"Return a JSON object with ok=true matching this schema: {ConnectionResultSchema}",
                    cancellationToken).ConfigureAwait(false);
                var result = JsonSerializer.Deserialize<AiConnectionTestResult>(response, JsonOptions);

                if (result?.Ok != true)
                {
                    throw new TranscriptEnhancementException(
                        "The configured model did not return the required structured response.");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (TranscriptEnhancementException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new TranscriptEnhancementException(
                    "The configured AI endpoint, authentication, model, or structured output could not be used.",
                    exception);
            }
        }

        private async Task<string> CompleteJsonAsync(string prompt, CancellationToken cancellationToken)
        {
            var response = await agent.RunAsync(prompt, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var content = response.Text;
            if (string.IsNullOrWhiteSpace(content))
            {
                throw new TranscriptEnhancementException("The AI model returned an empty structured response.");
            }

            return content;
        }

        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    }
}
