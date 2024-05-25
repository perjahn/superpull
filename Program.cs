using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
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
    static Uri BaseAdress { get; set; } = new("https://api.github.com");
    static ProductInfoHeaderValue UserAgent { get; set; } = new("useragent", "1.0");
    static int PerPage { get; set; } = 100;
    static int Throttle { get; set; } = 10;
    static int Timeout { get; set; } = 60;

    public static async Task<int> Main(string[] args)
    {
        List<string> parsedArgs = [.. args];

        var createsymboliclinks = GetFlagArgument(parsedArgs, "-l");
        Throttle = GetIntArgument(parsedArgs, "-p", Throttle);
        var teams = GetArrayArgument(parsedArgs, "-m");
        var recurse = GetFlagArgument(parsedArgs, "-r");
        Timeout = GetIntArgument(parsedArgs, "-t", Timeout);

        if ((parsedArgs.Count == 2 || parsedArgs.Count == 3) &&
            parsedArgs[0] == "ghclone" && (parsedArgs[1].StartsWith("orgs/") || parsedArgs[1].StartsWith("users/")))
        {
            var githubtoken = Environment.GetEnvironmentVariable("GITHUB_TOKEN") ?? string.Empty;
            return await SuperClone(parsedArgs[1], githubtoken, parsedArgs.Count == 3 ? parsedArgs[2] : string.Empty, teams, createsymboliclinks) ? 0 : 1;
        }

        if (parsedArgs.Count != 1)
        {
            Console.WriteLine(
                "Usage:\n" +
                "superpull [-p throttle] [-t timeout] [-r] <folder>\n" +
                "superpull [-p throttle] [-t timeout] ghclone [-l] [-m team1,team2,...] orgs/<orgname> [folder]\n" +
                "superpull [-p throttle] [-t timeout] ghclone [-l] [-m team1,team2,...] users/<username> [folder]\n" +
                "\n" +
                "ghclone: Clone all github repos, either an org or a user.\n" +
                "-l:      Create symbolic links between repos, based on git submodules.\n" +
                "-m:      Get repos for specific teams, comma separated list of team names.\n" +
                "-p:      Throttle parallel git pull/clone processes (default: 10).\n" +
                "-r:      Recurse subdirectories, looking for any .git folder to pull.\n" +
                "-t:      Timeout, in seconds (default: 60s).\n" +
                "\n" +
                "Environment variables:\n" +
                "GITHUB_TOKEN:  Personal access token for github (only for ghclone).");
            return 1;
        }

        return SuperPull(recurse, parsedArgs[0]) ? 0 : 1;
    }

    static int GetIntArgument(List<string> args, string flagname, int defaultValue)
    {
        var index = args.IndexOf(flagname);
        if (index < 0 || index > args.Count - 2)
        {
            return defaultValue;
        }

        var value = args[index + 1];
        args.RemoveRange(index, 2);
        return int.TryParse(value, out int intValue) ? intValue : defaultValue;
    }

    static bool GetFlagArgument(List<string> args, string flagname)
    {
        var index = args.IndexOf(flagname);
        if (index < 0)
        {
            return false;
        }

        args.RemoveAt(index);
        return true;
    }

    static string[] GetArrayArgument(List<string> args, string flagname)
    {
        var index = args.IndexOf(flagname);
        if (index < 0 || index > args.Count - 2)
        {
            return [];
        }

        var value = args[index + 1];
        args.RemoveRange(index, 2);
        return value.Split(',');
    }

    static bool SuperPull(bool recurse, string rootfolder)
    {
        var watch = Stopwatch.StartNew();

        var folder = rootfolder == string.Empty ? "." : rootfolder;
        if (!Directory.Exists(folder))
        {
            Console.WriteLine($"Folder not found: '{folder}'");
            return false;
        }

        string[] folders = [.. recurse ? Directory.GetDirectories(folder, string.Empty, SearchOption.AllDirectories) : Directory.GetDirectories(rootfolder)
            .Select(f => f.StartsWith("./") ? f[2..] : f)];

        string[] repofolders = [.. folders.Where(d => Directory.Exists(Path.Combine(d, ".git")))];

        Console.WriteLine($"Found {repofolders.Length} repos.");

        Array.Sort(repofolders);

        List<Process> processes = [];
        List<string> processFolders = [];

        var count = 0;

        foreach (var repofolder in repofolders)
        {
            count++;
            while (processes.Count(p => { p.Refresh(); return !p.HasExited; }) >= Throttle)
            {
                Thread.Sleep(100);
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"Pulling ({count}/{repofolders.Length}) {Path.GetFileName(repofolder)}...");
            Console.ResetColor();

            ProcessStartInfo startInfo = new("git", "pull -r") { WorkingDirectory = repofolder };

            var p = Process.Start(startInfo);
            if (p != null)
            {
                processes.Add(p);
                processFolders.Add(repofolder);
            }
        }

        var timer = Stopwatch.StartNew();
        var nextMessage = TimeSpan.FromSeconds(10);
        while (processes.Any(p => { p.Refresh(); return !p.HasExited; }))
        {
            Thread.Sleep(100);

            if (timer.Elapsed > nextMessage)
            {
                List<int> stillRunning = [];
                for (var i = 0; i < processes.Count; i++)
                {
                    processes[i].Refresh();
                    if (!processes[i].HasExited)
                    {
                        stillRunning.Add(i);
                    }
                }

                if (timer.Elapsed < TimeSpan.FromSeconds(Timeout))
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

        return true;
    }

    static async Task<bool> SuperClone(string entity, string githubtoken, string rootfolder, string[] teams, bool createsymboliclinks)
    {
        var watch = Stopwatch.StartNew();

        var folder = rootfolder == string.Empty ? "." : rootfolder;
        if (!Directory.Exists(folder))
        {
            Console.WriteLine($"Creating folder: '{folder}'");
            Directory.CreateDirectory(folder);
        }

        string[] repourls;
        if (teams.Length > 0)
        {
            repourls = await GetRepoUrlsTeams(entity, githubtoken, teams);
        }
        else
        {
            repourls = await GetRepoUrls(entity, githubtoken);
            if (repourls.Length == 0)
            {
                Console.WriteLine($"No git repos found.");
                return false;
            }
        }

        Console.WriteLine($"Got {repourls.Length} repo urls.");

        Array.Sort(repourls);

        List<Process> processes = [];
        List<string> processFolders = [];

        var count = 0;

        foreach (var repourl in repourls)
        {
            count++;
            var repofolder = CleanUrl(repourl);
            repofolder = rootfolder != string.Empty ? Path.Combine(rootfolder, repofolder) : repofolder;
            if (Directory.Exists(repofolder))
            {
                Console.WriteLine($"Folder already exists: '{repofolder}'");
                continue;
            }

            while (processes.Count(p => { p.Refresh(); return !p.HasExited; }) >= Throttle)
            {
                await Task.Delay(100);
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"Cloning ({count}/{repourls.Length}): '{repourl}' -> '{repofolder}'");
            Console.ResetColor();

            var repourlWithCredentials = repourl;
            if (githubtoken != string.Empty)
            {
                var index = repourl.IndexOf("://");
                if (index < 0)
                {
                    Console.WriteLine($"Invalid repo url: '{repourl}'");
                    continue;
                }
                repourlWithCredentials = $"{repourl[..(index + 3)]}{githubtoken}@{repourl[(index + 3)..]}";
            }

            var args = $"clone -- {repourlWithCredentials} {repofolder}";
            var p = Process.Start("git", args);
            if (p == null)
            {
                Console.WriteLine($"Warning: Couldn't start: 'git {args}'");
                continue;
            }
            processes.Add(p);
            processFolders.Add(repofolder);
        }

        var timer = Stopwatch.StartNew();
        var nextMessage = TimeSpan.FromSeconds(10);
        while (processes.Any(p => { p.Refresh(); return !p.HasExited; }))
        {
            await Task.Delay(100);

            if (timer.Elapsed > nextMessage)
            {
                List<int> stillRunning = [];
                for (var i = 0; i < processes.Count; i++)
                {
                    processes[i].Refresh();
                    if (!processes[i].HasExited)
                    {
                        stillRunning.Add(i);
                    }
                }

                if (timer.Elapsed < TimeSpan.FromSeconds(Timeout))
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

        if (createsymboliclinks)
        {
            await CreateSymbolicLinks(repourls, rootfolder);
        }

        Console.WriteLine($"Done: {watch.Elapsed}");

        return true;
    }

    static async Task CreateSymbolicLinks(string[] repourls, string rootfolder)
    {
        List<(Task task, Process process, string repofolder)> processes = [];
        foreach (var repourl in repourls)
        {
            var repofolder = CleanUrl(repourl);
            repofolder = rootfolder != string.Empty ? Path.Combine(rootfolder, repofolder) : repofolder;
            if (!Directory.Exists(repofolder))
            {
                Console.WriteLine($"Warning: Folder not found: '{repofolder}'");
                continue;
            }

            ProcessStartInfo startInfo = new("git", "submodule") { WorkingDirectory = repofolder, RedirectStandardOutput = true };
            var process = Process.Start(startInfo);
            if (process == null)
            {
                Console.WriteLine($"Warning: Couldn't start: '{startInfo.FileName} {startInfo.Arguments}' in '{startInfo.WorkingDirectory}'");
                continue;
            }
            processes.Add((process.WaitForExitAsync(), process, repofolder));
        }

        await Task.WhenAll(processes.Select(p => p.task));

        foreach (var (task, process, repofolder) in processes)
        {
            var output = await process.StandardOutput.ReadToEndAsync();
            if (output.Length == 0)
            {
                continue;
            }
            string[] submodules = [.. output.Split('\n')
                .Select(s => new { s, i = s.IndexOf(' ') })
                .Where(s => s.i >= 0)
                .Select(s => s.s[(s.i + 1)..])];

            foreach (var submodule in submodules)
            {
                var submoduleFolder = Path.Combine(repofolder, submodule);
                var target = Path.Combine("..", submodule);

                var entries = new DirectoryInfo(repofolder).GetFileSystemInfos(submodule);
                if (entries.Length == 1)
                {
                    if (entries[0].LinkTarget == target)
                    {
                        Console.WriteLine($"Existing symbolic link for submodule: '{repofolder}' '{submodule}' --> '{target}'");
                        continue;
                    }
                    entries[0].Delete();
                }

                Console.WriteLine($"Creating symbolic link for submodule: '{repofolder}' '{submodule}' --> '{target}'");
                Directory.CreateSymbolicLink(submodule, target);
            }
        }
    }

    static async Task<string[]> GetRepoUrls(string entity, string githubtoken)
    {
        List<string> repourls = [];

        using HttpClient client = new() { BaseAddress = BaseAdress };
        client.DefaultRequestHeaders.UserAgent.Add(UserAgent);
        if (githubtoken != string.Empty)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(githubtoken)));
        }

        var address = $"{entity}/repos?per_page={PerPage}";
        while (address != string.Empty)
        {
            Console.WriteLine($"Getting repos: '{address}'");

            var content = string.Empty;
            try
            {
                var response = await client.GetAsync(address);
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return [];
                }
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

                var jsonarray = JsonSerializer.Deserialize<GithubRepository[]>(content) ?? [];

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

        return [.. repourls.Distinct()];
    }

    static async Task<string[]> GetRepoUrlsTeams(string entity, string githubtoken, string[] teams)
    {
        List<string> repourls = [];

        using HttpClient client = new() { BaseAddress = BaseAdress };
        client.DefaultRequestHeaders.UserAgent.Add(UserAgent);
        if (githubtoken != string.Empty)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(githubtoken)));
        }

        foreach (var teamname in teams)
        {
            var address = $"{entity}/teams/{teamname}/repos?per_page={PerPage}";
            while (address != string.Empty)
            {
                Console.WriteLine($"Getting repos: '{address}'");

                var content = string.Empty;
                try
                {
                    var response = await client.GetAsync(address);
                    if (response.StatusCode == HttpStatusCode.NotFound)
                    {
                        return [];
                    }
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

                    var jsonarray = JsonSerializer.Deserialize<GithubRepository[]>(content) ?? [];

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
        }

        return [.. repourls.Distinct()];
    }

    static string GetNextLink(HttpResponseHeaders headers)
    {
        if (headers.Contains("Link"))
        {
            string[] links = [.. headers.GetValues("Link").SelectMany(l => l.Split(','))];
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

    static string CleanUrl(string url)
    {
        var foldername = url.Replace("%20", "_");
        var index = foldername.LastIndexOf('/');
        if (index >= 0)
        {
            foldername = foldername[(index + 1)..];
        }

        StringBuilder sb = new();
        foreach (var c in foldername)
        {
            sb.Append(char.IsLetterOrDigit(c) ? char.ToLower(c) : c == '-' || c == '.' ? c : "_");
        }
        var cleanname = sb.ToString();
        while (cleanname.Contains("__"))
        {
            cleanname = cleanname.Replace("__", "_");
        }
        return cleanname;
    }
}
