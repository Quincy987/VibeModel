using System;
using System.Collections.Generic;
using System.Threading;

namespace VibeModel.Services.Chat
{
    public interface IChatBackend : IDisposable
    {
        bool IsAvailable { get; }
        string StatusMessage { get; }

        // attachments may be null or empty — text-only messages take the unchanged path.
        void SendMessage(
            string prompt,
            IReadOnlyList<ChatAttachment> attachments,
            Action<string> onToken,
            Action<string> onComplete,
            Action<string> onError,
            CancellationToken cancellationToken);

        void Cancel();
        void ResetSession();

        /// <summary>
        /// Rebuild conversation state from a restored session's transcript so the user
        /// can continue where they left off. Backends without replayable state
        /// (Claude CLI) treat this as a fresh session instead.
        /// </summary>
        void RestoreHistory(IEnumerable<ChatSessionMessage> messages);
    }
}
