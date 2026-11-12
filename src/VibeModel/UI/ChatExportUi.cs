using System;
using System.Diagnostics;
using System.Threading;
using System.Windows.Controls;
using VibeModel.Infrastructure;
using VibeModel.Services.Chat;

namespace VibeModel.UI
{
    /// <summary>
    /// The "Exporteer chats…" button of Diagnose and Settings: exports every saved chat off the UI
    /// thread, then shows the result in <paramref name="status"/> and opens Explorer on the file —
    /// so the user can read it through before sending it.
    /// </summary>
    internal static class ChatExportUi
    {
        public static void Run(Button button, TextBlock status)
        {
            if (button != null) button.IsEnabled = false;
            if (status != null) status.Text = Strings.Get(Strings.ExportChatsBusy);
            var language = Strings.Current;
            var dispatcher = button?.Dispatcher ?? status?.Dispatcher;

            ThreadPool.QueueUserWorkItem(_ =>
            {
                ChatExportResult result;
                try { result = ChatExportService.Export(0, language); }
                catch (Exception ex) { result = new ChatExportResult { Error = ex.Message }; }

                dispatcher?.BeginInvoke(new Action(() =>
                {
                    if (button != null) button.IsEnabled = true;
                    if (result.Error != null)
                    {
                        if (status != null)
                        {
                            status.Text = result.Error == Strings.For(language, Strings.ExportChatsNone)
                                ? result.Error
                                : Strings.Get(Strings.ExportChatsFailed, result.Error);
                            status.ToolTip = null;
                        }
                        return;
                    }
                    if (status != null)
                    {
                        status.Text = Strings.Get(Strings.ExportChatsDone, result.Stats.Chats, System.IO.Path.GetFileName(result.Path));
                        status.ToolTip = result.Path;
                    }
                    Reveal(result.Path);
                }));
            });
        }

        /// <summary>Explorer with the file selected.</summary>
        public static void Reveal(string path)
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Logger.Warn("Chat export: could not open Explorer on " + path + " - " + ex.Message);
            }
        }
    }
}
