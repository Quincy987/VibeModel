using System;

namespace VibeModel.Infrastructure
{
    /// <summary>
    /// Last-known active Revit document title, updated from the ViewActivated event
    /// on the Revit UI thread. Lets UI code (the chat pane) label sessions with the
    /// project name without touching the Revit API outside an API context.
    /// </summary>
    public static class ActiveDocumentTracker
    {
        private static readonly object Lock = new object();
        private static string _title = "";

        public static string Title
        {
            get { lock (Lock) return _title; }
        }

        public static void Update(string title)
        {
            lock (Lock) _title = title ?? "";
        }
    }
}
