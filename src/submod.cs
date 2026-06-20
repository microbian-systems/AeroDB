using System;
using System.Diagnostics;
using System.IO;
using System.Text;

/// <summary>
/// A C# console application to automate Git operations across the main repository 
/// and all nested submodules.
/// Usage: app.exe [Optional: "Your commit message here"]
/// </summary>
public class GitAutomator
{
    private const string GitCommand = "git";

    public static void Main(string[] args)
    {
        string commitMessage = string.Empty;

        if (args.Length > 0 && !string.IsNullOrWhiteSpace(args[0]))
        {
            // Commit message provided via command-line argument
            commitMessage = args[0].Trim();
        }
        else
        {
            // Commit message not provided, prompt the user
            Console.Write("Enter your Git commit message (required): ");
            string input = Console.ReadLine();
            
            if (string.IsNullOrWhiteSpace(input))
            {
                Console.WriteLine("\nERROR: A commit message is required to proceed. Exiting.");
                return;
            }
            commitMessage = input.Trim();
        }

        Console.WriteLine($"Starting Git automation with commit message: '{commitMessage}'");
        Console.WriteLine("------------------------------------------");

        try
        {
            // 1. Stage and commit changes in all submodules recursively
            Console.WriteLine("\n--- Staging and committing changes in all submodules (git submodule foreach --recursive) ---");
            RunGitCommand($"submodule foreach --recursive \"git add .\"", "Failed to stage changes in submodules.");
            RunGitCommand($"submodule foreach --recursive \"git commit -m '{commitMessage}'\"", "Failed to commit changes in submodules. (This is acceptable if no changes exist.)");

            // 2. Update submodule references and stage changes in the main repository
            Console.WriteLine("\n--- Staging changes in the main repository (git add .) ---");
            RunGitCommand("add .", "Failed to stage changes in main repository.");

            
            // 3. Commit staged changes with the provided message
            Console.WriteLine($"\n--- Committing all staged changes in main repo (git commit -m \"{commitMessage}\") ---");
            RunGitCommand($"commit -m \"{commitMessage.Replace("\"", "\"\"")}\"", "Commit failed. Check if there are any staged changes.");

            // 4. Push changes to remote
            Console.WriteLine("\n--- Pushing changes to remote (git push) ---");
            RunGitCommand("push", "Failed to push changes to remote.");

            // 5. Push submodule changes to their remotes
            Console.WriteLine("\n--- Pushing submodule changes to their remotes (git push --recurse-submodules=on-demand) ---");
            RunGitCommand("push --recurse-submodules=on-demand", "Failed to push submodule changes. (This is acceptable if no changes exist.)");

            Console.WriteLine("\nSuccessfully completed staging, commit, and push operations.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n[FATAL ERROR] An unexpected error occurred: {ex.Message}");
        }
    }

    /// <summary>
    /// Executes a Git command and streams its output to the console.
    /// Throws an exception if the command fails (non-zero exit code).
    /// </summary>
    /// <param name="arguments">The arguments to pass to the git executable (e.g., "add .", "commit -m 'msg'").</param>
    /// <param name="errorMessage">The error message to display if the command fails.</param>
    private static void RunGitCommand(string arguments, string errorMessage)
    {
        ProcessStartInfo startInfo = new ProcessStartInfo
        {
            FileName = GitCommand,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Directory.GetCurrentDirectory() // Ensure we start in the correct directory
        };

        using (Process process = Process.Start(startInfo))
        {
            if (process == null)
            {
                throw new Exception($"Could not start '{GitCommand}'. Is it installed and in your PATH?");
            }

            // Stream and log output in real-time
            process.OutputDataReceived += (sender, e) => { if (!string.IsNullOrEmpty(e.Data)) Console.WriteLine(e.Data); };
            process.ErrorDataReceived += (sender, e) => { if (!string.IsNullOrEmpty(e.Data)) Console.WriteLine($"[GIT] {e.Data}"); };

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                // Handle common non-failure cases
                bool isExpectedNonFailure = false;

                // Exit code 1 for commit when nothing to commit
                if (process.ExitCode == 1 && arguments.StartsWith("commit"))
                {
                    Console.WriteLine("[INFO] Commit command exited with code 1 (nothing to commit). This is often acceptable.");
                    isExpectedNonFailure = true;
                }

                // Exit code 1 for submodule foreach when some submodules have no changes
                if (process.ExitCode == 1 && arguments.Contains("submodule foreach"))
                {
                    Console.WriteLine("[INFO] Submodule operation exited with code 1 (some submodules may have no changes). This is often acceptable.");
                    isExpectedNonFailure = true;
                }

                // Exit code 1 for push when nothing to push
                if (process.ExitCode == 1 && arguments.StartsWith("push"))
                {
                    Console.WriteLine("[INFO] Push command exited with code 1 (nothing to push). This is often acceptable.");
                    isExpectedNonFailure = true;
                }

                if (!isExpectedNonFailure)
                {
                    throw new Exception($"{errorMessage} (Exit Code: {process.ExitCode})");
                }
            }
        }
    }
}

