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
        var rootfolder = ".";
        if (args.Length == 1)
        {
            rootfolder = args[0];
        }

        var gitfolders = Directory.GetDirectories(rootfolder).Where(d => Directory.Exists(Path.Combine(d, ".git"))).ToArray();

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
