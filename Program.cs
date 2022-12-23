using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

class Program
{
    public static void Main(string[] args)
    {
        var parsedArgs = args;

        var recurse = parsedArgs.Contains("-r");
        parsedArgs = parsedArgs.Where(a => a != "-r").ToArray();

        var rootfolder = ".";
        if (parsedArgs.Length == 1)
        {
            rootfolder = parsedArgs[0];
        }

        var folders = recurse ? Directory.GetDirectories(rootfolder, string.Empty, SearchOption.AllDirectories) : Directory.GetDirectories(rootfolder);

        var gitfolders = folders.Where(d => Directory.Exists(Path.Combine(d, ".git"))).ToArray();

        Console.WriteLine($"Found {gitfolders.Length} repos.");

        Array.Sort(gitfolders);

        var processes = new List<Process>();
        var processFolders = new List<string>();

        foreach (var gitfolder in gitfolders)
        {
            while (processes.Where(p => { p.Refresh(); return !p.HasExited; }).Count() > 20)
            {
                Thread.Sleep(100);
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"Pulling {Path.GetFileName(gitfolder)}...");
            Console.ResetColor();

            var startInfo = new ProcessStartInfo("git", "pull -r") { WorkingDirectory = gitfolder };

            var p = Process.Start(startInfo);

            if (p != null)
            {
                processes.Add(p);
                processFolders.Add(gitfolder);
            }
        }

        int logtimer = 0;
        while (processes.Where(p => { p.Refresh(); return !p.HasExited; }).Count() > 0)
        {
            Thread.Sleep(100);

            logtimer++;
            if (logtimer % 10 == 0)
            {
                var stillRunning = new List<int>();
                for (int i = 0; i < processes.Count; i++)
                {
                    processes[i].Refresh();
                    if (!processes[i].HasExited)
                    {
                        stillRunning.Add(i);
                    }
                }

                if (logtimer < 100)
                {
                    Console.WriteLine($"Still running: {string.Join(", ", stillRunning.Select(i => processFolders[i]))}");
                }
                else
                {
                    foreach (var i in stillRunning)
                    {
                        Console.WriteLine($"Killing: '{processFolders[i]}'");
                        processes[i].Kill(entireProcessTree: true);
                    }
                }
            }
        }
    }
}
