using System.Collections.Frozen;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using TEKSteamClient.CM.Messages;
using TEKSteamClient.CM.Messages.Bodies;

namespace TEKSteamClient.CM;

/// <summary>Steam CM client.</summary>
public partial class CMClient
{
	static CMClient()
	{
		var version = Environment.OSVersion;
		var osVersion = version.Version;
		s_osType = version.Platform switch
		{
			PlatformID.Win32NT => osVersion.Major switch
			{
				10 when osVersion.Build >= 22000 => 20,
				10 => 16,
				_ => 0,
			},
			PlatformID.Unix when RuntimeInformation.IsOSPlatform(OSPlatform.Linux) => osVersion.Major switch
			{
				2 => osVersion.Minor switch
				{
					2 => -202,
					4 => -201,
					6 => -200,
					_ => -203,
				},
				3 => osVersion.Minor switch
				{
					2 => -199,
					5 => -198,
					6 => -197,
					10 => -196,
					16 => -195,
					18 => -194,
					_ => -193,
				},
				4 => osVersion.Minor switch
				{
					1 => -191,
					4 => -190,
					9 => -189,
					14 => -188,
					19 => -187,
					_ => -192,
				},
				5 => osVersion.Minor switch
				{
					4 => -185,
					10 => -182,
					_ => -186,
				},
				6 => -184,
				7 => -183,
				_ => -203,
			},
			PlatformID.Unix when RuntimeInformation.IsOSPlatform(OSPlatform.OSX) => osVersion.Major switch
			{
				19 => -82,
				20 => -80,
				21 => -77,
				_ => -102
			},
			_ => -1,
		};
	}
	/// <summary>Creates a new CM client.</summary>
	public CMClient() => _connection = new(() => Disconnected?.Invoke());
	/// <summary>Connection that is used to communicate with Steam CM server.</summary>
	private readonly WebSocketConnection _connection;
	/// <summary>OS type value included into logon messages.</summary>
	private static readonly int s_osType;
	/// <summary>HTTP client that downloads PICS app info.</summary>
	private static readonly HttpClient s_clientConfigClient = new()
	{
		BaseAddress = new("http://clientconfig.akamai.steamstatic.com/appinfo/"),
		DefaultRequestVersion = HttpVersion.Version20,
		Timeout = TimeSpan.FromSeconds(10)
	};
	/// <summary>When <see langword="true"/>, ensures that the client is connected and logged on before every request and logs on with anonymous user credentials if it's not.</summary>
	public bool EnsureLogOn { get; set; }
	/// <summary>Steam cell ID used in certain requests.</summary>
	public static uint CellId { get; internal set; }
	/// <summary>Collection of user-defined functions for getting manifest request codes for specific depots.</summary>
	public static FrozenDictionary<uint, Func<uint, uint, ulong, ulong>> ManifestRequestCodeSourceOverrides { get; set; } = FrozenDictionary<uint, Func<uint, uint, ulong, ulong>>.Empty;
	/// <summary>When <see cref="EnsureLogOn"/> is <see langword="true"/>, ensures that the client is connected and logged on with anonymous user credentials.</summary>
	/// <exception cref="SteamException">Client is not logged onto Steam network.</exception>
	private void EnsureLogOnIfNeeded()
	{
		if (EnsureLogOn && !_connection.IsLoggedOn)
			LogOnAnonymous();
		if (!_connection.IsLoggedOn)
			throw new SteamException(SteamException.ErrorType.CMNotLoggedOn);
	}
	/// <summary>Gets a value that indicates whether patch from one manifest to another in specified depot is available.</summary>
	/// <param name="appId">ID of the app that the depot belongs to.</param>
	/// <param name="depotId">ID of the depot that the manifests belong to.</param>
	/// <param name="sourceManifestId">ID of the source manifest for patching.</param>
	/// <param name="targetManifestId">ID of the target manifest.</param>
	/// <returns><see langword="true"/> if a patch from source manifest to target manifest is available.</returns>
	/// <exception cref="SteamException">Failed to get response message.</exception>
	internal bool GetPatchAvailability(uint appId, uint depotId, ulong sourceManifestId, ulong targetManifestId)
	{
		EnsureLogOnIfNeeded();
		ulong jobId = GlobalId.NextJobId;
		var message = new Message<DepotPatchInfo>(MessageType.ServiceMethod)
		{
			Header = new()
			{
				SourceJobId = jobId,
				TargetJobName = "ContentServerDirectory.GetDepotPatchInfo#1"
			}
		};
		message.Body.AppId = appId;
		message.Body.DepotId = depotId;
		message.Body.SourceManifestId = sourceManifestId;
		message.Body.TargetManifestId = targetManifestId;
		var response = _connection.TransceiveMessage<DepotPatchInfo, DepotPatchInfoResponse>(message, MessageType.ServiceMethodResponse, jobId)
			?? throw new SteamException(SteamException.ErrorType.CMFailedToGetPatchAvailablity);
		return response.Body.IsAvailable;
	}
	/// <summary>Gets Steam CDN server list.</summary>
	/// <returns>An array of Steam CDN server entries.</returns>
	/// <exception cref="SteamException">Failed to get response message.</exception>
	internal CDNServersResponse.Types.Server[] GetCDNServers()
	{
		EnsureLogOnIfNeeded();
		ulong jobId = GlobalId.NextJobId;
		var message = new Message<CDNServers>(MessageType.ServiceMethod)
		{
			Header = new()
			{
				SourceJobId = jobId,
				TargetJobName = "ContentServerDirectory.GetServersForSteamPipe#1"
			}
		};
		message.Body.CellId = CellId;
		var response = _connection.TransceiveMessage<CDNServers, CDNServersResponse>(message, MessageType.ServiceMethodResponse, jobId)
			?? throw new SteamException(SteamException.ErrorType.CMFailedToGetCDNServerList);
		var result = new CDNServersResponse.Types.Server[response.Body.Servers.Count];
		response.Body.Servers.CopyTo(result, 0);
		return result;
	}
	/// <summary>Disconnects client from the CM server.</summary>
	public void Disconnect() => _connection.Disconnect();
	/// <summary>Gets AES decryption key for specified depot.</summary>
	/// <param name="appId">ID of the app that depot belongs to.</param>
	/// <param name="depotId">ID of the depot to get decryption key for.</param>
	/// <param name="buffer">Span that will receive decryption key, must be at least 32 bytes long.</param>
	/// <exception cref="ArgumentException">Length of <paramref name="buffer"/> is less than 32.</exception>
	/// <exception cref="SteamException">Failed to get response message or result code does not indicate success.</exception>
	public void GetDepotDecryptionKey(uint appId, uint depotId, Span<byte> buffer)
	{
		if (buffer.Length < 32)
			throw new ArgumentException($"{nameof(buffer)} must be at least 32 bytes long");
		EnsureLogOnIfNeeded();
		var message = new Message<DepotDecryptionKey>(MessageType.DepotDecryptionKey);
		message.Body.DepotId = depotId;
		message.Body.AppId = appId;
		var response = _connection.TransceiveMessage<DepotDecryptionKey, DepotDecryptionKeyResponse>(message, MessageType.DepotDecryptionKeyResponse)
			?? throw new SteamException(SteamException.ErrorType.CMFailedToGetDepotDecryptionKey);
		if (response.Body.Result is not 1)
			throw new SteamException(SteamException.ErrorType.CMFailedToGetDepotDecryptionKey, response.Body.Result);
		response.Body.Key.Span.CopyTo(buffer);
	}
	/// <summary>Logs onto Steam network with given user's credentials.</summary>
	/// <param name="accountName">Name of the account.</param>
	/// <param name="password">Account password.</param>
	/// <param name="authCode">Authentication code (sent to email).</param>
	/// <param name="steamGuardCode">Steam Guard code.</param>
	/// <exception cref="SteamException">Failed to get response message or to log on.</exception>
	public void LogOn(string accountName, string password, [Optional]string? authCode, [Optional]string? steamGuardCode)
	{
		_connection.Connect();
		var message = new Message<LogOn>(MessageType.LogOn)
		{
			Header = new()
			{
				SessionId = 0,
				SteamId = 0x110000100000000
			}
		};
		message.Body.ProtocolVersion = 65580;
		message.Body.CellId = CellId;
		message.Body.ClientLanguage = "english";
		message.Body.ClientOsType = s_osType;
		message.Body.AccountName = accountName;
		message.Body.Password = password;
		if (!string.IsNullOrEmpty(authCode))
			message.Body.AuthCode = authCode;
		if (!string.IsNullOrEmpty(steamGuardCode))
			message.Body.SteamGuardCode = steamGuardCode;
		int result = 0;
		var response = _connection.TransceiveMessage<LogOn, LogOnResponse>(message, MessageType.LogOnResponse);
		if (response is not null)
		{
			result = response.Body.Result;
			if (result is 1)
			{
				CellId = response.Body.CellId;
				return;
			}
		}
		Disconnect();
		throw new SteamException(SteamException.ErrorType.CMLogOnFailed, result);
	}
	/// <summary>Logs onto Steam network with anonymous user's credentials.</summary>
	/// <exception cref="SteamException">Failed to get response message or to log on.</exception>
	public void LogOnAnonymous()
	{
		_connection.Connect();
		var message = new Message<LogOn>(MessageType.LogOn)
		{
			Header = new()
			{
				SessionId = 0,
				SteamId = 0x110000100000000
			}
		};
		message.Body.ProtocolVersion = 65580;
		message.Body.CellId = CellId;
		message.Body.ClientLanguage = "english";
		message.Body.ClientOsType = s_osType;
		message.Header.SessionId = 0;
		message.Header.SteamId = 0x1A0000000000000;
		int result = 0;
		var response = _connection.TransceiveMessage<LogOn, LogOnResponse>(message, MessageType.LogOnResponse);
		if (response is not null)
		{
			result = response.Body.Result;
			if (result is 1)
			{
				CellId = response.Body.CellId;
				return;
			}
		}
		Disconnect();
		throw new SteamException(SteamException.ErrorType.CMLogOnFailed, result);
	}
	/// <summary>Gets request code for specified manifest.</summary>
	/// <param name="appId">ID of the app that the depot belongs to.</param>
	/// <param name="depotId">ID of the depot that the manifest belongs to.</param>
	/// <param name="manifestId">ID of the manifest to get request code for.</param>
	/// <returns>The manifest request code.</returns>
	/// <exception cref="SteamException">Failed to get response message.</exception>
	public ulong GetManifestRequestCode(uint appId, uint depotId, ulong manifestId)
	{
		if (ManifestRequestCodeSourceOverrides.TryGetValue(depotId, out var function))
			return function(appId, depotId, manifestId);
		EnsureLogOnIfNeeded();
		ulong jobId = GlobalId.NextJobId;
		var message = new Message<ManifestRequestCode>(MessageType.ServiceMethod)
		{
			Header = new()
			{
				SourceJobId = jobId,
				TargetJobName = "ContentServerDirectory.GetManifestRequestCode#1"
			}
		};
		message.Body.AppId = appId;
		message.Body.DepotId = depotId;
		message.Body.ManifestId = manifestId;
		message.Body.AppBranch = "public";
		var response = _connection.TransceiveMessage<ManifestRequestCode, ManifestRequestCodeResponse>(message, MessageType.ServiceMethodResponse, jobId)
			?? throw new SteamException(SteamException.ErrorType.CMFailedToGetManifestRequestCode);
		return response.Body.RequestCode;
	}
	/// <summary>Gets latest manifest ID for specified workshop item.</summary>
	/// <param name="appId">ID of the app providing workshop.</param>
	/// <param name="itemId">ID of the workshop item to get manifest ID for.</param>
	/// <returns>The manifest ID.</returns>
	/// <exception cref="SteamException">Failed to get response message.</exception>
	public ulong GetWorkshopItemManifestId(uint appId, ulong itemId)
	{
		EnsureLogOnIfNeeded();
		ulong jobId = GlobalId.NextJobId;
		var message = new Message<WorkshopItemManifestId>(MessageType.ServiceMethod)
		{
			Header = new()
			{
				SourceJobId = jobId,
				TargetJobName = "PublishedFile.GetItemInfo#1"
			}
		};
		message.Body.AppId = appId;
		message.Body.Items.Add(new WorkshopItemManifestId.Types.Item { Id = itemId });
		var response = _connection.TransceiveMessage<WorkshopItemManifestId, WorkshopItemManifestIdResponse>(message, MessageType.ServiceMethodResponse, jobId);
		if (response is null || response.Body.Items.Count is 0)
			throw new SteamException(SteamException.ErrorType.CMFailedToGetManifestIds);
		return response.Body.Items[0].ManifestId;
	}
	/// <summary>Gets latest manifest IDs for given app's depots.</summary>
	/// <param name="appId">ID of the app to get manifest IDs for.</param>
	/// <returns>A dictionary with depot IDs as keys and latest manifest IDs as values.</returns>
	/// <exception cref="SteamException">Failed to get response message.</exception>
	public FrozenDictionary<uint, ulong> GetDepotManifestIds(uint appId)
	{
		EnsureLogOnIfNeeded();
		ulong jobId = GlobalId.NextJobId;
		var message = new Message<ProductInfo>(MessageType.ProductInfo) { Header = new() { SourceJobId = jobId } };
		message.Body.Apps.Add(new ProductInfo.Types.AppInfo { AppId = appId, AccessToken = 0 });
		message.Body.MetadataOnly = false;
		var response = _connection.TransceiveMessage<ProductInfo, ProductInfoResponse>(message, MessageType.ProductInfoResponse, jobId);
		if (response is null || response.Body.Apps.Count is 0)
			throw new SteamException(SteamException.ErrorType.CMFailedToGetManifestIds);
		if (response.Body.Apps[0].MissingToken)
		{
			jobId = GlobalId.NextJobId;
			var tokenMessage = new Message<PicsAccessToken>(MessageType.PicsAccessToken) { Header = new() { SourceJobId = jobId } };
			tokenMessage.Body.AppIds.Add(appId);
			var tokenResponse = _connection.TransceiveMessage<PicsAccessToken, PicsAccessTokenResponse>(tokenMessage, MessageType.PicsAccessTokenResponse, jobId);
			if (tokenResponse is null || tokenResponse.Body.Apps.Count is 0)
				throw new SteamException(SteamException.ErrorType.CMFailedToGetPicsAccessToken);
			jobId = GlobalId.NextJobId;
			message.Header.SourceJobId = jobId;
			message.Body.Apps[0].AccessToken = tokenResponse.Body.Apps[0].Token;
			response = _connection.TransceiveMessage<ProductInfo, ProductInfoResponse>(message, MessageType.ProductInfoResponse, jobId);
			if (response is null || response.Body.Apps.Count is 0)
				throw new SteamException(SteamException.ErrorType.CMFailedToGetManifestIds);
		}
		var appInfo = response.Body.Apps[0];
		List<VDFEntry>? entries;
		if (MemoryMarshal.TryGetArray(appInfo.Buffer.Memory, out var segment))
			using (var reader = new StreamReader(new MemoryStream(segment.Array!, false)))
				entries = new VDFEntry(reader)["depots"]?.Children;
		else
			try
			{
				var httpRequest = new HttpRequestMessage(HttpMethod.Get, new Uri($"{appId}/sha/{Convert.ToHexString(appInfo.Sha.Span)}.txt.gz")) { Version = HttpVersion.Version20 };
				using var httpResponse = s_clientConfigClient.SendAsync(httpRequest, HttpCompletionOption.ResponseContentRead, CancellationToken.None).Result.EnsureSuccessStatusCode();
				using var content = httpResponse.Content;
				using var reader = new StreamReader(content.ReadAsStream());
				entries = new VDFEntry(reader)["depots"]?.Children;
			}
			catch (HttpRequestException e) { throw new SteamException(SteamException.ErrorType.CMFailedToGetManifestIds, e); }
		if (entries is null)
			return FrozenDictionary<uint, ulong>.Empty;
		entries.RemoveAll(e => !uint.TryParse(e.Key, out _) || e["manifests"]?["public"] is null);
		return FrozenDictionary.ToFrozenDictionary(entries, e => uint.Parse(e.Key), e => ulong.Parse(e["manifests"]!["public"]!["gid"]!.Value!));
	}
	/// <summary>Gets details for specified workshop items.</summary>
	/// <param name="ids">IDs of the workshop items to get details for.</param>
	/// <returns>An array of item details.</returns>
	/// <exception cref="SteamException">Failed to get response message.</exception>
	public WorkshopItemDetails[] GetWorkshopItemDetails(params ulong[] ids)
	{
		EnsureLogOnIfNeeded();
		ulong jobId = GlobalId.NextJobId;
		var message = new Message<Messages.Bodies.WorkshopItemDetails>(MessageType.ServiceMethod)
		{
			Header = new()
			{
				SourceJobId = jobId,
				TargetJobName = "PublishedFile.GetDetails#1"
			}
		};
		message.Body.Ids.AddRange(ids);
		message.Body.IncludeMetadata = true;
		var response = _connection.TransceiveMessage<Messages.Bodies.WorkshopItemDetails, WorkshopItemDetailsResponse>(message, MessageType.ServiceMethodResponse, jobId)
			?? throw new SteamException(SteamException.ErrorType.CMFailedToGetWorkshopItemDetails);
		var result = new WorkshopItemDetails[response.Body.Items.Count];
		for (int i = 0; i < response.Body.Items.Count; i++)
		{
			var item = response.Body.Items[i];
			result[i] = new(item.Result, item.AppId, DateTimeOffset.FromUnixTimeSeconds(item.LastUpdated).LocalDateTime, item.Id, item.ManifestId, item.Name, item.PreviewUrl);
		}
		return result;
	}
	/// <summary>Searches items available in the workshop.</summary>
	/// <param name="appId">ID of the app providing the workshop.</param>
	/// <param name="page">Current page number.</param>
	/// <param name="itemsPerPage">The number of items returned per page; max number of items returned by this method.</param>
	/// <param name="total">When this method returns, contains the total number of available pages.</param>
	/// <param name="search">Search query.</param>
	/// <returns>An array of item details.</returns>
	/// <exception cref="SteamException">Failed to get response message.</exception>
	public WorkshopItemDetails[] SearchWorkshopItems(uint appId, uint page, uint itemsPerPage, out uint total, [Optional]string? search)
	{
		total = 0;
		EnsureLogOnIfNeeded();
		ulong jobId = GlobalId.NextJobId;
		var message = new Message<QueryWorkshopItems>(MessageType.ServiceMethod)
		{
			Header = new()
			{
				SourceJobId = jobId,
				TargetJobName = "PublishedFile.QueryFiles#1"
			}
		};
		message.Body.Page = page;
		message.Body.ItemsPerPage = itemsPerPage;
		message.Body.AppId = appId;
		if (!string.IsNullOrEmpty(search))
			message.Body.SearchText = search;
		message.Body.ReturnMetadata = true;
		var response = _connection.TransceiveMessage<QueryWorkshopItems, QueryWorkshopItemsResponse>(message, MessageType.ServiceMethodResponse, jobId)
			?? throw new SteamException(SteamException.ErrorType.CMFailedToGetWorkshopItemDetails);
		total = response.Body.Total;
		var result = new WorkshopItemDetails[response.Body.Items.Count];
		for (int i = 0; i < response.Body.Items.Count; i++)
		{
			var item = response.Body.Items[i];
			result[i] = new(item.Result, appId, DateTimeOffset.FromUnixTimeSeconds(item.LastUpdated).LocalDateTime, item.Id, item.ManifestId, item.Name, item.PreviewUrl);
		}
		return result;
	}
	/// <summary>Refreshes Steam CM server list via Web API.</summary>
	internal static void RefreshServerList()
	{
		string[]? serverList;
		using (var httpClient = new HttpClient() { DefaultRequestVersion = HttpVersion.Version20 })
			try { serverList = httpClient.GetFromJsonAsync($"https://api.steampowered.com/ISteamDirectory/GetCMList/v1?cellid={CellId}", JsonContext.Default.CMListResponse).Result?.Response?.ServerlistWebsockets; }
			catch { serverList = null; }
		if (serverList is null)
			return;
		var wscServerList = WebSocketConnection.ServerList;
		wscServerList.Clear();
		wscServerList.EnsureCapacity(serverList.Length);
		foreach (string host in serverList)
			wscServerList.Push(new($"wss://{host}/cmsocket/"));
	}
	/// <summary>Called when connection to the CM server is terminated.</summary>
	public event DisconnectedHandler? Disconnected;
	/// <summary>Generates global IDs.</summary>
	private static class GlobalId
	{
		/// <summary>Initializes <see cref="s_mask"/>.</summary>
		static GlobalId()
		{
			using var currentProcess = Process.GetCurrentProcess();
			s_mask = 0x3FF0000000000 | (((((ulong)currentProcess.StartTime.Ticks - 0x8C6BDABF8998000) / 10000000) & 0xFFFFF) << 20);
		}
		/// <summary>Increments with every new generated job ID.</summary>
		private static ulong s_counter;
		/// <summary>Mask for <see cref="s_counter"/> that also includes fields that need to be initialized only once.</summary>
		private static readonly ulong s_mask;
		/// <summary>Generates next unique job ID.</summary>
		public static ulong NextJobId => s_mask | ++s_counter;
	}
}