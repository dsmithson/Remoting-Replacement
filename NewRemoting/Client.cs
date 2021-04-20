﻿using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;
using System.Threading.Tasks;
using Castle.DynamicProxy;

namespace NewRemoting
{
    public sealed class Client : IDisposable, IInternalClient
    {
        private TcpClient _client;
        private BinaryWriter _writer;
        private BinaryReader _reader;
        private DefaultProxyBuilder _builder;
        private ProxyGenerator _proxy;
        private ConditionalWeakTable<object, string> _knownRemoteInstances;
        private Dictionary<string, WeakReference> _knownProxyInstances;
        private IFormatter _formatter;
        private Server _server;
        private object _accessLock;

        /// <summary>
        /// This contains references the client forwards to the server, that is, where the client hosts the actual object and the server gets the proxy.
        /// </summary>
        private Dictionary<string, object> _hardReverseReferences;

        public Client(string server, int port)
        {
            _accessLock = new object();
            _knownRemoteInstances = new();
            _hardReverseReferences = new ();
            _knownProxyInstances = new ();
            _formatter = new BinaryFormatter();
            _client = new TcpClient(server, port);
            _writer = new BinaryWriter(_client.GetStream(), Encoding.Unicode);
            _reader = new BinaryReader(_client.GetStream(), Encoding.Unicode);
            _builder = new DefaultProxyBuilder();
            _proxy = new ProxyGenerator(_builder);

            // This is used as return channel
            _server = new Server(port + 1, this);
        }

        object IInternalClient.CommunicationLinkLock
        {
            get
            {
                return _accessLock;
            }
        }

        public IPAddress[] LocalIpAddresses()
        {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            return host.AddressList;
        }

        public void Start()
        {
            lock (_accessLock)
            {
                if (!_server.IsRunning)
                {
                    _server.StartListening();
                    RemotingCallHeader openReturnChannel = new RemotingCallHeader(RemotingFunctionType.OpenReverseChannel, 0);
                    openReturnChannel.WriteTo(_writer);
                    var addresses = LocalIpAddresses();
                    var addressToUse = addresses.First(x => x.AddressFamily == AddressFamily.InterNetwork);
                    _writer.Write(addressToUse.ToString());
                    _writer.Write(_server.NetworkPort);
                }
            }
        }

        public void ShutdownServer()
        {
            lock (_accessLock)
            {
                RemotingCallHeader hd = new RemotingCallHeader(RemotingFunctionType.ShutdownServer, 0);
                hd.WriteTo(_writer);
            }
        }

        public T CreateRemoteInstance<T>() where T : MarshalByRefObject
        {
            return (T) CreateRemoteInstance(typeof(T));
        }

        public object CreateRemoteInstance(Type typeOfInstance)
        {
            if (typeOfInstance == null)
            {
                throw new ArgumentNullException(nameof(typeOfInstance));
            }

            if (!typeOfInstance.IsAssignableTo(typeof(MarshalByRefObject)))
            {
                throw new RemotingException("Can only create instances of type MarshalByRefObject remotely", RemotingExceptionKind.UnsupportedOperation);
            }

            Start();

            lock (_accessLock)
            {

                RemotingCallHeader hd = new RemotingCallHeader(RemotingFunctionType.CreateInstanceWithDefaultCtor, 0);
                hd.WriteTo(_writer);
                _writer.Write(typeOfInstance.AssemblyQualifiedName);
                _writer.Write(string.Empty);
                _writer.Write((int) 0); // Currently, we do not need the correct ctor identifier, since there can only be one default ctor
                _writer.Write((int) 0); // and no generic args, anyway
                RemotingCallHeader hdReply = default;
                bool hdParseSuccess = hdReply.ReadFrom(_reader);
                RemotingReferenceType remoteType = (RemotingReferenceType) _reader.ReadInt32();

                if (hdParseSuccess == false || remoteType != RemotingReferenceType.NewProxy)
                {
                    throw new RemotingException("Unexpected reply", RemotingExceptionKind.ProtocolError);
                }

                string typeName = _reader.ReadString();
                string objectId = _reader.ReadString();

                var interceptor = new ClientSideInterceptor(_client, this, _proxy, _formatter);

                ProxyGenerationOptions options = new ProxyGenerationOptions(interceptor);

                object instance = _proxy.CreateClassProxy(typeOfInstance, options, interceptor);
                _knownRemoteInstances.Add(instance, objectId);
                return instance;
            }
        }

        public void Dispose()
        {
            lock (_accessLock)
            {
                _server.Terminate();
                _server.Dispose();
                _client.Dispose();
                _hardReverseReferences.Clear();
                _knownRemoteInstances.Clear();
            }
        }

        void IInternalClient.AddKnownRemoteInstance(object obj, string objectId)
        {
            _knownRemoteInstances.AddOrUpdate(obj, objectId);
            _knownProxyInstances[objectId] = new WeakReference(obj);
        }

        bool IInternalClient.TryGetRemoteInstance(object obj, out string objectId)
        {
            return _knownRemoteInstances.TryGetValue(obj, out objectId);
        }

        object IInternalClient.GetLocalInstanceFromReference(string objectId)
        {
            if (_hardReverseReferences.TryGetValue(objectId, out object instance))
            {
                return instance;
            }

            if (_knownProxyInstances.TryGetValue(objectId, out WeakReference reference))
            {
                return reference.Target;
            }

            return null;
        }

        string IInternalClient.GetIdForLocalObject(object obj, out bool isNew)
        {
            var key = RealServerReferenceContainer.GetObjectInstanceId(obj);
            if (_hardReverseReferences.TryAdd(key, obj))
            {
                isNew = true;
                return key;
            }

            isNew = false;
            return key;
        }
    }
}