﻿using CommandLine;
using Discord;
using Discord.WebSocket;
using Kemocade.Vrc.Group.Tracker.Action;
using Kemocade.Vrc.Role.Tracker.Action;
using OtpNet;
using System.Text.Json;
using System.Text.Json.Serialization;
using VRChat.API.Api;
using VRChat.API.Client;
using VRChat.API.Model;
using static Kemocade.Vrc.Role.Tracker.Action.TrackedData;
using static Kemocade.Vrc.Role.Tracker.Action.TrackedData.TrackedDiscordServer;
using static Kemocade.Vrc.Role.Tracker.Action.TrackedData.TrackedVrcGroup;
using static System.Console;
using static System.IO.File;
using static System.Text.Json.JsonSerializer;

// Constants
const string USR_PREFIX = "usr_";
const int USR_LENGTH = 40;
const string VRC_GROUP_OWNER = "*";
const string VRC_GROUP_MODERATOR = "group-instance-moderate";
const int DISCORD_MAX_ATTEMPTS = 5;
const int DISCORD_MAX_MESSAGES = 100000;

// Configure Cancellation
using CancellationTokenSource tokenSource = new();
CancelKeyPress += delegate { tokenSource.Cancel(); };

// Configure Inputs
ParserResult<ActionInputs> parser = Parser.Default.ParseArguments<ActionInputs>(args);
if (parser.Errors.ToArray() is { Length: > 0 } errors)
{
    foreach (CommandLine.Error error in errors)
    { WriteLine($"{nameof(error)}: {error.Tag}"); }
    Environment.Exit(2);
    return;
}
ActionInputs inputs = parser.Value;

bool useGroups = !string.IsNullOrEmpty(inputs.Groups);
bool useWorlds = !string.IsNullOrEmpty(inputs.Worlds);
bool useDiscord = !string.IsNullOrEmpty(inputs.Bot) &&
    !string.IsNullOrEmpty(inputs.Discords) &&
    !string.IsNullOrEmpty(inputs.Channels);

// Parse delimeted inputs
string[] groupIds = useGroups ?
     inputs.Groups.Split(',') : Array.Empty<string>();
string[] worldIds = useWorlds ?
     inputs.Worlds.Split(',') : Array.Empty<string>();
ulong[] servers = useDiscord ?
     inputs.Discords.Split(',').Select(ulong.Parse).ToArray() : Array.Empty<ulong>();
ulong[] channels = useDiscord ?
    inputs.Channels.Split(',').Select(ulong.Parse).ToArray() : Array.Empty<ulong>();

// Ensure parallel Discord input arrays are equal lengths
if (servers.Length != channels.Length)
{
    WriteLine("Discord Servers Array and Channels Array must have the same Length!");
    Environment.Exit(2);
    return;
}
Dictionary<ulong, ulong> discordServerIdsToChannelIds =
    Enumerable.Range(0, servers.Length)
    .ToDictionary(i => servers[i],i => channels[i]);

// Find Local Files
DirectoryInfo workspace = new(inputs.Workspace);
DirectoryInfo output = workspace.CreateSubdirectory(inputs.Output);

// Discord bot tasks
DiscordSocketClient _discordBot = new();
if (useDiscord)
{
    WriteLine("Logging in to Discord Bot...");
    await _discordBot.LoginAsync(TokenType.Bot, inputs.Bot);
    await _discordBot.StartAsync();

    while
    (
        _discordBot.LoginState != LoginState.LoggedIn ||
        _discordBot.ConnectionState != ConnectionState.Connected
    )
    { await WaitSeconds(1); }
    WriteLine("Logged in to Discord Bot!");
}
else
{
    WriteLine("Skipping Discord Integration...");
}

// Map Discord Servers to Server Names, VRC User Roles, and All Roles
Dictionary<ulong, string> discordGuildIdsToDiscordServerNames = new();
Dictionary<ulong, Dictionary<string, SocketRole[]>> discordGuildIdsToVrcUserIdsToDiscordRoles = new();
Dictionary<ulong, SocketRole[]> discordGuildIdsToAllDiscordRoles = new();
foreach (KeyValuePair<ulong, ulong> kvp in discordServerIdsToChannelIds )
{
    ulong discordGuildId = kvp.Key;
    ulong discordChannelId = kvp.Value;

    WriteLine($"Getting Discord Users from server {discordGuildId}...");
    SocketGuild socketGuild = _discordBot.GetGuild(discordGuildId);
    await WaitSeconds(5);
    SocketTextChannel socketChannel = socketGuild.GetTextChannel(discordChannelId);
    await WaitSeconds(5);
    IGuildUser[] serverUsers = (await socketGuild.GetUsersAsync().FlattenAsync()).ToArray();
    await WaitSeconds(5);
    WriteLine($"Got Discord Users: {serverUsers.Length}");

    WriteLine($"Getting VRC-Discord connections from server {discordGuildId} channel {discordChannelId}...");

    // Get all messages from channel, try up to DISCORD_ATTEMPTS times if fails
    IEnumerable<IMessage> messages = null;
    for (int attempt = 0; messages is null && attempt < DISCORD_MAX_ATTEMPTS; attempt++)
    {
        messages = await socketChannel
            .GetMessagesAsync(DISCORD_MAX_MESSAGES)
            .FlattenAsync();

        if (messages is null)
        {
            WriteLine($"Getting messages failed, retrying ({attempt}/{DISCORD_MAX_ATTEMPTS})...");
            await WaitSeconds(30);
        }
    }

    // Build a mapping of VRC IDs to Discord Roles that prevents duplicates in both directions
    Dictionary<string, SocketRole[]> vrcUserIdsToDiscordRoles = messages
        // Validate VRC ID format
        .Where(m => TryGetVrcId(m, out _))
        // Prioritize the newest messages
        .OrderByDescending(m => m.Timestamp)
        // Consolidate messages by Author
        .GroupBy
        (
            m => m.Author.Id,
            m =>
            {
                TryGetVrcId(m, out string uid);
                return (VrcId: uid, DiscordUser: m.Author, Offset: m.Timestamp);
            }
        )
        // In the case of more than one user claiming the same VRC ID, use the oldest message
        .Select(g => g.OrderBy(m => m.Offset).First())
        // Get all roles for each user
        .ToDictionary
        (
            u => u.VrcId,
            u => serverUsers
                .First(su => su.Id == u.DiscordUser.Id)
                .RoleIds
                .Select(r => socketGuild.GetRole(r))
                .ToArray()
        );

    WriteLine($"Got VRC-Discord connections: {vrcUserIdsToDiscordRoles.Count}");

    // Store all gathered information about the Discord Server
    discordGuildIdsToDiscordServerNames.Add(discordGuildId, socketGuild.Name);
    discordGuildIdsToVrcUserIdsToDiscordRoles.Add(discordGuildId, vrcUserIdsToDiscordRoles);
    discordGuildIdsToAllDiscordRoles.Add
    (
        discordGuildId,
        vrcUserIdsToDiscordRoles
            .SelectMany(kvp => kvp.Value)
            .DistinctBy(r => r.Id)
            .ToArray()
    );
}

// Store data as it is collected from the API
// Group Data
Dictionary<string, Group> vrcGroupIdsToGroupModels = new();
Dictionary<string, GroupRole[]> vrcGroupIdsToAllVrcRoles = new();
Dictionary<string, Dictionary<string, string[]>> vrcGroupIdsToVrcDisplayNamesToVrcRoleIds = new();
// World Data
Dictionary<string, World> vrcWorldIdsToWorldModels = new();
// Discord Data
Dictionary<ulong, Dictionary<string, SocketRole[]>> discordGuildIdsToVrcDisplayNamesToDiscordRoles = new();
// Handle API exceptions
try
{
    // Authentication credentials
    Configuration config = new()
    {
        Username = inputs.Username,
        Password = inputs.Password,
        UserAgent = "kemocade/0.0.1 admin%40kemocade.com"
    };

    // Create instances of APIs we'll need
    AuthenticationApi authApi = new(config);
    UsersApi usersApi = new(config);
    GroupsApi groupsApi = new(config);
    WorldsApi worldsApi = new(config);

    // Log in
    WriteLine("Logging in...");
    CurrentUser currentUser = authApi.GetCurrentUser();

    // Check if 2FA is needed
    if (currentUser == null)
    {
        WriteLine("2FA needed...");

        // Generate a 2FA code with the stored secret
        string key = inputs.Key.Replace(" ", string.Empty);
        Totp totp = new(Base32Encoding.ToBytes(key));

        // Make sure there's enough time left on the token
        int remainingSeconds = totp.RemainingSeconds();
        if (remainingSeconds < 5)
        {
            WriteLine("Waiting for new token...");
            await Task.Delay(TimeSpan.FromSeconds(remainingSeconds + 1));
        }

        // Verify 2FA
        WriteLine("Using 2FA code...");
        authApi.Verify2FA(new(totp.ComputeTotp()));
        currentUser = authApi.GetCurrentUser();
        if (currentUser == null)
        {
            WriteLine("Failed to validate 2FA!");
            Environment.Exit(2);
            return;
        }
    }
    WriteLine($"Logged in as {currentUser.DisplayName}");

    // Get all users and roles from all tracked groups
    foreach (string groupId in groupIds)
    {
        // Get group
        Group group = groupsApi.GetGroup(groupId);
        int memberCount = group.MemberCount;
        WriteLine($"Got Group {group.Name}, Members: {memberCount}");

        // Ensure the Local User is in the VRC Group
        GroupMyMember self = group.MyMember;
        if (self == null || self.UserId != currentUser.Id)
        {
            WriteLine("Local User must be a member of the VRC Group!");
            Environment.Exit(2);
            return;
        }

        // Get group members
        WriteLine("Getting Group Members...");
        List<GroupMember> groupMembers = new();

        // Get non-self group members and add to group members list
        while (groupMembers.Count < memberCount - 1)
        {
            groupMembers.AddRange
                (groupsApi.GetGroupMembers(groupId, 100, groupMembers.Count, 0));
            WriteLine(groupMembers.Count);
            await WaitSeconds(1);
        }

        // Map Group Members to Roles
        Dictionary<string, string[]> groupDisplayNamesToVrcRoleIds =
            groupMembers.ToDictionary
            (
                m => m.User.DisplayName,
                m => m.RoleIds.ToArray()
            );

        // Add Self
        groupDisplayNamesToVrcRoleIds
            .Add(currentUser.DisplayName, self.RoleIds.ToArray());
        WriteLine($"Got Group Users: {groupDisplayNamesToVrcRoleIds.Keys.Count}");

        // Get All Group Roles
        WriteLine("Getting Group Roles...");
        List<GroupRole> groupRoles = groupsApi.GetGroupRoles(groupId);
        WriteLine($"Got Group Roles: {groupRoles.Count}");

        // Store all gathered information about the VRC Group
        vrcGroupIdsToGroupModels.Add(groupId, group);
        vrcGroupIdsToVrcDisplayNamesToVrcRoleIds
            .Add(groupId, groupDisplayNamesToVrcRoleIds);
        vrcGroupIdsToAllVrcRoles.Add(group.Id, groupRoles.ToArray());
    }

    // Get all info from all tracked worlds
    foreach (string worldId in worldIds)
    {
        // Get World
        World world = worldsApi.GetWorld(worldId);
        WriteLine($"Got World: {worldId}");
        vrcWorldIdsToWorldModels.Add(worldId, world);
        await WaitSeconds(1);
    }

    // Pull Discord Users from the VRC API
    WriteLine("Getting Discord Users...");
    discordGuildIdsToVrcDisplayNamesToDiscordRoles =
        discordGuildIdsToDiscordServerNames.Keys
        .ToDictionary(d => d, d => new Dictionary<string, SocketRole[]>());

    // Iterate over each Discord Guild
    foreach (ulong discordGuildId in discordGuildIdsToVrcDisplayNamesToDiscordRoles.Keys)
    {
        // Find the current Discord Guild's VRC User ID to Discord Role mapping
        Dictionary<string, SocketRole[]> vrcUserIdsToDiscordRoles =
            discordGuildIdsToVrcUserIdsToDiscordRoles[discordGuildId];

        // Iterate over each VRC User ID in the Discord Guild
        foreach (string vrcUserId in vrcUserIdsToDiscordRoles.Keys)
        {
            // Get the current VRC User's information from the VRC API
            User user = usersApi.GetUser(vrcUserId);
            await WaitSeconds(1);

            // Get the current VRC User's roles in the current Discord Guild
            SocketRole[] discordRoles = vrcUserIdsToDiscordRoles[vrcUserId];

            // Map the VRC User to their Discord Guild roles
            discordGuildIdsToVrcDisplayNamesToDiscordRoles[discordGuildId]
                .Add(user.DisplayName, discordRoles);
        }
    }
}
catch (ApiException e)
{
    WriteLine("Exception when calling API: {0}", e.Message);
    WriteLine("Status Code: {0}", e.ErrorCode);
    WriteLine(e.ToString());
    Environment.Exit(2);
    return;
}

// Combine all unique VRC Display Names across Groups and Discords
string[] vrcUserDisplayNames = vrcGroupIdsToVrcDisplayNamesToVrcRoleIds
    .SelectMany(g => g.Value.Keys)
    .Concat
    (
        discordGuildIdsToVrcDisplayNamesToDiscordRoles
        .SelectMany(d => d.Value.Keys)
    )
    .Distinct()
    .OrderBy(n => n)
    .ToArray();

int GetVrcUserIndex(string displayName) =>
    Array.IndexOf(vrcUserDisplayNames, displayName);

TrackedData data = new()
{
    FileTimeUtc = DateTime.Now.ToFileTimeUtc(),
    VrcUserDisplayNames = vrcUserDisplayNames,
    VrcGroupsById = vrcGroupIdsToVrcDisplayNamesToVrcRoleIds
        .ToDictionary
        (
            kvp => kvp.Key,
            kvp => new TrackedVrcGroup
            {
                Name = vrcGroupIdsToGroupModels[kvp.Key].Name,
                VrcUsers = kvp.Value.Keys
                    .Select(n => GetVrcUserIndex(n))
                    .Where(i => i != -1)
                    .ToArray(),
                Roles = vrcGroupIdsToAllVrcRoles[kvp.Key]
                    .ToDictionary
                    (
                        r => r.Id,
                        r => new TrackedVrcGroupRole
                        {
                            Name = r.Name,
                            IsAdmin = r.Permissions.Contains(VRC_GROUP_OWNER),
                            IsModerator = r.Permissions.Contains(VRC_GROUP_OWNER) ||
                                r.Permissions.Contains(VRC_GROUP_MODERATOR),
                            VrcUsers = kvp.Value
                                .Where(kvp2 => kvp2.Value.Contains(r.Id))
                                .Select(kvp2 => GetVrcUserIndex(kvp2.Key))
                                .ToArray()
                        }
                    )
            }
        ),
    VrcWorldsById = vrcWorldIdsToWorldModels.
        ToDictionary
        (
            kvp => kvp.Key,
            kvp => new TrackedVrcWorld
            {
                Name = kvp.Value.Name,
                Visits = kvp.Value.Visits,
                Favorites = kvp.Value.Favorites,
                Occupants = kvp.Value.Occupants
            }
        ),
    DiscordServersById = discordGuildIdsToVrcDisplayNamesToDiscordRoles.ToDictionary
    (
        d => d.Key.ToString(),
        d => new TrackedDiscordServer
        {
            Name = discordGuildIdsToDiscordServerNames[d.Key],
            VrcUsers = discordGuildIdsToVrcDisplayNamesToDiscordRoles[d.Key]
                .Select(m => GetVrcUserIndex(m.Key))
                .Where(i => i != -1)
                .ToArray(),
            Roles = discordGuildIdsToAllDiscordRoles[d.Key]
                .ToDictionary
                (
                    r => r.Id.ToString(),
                    r => new TrackedDiscordServerRole
                    {
                        Name = r.Name,
                        IsAdmin = r.Permissions.Administrator,
                        IsModerator = r.Permissions.Administrator ||
                            r.Permissions.ModerateMembers,
                        VrcUsers = discordGuildIdsToVrcDisplayNamesToDiscordRoles[d.Key]
                            .Where(kvp => kvp.Value.Any(sr => sr.Id == r.Id))
                            .Select(u => GetVrcUserIndex(u.Key))
                            .ToArray()
                    }
                )
        }
    )
};

// Build Json from data
JsonSerializerOptions options = new()
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault
};
string dataJsonString = Serialize(data, options);
WriteLine(dataJsonString);

// Write Json to file
FileInfo dataJsonFile = new(Path.Join(output.FullName, "data.json"));
WriteAllText(dataJsonFile.FullName, dataJsonString);

WriteLine("Done!");
Environment.Exit(0);

static bool TryGetVrcId(IMessage message, out string vrcId)
{
    // Ensure the content contains a VRC User ID Prefix
    vrcId = string.Empty;
    string content = message.Content;
    if (!content.Contains(USR_PREFIX)) { return false; }

    // Ensure there are enough characters following the string to extract a full User ID
    int lastIndex = content.LastIndexOf(USR_PREFIX);
    if (content.Length - lastIndex < USR_LENGTH) { return false; }

    // Ensure the userId contains a valid GUID
    string candidate = content.Substring(lastIndex, USR_LENGTH);
    if (!Guid.TryParse(candidate.AsSpan(USR_PREFIX.Length), out _)) { return false; }

    vrcId = candidate.ToLowerInvariant();
    return true;
}

static async Task WaitSeconds(int seconds) =>
    await Task.Delay(TimeSpan.FromSeconds(seconds));