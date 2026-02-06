using System;
using Autodesk.Revit.DB;

namespace VibeModel.Services.Helpers
{
    /// <summary>
    /// Wraps the Revit transaction pattern to eliminate boilerplate in commands.
    /// </summary>
    public static class TransactionHelper
    {
        /// <summary>
        /// Execute an action inside a Revit transaction. Returns null on success, error string on failure.
        /// </summary>
        public static string Execute(Document doc, string name, Action action)
        {
            using (var trans = new Transaction(doc, name))
            {
                trans.Start();
                try
                {
                    action();
                    trans.Commit();
                    return null;
                }
                catch (Exception ex)
                {
                    if (trans.HasStarted())
                        trans.RollBack();
                    return "ERROR: " + ex.Message;
                }
            }
        }
    }
}
