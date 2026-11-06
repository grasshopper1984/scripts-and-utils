using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AgentRoom
{
    internal static class LocalFiles
    {
        internal static string DefaultDirectory { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentRoom"); } }
        internal static void ProtectDirectory(string path)
        {
            if (path.StartsWith(@"\\", StringComparison.Ordinal)) throw new RoomError("local_only", "Room data must be on a local drive.");
            Directory.CreateDirectory(path);
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User,
                FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(path).SetAccessControl(security);
        }
        internal static string PipeName(string directory)
        {
            return "ScriptsAndUtils.AgentRoom." + Json.Hash(WindowsIdentity.GetCurrent().User.Value + "\n" + Path.GetFullPath(directory).ToUpperInvariant()).Substring(0, 24);
        }
        internal static string CanonicalDirectory(string path)
        {
            string full = Path.GetFullPath(path);
            if (full.StartsWith(@"\\", StringComparison.Ordinal) || !Directory.Exists(full))
                throw new RoomError("local_only", "Choose an existing local directory.");
            using (SafeFileHandle handle = CreateFile(full, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero))
            {
                if (handle.IsInvalid) throw new RoomError("invalid_path", "Cannot resolve that directory.");
                var text = new StringBuilder(32768);
                uint length = GetFinalPathNameByHandle(handle, text, (uint)text.Capacity, 0);
                if (length == 0 || length >= text.Capacity) throw new RoomError("invalid_path", "Cannot resolve that directory.");
                string resolved = text.ToString();
                if (resolved.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) throw new RoomError("local_only", "Network worktrees are not supported.");
                if (resolved.StartsWith(@"\\?\", StringComparison.Ordinal)) resolved = resolved.Substring(4);
                return resolved.Length == 3 && resolved[1] == ':' ? resolved : resolved.TrimEnd(Path.DirectorySeparatorChar);
            }
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint length, uint flags);
    }

    internal sealed class GitWorkspace
    {
        internal string Path, CommonDirectory, Branch, Commit;
        // The main checkout's own Git directory is the common directory; linked worktrees have a private one.
        internal bool IsMain;
        internal static GitWorkspace Read(string path)
        {
            string resolved = LocalFiles.CanonicalDirectory(path);
            string top = Run(resolved, "rev-parse --show-toplevel");
            if (!String.Equals(LocalFiles.CanonicalDirectory(top), resolved, StringComparison.OrdinalIgnoreCase))
                throw new RoomError("worktree_root", "Choose the worktree root, not a subdirectory.");
            string common = Run(resolved, "rev-parse --path-format=absolute --git-common-dir");
            string branch = Run(resolved, "rev-parse --abbrev-ref HEAD");
            string commonDirectory = LocalFiles.CanonicalDirectory(common);
            string gitDirectory = LocalFiles.CanonicalDirectory(Run(resolved, "rev-parse --absolute-git-dir"));
            return new GitWorkspace { Path = resolved, CommonDirectory = commonDirectory,
                IsMain = String.Equals(gitDirectory, commonDirectory, StringComparison.OrdinalIgnoreCase),
                Branch = branch == "HEAD" ? "" : branch, Commit = Run(resolved, "rev-parse HEAD") };
        }
        private static string Run(string directory, string arguments)
        {
            var start = new ProcessStartInfo("git.exe", arguments) { WorkingDirectory = directory,
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
            start.EnvironmentVariables["GIT_OPTIONAL_LOCKS"] = "0";
            foreach (string variable in new[] { "GIT_DIR", "GIT_WORK_TREE", "GIT_COMMON_DIR", "GIT_INDEX_FILE" }) start.EnvironmentVariables.Remove(variable);
            using (var process = Process.Start(start))
            {
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(5000)) { process.Kill(); throw new RoomError("git_timeout", "Git inspection timed out."); }
                if (process.ExitCode != 0) throw new RoomError("invalid_repository", "Git could not inspect that local worktree.");
                // Drain both pipes, without exposing Git diagnostics or personal paths to other agents.
                error.GetAwaiter().GetResult();
                return output.GetAwaiter().GetResult().Trim();
            }
        }
    }
}
