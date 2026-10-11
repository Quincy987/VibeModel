using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace VibeModel.PdfText
{
    /// <summary>
    /// Extracts reading-order text per page and writes {"pages": [...]} as UTF-8 JSON.
    /// Exit codes: 0 ok, 1 usage, 2 extraction failure (message on stderr).
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (args.Length != 2)
            {
                Console.Error.WriteLine("usage: VibeModel.PdfText <input.pdf> <output.json>");
                return 1;
            }

            try
            {
                var pages = new List<string>();
                using (var pdf = PdfDocument.Open(args[0]))
                {
                    foreach (var page in pdf.GetPages())
                    {
                        string text;
                        try { text = ContentOrderTextExtractor.GetText(page); }
                        catch { text = page.Text; } // fall back to raw order rather than losing the page
                        pages.Add(text ?? "");
                    }
                }

                var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }
                    .Serialize(new Dictionary<string, object> { { "pages", pages } });
                File.WriteAllText(args[1], json, new UTF8Encoding(false));
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.GetType().Name + ": " + ex.Message);
                return 2;
            }
        }
    }
}
