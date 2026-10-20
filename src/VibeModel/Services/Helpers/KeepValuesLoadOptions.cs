using Autodesk.Revit.DB;

namespace VibeModel.Services.Helpers
{
    /// <summary>
    /// Reload a family that the project already has: the family's new content wins, the project's
    /// type values (Material!) stay, new family parameters reach the existing instances. Public so
    /// an exec snippet can load an edited family back — it cannot declare a class itself, and
    /// <c>fd.LoadFamily(doc)</c> without options is refused for a loaded family ("Family loading
    /// failed.", measured in Revit 2023): <c>fd.LoadFamily(doc, new VibeModel.Services.Helpers.KeepValuesLoadOptions())</c>.
    /// </summary>
    public sealed class KeepValuesLoadOptions : IFamilyLoadOptions
    {
        public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
        {
            overwriteParameterValues = false;
            return true;
        }

        public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
        {
            source = FamilySource.Family;
            overwriteParameterValues = false;
            return true;
        }
    }
}
