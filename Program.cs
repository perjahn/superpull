using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;

class Program
{
    public static int Main(string[] args)
    {
        var parsedArgs = args;

        if (parsedArgs.Length == 2 && parsedArgs[0] == "ghclone")
        {
            var username = Environment.GetEnvironmentVariable("GITHUB_USERNAME") ?? string.Empty;
            var password = Environment.GetEnvironmentVariable("GITHUB_PASSWORD") ?? string.Empty;
            SuperClone($"{parsedArgs[1]}", username, password);
            return 0;
        }

        var recurse = parsedArgs.Contains("-r");
        parsedArgs = parsedArgs.Where(a => a != "-r").ToArray();

        if (parsedArgs.Length != 1 && parsedArgs.Length != 2)
        {
            Console.WriteLine("Usage: superpull [-r] <folder>");
            Console.WriteLine("Usage: superpull <ghclone> <username/orgname>");
            return 1;
        }

        var rootfolder = ".";
        if (parsedArgs.Length == 1)
        {
            rootfolder = parsedArgs[0];
        }
        SuperPull(recurse, rootfolder);

        return 0;
    }

    private static void SuperPull(bool recurse, string rootfolder)
    {
        var watch = Stopwatch.StartNew();

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

        Console.WriteLine($"Time: ({watch.Elapsed})");
    }

    static void SuperClone(string baseurl, string username, string password)
    {
        var watch = Stopwatch.StartNew();

        var repourls = GetRepoUrls(baseurl, username, password);

        Console.WriteLine($"Got {repourls.Length} repo urls.");

        Array.Sort(repourls);

        var processes = new List<Process>();
        var processFolders = new List<string>();

        foreach (var repourl in repourls)
        {
            var gitfolder = CleanName(repourl);
            if (Directory.Exists(gitfolder))
            {
                Console.WriteLine($"Folder already exists: '{gitfolder}'");
                continue;
            }

            while (processes.Where(p => { p.Refresh(); return !p.HasExited; }).Count() > 20)
            {
                Thread.Sleep(100);
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"Cloning: '{MaskCredentials(repourl)}' -> '{gitfolder}'");
            Console.ResetColor();

            var p = Process.Start("git", $"clone {repourl} {gitfolder}");
            if (p != null)
            {
                processes.Add(p);
                processFolders.Add(gitfolder);
            }
        }

        Console.WriteLine($"Time: ({watch.Elapsed})");
    }

    static string[] GetRepoUrls(string baseurl, string username, string password)
    {
        var repourls = new List<string>();

        using var client = new HttpClient();
        client.BaseAddress = new Uri("https://api.github.com");
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("gitpull", "1.0"));
        var creds = string.Empty;
        if (username != string.Empty && password != string.Empty)
        {
            creds = $"{username}:{password}";
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(creds)));
        }

        var pagenum = 1;
        bool hasrepo = false;
        do
        {
            Console.WriteLine($"Getting page: {pagenum}");
            var url = $"{baseurl}/repos?page={pagenum}";
            var repositories = new JArray();
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = client.Send(request);

            var jsonString = response.Content.ReadAsStringAsync();
            jsonString.Wait();
            var json = jsonString.Result;
            if (!response.IsSuccessStatusCode || !TryParseJArray(json, out repositories))
            {
                Console.WriteLine($"Warning: {response.StatusCode} ({response.ReasonPhrase}): {json}");
            }

            hasrepo = false;
            foreach (var repo in repositories)
            {
                var repourl = repo["clone_url"]?.Value<string>() ?? string.Empty;
                if (repourl.EndsWith(".git"))
                {
                    repourl = repourl.Substring(0, repourl.Length - 4);
                }
                if (creds != string.Empty)
                {
                    if (repourl.StartsWith("https://"))
                    {
                        repourl = $"{repourl.Substring(0, 8)}{creds}@{repourl.Substring(8)}";
                    }
                }
                if (repourl != string.Empty)
                {
                    repourls.Add(repourl);
                }
                hasrepo = true;
            }

            pagenum++;
        }
        while (hasrepo);

        return repourls.ToArray();
    }

    static bool TryParseJArray(string json, out JArray jarray)
    {
        try
        {
            jarray = JArray.Parse(json);
            return true;
        }
        catch
        {
            jarray = new JArray();
            return false;
        }
    }

    static string CleanName(string url)
    {
        var foldername = url.Replace("%20", "_");
        var index = foldername.LastIndexOf("/");
        if (index >= 0)
        {
            foldername = foldername.Substring(index + 1);
        }

        var sb = new StringBuilder();
        foreach (var c in foldername.ToCharArray())
        {
            sb.Append(char.IsLetterOrDigit(c) ? char.ToLower(c) : "_");
        }
        var cleanname = sb.ToString();
        while (cleanname.Contains("__"))
        {
            cleanname = cleanname.Replace("__", "_");
        }
        return cleanname;
    }

    static string MaskCredentials(string repourl)
    {
        var indexStart = repourl.IndexOf("//");
        if (indexStart < 0)
        {
            return repourl;
        }
        var indexEnd = repourl.IndexOf('@', indexStart);
        if (indexEnd < 0)
        {
            return repourl;
        }
        return $"{repourl.Substring(0, indexStart + 2)}***{repourl.Substring(indexEnd)}";
    }
}
