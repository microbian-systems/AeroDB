using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

/// <summary>
/// A C# console application to perform unattended, in-place upgrades 
/// of all .csproj files to .NET 9.0 using the .NET Upgrade Assistant.
/// This script uses the required flags (-o Inplace, -f net9.0, --yes, --all-steps)
/// to ensure a non-interactive execution.
/// </summary>
public class UpgradeRunner
{
    private const string TargetFramework = "net9.0";
    private const string UpgradeAssistantCommand = "upgrade-assistant";

    public static void Main(string[] args)
    {
        Console.WriteLine($"Starting fully unattended, in-place upgrade to {TargetFramework}...");

        try
        {
            // Recursively find all .csproj files in the current directory and subdirectories
            string[] projectFiles = Directory.GetFiles(
                Directory.GetCurrentDirectory(), 
                "*.csproj", 
                SearchOption.AllDirectories
            );

            if (!projectFiles.Any())
            {
                Console.WriteLine("No .csproj files found in the current directory or subdirectories. Exiting.");
                return;
            }

            Console.WriteLine($"Found {projectFiles.Length} project files to upgrade.");

            foreach (var file in projectFiles)
            {
                // Get the relative path for cleaner console output
                string relativePath = Path.GetRelativePath(Directory.GetCurrentDirectory(), file);
                Console.WriteLine($"\n--- Processing {relativePath} ---");

                // Construct the arguments array for the upgrade-assistant command
                string arguments = $"upgrade \"{file}\" " +
                                   $"-o Inplace " + // In-place upgrade (replaces --in-place in older versions)
                                   $"-f {TargetFramework} " + // Target framework to net9.0 (replaces --target-framework-version)
                                   "--non-interactive " + // Suppress general user prompts
                                   "--skip-backup " + // Prevents backup folder creation
                                   "--all-steps " + // Automatically select all steps (TFM, packages, configs)
                                   "--yes " + // Confirms all internal prompts and warnings
                                   "-t LTS "; // Required for the original logic but technically covered by -f net9.0

                // Create the process start information
                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = UpgradeAssistantCommand,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (Process process = Process.Start(startInfo))
                {
                    if (process == null)
                    {
                        Console.WriteLine($"ERROR: Could not start '{UpgradeAssistantCommand}'. Is it installed and in your PATH?");
                        continue;
                    }

                    // Log output in real-time
                    process.OutputDataReceived += (sender, e) => { if (!string.IsNullOrEmpty(e.Data)) Console.WriteLine(e.Data); };
                    process.ErrorDataReceived += (sender, e) => { if (!string.IsNullOrEmpty(e.Data)) Console.WriteLine($"[ERROR] {e.Data}"); };

                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    process.WaitForExit();

                    if (process.ExitCode == 0)
                    {
                        Console.WriteLine($"Successfully completed unattended upgrade for {relativePath}.");
                    }
                    else
                    {
                        Console.WriteLine($"!!! Upgrade of {relativePath} FAILED with exit code {process.ExitCode}. Check the output logs above.");
                    }
                }
                Console.WriteLine("------------------------");
            }

            Console.WriteLine("\nFully unattended upgrade process complete.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"An error occurred during execution: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
        }
    }
}

