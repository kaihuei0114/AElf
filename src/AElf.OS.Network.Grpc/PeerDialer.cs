using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Threading.Tasks;
using AElf.Kernel;
using AElf.Kernel.Account.Application;
using AElf.OS.Network.Grpc.Helpers;
using AElf.OS.Network.Protocol;
using AElf.OS.Network.Protocol.Types;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.X509;
using Volo.Abp.Threading;

namespace AElf.OS.Network.Grpc
{
    /// <summary>
    /// Provides functionality to setup a connection to a distant node by exchanging some
    /// low level information.
    /// </summary>
    public class PeerDialer : IPeerDialer
    {
        private NetworkOptions NetworkOptions => NetworkOptionsSnapshot.Value;
        public IOptionsSnapshot<NetworkOptions> NetworkOptionsSnapshot { get; set; }

        private readonly IAccountService _accountService;
        private readonly IHandshakeProvider _handshakeProvider;

        public ILogger<PeerDialer> Logger { get; set; }

        public PeerDialer(IAccountService accountService,
            IHandshakeProvider handshakeProvider)
        {
            _accountService = accountService;
            _handshakeProvider = handshakeProvider;

            Logger = NullLogger<PeerDialer>.Instance;
        }

        /// <summary>
        /// Given an IP address, will create a handshake to the distant node for
        /// further communications.
        /// </summary>
        /// <returns>The created peer</returns>
        public async Task<GrpcPeer> DialPeerAsync(IPEndPoint remoteEndPoint)
        {
            var handshake = await _handshakeProvider.GetHandshakeAsync();
            var client = CreateClient(remoteEndPoint);

            var handshakeReply = await CallDoHandshakeAsync(client, remoteEndPoint, handshake);

            // verify handshake
            if (handshakeReply.Error != HandshakeError.HandshakeOk)
            {
                Logger.LogWarning($"Handshake error: {handshakeReply.Error}.");
                return null;
            }

            if (await _handshakeProvider.ValidateHandshakeAsync(handshakeReply.Handshake) !=
                HandshakeValidationResult.Ok)
            {
                Logger.LogWarning($"Connect error: {handshakeReply}.");
                await client.Channel.ShutdownAsync();
                return null;
            }

            var peer = new GrpcPeer(client, remoteEndPoint, new PeerConnectionInfo
            {
                Pubkey = handshakeReply.Handshake.HandshakeData.Pubkey.ToHex(),
                ConnectionTime = TimestampHelper.GetUtcNow(),
                ProtocolVersion = handshakeReply.Handshake.HandshakeData.Version,
                SessionId = handshakeReply.Handshake.SessionId.ToByteArray(),
                IsInbound = false,
            });

            peer.UpdateLastReceivedHandshake(handshakeReply.Handshake);
            
            peer.InboundSessionId = handshake.SessionId.ToByteArray();
            peer.UpdateLastSentHandshake(handshake);

            return peer;
        }

        /// <summary>
        /// Calls the server side DoHandshake RPC method, in order to establish a 2-way connection.
        /// </summary>
        /// <returns>The reply from the server.</returns>
        private async Task<HandshakeReply> CallDoHandshakeAsync(GrpcClient client, IPEndPoint remoteEndPoint,
            Handshake handshake)
        {
            HandshakeReply handshakeReply;

            try
            {
                var metadata = new Metadata
                {
                    {GrpcConstants.RetryCountMetadataKey, "0"},
                    {GrpcConstants.TimeoutMetadataKey, (NetworkOptions.PeerDialTimeoutInMilliSeconds * 2).ToString()}
                };

                handshakeReply =
                    await client.Client.DoHandshakeAsync(new HandshakeRequest {Handshake = handshake}, metadata);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, $"Could not connect to {remoteEndPoint}.");
                await client.Channel.ShutdownAsync();
                throw;
            }

            return handshakeReply;
        }

        public async Task<GrpcPeer> DialBackPeerAsync(IPEndPoint endpoint, Handshake handshake)
        {
            var client = CreateClient(endpoint);
            await PingNodeAsync(client, endpoint);

            var peer = new GrpcPeer(client, endpoint, new PeerConnectionInfo
            {
                Pubkey = handshake.HandshakeData.Pubkey.ToHex(),
                ConnectionTime = TimestampHelper.GetUtcNow(),
                SessionId = handshake.SessionId.ToByteArray(),
                ProtocolVersion = handshake.HandshakeData.Version,
                IsInbound = true
            });

            peer.UpdateLastReceivedHandshake(handshake);

            return peer;
        }

        /// <summary>
        /// Checks that the distant node is reachable by pinging it.
        /// </summary>
        /// <returns>The reply from the server.</returns>
        private async Task PingNodeAsync(GrpcClient client, IPEndPoint ipAddress)
        {
            try
            {
                var metadata = new Metadata
                {
                    {GrpcConstants.RetryCountMetadataKey, "0"},
                    {GrpcConstants.TimeoutMetadataKey, NetworkOptions.PeerDialTimeoutInMilliSeconds.ToString()}
                };

                await client.Client.PingAsync(new PingRequest(), metadata);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, $"Could not ping {ipAddress}.");
                await client.Channel.ShutdownAsync();
                throw;
            }
        }

        /// <summary>
        /// Creates a channel/client pair with the appropriate options and interceptors.
        /// </summary>
        /// <returns>A tuple of the channel and client</returns>
        private GrpcClient CreateClient(IPEndPoint endpoint)
        {
            var certificate = RetrieveServerCertificate(endpoint);

            var rsaKeyPair = TlsHelper.GenerateRsaKeyPair();
            var clientCertificate = TlsHelper.GenerateCertificate(new X509Name("CN=" + GrpcConstants.DefaultTlsCommonName),
                new X509Name("CN=" + GrpcConstants.DefaultTlsCommonName), rsaKeyPair.Private, rsaKeyPair.Public);
            var clientKeyCertificatePair = new KeyCertificatePair(TlsHelper.ObjectToPem(clientCertificate), TlsHelper.ObjectToPem(rsaKeyPair.Private));

            var channelCredentials = new SslCredentials(TlsHelper.ObjectToPem(certificate), clientKeyCertificatePair);

            var channel = new Channel(endpoint.ToString(), channelCredentials, new List<ChannelOption>
            {
                new ChannelOption(ChannelOptions.MaxSendMessageLength, GrpcConstants.DefaultMaxSendMessageLength),
                new ChannelOption(ChannelOptions.MaxReceiveMessageLength, GrpcConstants.DefaultMaxReceiveMessageLength),
                new ChannelOption(ChannelOptions.SslTargetNameOverride, GrpcConstants.DefaultTlsCommonName)
            });

            var nodePubkey = AsyncHelper.RunSync(() => _accountService.GetPublicKeyAsync()).ToHex();

            var interceptedChannel = channel.Intercept(metadata =>
            {
                metadata.Add(GrpcConstants.PubkeyMetadataKey, nodePubkey);
                return metadata;
            }).Intercept(new RetryInterceptor());

            var client = new PeerService.PeerServiceClient(interceptedChannel);

            return new GrpcClient(channel, client);
        }
        
        private X509Certificate RetrieveServerCertificate(IPEndPoint endpoint)
        {
            try
            {
                var client = new TcpClient(endpoint.Address.ToString(), endpoint.Port);

                using (var sslStream = new SslStream(client.GetStream(), true, (a, b, c, d) => true))
                {
                    sslStream.AuthenticateAsClient(endpoint.Address.ToString());
                    
                    if (sslStream.RemoteCertificate == null)
                        throw new PeerDialException($"Certificate from {endpoint} is null");
                    
                    return FromX509Certificate(sslStream.RemoteCertificate);
                }
            }
            catch (Exception ex)
            {
                throw new PeerDialException($"Could not retrieve certificate from {endpoint}", ex);
            }
        }
        
        public static X509Certificate FromX509Certificate(
            System.Security.Cryptography.X509Certificates.X509Certificate x509Cert)
        {
            return new X509CertificateParser().ReadCertificate(x509Cert.GetRawCertData());
        }
    }
}