﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Castle.DynamicProxy;
using Microsoft.Extensions.Logging;
using NewRemoting.Toolkit;

// BinaryFormatter shouldn't be used
#pragma warning disable SYSLIB0011
namespace NewRemoting
{
	internal sealed class ClientSideInterceptor : IInterceptor, IProxyGenerationHook, IDisposable
	{
		private const int NumberOfCallsForGc = 100;
		private static readonly TimeSpan GcLoopTime = new TimeSpan(0, 0, 0, 20);
		private readonly ConnectionSettings _settings;
		private readonly Stream _serverLink;
		private readonly MessageHandler _messageHandler;
		private readonly ILogger _logger;
		private int _sequence;
		private ConcurrentDictionary<int, CallContext> _pendingInvocations;
		private Thread _receiverThread;
		private Thread _memoryCollectingThread;
		private bool _receiving;
		private CancellationTokenSource _terminator;
		private AutoResetEvent _gcEvent;
		private int _numberOfCallsInspected;

		/// <summary>
		/// This is only used withhin the receiver thread, but must be global so it can be closed on dispose (to
		/// force falling out of the blocking Read call)
		/// </summary>
		private BinaryReader _reader;

		public ClientSideInterceptor(string otherSideProcessId, string thisSideProcessId, bool clientSide, ConnectionSettings settings,
			Stream serverLink, MessageHandler messageHandler, ILogger logger)
		{
			_receiving = true;
			_numberOfCallsInspected = 0;
			OtherSideProcessId = otherSideProcessId;
			ThisSideProcessId = thisSideProcessId;
			DebuggerToStringBehavior = DebuggerToStringBehavior.ReturnProxyName;
			_sequence = clientSide ? 1 : 10000;
			_settings = settings;
			_serverLink = serverLink;
			_messageHandler = messageHandler ?? throw new ArgumentNullException(nameof(messageHandler));
			_logger = logger;
			_pendingInvocations = new ConcurrentDictionary<int, CallContext>();
			_terminator = new CancellationTokenSource();
			_reader = new BinaryReader(_serverLink, MessageHandler.DefaultStringEncoding);
			_receiverThread = new Thread(ReceiverThread);
			_receiverThread.Name = "ClientSideInterceptor - " + thisSideProcessId;
			_memoryCollectingThread = new Thread(MemoryCollectingThread);
			_memoryCollectingThread.Name = "Memory collector - " + thisSideProcessId;
			_gcEvent = new AutoResetEvent(false);
		}

		public DebuggerToStringBehavior DebuggerToStringBehavior
		{
			get;
			set;
		}

		public string OtherSideProcessId
		{
			get;
			set;
		}

		public string ThisSideProcessId { get; }

		/// <summary>
		/// The method is not implemented on the mono runtime (which is used e.g. for Android), therefore use this wrapper
		/// </summary>
		private static long GetGcMemoryInfoIndex()
		{
			if (IsAndroid() || IsIOS())
			{
				return 0;
			}
			else
			{
				// Since GetGCMemoryInfo is not available in .NET Standard 2.1, we use a simpler memory metric.
				// Returning a placeholder value as Index is not available.
				return GC.CollectionCount(0); // Get the count of collections for generation 0 as a simple proxy
			}
		}

		private static bool IsAndroid()
		{
			return RuntimeInformation.IsOSPlatform(OSPlatform.Create("ANDROID"));
		}

		private static bool IsIOS()
		{
			return RuntimeInformation.IsOSPlatform(OSPlatform.Create("IOS"));
		}

		internal int NextSequenceNumber()
		{
			return Interlocked.Increment(ref _sequence);
		}

		internal void Start()
		{
			_receiverThread.Start();
			_memoryCollectingThread.Start();
		}

		public void Intercept(IInvocation invocation)
		{
			string methodName = invocation.Method.ToString();

			if (_receiverThread == null)
			{
				if (methodName == "Void Dispose(Boolean)")
				{
					return;
				}

				throw new ObjectDisposedException("Remoting infrastructure has been shut down. Remote proxies are no longer valid");
			}

			if (methodName == "Void Dispose(Boolean)")
			{
				var arg = invocation.Arguments.FirstOrDefault();
				if (arg is false)
				{
					// Dispose(false) must not be remoted, this is called by the GC and only gets here for special objects like fileStream
					invocation.Proceed();
					return;
				}
			}

			// Todo: Check this stuff
			if (methodName == "ToString()" && DebuggerToStringBehavior != DebuggerToStringBehavior.EvaluateRemotely)
			{
				invocation.ReturnValue = "Remote proxy";
				return;
			}

			_gcEvent.Set();

			int thisSeq = NextSequenceNumber();

			_logger.Log(LogLevel.Debug, $"{ThisSideProcessId}: Intercepting {invocation.Method}, sequence {thisSeq}");

			MethodInfo me = invocation.Method;
			RemotingCallHeader hd = new RemotingCallHeader(RemotingFunctionType.MethodCall, thisSeq);

			if (me.IsStatic)
			{
				throw new RemotingException("Remote-calling a static method? No.");
			}

			if (!_messageHandler.InstanceManager.TryGetObjectId(invocation.Proxy, out var remoteInstanceId, out _))
			{
				// One valid case when we may get here is when the proxy is just being created (as a class proxy) and within that ctor,
				// a virtual member function is called. So we can execute the call locally (the object should be in an useful state, since its default ctor
				// has been called)
				// Another possible reason to end here is when a class proxy gets its Dispose(false) method called by the local finalizer thread.
				// The remote reference is long gone and the local destructor may not work as expected, because the object is not
				// in a valid state.
				_logger.Log(LogLevel.Debug, "Not a valid remoting proxy. Assuming within ctor of class proxy");
				try
				{
					invocation.Proceed();
				}
				catch (NotImplementedException x)
				{
					_logger.LogError(x, "Unable to proceed on suspected class ctor. Assuming disconnected interface instead");
					throw new RemotingException($"Unable to call method {me.Name} on remote object. Instance for object of type {invocation.Proxy.GetType()} not found.");
				}

				_pendingInvocations.TryRemove(thisSeq, out _);
				return;
			}

			if (invocation.Proxy is DelegateInternalSink di)
			{
				// Need the source reference for this one. There's something fishy here, as this sometimes is ok, sometimes not.
				remoteInstanceId = di.RemoteObjectReference;
			}

			using (CallContext ctx = CreateCallContext(invocation, thisSeq))
			{
				// If we have a delegate as argument, we must use the copy-before-send path
				bool largeAllocationExpected = !invocation.Arguments.Any(x => x is Delegate);

				if (largeAllocationExpected)
				{
					largeAllocationExpected = invocation.Arguments.Any(x =>
					{
						if (x is Array array && array.LongLength > 1024) // Expected to stay in the small object heap?
						{
							return true;
						}

						return false;
					});
				}

				if (largeAllocationExpected)
				{
					// Do not use this writer before gaining the lock in the next line!
					try
					{
						using (BinaryWriter writer = new BinaryWriter(_serverLink, MessageHandler.DefaultStringEncoding, true))
						using (var lck = hd.WriteHeader(writer))
						{
							if (!PreparePayload(invocation, writer, remoteInstanceId, me))
							{
								// We need to ensure we don't end here in this case (we can't abort what we've already written to the stream)
								throw new InvalidOperationException("Large allocation expected for delegate call - this shouldn't happen");
							}
						}
					}
					catch (IOException x)
					{
						throw new RemotingException("Error sending bulk data to server. Link down?", x);
					}
				}
				else
				{
					using (Stream targetStream = new MemoryStream(1024))
					using (BinaryWriter writer = new BinaryWriter(targetStream, MessageHandler.DefaultStringEncoding))
					{
						hd.WriteHeaderNoLock(writer);
						if (!PreparePayload(invocation, writer, remoteInstanceId, me))
						{
							// We don't need to inform the server (was a pure local delegate operation)
							return;
						}

						// now finally write the stream to the network. That way, we don't send incomplete messages if an exception happens encoding a parameter.
						SafeSendToServer(targetStream);
					}
				}

				WaitForReply(invocation, ctx);
			}
		}

		private bool PreparePayload(IInvocation invocation, BinaryWriter writer, string remoteInstanceId, MethodInfo me)
		{
			writer.Write(remoteInstanceId);
			// Also transmit the type of the calling object (if the method is called on an interface, this is different from the actual object)
			if (me.DeclaringType != null)
			{
				writer.Write(me.DeclaringType.AssemblyQualifiedName ?? string.Empty);
				if (me.DeclaringType == typeof(FileStream) && invocation.Method.Name == nameof(Dispose))
				{
					// We have created an actual instance of this class, so also dispose it properly
					invocation.Proceed();
				}
			}
			else
			{
				writer.Write(string.Empty);
			}

			string methodNameToCall = InstanceManager.GetMethodIdentifier(me);
			writer.Write(methodNameToCall);
			if (me.ContainsGenericParameters)
			{
				// This should never happen (or the compiler has done something wrong)
				throw new RemotingException("Cannot call methods with open generic arguments");
			}

			var genericArgs = me.GetGenericArguments();
			writer.Write(genericArgs.Length);
			foreach (var genericType in genericArgs)
			{
				string arg = genericType.AssemblyQualifiedName;
				if (arg == null)
				{
					throw new RemotingException("Unresolved generic type or some other undefined case");
				}

				writer.Write(arg);
			}

			writer.Write(invocation.Arguments.Length);

			foreach (var argument in invocation.Arguments)
			{
				if (argument is Delegate del)
				{
					if (!_messageHandler.WriteDelegateArgumentToStream(writer, del, invocation.Method, OtherSideProcessId, remoteInstanceId))
					{
						return false;
					}
				}
				else
				{
					_messageHandler.WriteArgumentToStream(writer, argument, OtherSideProcessId, _settings);
				}
			}

			return true;
		}

		private void SafeSendToServer(Stream rawDataMessage)
		{
			try
			{
				rawDataMessage.Position = 0;
				lock (_serverLink)
				{
					rawDataMessage.CopyTo(_serverLink);
				}
			}
			catch (IOException x)
			{
				throw new RemotingException("Error sending data to server. Link down?", x);
			}
		}

		public void InitiateGc()
		{
			Interlocked.Exchange(ref _numberOfCallsInspected, NumberOfCallsForGc + 10);
			_gcEvent.Set();
		}

		/// <summary>
		/// Informs the server about stale object references.
		/// Ideally, we would listen to GC callbacks, but the documentation on <see cref="GC.RegisterForFullGCNotification"/> is ambiguous to say the least: It says this
		/// operation is not available when using the concurrent GC, but that is the default meanwhile.
		/// </summary>
		private void MemoryCollectingThread()
		{
			long lastGc = GetGcMemoryInfoIndex();

			WaitHandle[] handles = new WaitHandle[] { _gcEvent, _terminator.Token.WaitHandle };
			while (_receiving)
			{
				if (Interlocked.Increment(ref _numberOfCallsInspected) > NumberOfCallsForGc)
				{
					using (MemoryStream rawDataMessage = new MemoryStream(1024))
					using (BinaryWriter writer = new BinaryWriter(rawDataMessage, MessageHandler.DefaultStringEncoding))
					{
						_logger.LogInformation($"Starting GC on {ThisSideProcessId}");
						_messageHandler.InstanceManager.PerformGc(writer, false);
						SafeSendToServer(rawDataMessage);
						Interlocked.Exchange(ref _numberOfCallsInspected, 0);
						lastGc = GetGcMemoryInfoIndex();
					}
				}

				int waitEventNo = WaitHandle.WaitAny(handles, GcLoopTime);
				if (waitEventNo == 1)
				{
					return;
				}

				if (waitEventNo == 0)
				{
					continue;
				}

				// timeout case
				// If we run into a timeout here, we want to perform a GC a bit more aggressive
				int newValue = _numberOfCallsInspected + 20;
				// If we loose an assignment in the worst case here, nothing ugly is going to happen
				Interlocked.Exchange(ref _numberOfCallsInspected, newValue);

				long newIndex = GetGcMemoryInfoIndex();
				if (newIndex > lastGc)
				{
					// A GC has happened
					Interlocked.Exchange(ref _numberOfCallsInspected, NumberOfCallsForGc + 10);
				}
			}
		}

		internal CallContext CreateCallContext(IInvocation invocation, int thisSeq)
		{
			CallContext ctx = new CallContext(invocation, thisSeq, _terminator.Token);
			if (!_pendingInvocations.TryAdd(thisSeq, ctx))
			{
				// This really shouldn't happen
				throw new InvalidOperationException("A call with the same id is already being processed");
			}

			return ctx;
		}

		internal void WaitForReply(IInvocation invocation, CallContext ctx)
		{
			// The event is signaled by the receiver thread when the message was processed
			string methodName = invocation.Method != null ? invocation.Method.ToString() : invocation.ToString();
			ctx.Wait();
			if (ctx.Exception != null)
			{
				_logger.LogDebug($"{ThisSideProcessId}: {methodName} caused an exception to be thrown: {ctx.Exception.Message}.");
				if (ctx.IsInTerminationMethod())
				{
					return;
				}

				// Rethrow remote exception
				ExceptionDispatchInfo.Capture(ctx.Exception).Throw();
			}

			_logger.Log(LogLevel.Debug, $"{ThisSideProcessId}: {methodName} returns.");
		}

		private void ReceiverThread()
		{
			try
			{
				while (_receiving && !_terminator.IsCancellationRequested)
				{
					RemotingCallHeader hdReturnValue = new RemotingCallHeader();
					// This read is blocking
					if (!hdReturnValue.ReadFrom(_reader))
					{
						throw new RemotingException("Unexpected reply or stream out of sync");
					}

					_logger.Log(LogLevel.Debug, $"{ThisSideProcessId}: Decoding message {hdReturnValue.Sequence} of type {hdReturnValue.Function}");

					if (hdReturnValue.Function == RemotingFunctionType.ServerShuttingDown)
					{
						// Quit here.
						foreach (var inv in _pendingInvocations)
						{
							inv.Value.Exception = new RemotingException("Server terminated itself. Call aborted");
							inv.Value.Set();
						}

						_pendingInvocations.Clear();
						_terminator.Cancel();
						return;
					}

					if (hdReturnValue.Function != RemotingFunctionType.MethodReply && hdReturnValue.Function != RemotingFunctionType.ExceptionReturn)
					{
						throw new RemotingException("Only replies or exceptions should end here");
					}

					if (_pendingInvocations.TryRemove(hdReturnValue.Sequence, out var ctx))
					{
						if (hdReturnValue.Function == RemotingFunctionType.ExceptionReturn)
						{
							_logger.Log(LogLevel.Debug, $"{ThisSideProcessId}: Receiving exception in reply to {ctx.Invocation.Method}");
							var exception = MessageHandler.DecodeException(_reader, OtherSideProcessId, _messageHandler);
							ctx.Exception = exception;
							ctx.Set();
						}
						else
						{
							_logger.Log(LogLevel.Debug, $"{ThisSideProcessId}: Receiving reply for {ctx.Invocation.Method}");
							_messageHandler.ProcessCallResponse(ctx.Invocation, _reader, ThisSideProcessId, _settings);
							ctx.Set();
						}
					}
					else
					{
						throw new RemotingException($"There's no pending call for sequence id {hdReturnValue.Sequence}");
					}
				}
			}
			catch (Exception x) when (x is IOException || x is ObjectDisposedException)
			{
				_logger.Log(LogLevel.Error, "Terminating client receiver thread - Communication Exception: " + x.Message);
				_receiving = false;
				_terminator.Cancel();
			}
		}

		public void MethodsInspected()
		{
		}

		public void NonProxyableMemberNotification(Type type, MemberInfo memberInfo)
		{
			_logger.Log(LogLevel.Error, $"Type {type} has non-virtual method {memberInfo} - cannot be used for proxying");
		}

		public bool ShouldInterceptMethod(Type type, MethodInfo methodInfo)
		{
			return true;
		}

		public void Dispose()
		{
			_terminator.Cancel();
			_receiving = false;
			_reader.Dispose();
			_receiverThread?.Join();
			_receiverThread = null;
			_memoryCollectingThread?.Join();
			_memoryCollectingThread = null;
		}

		internal sealed class CallContext : IDisposable
		{
			private static readonly MethodInfo TerminationMethod =
				typeof(RemoteServerService).GetMethod(nameof(RemoteServerService.TerminateRemoteServerService));

			private readonly CancellationToken _externalTerminator;

			public CallContext(IInvocation invocation, int sequence, CancellationToken externalTerminator)
			{
				_externalTerminator = externalTerminator;
				Invocation = invocation;
				SequenceNumber = sequence;
				EventToTrigger = new AutoResetEvent(false);
				Exception = null;
			}

			public IInvocation Invocation
			{
				get;
			}

			public int SequenceNumber { get; }

			private AutoResetEvent EventToTrigger
			{
				get;
				set;
			}

			public Exception Exception
			{
				get;
				set;
			}

			public void Wait()
			{
				WaitHandle[] handles = new WaitHandle[] { EventToTrigger, _externalTerminator.WaitHandle };
				if (handles.All(x => x != null))
				{
					WaitHandle.WaitAny(handles);
				}

				if (IsInTerminationMethod())
				{
					// Report, but don't throw (special handling by parent)
					Exception = new RemotingException("Error executing remote call: Link is going down");
					return;
				}

				if (_externalTerminator.IsCancellationRequested)
				{
					throw new RemotingException("Error executing remote call: Link is going down");
				}
			}

			public bool IsInTerminationMethod()
			{
				if (Invocation.Method != null && Invocation.Method.Name == TerminationMethod.Name && Invocation.Method.DeclaringType == typeof(IRemoteServerService))
				{
					// If this is a call to the above method, we drop the exception, because it is expected.
					return true;
				}

				return false;
			}

			public void Set()
			{
				EventToTrigger?.Set();
			}

			public void Dispose()
			{
				if (EventToTrigger != null)
				{
					EventToTrigger.Dispose();
					EventToTrigger = null;
				}
			}
		}
	}
}
