using Desktop.Assistant.Models;
using LLama;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Desktop.Assistant.Services
{
    public class StatefulChatService : IDisposable
    {
        private readonly ChatSession _session;
        private readonly LLamaContext _context;
        private bool _continue = false;

        private const string SystemPrompt = "Transcript of a dialog, where the User interacts with an Assistant. Assistant is helpful, kind, honest, good at writing, and never fails to answer the User's requests immediately and with precision.";

        public StatefulChatService(string GgufPath)
        {
            var @params = new LLama.Common.ModelParams(GgufPath)
            {
                ContextSize = 2048,
            };

            // todo: share weights from a central service
            using var weights = LLamaWeights.LoadFromFile(@params);


            _context = new LLamaContext(weights, @params);

            _session = new ChatSession(new InteractiveExecutor(_context));
            _session.History.AddMessage(LLama.Common.AuthorRole.System, SystemPrompt);
        }

        public void Dispose()
        {
            _context?.Dispose();
        }

        public async Task<string> Send(SendMessageInput input)
        {

            if (!_continue)
            {

                _continue = true;
            }

            var outputs = _session.ChatAsync(
                new LLama.Common.ChatHistory.Message(LLama.Common.AuthorRole.User, input.Text),
                new LLama.Common.InferenceParams()
                {
                    RepeatPenalty = 1.0f,
                    AntiPrompts = new string[] { "User:" },
                });

            var result = "";
            await foreach (var output in outputs)
            {
                result += output;
            }

            return result;
        }

        public async IAsyncEnumerable<string> SendStream(SendMessageInput input)
        {
            if (!_continue)
            {
                _continue = true;
            }


            var outputs = _session.ChatAsync(
                new LLama.Common.ChatHistory.Message(LLama.Common.AuthorRole.User, input.Text!)
                , new LLama.Common.InferenceParams()
                {
                    RepeatPenalty = 1.0f,
                    AntiPrompts = new string[] { "User:" },
                });

            await foreach (var output in outputs)
            {
                yield return output;
            }
        }
    }

}
