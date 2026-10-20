using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using Autodesk.Revit.UI;

namespace VibeModel.Loader
{
    /// <summary>
    /// What VibeModel.addin starts (plan 19). Picks the installed VibeModel version
    /// (<see cref="LoaderPaths.Choose"/>), loads it from its own folder and passes OnStartup /
    /// OnShutdown on to its <c>VibeModel.VibeModelApp</c>. Revit's add-in security prompt is about
    /// this file only; an in-app update adds a version folder and never touches it.
    /// Every start writes one line to %LOCALAPPDATA%\VibeModel\logs\loader.log.
    /// </summary>
    public class LoaderApp : IExternalApplication
    {
        private const string PayloadType = "VibeModel.VibeModelApp";
        private const string LoaderInfoType = "VibeModel.Services.Update.LoaderInfo";

        private IExternalApplication _payload;
        private static string _payloadFolder;

        public Result OnStartup(UIControlledApplication application)
        {
            var loaderPath = typeof(LoaderApp).Assembly.Location;
            var loaderFolder = Path.GetDirectoryName(loaderPath);
            var loaderVersion = SafeFileVersion(loaderPath);

            PayloadChoice choice;
            try
            {
                choice = LoaderPaths.Choose(loaderFolder, LoaderPaths.DefaultVersionsRoot);
            }
            catch (Exception ex)
            {
                Log("loader " + loaderVersion + " could not look for VibeModel: " + ex);
                choice = new PayloadChoice();
            }

            if (choice.Path == null)
            {
                Log("loader " + loaderVersion + " found no VibeModel.dll; looked in: " + string.Join(" | ", choice.LookedIn));
                ShowDialog(Text(
                    "VibeModel is niet volledig geïnstalleerd: VibeModel.dll is niet gevonden. Voer install.cmd uit de release-zip opnieuw uit.",
                    "VibeModel is not installed completely: VibeModel.dll was not found. Run install.cmd from the release zip again."),
                    string.Join("\n", choice.LookedIn) + "\n\n" + LogFile);
                return Result.Failed;
            }

            try
            {
                _payloadFolder = Path.GetDirectoryName(choice.Path);
                AppDomain.CurrentDomain.AssemblyResolve += ResolveFromPayloadFolder;

                var assembly = Assembly.LoadFrom(choice.Path);
                var type = assembly.GetType(PayloadType, true);
                PublishLoaderInfo(assembly, loaderVersion, loaderPath, choice);
                _payload = (IExternalApplication)Activator.CreateInstance(type);
            }
            catch (Exception ex)
            {
                Log("loader " + loaderVersion + " could not load " + choice.Path + " (" + choice.RuleText + "): " + ex);
                ShowDialog(Text(
                    "VibeModel kon niet worden geladen uit " + _payloadFolder + ".",
                    "VibeModel could not be loaded from " + _payloadFolder + "."),
                    ex.GetType().Name + ": " + ex.Message + "\n\n" + LogFile);
                return Result.Failed;
            }

            Log("loader " + loaderVersion + " starts VibeModel " + SafeFileVersion(choice.Path) + " from " + choice.Path +
                " (" + choice.RuleText + ")");

            try
            {
                var result = _payload.OnStartup(application);
                if (result != Result.Succeeded)
                    Log("VibeModel " + choice.Path + " returned " + result + " from OnStartup (see the VibeModel log)");
                return result;
            }
            catch (Exception ex)
            {
                Log("VibeModel " + choice.Path + " failed in OnStartup: " + ex);
                ShowDialog(Text(
                    "VibeModel is niet gestart (versie in " + _payloadFolder + ").",
                    "VibeModel did not start (version in " + _payloadFolder + ")."),
                    ex.GetType().Name + ": " + ex.Message + "\n\n" + LogFile);
                return Result.Failed;
            }
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            if (_payload == null) return Result.Succeeded;
            try
            {
                return _payload.OnShutdown(application);
            }
            catch (Exception ex)
            {
                Log("VibeModel failed in OnShutdown: " + ex);
                return Result.Succeeded;
            }
        }

        /// <summary>
        /// Markdig and other dependencies normally resolve from the add-in's own folder (the LoadFrom
        /// context probes it); this is the fallback for anything that does not.
        /// </summary>
        private static Assembly ResolveFromPayloadFolder(object sender, ResolveEventArgs args)
        {
            try
            {
                if (string.IsNullOrEmpty(_payloadFolder)) return null;
                var name = new AssemblyName(args.Name).Name;
                var candidate = Path.Combine(_payloadFolder, name + ".dll");
                return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
            }
            catch
            {
                return null;
            }
        }

        // The add-in learns which loader started it (Diagnose, Settings, the update check). An older
        // VibeModel.dll without the class is simply not told.
        private static void PublishLoaderInfo(Assembly payload, string loaderVersion, string loaderPath, PayloadChoice choice)
        {
            try
            {
                var info = payload.GetType(LoaderInfoType, false);
                if (info == null) return;
                Set(info, "Version", loaderVersion);
                Set(info, "Path", loaderPath);
                Set(info, "Rule", choice.Rule.ToString());
            }
            catch (Exception ex)
            {
                Log("could not tell VibeModel which loader started it: " + ex.Message);
            }
        }

        private static void Set(Type type, string property, string value)
        {
            var p = type.GetProperty(property, BindingFlags.Public | BindingFlags.Static);
            if (p != null && p.CanWrite) p.SetValue(null, value);
        }

        private static string SafeFileVersion(string path)
        {
            try
            {
                return FileVersionInfo.GetVersionInfo(path).FileVersion ?? "?";
            }
            catch
            {
                return "?";
            }
        }

        // --- loader.log + the one dialog ---------------------------------------------------

        private static string LogFile => Path.Combine(LoaderPaths.DataRoot, "logs", "loader.log");

        private static void Log(string line)
        {
            try
            {
                var file = LogFile;
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                // A start is one line; keep the file small rather than rotating it.
                var info = new FileInfo(file);
                if (info.Exists && info.Length > 256 * 1024) info.Delete();
                File.AppendAllText(file, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + line + Environment.NewLine);
            }
            catch
            {
                // Logging never stops Revit from starting.
            }
        }

        private static void ShowDialog(string main, string details)
        {
            try
            {
                var dialog = new TaskDialog("VibeModel")
                {
                    MainInstruction = main,
                    MainContent = details,
                    CommonButtons = TaskDialogCommonButtons.Close
                };
                dialog.Show();
            }
            catch (Exception ex)
            {
                Log("could not show the dialog: " + ex.Message);
            }
        }

        // The add-in's own language setting (Settings → Taal / Language), read without loading it.
        private static string Text(string dutch, string english)
        {
            try
            {
                var settings = Path.Combine(LoaderPaths.DataRoot, "settings.json");
                if (File.Exists(settings) &&
                    Regex.IsMatch(File.ReadAllText(settings), "\"Language\"\\s*:\\s*\"en", RegexOptions.IgnoreCase))
                    return english;
            }
            catch
            {
                // Dutch is the default.
            }
            return dutch;
        }
    }
}
