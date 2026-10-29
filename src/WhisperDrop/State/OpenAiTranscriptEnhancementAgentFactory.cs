using System;
using System.ClientModel;
using System.Diagnostics;
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

    private readonly ITranscriptEnhancementDiagnostics? diagnostics;

    public OpenAiTranscriptEnhancementAgentFactory(ITranscriptEnhancementDiagnostics? diagnostics = null)
    {
        this.diagnostics = diagnostics;
    }

    public ITranscriptEnhancementAgent Create(AiPostProcessingOptions options)
    {
        var endpoint = options.GetValidatedEndpoint();
        var apiKey = string.IsNullOrWhiteSpace(options.ApiKey) ? "ollama" : options.ApiKey;
        diagnostics?.Write(
            $"agent-create-start model={options.Model} endpoint={DescribeEndpoint(endpoint)} " +
            $"apiKeyConfigured={!string.IsNullOrWhiteSpace(options.ApiKey)} timeoutSeconds=600");

        try
        {
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

            diagnostics?.Write("agent-create-complete");
            return new OpenAiTranscriptEnhancementAgent(agent, diagnostics);
        }
        catch (Exception exception)
        {
            diagnostics?.WriteException("agent-create-failed", exception);
            throw;
        }
    }

    private static string DescribeEndpoint(Uri endpoint)
    {
        var host = endpoint.HostNameType == UriHostNameType.IPv6 ? $"[{endpoint.Host}]" : endpoint.IdnHost;
        return $"{endpoint.Scheme}://{host}{(endpoint.IsDefaultPort ? string.Empty : $":{endpoint.Port}")}";
    }

    private sealed class OpenAiTranscriptEnhancementAgent(
        AIAgent agent,
        ITranscriptEnhancementDiagnostics? diagnostics) : ITranscriptEnhancementAgent
    {
        public async Task<TranscriptChunkResult> CorrectAsync(
            TranscriptChunk chunk,
            string? detectedLanguage,
            string userInstructions,
            string? previousResponseError,
            CancellationToken cancellationToken)
        {
            var elapsed = Stopwatch.StartNew();
            diagnostics?.Write(
                $"correction-request-start targetSegments={chunk.Segments.Count} " +
                $"contextSegments={chunk.PreviousContext.Count + chunk.NextContext.Count} " +
                $"language={detectedLanguage ?? "unknown"} retryFeedbackPresent={!string.IsNullOrWhiteSpace(previousResponseError)}");
            try
            {
                var response = await CompleteJsonAsync(
                    TranscriptEnhancementPromptBuilder.BuildUserPrompt(
                        chunk,
                        detectedLanguage,
                        previousResponseError),
                    "correction",
                    cancellationToken).ConfigureAwait(false);
                var result = JsonSerializer.Deserialize<TranscriptChunkResult>(response, JsonOptions)
                    ?? throw new TranscriptEnhancementException("The AI model returned an empty structured response.");
                diagnostics?.Write(
                    $"correction-response-parsed targetSegments={chunk.Segments.Count} " +
                    $"returnedSegments={result.Segments?.Count ?? 0} responseCharacters={response.Length} " +
                    $"elapsedMs={elapsed.ElapsedMilliseconds}");
                return result;
            }
            catch (OperationCanceledException exception)
            {
                diagnostics?.WriteException($"correction-request-cancelled elapsedMs={elapsed.ElapsedMilliseconds}", exception);
                throw;
            }
            catch (Exception exception)
            {
                diagnostics?.WriteException($"correction-request-failed elapsedMs={elapsed.ElapsedMilliseconds}", exception);
                throw new TranscriptEnhancementException(
                    "AI post-processing request failed.",
                    exception);
            }
        }

        public async Task TestConnectionAsync(CancellationToken cancellationToken)
        {
            var elapsed = Stopwatch.StartNew();
            diagnostics?.Write("connection-request-start responseFormat=json");
            try
            {
                var response = await CompleteJsonAsync(
                    $"Return a JSON object with ok=true matching this schema: {ConnectionResultSchema}",
                    "connection-test",
                    cancellationToken).ConfigureAwait(false);
                var result = JsonSerializer.Deserialize<AiConnectionTestResult>(response, JsonOptions);

                if (result?.Ok != true)
                {
                    diagnostics?.Write(
                        $"connection-response-invalid responseCharacters={response.Length} elapsedMs={elapsed.ElapsedMilliseconds}");
                    throw new TranscriptEnhancementException(
                        "The configured model did not return the required structured response.");
                }

                diagnostics?.Write(
                    $"connection-response-valid responseCharacters={response.Length} elapsedMs={elapsed.ElapsedMilliseconds}");
            }
            catch (OperationCanceledException exception)
            {
                diagnostics?.WriteException($"connection-request-cancelled elapsedMs={elapsed.ElapsedMilliseconds}", exception);
                throw;
            }
            catch (TranscriptEnhancementException exception)
            {
                diagnostics?.WriteException($"connection-request-failed elapsedMs={elapsed.ElapsedMilliseconds}", exception);
                throw;
            }
            catch (Exception exception)
            {
                diagnostics?.WriteException($"connection-request-failed elapsedMs={elapsed.ElapsedMilliseconds}", exception);
                throw new TranscriptEnhancementException(
                    "The configured AI endpoint, authentication, model, or structured output could not be used.",
                    exception);
            }
        }

        private async Task<string> CompleteJsonAsync(
            string prompt,
            string requestKind,
            CancellationToken cancellationToken)
        {
            var elapsed = Stopwatch.StartNew();
            diagnostics?.Write($"transport-request-start kind={requestKind} structuredOutput=json");
            var response = await agent.RunAsync(prompt, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var content = response.Text;
            if (string.IsNullOrWhiteSpace(content))
            {
                diagnostics?.Write($"transport-response-empty kind={requestKind} elapsedMs={elapsed.ElapsedMilliseconds}");
                throw new TranscriptEnhancementException("The AI model returned an empty structured response.");
            }

            diagnostics?.Write(
                $"transport-response-received kind={requestKind} responseCharacters={content.Length} " +
                $"elapsedMs={elapsed.ElapsedMilliseconds}");
            return content;
        }

        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    }
}
