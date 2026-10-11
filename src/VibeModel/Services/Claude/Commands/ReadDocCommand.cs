using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.UI;
using VibeModel.Services.Chat;
using VibeModel.Services.Standards;

namespace VibeModel.Services.Claude.Commands
{
    /// <summary>
    /// Plan 10: returns one document from the project folder. Text files inline (capped like
    /// attachments), PDFs as text through the plan-11 PDF helper when it is deployed, Word
    /// documents as their paragraph text; images and anything else as the absolute path plus a
    /// hint. Names resolve case-insensitively inside the folder only — nothing outside it is
    /// ever read, and nothing inside it is written.
    /// </summary>
    public class ReadDocCommand : IClaudeCommand, IStructuredCommand
    {
        public string Name => "readdoc";
        public string Description => "Read one document from the project folder (name as listed by docs; case-insensitive, unique prefix ok). Text/PDF/Word inline, images as a path";
        public string Usage => "readdoc <name>";

        // Revit's thread waits on these reads; a share that stops answering must not hang it.
        private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);

        public string Execute(string args, UIApplication uiApp) => ExecuteStructured(args, uiApp).RenderText();

        public CommandResult ExecuteStructured(string args, UIApplication uiApp)
        {
            var name = (args ?? "").Trim().Trim('"');
            if (name.Length == 0)
                return CommandResult.Error("BAD_ARGS", "Which document? " + Usage, "Run 'docs' to see the project folder.");

            var error = DocsCommand.ResolveFolder(uiApp, out var folder);
            if (error != null) return error;

            if (!ProjectDocs.TryResolveInside(folder.Path, name, out var full, out var why))
                return CommandResult.Error("OUTSIDE_PROJECT_FOLDER", why + " (" + ProjectDocs.Header(folder) + ")",
                    "Use a name from 'docs' — readdoc only reads inside the project folder.");

            var entry = FindEntry(folder, name, full, out error);
            if (error != null) return error;

            if (ProjectDocs.CrossesReparsePoint(folder.Path, entry.FullPath))
                return CommandResult.Error("OUTSIDE_PROJECT_FOLDER",
                    "'" + entry.RelativePath + "' is reached through a link (junction/symlink) — links can lead outside the project folder and are not followed.");

            return Read(folder, entry);
        }

        private static ProjectDocEntry FindEntry(ProjectFolder folder, string name, string full, out CommandResult error)
        {
            error = null;
            var exists = PathProbe.FileExists(full);
            if (exists == null)
            {
                error = CommandResult.Error("FOLDER_UNAVAILABLE", ProjectDocs.Header(folder) + " did not answer in time (network drive offline?).");
                return null;
            }
            if (exists == true)
                return ToEntry(folder, full);

            var listing = ProjectDocs.TryList(folder.Path, null, out var listError);
            if (listing == null)
            {
                error = CommandResult.Error("FOLDER_UNAVAILABLE", ProjectDocs.Header(folder) + ": " + listError);
                return null;
            }
            var hit = ProjectDocs.Find(listing.Entries, name, out var matches);
            if (hit != null) return hit;

            if (matches.Count > 1)
            {
                error = CommandResult.Error("AMBIGUOUS",
                    "'" + name + "' matches " + matches.Count + " documents: " +
                    string.Join(", ", matches.Take(15).Select(m => m.RelativePath)) + (matches.Count > 15 ? ", …" : ""),
                    "Give more of the name.");
                return null;
            }
            error = CommandResult.Error("NOT_FOUND", "No document '" + name + "' in " + ProjectDocs.Header(folder),
                "Run 'docs' to see what is there.");
            return null;
        }

        private static ProjectDocEntry ToEntry(ProjectFolder folder, string full)
        {
            long size = 0;
            try { size = new FileInfo(full).Length; } catch { }
            return new ProjectDocEntry
            {
                RelativePath = ProjectDocs.Relative(Path.GetFullPath(folder.Path).TrimEnd('\\'), full),
                FullPath = full,
                Kind = ChatAttachment.ClassifyExtension(Path.GetExtension(full)),
                SizeBytes = size
            };
        }

        private static CommandResult Read(ProjectFolder folder, ProjectDocEntry e)
        {
            var header = e.RelativePath + "  [" + e.Tag + "]  " + ChatAttachment.FormatSize(e.SizeBytes) +
                         "  — " + ProjectDocs.Header(folder);
            var data = new Dictionary<string, object>
            {
                { "name", e.RelativePath },
                { "path", e.FullPath },
                { "kind", e.Tag },
                { "sizeBytes", e.SizeBytes },
                { "folder", folder.Path },
                { "source", DocsCommand.SourceKey(folder.Source) },
                { "sourceLabel", folder.SourceLabel }
            };

            switch (e.Kind)
            {
                case AttachmentKind.Text:
                {
                    string err = null;
                    var content = PathProbe.Run(() =>
                    {
                        try { return File.ReadAllText(e.FullPath); }
                        catch (Exception ex) { err = ex.Message; return null; }
                    }, ReadTimeout, null);
                    if (content == null)
                        return CommandResult.Error("READ_FAILED", "Could not read " + e.RelativePath + ": " + (err ?? "timed out"));
                    return Inline(header, content, e, data);
                }

                case AttachmentKind.Pdf:
                {
                    var pages = ProjectDocs.ReadPdfPages(e.FullPath, ProjectDocs.PdfWait, out var state, out var pdfError);
                    if (pages != null && pages.Any(p => !string.IsNullOrWhiteSpace(p)))
                    {
                        var sb = new StringBuilder();
                        for (int i = 0; i < pages.Count; i++)
                            sb.Append("--- page ").Append(i + 1).Append(" ---\n").Append(pages[i].TrimEnd()).Append('\n');
                        data["pages"] = pages.Count;
                        return Inline(header + "  (text of " + pages.Count + " page" + (pages.Count == 1 ? "" : "s") + ")",
                            sb.ToString(), e, data);
                    }
                    string hint;
                    switch (state)
                    {
                        case ProjectDocs.PdfTextState.Ready:
                            hint = "This PDF has no text layer (a scan or a drawing) — look at it by reading the file at the path.";
                            break;
                        case ProjectDocs.PdfTextState.Running:
                            hint = "Text extraction is still running in the background — run 'readdoc " + e.RelativePath + "' again in a moment, or read the file at the path.";
                            break;
                        case ProjectDocs.PdfTextState.Failed:
                            hint = "Text extraction failed (" + pdfError + ") — read the file at the path instead.";
                            break;
                        default:
                            hint = "The PDF text helper is not installed — Claude Code can Read the file at the path; in the other chat backends attach it to the message.";
                            break;
                    }
                    return PathOnly(header, e, data, hint);
                }

                case AttachmentKind.Image:
                    return PathOnly(header, e, data,
                        "An image: Claude Code can Read it at the path; in the other chat backends attach it to the message to see it.");

                default:
                    if (e.Tag == "docx")
                    {
                        string err = null;
                        var content = PathProbe.Run(() =>
                        {
                            try { return ProjectDocs.ReadDocxText(e.FullPath); }
                            catch (Exception ex) { err = ex.Message; return null; }
                        }, ReadTimeout, null);
                        if (!string.IsNullOrWhiteSpace(content))
                            return Inline(header + "  (Word text)", content, e, data);
                        return PathOnly(header, e, data, "Could not read the Word text (" + (err ?? "empty or timed out") + ").");
                    }
                    return PathOnly(header, e, data,
                        "Not readable inline (." + e.Tag + "). Claude Code may be able to Read it at the path; otherwise ask the user what it contains.");
            }
        }

        private static CommandResult Inline(string header, string content, ProjectDocEntry e, Dictionary<string, object> data)
        {
            var capped = ProjectDocs.CapText(content, e.FullPath, out var truncated);
            data["content"] = capped;
            data["truncated"] = truncated;
            return CommandResult.Ok(header + "\n" + capped.TrimEnd() + "\n", data);
        }

        private static CommandResult PathOnly(string header, ProjectDocEntry e, Dictionary<string, object> data, string hint)
        {
            data["hint"] = hint;
            return CommandResult.Ok(header + "\nPath: " + e.FullPath + "\nHint: " + hint + "\n", data);
        }
    }
}
