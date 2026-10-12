using System;

namespace VibeModel.Infrastructure
{
    /// <summary>
    /// Last-known active Revit document title and path, updated on the Revit UI thread (the
    /// ViewActivated event, and before every command). Lets UI code (the chat pane) and the
    /// chat backends' worker threads label sessions and resolve the project folder without
    /// touching the Revit API outside an API context.
    /// </summary>
    public static class ActiveDocumentTracker
    {
        private static readonly object Lock = new object();
        private static string _title = "";
        private static string _pathName = "";

        public static string Title
        {
            get { lock (Lock) return _title; }
        }

        /// <summary>Document.PathName of the active document ("" when unsaved or none open).</summary>
        public static string PathName
        {
            get { lock (Lock) return _pathName; }
        }

        /// <summary>
        /// Raised (on the thread that called <see cref="Update"/>) when the title or path actually
        /// changed — the chat pane refreshes its welcome card with it.
        /// </summary>
        public static event EventHandler Changed;

        public static void Update(string title, string pathName)
        {
            bool changed;
            lock (Lock)
            {
                title = title ?? "";
                pathName = pathName ?? "";
                changed = title != _title || pathName != _pathName;
                _title = title;
                _pathName = pathName;
            }
            if (changed)
            {
                try { Changed?.Invoke(null, EventArgs.Empty); }
                catch (Exception ex) { Logger.Info("ActiveDocumentTracker.Changed handler failed - " + ex.Message); }
            }
        }
    }
}
