using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace VibeModel.Services.Standards
{
    /// <summary>
    /// Collects the ids of elements added while a VibeModel command (or batch) runs, fed by the
    /// DocumentChanged subscription. The post-command check then looks at exactly what the
    /// command created. Main-thread only (DocumentChanged and commands both run there).
    /// </summary>
    public static class CreatedElementsRecorder
    {
        private static List<ElementId> _ids;

        public static bool IsArmed => _ids != null;

        public static void Arm() => _ids = new List<ElementId>();

        /// <summary>Stop recording and return what was added (empty when never armed).</summary>
        public static List<ElementId> Disarm()
        {
            var ids = _ids ?? new List<ElementId>();
            _ids = null;
            return ids;
        }

        public static void Record(ICollection<ElementId> added)
        {
            if (_ids == null || added == null) return;
            _ids.AddRange(added);
        }
    }
}
