using CommandLine;
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
bool useDiscord = !string.IsNullOrEmpty(inputs.Bot) &&
    !string.IsNullOrEmpty(inputs.Discords) &&
    !string.IsNullOrEmpty(inputs.Channels);

// Parse delimeted inputs
string[] groupIds = useGroups ?
     inputs.Groups.Split(',') : Array.Empty<string>();
ulong[] servers = useDiscord ?
     inputs.Discords.Split(',').Select(ulong.Parse).ToArray() : Array.Empty<ulong>();
ulong[] channels = useDiscord ?
    inputs.Channels.Split(',').Select(ulong.Parse).ToArray() : Array.Empty<ulong>();

// Ensure parallel arrays are equal
if (servers.Length != channels.Length)
{
    WriteLine("Discord Servers Array and Channels Array must have the same Length!");
    Environment.Exit(2);
    return;
}

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

// Map Discord Servers to VRC IDs to Discord Roles
Dictionary<ulong, (string Name, Dictionary<string, SocketRole[]> RoleMap)> discordServersRaw = new();
for (int i = 0; i < servers.Length; i++)
{
    ulong server = servers[i];
    ulong channel = channels[i];

    WriteLine($"Getting Discord Users from server {server}...");
    SocketGuild socketServer = _discordBot.GetGuild(server);
    await WaitSeconds(5);
    SocketTextChannel socketChannel = socketServer.GetTextChannel(channel);
    await WaitSeconds(5);
    IGuildUser[] serverUsers = (await socketServer.GetUsersAsync().FlattenAsync()).ToArray();
    await WaitSeconds(5);
    WriteLine($"Got Discord Users: {serverUsers.Length}");

    WriteLine($"Getting VRC-Discord connections from server {server} channel {channel}...");

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
    Dictionary<string, SocketRole[]> vrcIdsToDiscordRoles = messages
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
                .Select(r => socketServer.GetRole(r))
                .ToArray()
        );

    WriteLine($"Got VRC-Discord connections: {vrcIdsToDiscordRoles.Count}");
    discordServersRaw.Add(server, (socketServer.Name, vrcIdsToDiscordRoles));
}

// Store data as it is collected from the API
Dictionary<ulong, (string Name, SocketRole[] AllRoles, Dictionary<User, SocketRole[]> RoleMap)> discords = new();
Dictionary<Group, (GroupMember[] Members, GroupRole[] Roles)> groups = new();
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

        // Get self and ensure self is in group
        GroupMyMember self = group.MyMember;
        if (self == null)
        {
            WriteLine("User must be a member of the group!");
            Environment.Exit(2);
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

        // Get self group member and add to group members list
        WriteLine("Getting Self...");
        groupMembers.Add
        (
            new
            (
                self.Id,
                self.GroupId,
                self.UserId,
                self.IsRepresenting,
                new(currentUser.Id, currentUser.DisplayName),
                self.RoleIds,
                self.JoinedAt,
                self.MembershipStatus,
                self.Visibility,
                self.IsSubscribedToAnnouncements
            )
        );
        WriteLine($"Got Group Members: {groupMembers.Count}");

        // Get group roles
        WriteLine("Getting Group Roles...");
        List<GroupRole> groupRoles = groupsApi.GetGroupRoles(groupId);
        WriteLine($"Got Group Roles: {groupRoles.Count}");

        groups.Add(group, (groupMembers.ToArray(), groupRoles.ToArray()));
    }

    // Pull Discord Users from the VRC API
    WriteLine("Getting Discord Users...");
    discords = discordServersRaw
        .ToDictionary
        (
            d => d.Key,
            d =>
            (
                d.Value.Name,
                d.Value.RoleMap.Values
                    .SelectMany(r => r)
                    .DistinctBy(r => r.Id)
                    .ToArray(),
                new Dictionary<User, SocketRole[]>()
            )
        );

    foreach (var outer in discordServersRaw)
    {
        foreach (var inner in outer.Value.RoleMap)
        {
            User user = usersApi.GetUser(inner.Key);
            await WaitSeconds(1);
            discords[outer.Key].RoleMap.Add(user, inner.Value);
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

string[] vrcUserDisplayNames = groups
    .SelectMany(g => g.Value.Members)
    .Select(u => u.User.DisplayName)
    .Concat
    (
        discords.Values
        .Select(s => s.RoleMap)
        .SelectMany(s => s.Keys)
        .Select(u => u.DisplayName)
    )
    .Distinct()
    .OrderBy(s => s)
    .ToArray();

TrackedData data = new()
{
    VrcUserDisplayNames = vrcUserDisplayNames,
    VrcGroupsById = groups
        .ToDictionary
        (
            g => g.Key.Id,
            g => new TrackedData.TrackedVrcGroup
            {
                Name = g.Key.Name,
                VrcUsers = g.Value.Members
                    .Select(m => Array.IndexOf(vrcUserDisplayNames, m.User.DisplayName))
                    .Where(i => i != -1)
                    .ToArray(),
                Roles = g.Value.Roles
                    .ToDictionary
                    (
                        r => r.Id,
                        r => new TrackedData.TrackedVrcGroup.TrackedVrcGroupRole
                        {
                            Name = r.Name,
                            IsAdmin = r.Permissions.Contains(VRC_GROUP_OWNER),
                            IsModerator = r.Permissions.Contains(VRC_GROUP_OWNER) ||
                                r.Permissions.Contains(VRC_GROUP_MODERATOR),
                            VrcUsers = g.Value.Members
                                .Where(m => m.RoleIds.Contains(r.Id))
                                .Select(m => Array.IndexOf(vrcUserDisplayNames, m.User.DisplayName))
                                .ToArray()
                        }
                    )
            }
        ),
    DiscordServersById = discords.ToDictionary
    (
        d => d.Key.ToString(),
        d => new TrackedData.TrackedDiscordServer
        {
            Name = d.Value.Name,
            VrcUsers = d.Value.RoleMap
                .Select(m => Array.IndexOf(vrcUserDisplayNames, m.Key.DisplayName))
                .Where(i => i != -1)
                .ToArray(),
            Roles = d.Value.AllRoles
                .ToDictionary
                (
                    r => r.Id.ToString(),
                    r => new TrackedData.TrackedDiscordServer.TrackedDiscordServerRole
                    {
                        Name = r.Name,
                        IsAdmin = r.Permissions.Administrator,
                        IsModerator = r.Permissions.Administrator ||
                            r.Permissions.ModerateMembers,
                        VrcUsers = d.Value.RoleMap.Keys
                            .Where(u => d.Value.RoleMap[u].Any(rr => rr.Id == r.Id))
                            .Select(u => Array.IndexOf(vrcUserDisplayNames, u.DisplayName))
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

static async Task WaitSeconds(int seconds) =>
    await Task.Delay(TimeSpan.FromSeconds(seconds));

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