using System.IO.Compression;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Google.Protobuf;
using TEKSteamClient.CM.Messages;
using TEKSteamClient.CM.Messages.Bodies;

namespace TEKSteamClient.CM;

/// <summary>Connection provider that uses WebSocket protocol to connect to Steam CM servers.</summary>
internal class WebSocketConnection
{
	/// <summary>Creates a new web socket connection.</summary>
	/// <param name="disconnectedHandler">Handler to be called when connection to CM server is terminated.</param>
	public WebSocketConnection(DisconnectedHandler disconnectedHandler)
	{
		_heartbeatMessage.Body.SendReply = true;
		_timer = new(delegate
		{
			if (IsLoggedOn)
				Send(_heartbeatMessage);
		});
		Disconnected += disconnectedHandler;
	}
	/// <summary>Buffer used by <see cref="Send{T}(Message{T})"/> to serialize messages.</summary>
	private byte[] _serializationBuffer = new byte[0x400];
	/// <summary>The interval between sending heartbeat messages, in milliseconds.</summary>
	private int _heartbeatInterval;
	/// <summary>ID of the current session.</summary>
	private int? _sessionId;
	/// <summary>Steam ID of the user currently logged on.</summary>
	private ulong? _steamId;
	/// <summary>Cancellation token source for aborting the connection.</summary>
	private CancellationTokenSource? _cts;
	/// <summary>WebSocket client.</summary>
	private ClientWebSocket? _socket;
	/// <summary>Connection loop thread.</summary>
	private Thread? _thread;
	/// <summary>List of message callbacks that client is waiting on.</summary>
	private readonly List<MessageCallback> _callbacks = [];
	/// <summary>Singleton heartbeat message object.</summary>
	private readonly Message<Heartbeat> _heartbeatMessage = new(MessageType.Heartbeat);
	/// <summary>Timer responsible for sending heartbeat messages.</summary>
	private readonly Timer _timer;
	/// <summary>The list of cached CM server URLs.</summary>
	internal static readonly Stack<Uri> ServerList = new(20);
	/// <summary>Indicates whether client is currently logged onto a Steam CM server.</summary>
	public bool IsLoggedOn { get; private set; }
	/// <summary>Processes all incoming data in a loop.</summary>
	private void ConnectionLoop()
	{
		try
		{
			byte[] buffer = GC.AllocateUninitializedArray<byte>(0x20000); //128 kiB
			_heartbeatInterval = 2500;
			while (!_cts!.IsCancellationRequested)
			{
				var receiveTask = _socket!.ReceiveAsync(buffer, _cts.Token);
				if (!receiveTask.Wait(_heartbeatInterval * 2 + 300, _cts.Token))
				{
					_cts.Cancel();
					if (_socket.State is WebSocketState.Open)
						_socket.CloseAsync(WebSocketCloseStatus.EndpointUnavailable, null, default).Wait(5000);
					break;
				}
				var result = receiveTask.Result;
				if (result.MessageType is WebSocketMessageType.Close)
				{
					if (_socket.State is not WebSocketState.Closed or WebSocketState.Aborted)
						_socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, _cts.Token).Wait(5000);
					break;
				}
				else if (result.MessageType is WebSocketMessageType.Text || result.Count < 8)
					continue;
				ref byte bufferRef = ref MemoryMarshal.GetArrayDataReference(buffer);
				uint rawType = Unsafe.As<byte, uint>(ref bufferRef);
				if ((rawType & 0x80000000) is 0)
					continue;
				var type = (MessageType)(rawType & 0x7FFFFFFF);
				if (type is MessageType.ServerUnavailable)
				{
					lock (_callbacks)
					{
						foreach (var callback in _callbacks)
							callback.CompletionSource.SetResult(null);
						_callbacks.Clear();
					}
					Disconnect();
					break;
				}
				int headerSize = Unsafe.As<byte, int>(ref Unsafe.AddByteOffset(ref bufferRef, 4));
				var header = MessageHeader.Parser.ParseFrom(new ReadOnlySpan<byte>(buffer, 8, headerSize));
				var body = new ReadOnlySpan<byte>(buffer, headerSize + 8, result.Count - headerSize - 8);
				if (type is MessageType.Multi)
				{
					var message = new Message<Multi>(type);
					message.DeserializeBody(body);
					byte[] innerBuffer;
					int messageOffset;
					if (!MemoryMarshal.TryGetArray(message.Body.InnerMessages.Memory, out var segment))
						continue;
					if (message.Body.UncompressedSize > 0)
					{
						innerBuffer = GC.AllocateUninitializedArray<byte>(message.Body.UncompressedSize);
						messageOffset = 0;
						try
						{
							using var gzipStream = new GZipStream(new MemoryStream(segment.Array!, false), CompressionMode.Decompress);
							gzipStream.ReadExactly(innerBuffer);
						}
						catch { continue; }
					}
					else
					{
						innerBuffer = segment.Array!;
						messageOffset = segment.Offset;
					}
					int messageLength;
					ref byte innerBufferRef = ref MemoryMarshal.GetArrayDataReference(innerBuffer);
					for (; messageOffset < innerBuffer.Length - 12; messageOffset += 12 + messageLength)
					{
						messageLength = Unsafe.As<byte, int>(ref Unsafe.AddByteOffset(ref innerBufferRef, messageOffset)) - 8;
						if (messageLength < 0)
							continue;
						rawType = Unsafe.As<byte, uint>(ref Unsafe.AddByteOffset(ref innerBufferRef, messageOffset + 4));
						if ((rawType & 0x80000000) is 0)
							continue;
						type = (MessageType)(rawType & 0x7FFFFFFF);
						if (type is MessageType.ServerUnavailable)
						{
							lock (_callbacks)
							{
								foreach (var callback in _callbacks)
									callback.CompletionSource.SetResult(null);
								_callbacks.Clear();
							}
							Disconnect();
							break;
						}
						headerSize = Unsafe.As<byte, int>(ref Unsafe.AddByteOffset(ref innerBufferRef, messageOffset + 8));
						header = MessageHeader.Parser.ParseFrom(new ReadOnlySpan<byte>(innerBuffer, messageOffset + 12, headerSize));
						body = new(innerBuffer, messageOffset + 12 + headerSize, messageLength - headerSize);
						if (type is MessageType.LogOnResponse)
						{
							var logOnResponseMessage = new Message<LogOnResponse>(MessageType.LogOnResponse) { Header = header };
							logOnResponseMessage.DeserializeBody(body);
							if (logOnResponseMessage.Body.Result is 1)
								IsLoggedOn = true;
							lock (_callbacks)
								_callbacks.Find(c => c.ExpectedResponseType is MessageType.LogOnResponse)?.CompletionSource?.SetResult(logOnResponseMessage);
							if (logOnResponseMessage.Body.Result is 1)
							{
								_heartbeatInterval = logOnResponseMessage.Body.HeartbeatInterval * 1000;
								_sessionId = header.SessionId;
								_steamId = header.SteamId;
								_timer.Change(0, _heartbeatInterval);
								continue;
							}
							else
							{
								Disconnect();
								break;
							}
						}
						lock (_callbacks)
							foreach (var callback in _callbacks)
								if (type == callback.ExpectedResponseType && (callback.ExpectedTargetJobId is 0 || callback.ExpectedTargetJobId == header.TargetJobId))
								{
									callback.CompletionSource.SetResult(callback.DeserializeMessage(body, header));
									if (callback.ExpectedTargetJobId is not 0)
										break;
								}
					}
				}
				else if (type is MessageType.LogOnResponse)
				{
					var message = new Message<LogOnResponse>(MessageType.LogOnResponse) { Header = header };
					message.DeserializeBody(body);
					if (message.Body.Result is 1)
						IsLoggedOn = true;
					lock (_callbacks)
						_callbacks.Find(c => c.ExpectedResponseType is MessageType.LogOnResponse)?.CompletionSource?.SetResult(message);
					if (message.Body.Result is 1)
					{
						_heartbeatInterval = message.Body.HeartbeatInterval * 1000;
						_sessionId = header.SessionId;
						_steamId = header.SteamId;
						_timer.Change(0, _heartbeatInterval);
					}
					else
						Disconnect();
				}
				else
					lock (_callbacks)
						foreach (var callback in _callbacks)
							if (type == callback.ExpectedResponseType && (callback.ExpectedTargetJobId is 0 || callback.ExpectedTargetJobId == header.TargetJobId))
							{
								callback.CompletionSource.SetResult(callback.DeserializeMessage(body, header));
								if (callback.ExpectedTargetJobId is not 0)
									break;
							}
			}
		}
		catch (TaskCanceledException) { }
		catch
		{
			try
			{
				_cts!.Cancel();
				if (_socket!.State is WebSocketState.Open)
					_socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, default).Wait(5000);
			}
			catch { }
		}
		IsLoggedOn = false;
		_socket!.Dispose();
		_cts!.Dispose();
		_timer.Change(Timeout.Infinite, Timeout.Infinite);
		Disconnected();
	}
	/// <summary>Sends a message to CM server.</summary>
	/// <param name="message">Message to send.</param>
	/// <returns><see langword="true"/> if message has been sent successfully within timeout period of 5 seconds; otherwise, <see langword="false"/>.</returns>
	private bool Send<T>(Message<T> message) where T : IMessage<T>, new()
	{
		message.Header ??= new();
		if (_sessionId.HasValue)
			message.Header.SessionId = _sessionId.Value;
		if (_steamId.HasValue)
			message.Header.SteamId = _steamId.Value;
		int messageSize = message.Serialize(ref _serializationBuffer);
		try { return _socket!.SendAsync(new(_serializationBuffer, 0, messageSize), WebSocketMessageType.Binary, true, _cts!.Token).Wait(5000); }
		catch { return false; }
	}
	/// <summary>Initiates connection to a Steam CM server.</summary>
	/// <exception cref="SteamException">An error has occurred when connecting to CM server.</exception>
	public void Connect()
	{
		_sessionId = null;
		_steamId = null;
		Uri endpointUrl = null!;
		Exception? exception = null;
		int errorCode = 0;
		for (int i = 0; i < 5; i++)
		{
			if (i % 2 is 0) //Try connecting to first two servers twice
				lock (ServerList)
				{
					if (ServerList.Count is 0)
					{
						CMClient.RefreshServerList();
						if (ServerList.Count is 0)
							throw new SteamException(SteamException.ErrorType.CMConnectionFailed, 0);
					}
					endpointUrl = ServerList.Pop();
				}
			_cts = new();
			_socket = new();
			bool connected = false;
			try
			{
				connected = _socket.ConnectAsync(endpointUrl, _cts.Token).Wait(5000);
				if (!connected)
				{
					_cts.Cancel();
					exception = null;
					errorCode = 1;
				}
			}
			catch (Exception e) { exception = e; }
			if (!connected || _socket.State is not WebSocketState.Open)
			{
				if (connected)
					errorCode = 2;
				_cts.Dispose();
				_socket.Dispose();
				continue;
			}
			_thread = new Thread(ConnectionLoop);
			_thread.Start();
			return;
		}
		throw exception is null ? new SteamException(SteamException.ErrorType.CMConnectionFailed, errorCode) : new SteamException(SteamException.ErrorType.CMConnectionFailed, exception);
	}
	/// <summary>Initiates disconnection from Steam CM server.</summary>
	public void Disconnect()
	{
		if (IsLoggedOn)
		{
			if (!Send(new Message<Empty>(MessageType.LogOff)))
			{
				_cts!.Dispose();
				if (_socket?.State is WebSocketState.Open)
					_socket.CloseOutputAsync(WebSocketCloseStatus.EndpointUnavailable, null, default);
			}
		}
		else if (_socket?.State is WebSocketState.Open)
			_socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, _cts!.Token);
		if (Thread.CurrentThread != _thread)
			_thread?.Join();
	}
	/// <summary>Sends a message to CM server and returns its response if there is any.</summary>
	/// <typeparam name="T">Type of expected response message body.</typeparam>
	/// <param name="message">Message to send.</param>
	/// <param name="expectedResponseType">Type of expected response message.</param>
	/// <param name="expectedTargetJobId">Target Job ID of expected response message.</param>
	/// <returns>Response message or <see langword="null"/> if none was received during timeout period of 5 seconds.</returns>
	public Message<TResponse>? TransceiveMessage<TRequest, TResponse>(Message<TRequest> message, MessageType expectedResponseType, [Optional]ulong expectedTargetJobId) where TRequest : IMessage<TRequest>, new() where TResponse : IMessage<TResponse>, new()
	{
		var callback = new MessageCallback<TResponse>()
		{
			ExpectedResponseType = expectedResponseType,
			ExpectedTargetJobId = expectedTargetJobId
		};
		lock (_callbacks)
			_callbacks.Add(callback);
		var result = Send(message) && callback.CompletionSource.Task.Wait(5000) ? (Message<TResponse>?)callback.CompletionSource.Task.Result : null;
		lock (_callbacks)
			_callbacks.Remove(callback);
		return result;
	}
	/// <summary>Called when connection to the CM server is terminated.</summary>
	private event DisconnectedHandler Disconnected;
	/// <summary>Item that allows caller to specify which message it is waiting on and get that message when it's received from the server.</summary>
	private abstract class MessageCallback
	{
		/// <summary>Target job ID of the expected message.</summary>
		public ulong ExpectedTargetJobId { get; init; }
		/// <summary>Completion source that will return message object when it's received.</summary>
		public TaskCompletionSource<object?> CompletionSource { get; } = new();
		/// <summary>Type of the expected message.</summary>
		public required MessageType ExpectedResponseType { get; init; }
		/// <summary>Used by connection thread to deserialize message contents before setting it to <see cref="CompletionSource"/>.</summary>
		/// <param name="body">Span containing serialized message body.</param>
		/// <param name="header">Header of the message.</param>
		/// <returns>Deserialized message object.</returns>
		public abstract object DeserializeMessage(ReadOnlySpan<byte> body, MessageHeader header);
	}
	/// <summary>Implementation of <see cref="MessageCallback"/> for <see cref="Message{T}"/>.</summary>
	private class MessageCallback<T> : MessageCallback where T : IMessage<T>, new()
	{
		public override object DeserializeMessage(ReadOnlySpan<byte> buffer, MessageHeader header)
		{
			var message = new Message<T>(ExpectedResponseType) { Header = header };
			message.DeserializeBody(buffer);
			return message;
		}
	}
}