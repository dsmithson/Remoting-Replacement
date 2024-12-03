﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Castle.DynamicProxy;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace NewRemoting
{
	/// <summary>
	/// This class keeps track of all references that are used in remoting.
	/// It keeps a list (or rather: multiple lists) of instance ids together with their object references.
	/// Both local and remote objects are tracked. And the object list is static, meaning multiple instances
	/// of this class share the same object lists (this is needed when a process acts as server for many
	/// processes, or simultaneously as client and as server)
	/// </summary>
	internal class InstanceManager : IInstanceManager
	{
		private const string DelegateIdentifier = ".Method";

		/// <summary>
		/// The global(!) object registry. Contains references to all objects involved in remoting.
		/// The instances can be local, in which case we use it to look up their ids, or remote, in
		/// which case we use it to look up the correct proxy.
		/// </summary>
		private static readonly ConcurrentDictionary<string, InstanceInfo> s_objects;

		private static readonly object s_instanceNamesLock = new object();

		/// <summary>
		/// The list of known remote identifiers we have given references to.
		/// Key: Identifier, Value: Index
		/// </summary>
		private static readonly ConcurrentDictionary<string, int> s_knownRemoteInstances;

		/// <summary>
		/// This serves as reverse lookup to the above. To make sure this consists only
		/// of weak references, we use a ConditionalWeakTable.
		/// </summary>
		private static ConditionalWeakTable<object, ReverseInstanceInfo> s_instanceNames;

		private static int s_nextIndex;
		private static int s_numberOfInstancesUsed = 1;

		private readonly object _nextIdLock = new object();

		/// <summary>
		/// Weak dictionary to keep and find the ID of an object
		/// </summary>
		private readonly ConditionalWeakTable<object, InstanceId> _objectIds;

		private readonly ILogger _logger;
		private readonly Dictionary<string, ClientSideInterceptor> _interceptors;

		private long _nextId = 0;

		static InstanceManager()
		{
			s_objects = new ConcurrentDictionary<string, InstanceInfo>();
			s_instanceNames = new ConditionalWeakTable<object, ReverseInstanceInfo>();
			s_knownRemoteInstances = new ConcurrentDictionary<string, int>();
			s_nextIndex = -1;
		}

		public InstanceManager(ProxyGenerator proxyGenerator, ILogger logger)
		{
			_logger = logger;
			ProxyGenerator = proxyGenerator;
			_interceptors = new Dictionary<string, ClientSideInterceptor>();

			// Get process ID using System.Diagnostics.Process class.
			int processId = System.Diagnostics.Process.GetCurrentProcess().Id;

			ProcessIdentifier = Environment.MachineName + ":" + processId.ToString("X", CultureInfo.CurrentCulture) + "." + s_numberOfInstancesUsed++;
			_objectIds = new ConditionalWeakTable<object, InstanceId>();
		}

		/// <summary>
		/// A Destructor, to make sure the static list is properly cleaned up
		/// </summary>
		~InstanceManager()
		{
			Dispose(false);
		}

		public string ProcessIdentifier
		{
			get;
		}

		public ProxyGenerator ProxyGenerator
		{
			get;
		}

		public static ClientSideInterceptor GetInterceptor(Dictionary<string, ClientSideInterceptor> interceptors, string objectId)
		{
			// When first starting the client, we don't know the other side's instance id, so we use
			// an empty string for it. The client should only ever have this one entry in the list, anyway
			if (interceptors.TryGetValue(string.Empty, out var other))
			{
				return other;
			}

			// TODO: Since the list of interceptors is typically small, iterating may be faster
			string interceptorName = objectId.Substring(0, objectId.IndexOf("/", StringComparison.Ordinal));
			if (interceptors.TryGetValue(interceptorName, out var ic))
			{
				return ic;
			}
			else if (interceptors.Count >= 1)
			{
				// If the above fails, we assume the instance lives on a third system.
				// Here, we assume the first (or only) remote connection is the master one and the only one that can lead to further connections
				return interceptors.First().Value;
			}

			throw new InvalidOperationException("No interceptors available");
		}

		/// <summary>
		/// Get a method identifier (basically the unique name of a remote method)
		/// </summary>
		/// <param name="me">The method to encode</param>
		/// <returns></returns>
		public static string GetMethodIdentifier(MethodInfo me)
		{
			StringBuilder id = new StringBuilder($"{me.GetType().FullName}/.M/{me.Name}");
			var gen = me.GetGenericArguments();
			if (gen.Length > 0)
			{
				id.Append('<');
				id.Append(string.Join(",", gen.Select(x => x.FullName)));
				id.Append('>');
			}

			var parameters = me.GetParameters();
			id.Append('(');
			id.Append(string.Join(",", parameters.Select(p =>
			{
				var pt = p.ParameterType;
				if (pt.GenericTypeArguments.Any())
				{
					// If the argument is Action<T,...> or similar, construct manually, otherwise the string gets very long
					return $"{pt.Name}[{string.Join(",", (System.Collections.Generic.IEnumerable<Type>)pt.GenericTypeArguments)}]";
				}

				// Normally, the FullName does not include the assembly or the private key, which is good
				return $"{p.ParameterType.FullName} {p.Name}";
			})));
			id.Append(')');

			return id.ToString();
		}

		public bool IsLocalInstanceId(string objectId)
		{
			return objectId.StartsWith(ProcessIdentifier);
		}

		/// <summary>
		/// Use a dictionary to find the ID of an object, if needed create a new ID based on process ID and a 64bit counter
		/// </summary>
		public string RegisterRealObjectAndGetId(object instance, string willBeSentTo)
		{
			string id;
			lock (_nextIdLock)
			{
				if (_objectIds.TryGetValue(instance, out var instanceId))
				{
					id = instanceId.Id;
				}
				else
				{
					id = FormattableString.Invariant($"{ProcessIdentifier}/{_nextId++:x}");
					_objectIds.Add(instance, new InstanceId(id));
				}
			}

			AddInstance(instance, id, willBeSentTo, instance.GetType(), instance.GetType().AssemblyQualifiedName, true);
			return id;
		}

		/// <summary>
		/// Create an identifier for a delegate target (method + instance)
		/// </summary>
		/// <param name="del">The delegate to identify</param>
		/// <param name="remoteInstanceId">The instance of the target class to call</param>
		/// <returns></returns>
		public string GetDelegateTargetIdentifier(Delegate del, string remoteInstanceId)
		{
			StringBuilder id = new StringBuilder(FormattableString.Invariant($"{ProcessIdentifier}/{del.Method.GetType().FullName}/{DelegateIdentifier}/{del.Method.Name}/I/{remoteInstanceId}"));
			foreach (var g in del.Method.GetGenericArguments())
			{
				id.Append($"/{g.FullName}");
			}

			var parameters = del.Method.GetParameters();
			id.Append($"?{parameters.Length}");
			foreach (var p in parameters)
			{
				id.Append($"/{p.ParameterType.FullName}|{p.Name}");
			}

			if (del.Target != null)
			{
				id.Append($"/{RuntimeHelpers.GetHashCode(del.Target)}");
			}

			return id.ToString();
		}

		/// <summary>
		/// Get an actual instance from an object Id
		/// </summary>
		/// <param name="id">The object id</param>
		/// <param name="instance">Returns the object instance (this is normally a real instance and not a proxy, but this is not always true
		/// when transient servers exist)</param>
		/// <returns>True when an object with the given id was found, false otherwise</returns>
		public bool TryGetObjectFromId(string id, out object instance)
		{
			if (s_objects.TryGetValue(id, out InstanceInfo value))
			{
				instance = value.QueryInstance();
				if (instance != null)
				{
					return true;
				}
			}

			instance = null;
			return false;
		}

		public object AddInstance(object instance, string objectId, string willBeSentTo, Type originalType, string originalTypeName, bool doThrowOnDuplicate)
		{
			if (instance == null)
			{
				throw new ArgumentNullException(nameof(instance));
			}

			if (Client.IsProxyType(originalType))
			{
				throw new ArgumentException("The original type cannot be a proxy", nameof(originalType));
			}

			if (originalType != null)
			{
				if (originalType.AssemblyQualifiedName != originalTypeName)
				{
					throw new ArgumentException("This method must be called with type and typename matching the same instance");
				}
			}

			object usedInstance = instance;
			var ret = s_objects.AddOrUpdate(objectId, s =>
			{
				// Not found in list - insert new info object
				var ii = new InstanceInfo(instance, objectId, IsLocalInstanceId(objectId), originalTypeName, this);
				usedInstance = instance;
				MarkInstanceAsInUseBy(willBeSentTo, ii);
				_logger?.LogDebug($"Added new instance {ii.Identifier} to instance manager");
				return ii;
			}, (id, existingInfo) =>
			{
				// Update existing info object with new client information
				lock (existingInfo)
				{
					usedInstance = existingInfo.QueryInstance();
					if (existingInfo.IsReleased)
					{
						// if marked as no longer needed, revive by setting the instance
						existingInfo.SetInstance(instance);
						usedInstance = instance;
					}
					else
					{
						if (doThrowOnDuplicate && !ReferenceEquals(usedInstance, instance))
						{
							var msg = FormattableString.Invariant(
								$"Added new instance of {instance.GetType()} is not equals to {existingInfo.Identifier} to instance manager, but no duplicate was expected");
							_logger?.LogError(msg);
							throw new InvalidOperationException(msg);
						}

						// We have created the new instance twice due to a race condition
						// drop it again and use the old one instead
						_logger?.LogTrace($"Race condition detected: Duplicate instance for object id {objectId} will be discarded.");
					}

					// Update existing living info object with new client information
					MarkInstanceAsInUseBy(willBeSentTo, existingInfo);
					return existingInfo;
				}
			});

			// usedInstance cannot and must not be null here.
			lock (s_instanceNamesLock)
			{
				s_instanceNames.AddOrUpdate(usedInstance, new ReverseInstanceInfo(objectId, originalTypeName));
			}

			return usedInstance;
		}

		/// <summary>
		/// Gets the instance id for a given object.
		/// </summary>
		public bool TryGetObjectId(object instance, out string instanceId, out string originalTypeName)
		{
			if (ReferenceEquals(instance, null))
			{
				throw new ArgumentNullException(nameof(instance));
			}

			lock (s_instanceNamesLock)
			{
				ReverseInstanceInfo ri;
				if (s_instanceNames.TryGetValue(instance, out ri))
				{
					instanceId = ri.ObjectId;
					originalTypeName = ri.ObjectType;
					return true;
				}
			}

			// In case the above fails, try the slow method
			var values = s_objects.Values.ToList();
			foreach (var v in values)
			{
				if (ReferenceEquals(v.QueryInstance(), instance))
				{
					instanceId = v.Identifier;
					originalTypeName = v.OriginalType;
					return true;
				}
			}

			instanceId = null;
			originalTypeName = null;
			return false;
		}

		/// <summary>
		/// Returns the object for a given id
		/// </summary>
		/// <param name="id">The object id</param>
		/// <param name="typeOfCallerName">The type of caller (only used for debugging purposes)</param>
		/// <param name="methodId">The method about to call (only used for debugging)</param>
		/// <param name="wasDelegateTarget">True if the <paramref name="id"/> references a delegate target, but is no longer present (rare
		/// race condition when a callback happens at the same time the event is disconnected)</param>
		/// <returns>The object from the global cache</returns>
		/// <exception cref="InvalidOperationException">The object didn't exist (unless it was a delegate target call)</exception>
		public object GetObjectFromId(string id, string typeOfCallerName, string methodId, out bool wasDelegateTarget)
		{
			if (!TryGetObjectFromId(id, out object instance))
			{
				if (id.Contains($"/{DelegateIdentifier}/"))
				{
					_logger.LogWarning($"Callback delegate for {id} is gone. Attempting to ignore this, as it likely just deregistered");
					wasDelegateTarget = true;
					return null;
				}

				throw new InvalidOperationException($"Could not locate instance with ID {id} or it is not local. Local identifier: {ProcessIdentifier}, type of caller {typeOfCallerName}, methodId {methodId}");
			}

			wasDelegateTarget = false;
			return instance;
		}

		public void Clear()
		{
			foreach (var o in s_objects)
			{
				if (o.Value.Owner == this)
				{
					s_objects.TryRemove(o.Key, out _);
				}
			}
		}

		[Obsolete("Unittest only")]
		internal InstanceInfo QueryInstanceInfo(string id)
		{
			return s_objects[id];
		}

		/// <summary>
		/// Completely clears this instance. Only to be used for testing purposes
		/// </summary>
		/// <param name="fullyClear">Pass in true</param>
		public void Clear(bool fullyClear)
		{
			Clear();
			if (fullyClear)
			{
				s_objects.Clear();
				s_knownRemoteInstances.Clear();
				s_numberOfInstancesUsed = 1;
				s_nextIndex = -1;

				lock (s_instanceNamesLock)
				{
					s_instanceNames = new ConditionalWeakTable<object, ReverseInstanceInfo>();
				}
			}
		}

		protected virtual void Dispose(bool disposing)
		{
			Clear(); // Call whether disposing is true or not!
		}

		public void Dispose()
		{
			Dispose(true);
			GC.SuppressFinalize(this);
		}

		/// <summary>
		/// Checks for dead references in our instance cache and tells the server to clean them up.
		/// </summary>
		/// <param name="w">The link to the server</param>
		/// <param name="dropAll">True to drop all references, including active ones (used if about to disconnect)</param>
		public void PerformGc(BinaryWriter w, bool dropAll)
		{
			// Would be good if we could synchronize our updates with the GC, but that appears to be a bit fuzzy and fails if the
			// GC is in concurrent mode.
			List<InstanceInfo> instancesToClear = new List<InstanceInfo>();
			foreach (var e in s_objects)
			{
				lock (e.Value)
				{
					// Iterating over a ConcurrentDictionary should be thread safe
					if (e.Value.IsReleased || (dropAll && e.Value.Owner == this))
					{
						if (e.Value.IsLocal == false)
						{
							instancesToClear.Add(e.Value);
						}

						_logger?.LogDebug($"Instance {e.Value.Identifier} is released locally");
						MarkInstanceAsUnusedLocally(e.Value.Identifier);
					}
				}
			}

			if (instancesToClear.Count == 0)
			{
				return;
			}

			_logger?.Log(LogLevel.Debug, $"Cleaning up references to {instancesToClear.Count} objects");
			RemotingCallHeader hd = new RemotingCallHeader(RemotingFunctionType.GcCleanup, 0);
			using (var lck = hd.WriteHeader(w))
			{
				w.Write(instancesToClear.Count);
				foreach (var x in instancesToClear)
				{
					w.Write(x.Identifier);
				}
			}
		}

		/// <summary>
		/// Mark instance as unused and remove it if possible - needs to be called with lock on ii
		/// </summary>
		private void MarkInstanceAsUnusedLocally(string id)
		{
			if (s_objects.TryGetValue(id, out var ii))
			{
				ii.MarkAsUnusedViaRemoting();
				// Instances that are managed on the server side shall not be removed,
				// because others might just ask for them.
				if (!ii.IsReleased)
				{
					return;
				}

				if (ii.ReferenceBitVector != 0)
				{
					_logger?.LogError($"Instance {ii.Identifier} has inconsistent state preventing removal from the instance manager");
					return;
				}
			}

			if (s_objects.TryRemove(id, out ii))
			{
				if (ii.ReferenceBitVector != 0 || ii.IsReleased == false)
				{
					throw new InvalidOperationException(FormattableString.Invariant($"Attempting to free a reference ({ii.Identifier}) that is still in use"));
				}

				_logger?.LogDebug($"Instance {ii.Identifier} is removed from the instance manager");
			}
		}

		object IInstanceManager.CreateOrGetProxyForObjectId(bool canAttemptToInstantiate,
			Type typeOfArgument, string typeName, string objectId, List<string> knownInterfaceNames)
		{
			return CreateOrGetProxyForObjectId(null, canAttemptToInstantiate, typeOfArgument, typeName, objectId, knownInterfaceNames);
		}

		public object CreateOrGetProxyForObjectId(IInvocation invocation, bool canAttemptToInstantiate,
			Type typeOfArgument, string typeName, string objectId, List<string> knownInterfaceNames)
		{
			if (!_interceptors.Any())
			{
				throw new InvalidOperationException("Interceptor not set. Invalid initialization sequence");
			}

			object instance;
			Type type = string.IsNullOrEmpty(typeName) ? null : Server.GetTypeFromAnyAssembly(typeName,
				knownInterfaceNames == null || knownInterfaceNames.Count == 0);
			if (type == null)
			{
				// The type name may be omitted if the client knows that this instance must exist
				// (i.e. because it is sending a reference to a proxy back)
				if (TryGetObjectFromId(objectId, out instance))
				{
					_logger?.LogDebug($"Found an instance for object id {objectId}");
					return instance;
				}

				if (knownInterfaceNames.Count == 0)
				{
					throw new RemotingException("Unknown type found in argument stream");
				}
			}
			else
			{
				if (TryGetObjectFromId(objectId, out instance))
				{
					_logger?.LogDebug($"Found an instance for object id {objectId}");
					return instance;
				}
			}

			if (IsLocalInstanceId(objectId))
			{
				throw new InvalidOperationException("Got an instance that should be proxied, but it is a local object");
			}

			var interceptor = GetInterceptor(_interceptors, objectId);
			// Create a class proxy with all interfaces proxied as well.
			Type[] interfaces = null;
			if (type != null)
			{
				interfaces = type.GetInterfaces();
			}
			else
			{
				interfaces = new Type[knownInterfaceNames.Count];
				for (var index = 0; index < knownInterfaceNames.Count; index++)
				{
					var interfaceToFind = knownInterfaceNames[index];
					var tt = Server.GetTypeFromAnyAssembly(interfaceToFind, true);
					interfaces[index] = tt;
				}
			}

			ManualInvocation mi = invocation as ManualInvocation;
			if (typeOfArgument != null && typeOfArgument.IsInterface)
			{
				_logger?.Log(LogLevel.Debug, $"Create interface proxy for main type {typeOfArgument}");
				// If the call returns an interface, only create an interface proxy, because we might not be able to instantiate the actual class (because it's not public, it's sealed, has no public ctors, etc)
				instance = ProxyGenerator.CreateInterfaceProxyWithoutTarget(typeOfArgument, interfaces, interceptor);
			}
			else if (canAttemptToInstantiate && (!type.IsSealed) && (MessageHandler.HasDefaultCtor(type) || (mi != null && invocation.Arguments.Length > 0 && mi.Constructor != null)))
			{
				_logger?.Log(LogLevel.Debug, $"Create class proxy for main type {type}");
				if (MessageHandler.HasDefaultCtor(type))
				{
					instance = ProxyGenerator.CreateClassProxy(type, interfaces, ProxyGenerationOptions.Default, Array.Empty<object>(), interceptor);
				}
				else
				{
					// We can attempt to create a class proxy if we have ctor arguments and the type is not sealed. But only if we are really calling into a ctor, otherwise the invocation
					// arguments are the method arguments that created this instance as return value and then obviously the arguments are different.
					instance = ProxyGenerator.CreateClassProxy(type, interfaces, ProxyGenerationOptions.Default, invocation.Arguments, interceptor);
				}
			}
			else if ((type.IsSealed || !MessageHandler.HasDefaultCtor(type) || typeOfArgument == typeof(object)) && interfaces.Length > 0)
			{
				// If the type is sealed or has no default ctor, we need to create an interface proxy, even if the target type is not an interface and may therefore not match.
				// If the target type is object, we also try an interface proxy instead, since everything should be convertible to object.
				_logger?.Log(LogLevel.Debug, $"Create interface proxy as backup for main type {type} with {interfaces[0]}");
				if (type == typeof(FileStream))
				{
					// Special case of the Stream case below. This is not a general solution, but for this type, we can then create the correct type, so when
					// it is casted or marshalled again, it gets the correct proxy type.
					// As of .NET6.0, we need to create a real local instance, since creating a fake handle no longer works.
					string mySelf = Assembly.GetExecutingAssembly().Location;
					instance = ProxyGenerator.CreateClassProxy(typeof(FileStream), interfaces, ProxyGenerationOptions.Default,
						new object[] { mySelf, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 }, interceptor);
				}
				else if (typeof(Stream).IsAssignableFrom(type))
				{
					// This is a bit of a special case, not sure yet for what other classes we should use this (otherwise, this gets an interface proxy for IDisposable, which is
					// not castable to Stream, which is most likely required)
					instance = ProxyGenerator.CreateClassProxy(typeof(Stream), interfaces, ProxyGenerationOptions.Default, interceptor);
				}
				else if (typeof(WaitHandle).IsAssignableFrom(type))
				{
					instance = ProxyGenerator.CreateClassProxy(typeof(WaitHandle), interfaces, ProxyGenerationOptions.Default, interceptor);
				}
				else
				{
					// Best would be to create a class proxy but we can't. So try an interface proxy with one of the interfaces instead
					instance = ProxyGenerator.CreateInterfaceProxyWithoutTarget(interfaces[0], interfaces, interceptor);
				}
			}
			else
			{
				_logger?.Log(LogLevel.Debug, $"Create class proxy as fallback for main type {type}");
				instance = ProxyGenerator.CreateClassProxy(type, interfaces, interceptor);
			}

			object instance2 = AddInstance(instance, objectId, null, type, typeName, false);

			return instance2;
		}

		/// <summary>
		/// Remove the given object ID from the container. This is mostly used for garbage collection, but also for explicit disposal.
		/// </summary>
		/// <param name="objectId">The object Id to remove</param>
		/// <param name="remoteProcessId">The process attempting to remove the object</param>
		/// <param name="reallyRemove">If the target object has no noted references any more, force a removal.</param>
		public void Remove(string objectId, string remoteProcessId, bool reallyRemove)
		{
			if (!s_knownRemoteInstances.TryGetValue(remoteProcessId, out int id))
			{
				// Not a known remote instance, cannot have any objects
				return;
			}

			ulong bit = GetBitFromIndex(id);

			if (s_objects.TryGetValue(objectId, out InstanceInfo ii))
			{
				lock (ii)
				{
					ii.ReferenceBitVector &= ~bit;
					if (ii.ReferenceBitVector == 0)
					{
						// If not more clients, forget about this object - the server GC will care for the rest.
						MarkInstanceAsUnusedLocally(ii.Identifier);
						if (reallyRemove)
						{
							s_objects.TryRemove(objectId, out _);
						}
					}
				}
			}
		}

		public void AddInterceptor(ClientSideInterceptor interceptor)
		{
			_interceptors.Add(interceptor.OtherSideProcessId, interceptor);
		}

		private void MarkInstanceAsInUseBy(string willBeSentTo, InstanceInfo instanceInfo)
		{
			if (!instanceInfo.IsLocal)
			{
				// Only local instances need reference "counting"
				return;
			}

			if (willBeSentTo == null)
			{
				throw new ArgumentNullException(nameof(willBeSentTo));
			}

			int indexOfClient = s_knownRemoteInstances.AddOrUpdate(willBeSentTo, (s) =>
			{
				return Interlocked.Increment(ref s_nextIndex);
			}, (s, i) => i);

			// To save memory and processing time, we use a bitvector to keep track of which client has a reference to
			// a specific instance
			if (s_nextIndex > 8 * sizeof(UInt64))
			{
				_logger?.LogWarning($"Too many instances registered {0}", s_nextIndex);
				foreach (var i in s_knownRemoteInstances)
				{
					_logger?.LogWarning(i.Key);
				}

				throw new InvalidOperationException("To many different instance identifiers seen - only up to 64 allowed right now");
			}

			ulong bit = GetBitFromIndex(indexOfClient);
			instanceInfo.ReferenceBitVector |= bit;
		}

		private ulong GetBitFromIndex(int index)
		{
			return 1ul << index;
		}

		internal class InstanceId
		{
			public string Id { get; }

			public InstanceId(string id)
			{
				Id = id;
			}
		}

		/// <summary>
		/// Keeps track of MarshalByRef-Instances (or proxies of them)
		/// If the actual instance lives in the remote process, we need to keep the hard reference, because the
		/// remoting infrastructure might be the only owner of that object (a proxy in this case).
		/// If it is a reference to a local object, we can use a weak reference. It will be gone, once there are no
		/// other references to it within our process - meaning no one has a reference to the actual instance any more.
		/// </summary>
		internal class InstanceInfo
		{
			private readonly InstanceManager _owningInstanceManager;
			private object _instanceHardReference;
			private WeakReference _instanceWeakReference;

			public InstanceInfo(object obj, string identifier, bool isLocal, string originalTypeName, InstanceManager owner)
			{
				IsLocal = isLocal;
				SetInstance(obj);

				Identifier = identifier;
				OriginalType = originalTypeName ?? throw new ArgumentNullException(nameof(originalTypeName));
				_owningInstanceManager = owner;
				ReferenceBitVector = 0;
			}

			public string Identifier
			{
				get;
			}

			public bool IsLocal { get; }

			public string OriginalType
			{
				get;
			}

			public bool IsReleased
			{
				get
				{
					return _instanceHardReference == null && (_instanceWeakReference == null || !_instanceWeakReference.IsAlive);
				}
			}

			public InstanceManager Owner => _owningInstanceManager;

			/// <summary>
			/// Contains a binary 1 for each remote instance from the <see cref="InstanceManager.s_knownRemoteInstances"/> that has
			/// references to this instance. If this is 0, the object is eligible for garbage collection from the view of the
			/// remoting infrastructure.
			/// </summary>
			public UInt64 ReferenceBitVector
			{
				get;
				set;
			}

			public void MarkAsUnusedViaRemoting()
			{
				if (_instanceHardReference != null)
				{
					_instanceWeakReference = new WeakReference(_instanceHardReference, false);
					_instanceHardReference = null;
				}
			}

			private void Resurrect()
			{
				object instance = _instanceWeakReference?.Target;
				_instanceHardReference = instance;
				_instanceWeakReference = null;
			}

			/// <summary>
			/// Returns the currently stored item. Resurrects the instance, if needed.
			/// </summary>
			/// <remarks>
			/// Implemented as method, because has a side effect.
			/// </remarks>
			public object QueryInstance()
			{
				if (_instanceHardReference != null)
				{
					return _instanceHardReference;
				}

				var ret = _instanceWeakReference?.Target;

				if (IsLocal)
				{
					// If this should be a hard reference, resurrect it
					Resurrect();
				}

				return ret;
			}

			public void SetInstance(object value)
			{
				if (IsLocal)
				{
					_instanceHardReference = value;
					_instanceWeakReference = null;
				}
				else
				{
					_instanceWeakReference = new WeakReference(value, false);
				}
			}
		}

		private class ReverseInstanceInfo
		{
			public ReverseInstanceInfo(string objectId, string objectType)
			{
				ObjectId = objectId;
				ObjectType = objectType;
			}

			public string ObjectId { get; }

			public string ObjectType { get; }
		}
	}
}
