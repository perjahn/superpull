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
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

public class GithubRepository
{
    public string name { get; set; } = string.Empty;
    public string clone_url { get; set; } = string.Empty;
    public int size { get; set; }
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

        var usebearer = ExtractFlagArgument(parsedArgs, "-b");
        var teams = ExtractArrayArguments(parsedArgs, "-e");
        var createsymboliclinks = ExtractFlagArgument(parsedArgs, "-l");
        var maxsizekb = ExtractIntArgument(parsedArgs, "-m", -1);
        Throttle = ExtractIntArgument(parsedArgs, "-p", Throttle);
        var reponamepatterns = ExtractArrayArguments(parsedArgs, "-n");
        var reponamepatternsexclude = ExtractArrayArguments(parsedArgs, "-o");
        var recurse = ExtractFlagArgument(parsedArgs, "-r");
        Timeout = ExtractIntArgument(parsedArgs, "-t", Timeout);

        if ((parsedArgs.Count == 2 || parsedArgs.Count == 3) &&
            parsedArgs[0] == "ghclone" && (parsedArgs[1].StartsWith("orgs/") || parsedArgs[1].StartsWith("users/")))
        {
            var githubtoken = Environment.GetEnvironmentVariable("GITHUB_TOKEN") ?? string.Empty;
            return await SuperClone(parsedArgs[1], githubtoken, usebearer, parsedArgs.Count == 3 ? parsedArgs[2] : string.Empty,
                teams, reponamepatterns, reponamepatternsexclude, maxsizekb, createsymboliclinks) ? 0 : 1;
        }

        if (parsedArgs.Count != 1)
        {
            Console.WriteLine(
                "Usage:\n" +
                "superpull [-b] [-p throttle] [-t timeout] [-r] <folder>\n" +
                "superpull [-b] [-p throttle] [-t timeout] ghclone [-e team] [-l] [-m size] [-n regex] [-o regex] orgs/<orgname> [folder]\n" +
                "superpull [-b] [-p throttle] [-t timeout] ghclone [-e team] [-l] [-m size] [-n regex] [-o regex] users/<username> [folder]\n" +
                "\n" +
                "ghclone: Clone all github repos, either from an org or a user.\n" +
                "-b:      Use bearer token auth, instead of basic auth.\n" +
                "-e:      Filter repos for specific team. Can be specified multiple times.\n" +
                "-l:      Create symbolic links between repos, based on git submodules.\n" +
                "-m:      Filter repos for max size in kb of the .git folder, transferred over the network.\n" +
                "-n:      Filter repos for specific name, using regex. Can be specified multiple times.\n" +
                "-o:      Exclude filter repos for specific name, using regex. Can be specified multiple times.\n" +
                "-p:      Throttle parallel git pull/clone processes (default: 10).\n" +
                "-r:      Recurse subfolders, looking for any .git folder to pull.\n" +
                "-t:      Timeout, in seconds (default: 60s).\n" +
                "\n" +
                "Environment variables:\n" +
                "GITHUB_TOKEN:  Personal access token for github (only for ghclone).");
            return 1;
        }

        return SuperPull(recurse, parsedArgs[0]) ? 0 : 1;
    }

    static int ExtractIntArgument(List<string> args, string flagname, int defaultValue)
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

    static bool ExtractFlagArgument(List<string> args, string flagname)
    {
        var index = args.IndexOf(flagname);
        if (index < 0)
        {
            return false;
        }

        args.RemoveAt(index);
        return true;
    }

    static string[] ExtractArrayArguments(List<string> args, string flagname)
    {
        List<string> values = [];

        int index;
        while ((index = args.IndexOf(flagname)) >= 0 && index < args.Count - 1)
        {
            values.Add(args[index + 1]);
            args.RemoveRange(index, 2);
        }

        return [.. values];
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

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"Done: {watch.Elapsed}");
        Console.ResetColor();

        return true;
    }

    static async Task<bool> SuperClone(string entity, string githubtoken, bool usebearer, string rootfolder,
        string[] teams, string[] reponamepatterns, string[] reponamepatternsexclude, int maxsizekb, bool createsymboliclinks)
    {
        var watch = Stopwatch.StartNew();

        var folder = rootfolder == string.Empty ? "." : rootfolder;
        if (!Directory.Exists(folder))
        {
            Console.WriteLine($"Creating folder: '{folder}'");
            _ = Directory.CreateDirectory(folder);
        }

        var repos = await GetRepoUrls(entity, githubtoken, usebearer, reponamepatterns, reponamepatternsexclude, maxsizekb, teams);
        var repourls = repos.repourls;
        if (repourls.Length == 0)
        {
            if (githubtoken == string.Empty)
            {
                Console.WriteLine($"No git repos found. GITHIB_TOKEN environment variable isn't set, for access to private repos it must be set.");
            }
            else
            {
                Console.WriteLine($"No git repos found.");
            }
            return false;
        }

        if (reponamepatterns.Length > 0 || repourls.Length != repos.totalrepos)
        {
            Console.WriteLine($"Found {repos.totalrepos} repo urls, filtered to {repourls.Length}.");
        }
        else
        {
            Console.WriteLine($"Got {repourls.Length} repos.");
        }

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

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"Done: {watch.Elapsed}");
        Console.ResetColor();

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
                _ = Directory.CreateSymbolicLink(submodule, target);
            }
        }
    }

    static async Task<(string[] repourls, int totalrepos)> GetRepoUrls(string entity, string githubtoken, bool usebearer,
        string[] reponamepatterns, string[] reponamepatternsexclude, int maxsizekb, string[] teams)
    {
        List<GithubRepository> repos = [];

        if (teams.Length > 0)
        {
            foreach (var teamname in teams)
            {
                var address = $"{entity}/teams/{teamname}/repos?per_page={PerPage}";
                repos.AddRange(await GetRepos(address, githubtoken, usebearer));
            }
        }
        else
        {
            var address = $"{entity}/repos?per_page={PerPage}";
            repos = await GetRepos(address, githubtoken, usebearer);
        }

        repos = [.. repos.GroupBy(r => r.name).Select(g => g.First())];

        var totalrepos = repos.Count;

        if (maxsizekb >= 0)
        {
            repos = [.. repos.Where(r => r.size <= maxsizekb)];
        }

        if (reponamepatterns.Length > 0)
        {
            Regex[] regexes = [.. reponamepatterns.Select(p => new Regex(p))];
            repos = [.. repos.Where(r => regexes.Any(re => re.IsMatch(r.name)))];
        }

        if (reponamepatternsexclude.Length > 0)
        {
            Regex[] regexes = [.. reponamepatternsexclude.Select(p => new Regex(p))];
            repos = [.. repos.Where(r => regexes.All(re => !re.IsMatch(r.name)))];
        }

        string[] repourls = [.. repos.Select(r => r.clone_url)];

        for (var i = 0; i < repourls.Length; i++)
        {
            repourls[i] = repourls[i].EndsWith(".git") ? repourls[i][..^4] : repourls[i];
        }

        return ([.. repourls], totalrepos);
    }

    static async Task<List<GithubRepository>> GetRepos(string address, string githubtoken, bool usebearer)
    {
        List<GithubRepository> repos = [];

        using HttpClient client = new() { BaseAddress = BaseAdress };
        client.DefaultRequestHeaders.UserAgent.Add(UserAgent);
        if (githubtoken != string.Empty)
        {
            client.DefaultRequestHeaders.Authorization = usebearer ?
                new("Bearer", githubtoken) :
                new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(githubtoken)));
        }

        while (address != string.Empty)
        {
            Console.WriteLine($"Getting repos: '{address}'");

            var content = string.Empty;
            try
            {
                using HttpResponseMessage response = await client.GetAsync(address);

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
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                Console.WriteLine($"Get '{address}'");
                Console.WriteLine($"Result: >>>{content}<<<");
                Console.WriteLine($"Exception: >>>{ex}<<<");
                continue;
            }
            GithubRepository[] jsonarray;
            try
            {
                jsonarray = JsonSerializer.Deserialize<GithubRepository[]>(content) ?? [];
            }
            catch (JsonException ex)
            {
                Console.WriteLine($"Get '{address}'");
                Console.WriteLine($"Result: >>>{content}<<<");
                Console.WriteLine($"Exception: >>>{ex}<<<");
                continue;
            }

            repos.AddRange(jsonarray);
        }

        return repos;
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
            _ = sb.Append(char.IsLetterOrDigit(c) ? char.ToLower(c) : c is '-' or '.' ? c : "_");
        }
        var cleanname = sb.ToString();
        while (cleanname.Contains("__"))
        {
            cleanname = cleanname.Replace("__", "_");
        }
        return cleanname;
    }
}
