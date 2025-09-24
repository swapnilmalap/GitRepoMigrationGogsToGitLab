using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json.Linq;

class Program
{
    static HashSet<string> migratedRepos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    static string migratedFile = @"E:\Workspace\GitRepoMigrationGogsToGitLab\migrated_repos.txt";

    static void LoadMigratedRepos()
    {
        if (File.Exists(migratedFile))
        {
            foreach (var line in File.ReadAllLines(migratedFile))
            {
                if (!string.IsNullOrWhiteSpace(line))
                    migratedRepos.Add(line.Trim());
            }
        }
    }

    static void SaveMigratedRepo(string repoName)
    {
        File.AppendAllLines(migratedFile, new[] { repoName });
    }

    static async Task Main()
    {
        // Load config
        var config = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .Build();

        string gogsBaseUrl = config["Gogs:BaseUrl"].TrimEnd('/');
        string gogsUsername = config["Gogs:Username"];
        string gogsToken = config["Gogs:Token"];

        string gitlabBaseUrl = config["GitLab:BaseUrl"].TrimEnd('/');
        string gitlabUsername = config["GitLab:Username"];
        string gitlabToken = config["GitLab:Token"];

        LoadMigratedRepos();

        using var gogsClient = new HttpClient();
        gogsClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("token", gogsToken);

        var allRepos = new List<JToken>();

        // 1. Fetch user repos
        Console.WriteLine("Fetching Gogs user repositories...");
        var userReposResponse = await gogsClient.GetStringAsync($"{gogsBaseUrl}/api/v1/users/{gogsUsername}/repos");
        allRepos.AddRange(JArray.Parse(userReposResponse));

        // 2. Fetch orgs
        Console.WriteLine("Fetching Gogs organizations...");
        var orgsResponse = await gogsClient.GetStringAsync($"{gogsBaseUrl}/api/v1/users/{gogsUsername}/orgs");
        var orgs = JArray.Parse(orgsResponse);
        Console.WriteLine(orgs.Count);

        foreach (var org in orgs)
        {
            string orgName = org["username"].ToString();
            Console.WriteLine($"Fetching repos for org: {orgName}");
            var orgReposResponse = await gogsClient.GetStringAsync($"{gogsBaseUrl}/api/v1/orgs/{orgName}/repos");
            allRepos.AddRange(JArray.Parse(orgReposResponse));
        }

        Console.WriteLine(allRepos.Count);
        // 3. Remove duplicates & sort
        var distinctRepos = allRepos
            .GroupBy(r => r["full_name"].ToString(), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderByDescending(r => int.Parse(r["id"].ToString()))
            .ToList();

        using var gitlabClient = new HttpClient();
        gitlabClient.DefaultRequestHeaders.Add("PRIVATE-TOKEN", gitlabToken);
        int count = 0;

        foreach (var repo in distinctRepos)
        {
            count++;  if (count > 1) break;

            string repoName = repo["name"].ToString();
            string ownerName = repo["owner"]["username"].ToString();

            SaveMigratedRepo(repoName + " | " + ownerName);


            if (migratedRepos.Contains($"{ownerName}/{repoName}"))
            {
                Console.WriteLine($"Skipping {ownerName}/{repoName}, already migrated.");
                //continue;
            }

            Console.WriteLine($"\nProcessing repo: {ownerName}/{repoName}");
            //Step 1 : Create group in GitLab
            var groupData = new
            {
                name = ownerName,
                path = ownerName,
                visibility = "private"
            };

            var groupContent = new StringContent(Newtonsoft.Json.JsonConvert.SerializeObject(groupData), Encoding.UTF8, "application/json");
            var createGroupResponse = await gitlabClient.PostAsync($"{gitlabBaseUrl}/api/v4/groups", groupContent);

            if (createGroupResponse.IsSuccessStatusCode)
            {
                var created = JObject.Parse(await createGroupResponse.Content.ReadAsStringAsync());
                Console.WriteLine($"Created new GitLab group: {ownerName}");
            }
            else
            {
                Console.WriteLine($"[Error] Failed to create group {ownerName}: {await createGroupResponse.Content.ReadAsStringAsync()}");
            }


            // Step 1: Create repo in GitLab
            // If repo belongs to an org → create/find GitLab group
            string? namespaceId = null;
            namespaceId = await EnsureGitLabGroup(gitlabClient, gitlabBaseUrl, ownerName);

            // Prepare repo creation data
            var createRepoData = new Dictionary<string, object>
            {
                { "name", repoName },
                { "visibility", "private" }
            };

            if (!string.IsNullOrEmpty(namespaceId))
                createRepoData["namespace_id"] = namespaceId;

            var content = new StringContent(Newtonsoft.Json.JsonConvert.SerializeObject(createRepoData), Encoding.UTF8, "application/json");
            var createResponse = await gitlabClient.PostAsync($"{gitlabBaseUrl}/api/v4/projects", content);
            if (!createResponse.IsSuccessStatusCode)
            {
                Console.WriteLine($"[Error] Failed to create repo {repoName} in GitLab: {await createResponse.Content.ReadAsStringAsync()}");
            }

            // Step 2: Clone from Gogs
            string tempDir = Path.Combine(@"E:\Workspace\Neoquant Temp Gogs Repos", $"repo-{repoName}-{Guid.NewGuid()}");
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);

            Directory.CreateDirectory(tempDir);

            string? gogsRepoUrl = repo["clone_url"]?.ToString();
            gogsRepoUrl = gogsRepoUrl?.Replace("https://", $"https://{gogsToken}@");

            Console.WriteLine("Cloning from Gogs...");
            if (!RunGitCommand($"clone --mirror {gogsRepoUrl} .", tempDir))
            {
                Console.WriteLine("[Error] Git clone failed.");
                Directory.Delete(tempDir, true);
                continue;
            }

            // Step 3: Push to GitLab
            string gitlabRepoUrl = $"{gitlabBaseUrl.Replace("/api/v4", "")}/{ownerName}/{repoName}.git";
            gitlabRepoUrl = gitlabRepoUrl.Replace("https://", $"https://oauth2:{gitlabToken}@");

            Console.WriteLine("Pushing to GitLab...");
            if (!RunGitCommand($"push --mirror {gitlabRepoUrl}", tempDir))
            {
                Console.WriteLine("[Error] Git push failed.");
            }
            else
            {
                SaveMigratedRepo($"{ownerName}/{repoName}");
            }

            // Step 4: Clean up
            //Directory.Delete(tempDir, true);
            Console.WriteLine("Done.");
        }
    }

    static bool RunGitCommand(string command, string workingDir)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = command,
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };

        var output = new StringBuilder();
        var error = new StringBuilder();

        process.OutputDataReceived += (s, e) => { if (e.Data != null) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (s, e) => { if (e.Data != null) error.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.WaitForExit(); // ✅ ensures git push/clone completes

        if (output.Length > 0) Console.WriteLine(output.ToString());
        if (error.Length > 0) Console.WriteLine(error.ToString());

        return process.ExitCode == 0;
    }


    static async Task<string?> EnsureGitLabGroup(HttpClient gitlabClient, string gitlabBaseUrl, string groupName)
    {
        // 1. Check if group already exists
        var searchResponse = await gitlabClient.GetAsync($"{gitlabBaseUrl}/api/v4/groups?search={Uri.EscapeDataString(groupName)}");
        if (searchResponse.IsSuccessStatusCode)
        {
            var groups = JArray.Parse(await searchResponse.Content.ReadAsStringAsync());
            var existing = groups.FirstOrDefault(g => g["path"]?.ToString().Equals(groupName, StringComparison.OrdinalIgnoreCase) == true);
            if (existing != null)
            {
                Console.WriteLine($"Group '{groupName}' already exists in GitLab.");
                return existing["id"]?.ToString();
            }
        }

        // 2. Create the group
        var groupData = new
        {
            name = groupName,
            path = groupName,
            visibility = "private"
        };

        var content = new StringContent(Newtonsoft.Json.JsonConvert.SerializeObject(groupData), Encoding.UTF8, "application/json");
        var createResponse = await gitlabClient.PostAsync($"{gitlabBaseUrl}/api/v4/groups", content);

        if (createResponse.IsSuccessStatusCode)
        {
            var created = JObject.Parse(await createResponse.Content.ReadAsStringAsync());
            Console.WriteLine($"Created new GitLab group: {groupName}");
            return created["id"]?.ToString();
        }
        else
        {
            Console.WriteLine($"[Error] Failed to create group {groupName}: {await createResponse.Content.ReadAsStringAsync()}");
            return null;
        }
    }

}
