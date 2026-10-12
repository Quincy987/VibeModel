using System.Collections.Generic;
using System.Text;

namespace VibeModel.Services.Chat.QuickReplies
{
    /// <summary>
    /// Labels whose only effect is that nothing happens ("Nee bedankt", "Not now"). The prompt
    /// forbids them; this is the safety net. The WHOLE normalized label is compared — "Nu niet,
    /// eerst de lijst" is a real request and stays. A hit is logged: it means the prompt regressed.
    /// </summary>
    public static class DeclineLabels
    {
        private static readonly HashSet<string> Declines = new HashSet<string>
        {
            // English
            "no", "nothanks", "nothankyou", "notnow", "maybelater", "later", "keepit", "keepitasis",
            "leaveit", "leaveitasis", "leaveitbe", "leavethembothalone", "leavethemboth", "skipit", "skip",
            "donothing", "imgood", "nokeepit", "noleaveit", "nevermind", "nonotnow", "noimgood",
            // Dutch
            "nee", "neebedankt", "needankje", "needankjewel", "laatmaar", "neelaatmaar", "nuniet", "neenuniet",
            "latermisschien", "misschienlater", "nietnodig", "neenietnodig", "zolaten", "laatzo", "laathetzo", "neelaathetzo",
            "houdhem", "neehoudhem", "houdhet", "neehoudhet", "houdze", "neehoudze", "neelaatstaan", "neelaathemstaan",
            "laatstaan", "laathemstaan", "laatzestaan", "houhetzo", "houhem", "houze", "neehouhem", "niets",
            "doeniets", "overslaan", "slaover", "hoeftniet", "neehoeftniet", "goedzo",
        };

        /// <summary>Letters and digits only, lower case: "Nee, bedankt!" → "neebedankt".</summary>
        public static string Normalize(string label)
        {
            var sb = new StringBuilder();
            foreach (var c in label ?? "")
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        public static bool IsDecline(string label) => Declines.Contains(Normalize(label));
    }
}
