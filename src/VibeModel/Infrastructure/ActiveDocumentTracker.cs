using System;

namespace VibeModel.Infrastructure
{
    /// <summary>
    /// Last-known active Revit document title and path, plus the active view and the selection
    /// size, updated on the Revit UI thread (the ViewActivated event, a throttled Idling sample,
    /// and before every command). Lets UI code (the chat pane) and the chat backends' worker
    /// threads label sessions, resolve the project folder and pick suggestion chips without
    /// touching the Revit API outside an API context.
    /// </summary>
    public static class ActiveDocumentTracker
    {
        private static readonly object Lock = new object();
        private static string _title = "";
        private static string _pathName = "";
        private static string _activeViewName = "";
        private static bool _activeViewIsSheet;
        private static int _selectedCount;

        public static string Title
        {
            get { lock (Lock) return _title; }
        }

        /// <summary>Document.PathName of the active document ("" when unsaved or none open).</summary>
        public static string PathName
        {
            get { lock (Lock) return _pathName; }
        }

        /// <summary>Name of the active view ("" when unknown or no document is open).</summary>
        public static string ActiveViewName
        {
            get { lock (Lock) return _activeViewName; }
        }

        /// <summary>True while the active view is a sheet.</summary>
        public static bool ActiveViewIsSheet
        {
            get { lock (Lock) return _activeViewIsSheet; }
        }

        /// <summary>Number of selected elements at the last sample.</summary>
        public static int SelectedCount
        {
            get { lock (Lock) return _selectedCount; }
        }

        /// <summary>
        /// Raised (on the thread that called <see cref="Update"/> or <see cref="UpdateView"/>) when
        /// a tracked value actually changed — the chat pane refreshes its welcome card with it.
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
                if (title.Length == 0)
                {
                    // No document: no view and no selection either.
                    changed |= _activeViewName.Length > 0 || _activeViewIsSheet || _selectedCount != 0;
                    _activeViewName = "";
                    _activeViewIsSheet = false;
                    _selectedCount = 0;
                }
            }
            if (changed) RaiseChanged();
        }

        /// <summary>The active view and the selection size (cheap; called from a throttled Idling sample).</summary>
        public static void UpdateView(string viewName, bool isSheet, int selectedCount)
        {
            bool changed;
            lock (Lock)
            {
                viewName = viewName ?? "";
                selectedCount = Math.Max(0, selectedCount);
                changed = viewName != _activeViewName || isSheet != _activeViewIsSheet || selectedCount != _selectedCount;
                _activeViewName = viewName;
                _activeViewIsSheet = isSheet;
                _selectedCount = selectedCount;
            }
            if (changed) RaiseChanged();
        }

        private static void RaiseChanged()
        {
            try { Changed?.Invoke(null, EventArgs.Empty); }
            catch (Exception ex) { Logger.Info("ActiveDocumentTracker.Changed handler failed - " + ex.Message); }
        }
    }
}
