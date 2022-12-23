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

        foreach (var gitfolder in gitfolders)
        {
            while (processes.Where(p => { p.Refresh(); return !p.HasExited; }).Count() > 20)
            {
                Thread.Sleep(100);
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"Pulling {Path.GetFileName(gitfolder)}...");
            Console.ResetColor();

            var startInfo = new ProcessStartInfo("git", "pull -r");
            startInfo.WorkingDirectory = gitfolder;

            var p = Process.Start(startInfo);

            if (p != null)
            {
                processes.Add(p);
            }
        }

        while (processes.Where(p => { p.Refresh(); return !p.HasExited; }).Count() > 0)
        {
            Thread.Sleep(100);
        }
    }
}
