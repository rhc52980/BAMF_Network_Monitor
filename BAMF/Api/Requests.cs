using LanWatch.Services;

// The bodies the endpoints take, and the floor plan's file format.
record KnownRequest(bool Known);
record NameRequest(string? Name);
record NoteRequest(string? Note);
record LinkRequest(string? Link);
record PlugRequest(long SwitchId, int Port);
record GatewayRequest(string? Ip, bool Enabled);
record CombineRequest(long ParentId);
record KindsRequest(List<string>? Kinds);
record DestinationsRequest(List<ScannerService.DestinationInput>? Destinations);
record SnmpRequest(bool Enabled, string? Address, string? Community);
record TagsRequest(List<string>? Tags);
record TrustRequest(string? Kind, string? Ip, bool Trusted);
record ReportRequest(string? Schedule, int? Hour, int? Day);
record NudgeRequest(bool Off);
record NewDaysRequest(int Days);
record BackupRequest(bool? Enabled, int? Hour, int? Keep, string? CopyTo);
record SavedRestoreRequest(string? Name);
record UnusualSettingRequest(bool Enabled);
record QuietRequest(string? From, string? To, bool Digest);
record PortEntry(long HostId, int Port);
record BlinkRequest(int? Seconds);
record DeviceTypeRequest(string? Type);
record TypeIconsRequest(Dictionary<string, string?>? Icons);
record MapPositionsRequest(string? Subnet, Dictionary<string, double[]?>? Positions);
record PortLabel(int Port, string? Label);
record SwitchPortsRequest(List<PortEntry>? Ports, List<PortLabel>? Labels);
record WebhookRequest(string? Url, string? Format);
record IgnoreRequest(bool Ignored);
record SnoozeRequest(int Minutes);
record DiskAlertRequest(bool Enabled, int? Percent);
record ToolRequest(string? Tool);
record ViewRequest(string? Name, string? Tab, string? Network, string? Status, string? Guess, string? Tag, string? Query);
record ViewDeleteRequest(string? Name);
record BulkRequest(List<long>? Ids, string? Action, string? Tag, int? Minutes);
record TidyRequest(bool Enabled, int? Days);
record HeartbeatRequest(bool Enabled, string? Url, int? Minutes);
record PauseRequest(int Minutes);
record WatchRequest(bool Watched);
record SignInRequest(string? Password);
record NetworksRequest(string[]? Networks);
record MqttRequest(string? Server, int? Port, string? Username, string? Password, bool? Tls, bool? Discovery, string? TopicPrefix);
record RemoteEntry(string? Name, string? Url, string? Password);
record RemotesRequest(RemoteEntry[]? Remotes);
record HookTokenRequest(string? Action);
record SetupRequest(string[]? Networks, string? Password, bool? Open, bool? Skip);
record PasswordRequest(string? Role, string? Current, string? Password);
record HttpsRequest(string? Action);
record ForgetRequest(bool Forgotten);
record ActiveArpRequest(bool Enabled);
record RouterNamesApply(bool Overwrite);
record FloorPlaceRequest(long HostId, double X, double Y);
record WanTargetRequest(string? Target);
record WanIntervalRequest(int Seconds);
record WanSlowRequest(string? Mode, int? Ms);
record SpeedTestRequest(string? Schedule);
// A plan as it travels: walls, doors and windows as two points each, labels as
// a point and a word, plus the grid that gives them their real-world size.
record PlanItem(string? K, double[]? A, double[]? B, double[]? P, string? T);
record PlanDoc(int V, int Width, int Height, string? Unit, double Step, double PerStep, List<PlanItem>? Items);
record PlanFile(string Json, int Width, int Height);
record NightRequest(bool Enabled, string? From, string? To, string? Theme);
record ScanSettingsRequest(
    int? ScanIntervalSeconds,
    int? PingConcurrency,
    int? HistoryRetentionDays,
    int? OfflineAfterMissedScans,
    bool? MdnsListen,
    Dictionary<string, int>? SubnetIntervalSeconds,
    List<string>? DisabledSubnets);
