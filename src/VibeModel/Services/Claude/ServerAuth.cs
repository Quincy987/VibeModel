using System;
using System.Security.Cryptography;
using System.Text;
using VibeModel.Infrastructure;

namespace VibeModel.Services.Claude
{
    /// <summary>How the local HTTP server is gated.</summary>
    public enum ServerAuthMode
    {
        /// <summary>A random token made for this Revit session (the default).</summary>
        SessionToken,
        /// <summary>The token in the VIBEMODEL_TOKEN environment variable.</summary>
        EnvironmentToken,
        /// <summary>No token at all (OpenServer = true): how the server behaved up to 1.2.10.</summary>
        Open
    }

    /// <summary>
    /// The token of this Revit session's server. One server per Revit process, so a static holder:
    /// the in-pane backends read <see cref="Token"/> at call time, the Claude Code CLI child gets
    /// it as VIBEMODEL_TOKEN, terminal users read it from the servers file (<see cref="ServerDiscovery"/>).
    /// An explicit VIBEMODEL_TOKEN wins; else OpenServer (managed.json, then settings) switches the
    /// token off; else a random token is generated.
    /// </summary>
    public static class ServerAuth
    {
        /// <summary>The token every request but /health must carry; null when the server is open.</summary>
        public static string Token { get; private set; }

        public static ServerAuthMode Mode { get; private set; } = ServerAuthMode.Open;

        /// <summary>Decides once, at server start; returns the token (null = open).</summary>
        internal static string Initialize()
        {
            var env = Environment.GetEnvironmentVariable(RevitHttpServer.TokenEnvVar);
            Resolve(env, SettingsManager.GetOpenServer(), GenerateToken, out var token, out var mode);
            Token = token;
            Mode = mode;
            return token;
        }

        /// <summary>The decision itself: explicit token, else open when asked, else generated.</summary>
        internal static void Resolve(string envToken, bool openServer, Func<string> generate,
            out string token, out ServerAuthMode mode)
        {
            if (!string.IsNullOrWhiteSpace(envToken))
            {
                token = envToken.Trim();
                mode = ServerAuthMode.EnvironmentToken;
            }
            else if (openServer)
            {
                token = null;
                mode = ServerAuthMode.Open;
            }
            else
            {
                token = generate();
                mode = ServerAuthMode.SessionToken;
            }
        }

        /// <summary>256 random bits as lowercase hex: no quoting needed in a header, a URL or JSON.</summary>
        internal static string GenerateToken()
        {
            var bytes = new byte[32];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(bytes);
            var sb = new StringBuilder(64);
            foreach (var b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        /// <summary>One line for Diagnose and the startup log.</summary>
        public static string Describe(int port)
        {
            switch (Mode)
            {
                case ServerAuthMode.SessionToken:
                    return "per-session token (in " + ServerDiscovery.GetFilePath(port) + ", field \"token\")";
                case ServerAuthMode.EnvironmentToken:
                    return "token from " + RevitHttpServer.TokenEnvVar;
                default:
                    return "OPEN, no token (OpenServer = true" +
                           (ManagedSettings.IsManaged(ManagedSettings.OpenServer) ? ", managed by IT)" : ", settings)");
            }
        }

        /// <summary>Tests only.</summary>
        internal static void SetForTests(string token, ServerAuthMode mode)
        {
            Token = token;
            Mode = mode;
        }
    }
}
