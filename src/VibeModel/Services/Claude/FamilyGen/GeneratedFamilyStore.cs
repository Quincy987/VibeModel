using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using VibeModel.Infrastructure;
using VibeModel.Services.Chat;
using VibeModel.Services.Standards;

namespace VibeModel.Services.Claude.FamilyGen
{
    /// <summary>
    /// Where generated families live and how they are (re)loaded (plan 12 §2.3): the .rfa is
    /// built in a local staging folder, loaded from there (so the model never waits on a network
    /// share) and copied to &lt;project folder&gt;\VibeModel\families\ with the project-folder
    /// deadline; an unreachable folder leaves the file in the local fallback and says so.
    /// </summary>
    internal static class GeneratedFamilyStore
    {
        public const string FamiliesFolder = "families";

        // ---------------------------------------------------------------- folders

        public sealed class Target
        {
            public string Directory;      // where the .rfa should end up
            public bool IsFallback;       // the model has no project folder
            public string Label;          // "… (map van het model)" for responses
        }

        /// <summary>
        /// &lt;project folder&gt;\VibeModel\families, or the pack's folder relative to the project
        /// folder, or %LOCALAPPDATA%\VibeModel\projects\&lt;title&gt;\families without one.
        /// </summary>
        public static Target Resolve(Document doc, ProfileDefaults pack)
        {
            var folder = ProjectDocs.ResolveForDocument(doc.Title, doc.PathName);
            var loc = ProjectData.Locate(folder, doc.Title);
            if (loc == null) return new Target { Directory = Path.Combine(StagingRoot, "_unsaved"), IsFallback = true, Label = "(lokaal)" };
            string dir = Path.Combine(loc.Directory, FamiliesFolder);
            if (!loc.IsFallback && pack?.Folder != null)
            {
                try
                {
                    dir = Path.IsPathRooted(pack.Folder) ? pack.Folder : Path.GetFullPath(Path.Combine(folder.Path, pack.Folder));
                }
                catch { /* malformed: keep the default */ }
            }
            return new Target { Directory = dir, IsFallback = loc.IsFallback, Label = loc.IsFallback ? ProjectData.FallbackLabel : loc.Folder.SourceLabel };
        }

        internal static string LocalRoot => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VibeModel");

        /// <summary>Per Revit process, so two instances never build into the same file.</summary>
        internal static string StagingRoot => Path.Combine(LocalRoot, FamiliesFolder, "_staging",
            System.Diagnostics.Process.GetCurrentProcess().Id.ToString(System.Globalization.CultureInfo.InvariantCulture));

        /// <summary>The staging path for a family (the file name is the family name — Revit loads it under that name).</summary>
        public static string StagingPath(string familyName)
        {
            Directory.CreateDirectory(StagingRoot);
            return Path.Combine(StagingRoot, familyName + ".rfa");
        }

        /// <summary>
        /// Copy the staged file to its target folder within the project-folder deadline. Returns the
        /// final path, or null with <paramref name="note"/> saying where the file is instead.
        /// </summary>
        public static string CopyOut(string staged, Target target, out string note)
        {
            note = null;
            var dest = Path.Combine(target.Directory, Path.GetFileName(staged));
            bool ok = PathProbe.Run(() =>
            {
                Directory.CreateDirectory(target.Directory);
                File.Copy(staged, dest, true);
                return true;
            }, ProjectData.IoTimeout, false);
            if (ok) return dest;

            // Unreachable (offline share, read-only): the local fallback keeps a copy.
            try
            {
                var local = Path.Combine(LocalRoot, FamiliesFolder, "_offline");
                Directory.CreateDirectory(local);
                var fallback = Path.Combine(local, Path.GetFileName(staged));
                File.Copy(staged, fallback, true);
                note = "the folder " + target.Directory + " could not be written within " + ProjectData.IoTimeout.TotalSeconds +
                       " s — saved to " + fallback + " instead (the model does not need the file: the family is in it)";
                return fallback;
            }
            catch (Exception ex)
            {
                note = "the .rfa could not be saved (" + ex.Message + "); the family is in the model all the same";
                return null;
            }
        }

        // ---------------------------------------------------------------- templates

        public static readonly Dictionary<string, string> DefaultTemplates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "lineBased", "Metric Generic Model line based.rft" },
            { "generic", "Metric Generic Model.rft" },
            { "foundation", "Metric Structural Foundation.rft" },
            { "framing", "Metric Structural Framing - Beams and Braces.rft" },
            { "profile", "Metric Profile.rft" }
        };

        /// <summary>
        /// The template file for a kind: the pack's template (pack folder, Revit's family template
        /// folder), else the Autodesk metric template (Revit's folder, then ProgramData). A template on
        /// a network share is copied to the local cache once, with a deadline.
        /// </summary>
        public static string Template(Application app, string kind, StandardsPack pack, out string note)
        {
            note = null;
            var tried = new List<string>();
            string office = null;
            if (pack?.Profiles?.FamilyTemplates != null) pack.Profiles.FamilyTemplates.TryGetValue(kind, out office);
            string revitFolder = null;
            try { revitFolder = app.FamilyTemplatePath; } catch { }

            if (!string.IsNullOrWhiteSpace(office))
            {
                var candidates = new List<string>();
                if (Path.IsPathRooted(office)) candidates.Add(office);
                else
                {
                    if (pack.Folder != null) candidates.Add(Path.Combine(pack.Folder, office));
                    if (!string.IsNullOrWhiteSpace(revitFolder)) candidates.Add(Path.Combine(revitFolder, office));
                }
                foreach (var c in candidates)
                {
                    var local = Localize(c, pack);
                    if (local != null) return local;
                    tried.Add(c);
                }
                note = "office template '" + office + "' not found (" + string.Join(", ", tried) + ") — used the Autodesk template";
            }

            var file = DefaultTemplates[kind];
            var dirs = new List<string>();
            if (!string.IsNullOrWhiteSpace(revitFolder)) dirs.Add(revitFolder);
            dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Autodesk", "RVT " + app.VersionNumber, "Family Templates", "English"));
            foreach (var d in dirs)
            {
                var local = Localize(Path.Combine(d, file), pack);
                if (local != null) return local;
            }
            throw new InvalidOperationException("Family template '" + file + "' not found in " + string.Join(" or ", dirs) +
                                                ". Name an office template in the pack (profiles.familyTemplates." + kind + ").");
        }

        /// <summary>A local path for the template: itself when local, else a cached copy (≤ 3 s to fetch).</summary>
        private static string Localize(string path, StandardsPack pack)
        {
            if (!PathProbe.IsNetworkPath(path)) return File.Exists(path) ? path : null;
            var cache = Path.Combine(LocalRoot, "standards", "_cache", pack?.Id ?? "_templates", "templates", Path.GetFileName(path));
            bool ok = PathProbe.Run(() =>
            {
                if (!File.Exists(path)) return File.Exists(cache);
                if (!File.Exists(cache) || File.GetLastWriteTimeUtc(cache) < File.GetLastWriteTimeUtc(path))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(cache));
                    File.Copy(path, cache, true);
                }
                return true;
            }, PathProbe.Default, false);
            return ok || File.Exists(cache) ? cache : null;
        }

        // ---------------------------------------------------------------- shared parameters

        public const string SharedFileName = GeneratedSharedParameters.FileName;
        public const string LengthName = GeneratedSharedParameters.LengthName;
        public const string SourceName = GeneratedSharedParameters.SourceName;
        public static readonly Guid LengthGuid = GeneratedSharedParameters.LengthGuid;
        public static readonly Guid SourceGuid = GeneratedSharedParameters.SourceGuid;

        private static string EnsureSharedFile()
        {
            var path = Path.Combine(LocalRoot, SharedFileName);
            var text = GeneratedSharedParameters.FileText();
            Directory.CreateDirectory(LocalRoot);
            if (!File.Exists(path) || File.ReadAllText(path) != text)
                File.WriteAllText(path, text, Encoding.Unicode);
            return path;
        }

        /// <summary>
        /// Run <paramref name="build"/> with the shared definitions it needs. Revit's
        /// SharedParametersFilename is an application-wide user setting: it is swapped for the
        /// duration and ALWAYS restored, also when the build throws.
        /// </summary>
        public static T WithShared<T>(Application app, Document doc, ProfileDefaults pack, List<string> notes, Func<SharedDefinitions, T> build)
        {
            string previous = null;
            try { previous = app.SharedParametersFilename; } catch { }
            try
            {
                var defs = new SharedDefinitions();
                app.SharedParametersFilename = EnsureSharedFile();
                var ours = app.OpenSharedParameterFile();
                var group = ours?.Groups.get_Item("VibeModel");
                defs.Length = group?.Definitions.get_Item(LengthName) as ExternalDefinition;
                defs.Source = group?.Definitions.get_Item(SourceName) as ExternalDefinition;

                if (pack?.SharedParameterFile != null && (pack.LengthParameter != null || pack.ObjectCodeParameter != null))
                {
                    if (PathProbe.FileExists(pack.SharedParameterFile) == true)
                    {
                        app.SharedParametersFilename = pack.SharedParameterFile;
                        var office = app.OpenSharedParameterFile();
                        var len = Find(office, pack.LengthParameter);
                        if (len != null) defs.Length = len;
                        else if (pack.LengthParameter != null) notes.Add("office length parameter '" + pack.LengthParameter + "' not in " + pack.SharedParameterFile + " — used " + LengthName);
                        var code = Find(office, pack.ObjectCodeParameter);
                        if (code != null && !IsBound(doc, code.GUID)) defs.ObjectCode = code;
                    }
                    else notes.Add("office shared-parameter file " + pack.SharedParameterFile + " not reachable — used " + LengthName);
                }
                return build(defs);
            }
            finally
            {
                try { app.SharedParametersFilename = previous ?? ""; }
                catch (Exception ex) { Logger.Error("Could not restore SharedParametersFilename to '" + previous + "'", ex); }
            }
        }

        private static ExternalDefinition Find(DefinitionFile file, string name)
        {
            if (file == null || string.IsNullOrWhiteSpace(name)) return null;
            foreach (DefinitionGroup g in file.Groups)
                foreach (Definition d in g.Definitions)
                    if (string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)) return d as ExternalDefinition;
            return null;
        }

        private static bool IsBound(Document doc, Guid guid)
        {
            try { return SharedParameterElement.Lookup(doc, guid) != null; }
            catch { return false; }
        }

        // ---------------------------------------------------------------- the family in the project

        public static Family FindFamily(Document doc, string name)
            => new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>()
                .FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

        /// <summary>The VM_Source stamp of a loaded family (its first type that carries one), or null.</summary>
        public static string ReadStamp(Document doc, Family fam)
        {
            if (fam == null) return null;
            foreach (var id in fam.GetFamilySymbolIds())
            {
                var p = doc.GetElement(id)?.get_Parameter(SourceGuid);
                var s = p?.AsString();
                if (!string.IsNullOrWhiteSpace(s)) return s;
            }
            return null;
        }

        /// <summary>
        /// The structure hash of the family as it is loaded now (opens it with EditFamily — no
        /// transaction may be open). Null when it cannot be opened.
        /// </summary>
        public static string CurrentStructure(Document doc, Family fam, bool withSketchPoints, out string error)
        {
            error = null;
            Document fd = null;
            try
            {
                fd = doc.EditFamily(fam);
                return FamilyBuilder.StructureHash(fd, withSketchPoints);
            }
            catch (Exception ex) { error = ex.Message; return null; }
            finally { try { fd?.Close(false); } catch { } }
        }

        /// <summary>Reload keeps the project's type values (Material!) and adds new family parameters to existing instances.</summary>
        internal sealed class KeepValuesLoadOptions : IFamilyLoadOptions
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

        /// <summary>Load (or reload) from the staged file inside the open project transaction.</summary>
        public static Family Load(Document doc, string path, string familyName)
        {
            doc.LoadFamily(path, new KeepValuesLoadOptions(), out Family fam);
            fam = fam ?? FindFamily(doc, familyName);
            if (fam == null) throw new InvalidOperationException("Revit did not load " + path + ".");
            return fam;
        }
    }
}
