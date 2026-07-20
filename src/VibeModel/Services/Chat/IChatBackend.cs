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
    }
}
