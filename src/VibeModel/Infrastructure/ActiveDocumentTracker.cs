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

        public static void Update(string title, string pathName)
        {
            lock (Lock)
            {
                _title = title ?? "";
                _pathName = pathName ?? "";
            }
        }
    }
}
