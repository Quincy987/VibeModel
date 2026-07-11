using System;
using System.Collections.Generic;

namespace VibeModel.Services.Claude
{
    /// <summary>
    /// Pure parsing of a POST /batch body into a command list + atomic flag.
    /// Split out from <see cref="RevitHttpServer"/> so the parsing rules
    /// (line splitting, blank-line skipping, '#atomic' directive, '?atomic=1' query,
    /// '#'-comment skipping, command/args split) are unit-testable without Revit.
    /// </summary>
    internal static class BatchParser
    {
        /// <summary>
        /// Parse a batch body. <paramref name="rawQuery"/> is the raw HTTP query string
        /// (e.g. "atomic=1"). Returns a result whose <see cref="BatchParseResult.Error"/>
        /// is non-null when the request should be rejected (empty body / no commands).
        /// </summary>
        public static BatchParseResult Parse(string body, string rawQuery)
        {
            if (string.IsNullOrEmpty(body))
                return BatchParseResult.Fail("ERROR: Empty batch body");

            // Atomic (all-or-nothing) is opt-in: ?atomic=1 on the POST, or a leading #atomic line.
            bool atomic = rawQuery != null &&
                          rawQuery.IndexOf("atomic=1", StringComparison.OrdinalIgnoreCase) >= 0;

            var lines = body.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            var commands = new List<BatchCommand>();

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;

                // Directive / comment lines start with '#'. Recognise #atomic; skip the rest.
                if (trimmed.StartsWith("#"))
                {
                    if (trimmed.Equals("#atomic", StringComparison.OrdinalIgnoreCase))
                        atomic = true;
                    continue;
                }

                var parts = trimmed.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
                var cmd = parts[0];
                var args = parts.Length > 1 ? parts[1] : "";
                commands.Add(new BatchCommand(cmd, args));
            }

            if (commands.Count == 0)
                return BatchParseResult.Fail("ERROR: No commands in batch");

            return BatchParseResult.Ok(commands, atomic);
        }
    }

    /// <summary>Outcome of <see cref="BatchParser.Parse"/>.</summary>
    internal sealed class BatchParseResult
    {
        public IReadOnlyList<BatchCommand> Commands { get; private set; }
        public bool Atomic { get; private set; }

        /// <summary>Non-null => the batch is invalid; this is the 400 response body.</summary>
        public string Error { get; private set; }

        public bool IsValid => Error == null;

        public static BatchParseResult Ok(IReadOnlyList<BatchCommand> commands, bool atomic)
            => new BatchParseResult { Commands = commands, Atomic = atomic };

        public static BatchParseResult Fail(string error)
            => new BatchParseResult { Error = error };
    }
}
