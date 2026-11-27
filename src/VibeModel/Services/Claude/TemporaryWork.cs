using System;

namespace VibeModel.Services.Claude
{
    /// <summary>
    /// Work a read-only command does inside a transaction group it always rolls back — the
    /// temporary view copy of a zoomed screenshot (plan 23). While a scope is open, the commits of
    /// that work are invisible to the edit counter, the undo ledger and the change recorder
    /// (`VibeModelApp.OnDocumentChanged` returns early): nothing of it survives the rollback, so
    /// nothing of it may be counted, journaled or offered to `undo`. Revit's main thread only.
    /// </summary>
    public static class TemporaryWork
    {
        private static int _depth;

        public static bool Active => _depth > 0;

        public static IDisposable Begin()
        {
            _depth++;
            return new Scope();
        }

        private sealed class Scope : IDisposable
        {
            private bool _done;

            public void Dispose()
            {
                if (_done) return;
                _done = true;
                _depth--;
            }
        }
    }
}
