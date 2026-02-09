using System;
using System.Threading;

namespace VibeModel.Services.Chat
{
    public interface IChatBackend : IDisposable
    {
        bool IsAvailable { get; }
        string StatusMessage { get; }

        void SendMessage(
            string prompt,
            Action<string> onToken,
            Action<string> onComplete,
            Action<string> onError,
            CancellationToken cancellationToken);

        void Cancel();
        void ResetSession();
    }
}
