using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

public class GithubRepository
{
    public string name { get; set; } = string.Empty;
    public string clone_url { get; set; } = string.Empty;
}

class Program
{
    static Uri BaseAdress { get; set; } = new Uri("https://api.github.com");
    static ProductInfoHeaderValue UserAgent { get; set; } = new ProductInfoHeaderValue("useragent", "1.0");
    static int PerPage { get; set; } = 100;
    static int Throttle { get; set; } = 30;

    public static async Task<int> Main(string[] args)
    {
        var parsedArgs = args;

        if (parsedArgs.Length == 2 && parsedArgs[0] == "ghclone")
        {
            var username = Environment.GetEnvironmentVariable("GITHUB_USERNAME") ?? string.Empty;
            var password = Environment.GetEnvironmentVariable("GITHUB_PASSWORD") ?? string.Empty;
            await SuperClone($"{parsedArgs[1]}", username, password);
            return 0;
        }

        var recurse = parsedArgs.Contains("-r");
        parsedArgs = parsedArgs.Where(a => a != "-r").ToArray();

        if (parsedArgs.Length != 1 && parsedArgs.Length != 2)
        {
            Console.WriteLine("Usage: superpull [-r] <folder>");
            Console.WriteLine("Usage: superpull ghclone orgs/<orgname>");
            Console.WriteLine("Usage: superpull ghclone users/<username>");
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

    static void SuperPull(bool recurse, string rootfolder)
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
            while (processes.Count(p => { p.Refresh(); return !p.HasExited; }) > Throttle)
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

        var timer = Stopwatch.StartNew();
        var nextMessage = TimeSpan.FromSeconds(10);
        while (processes.Any(p => { p.Refresh(); return !p.HasExited; }))
        {
            Thread.Sleep(100);

            if (timer.Elapsed > nextMessage)
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

                if (timer.Elapsed < TimeSpan.FromMinutes(1))
                {
                    Console.WriteLine($"Still running: {string.Join(", ", stillRunning.Select(i => processFolders[i]))}");
                    nextMessage += TimeSpan.FromSeconds(10);
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

        Console.WriteLine($"Done: {watch.Elapsed}");
    }

    static async Task SuperClone(string entity, string username, string password)
    {
        var watch = Stopwatch.StartNew();

        var repourls = await GetRepoUrls(entity, username, password);

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

            while (processes.Count(p => { p.Refresh(); return !p.HasExited; }) > Throttle)
            {
                Thread.Sleep(100);
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"Cloning: '{repourl}' -> '{gitfolder}'");
            Console.ResetColor();

            var repourlWithCredentials = repourl;
            if (username != string.Empty && password != string.Empty)
            {
                var index = repourl.IndexOf("://");
                if (index < 0)
                {
                    Console.WriteLine($"Invalid repo url: '{repourl}'");
                    continue;
                }
                repourlWithCredentials = $"{repourl[..(index + 3)]}{username}:{password}@{repourl[(index + 3)..]}";
            }

            var p = Process.Start("git", $"clone {repourlWithCredentials} {gitfolder}");
            if (p != null)
            {
                processes.Add(p);
                processFolders.Add(gitfolder);
            }
        }

        Console.WriteLine($"Done: {watch.Elapsed}");
    }

    static async Task<string[]> GetRepoUrls(string entity, string username, string password)
    {
        var repourls = new List<string>();

        using var client = new HttpClient() { BaseAddress = BaseAdress };
        client.DefaultRequestHeaders.UserAgent.Add(UserAgent);
        var creds = string.Empty;
        if (username != string.Empty && password != string.Empty)
        {
            creds = $"{username}:{password}";
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(creds)));
        }

        var address = $"{entity}/repos?per_page={PerPage}";
        while (address != string.Empty)
        {
            Console.WriteLine($"Getting repos: '{address}'");

            var content = string.Empty;
            try
            {
                var response = await client.GetAsync(address);
                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"Get '{address}', StatusCode: {response.StatusCode}");
                }
                content = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"Result: >>>{content}<<<");
                }
                address = GetNextLink(response.Headers);

                var jsonarray = JsonSerializer.Deserialize<GithubRepository[]>(content) ?? new GithubRepository[] { };

                repourls.AddRange(jsonarray.Select(repo => repo.clone_url.EndsWith(".git") ? repo.clone_url[..^4] : repo.clone_url));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Get '{address}'");
                Console.WriteLine($"Result: >>>{content}<<<");
                Console.WriteLine($"Exception: >>>{ex}<<<");
                continue;
            }
        }

        return repourls.ToArray();
    }

    static string GetNextLink(HttpResponseHeaders headers)
    {
        if (headers.Contains("Link"))
        {
            var links = headers.GetValues("Link").SelectMany(l => l.Split(',')).ToArray();
            foreach (var link in links)
            {
                var parts = link.Split(';');
                if (parts.Length == 2 && parts[0].Trim().StartsWith('<') && parts[0].Trim().EndsWith('>') && parts[1].Trim() == "rel=\"next\"")
                {
                    var url = parts[0].Trim()[1..^1];
                    return url;
                }
            }
        }

        return string.Empty;
    }

    static string CleanName(string url)
    {
        var foldername = url.Replace("%20", "_");
        var index = foldername.LastIndexOf("/");
        if (index >= 0)
        {
            foldername = foldername[(index + 1)..];
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
}
