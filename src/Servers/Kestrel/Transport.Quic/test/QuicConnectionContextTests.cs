// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Net.Http;
using System.Net.Quic;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.FunctionalTests;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Quic.Internal;
using Microsoft.AspNetCore.Testing;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.AspNetCore.Server.Kestrel.Transport.Quic.Tests
{
    public class QuicConnectionContextTests : TestApplicationErrorLoggerLoggedTest
    {
        private static readonly byte[] TestData = Encoding.UTF8.GetBytes("Hello world");

        [ConditionalFact]
        [MsQuicSupported]
        public async Task AcceptAsync_ClientStartsAndStopsUnidirectionStream_ServerAccepts()
        {
            // Arrange
            await using var connectionListener = await QuicTestHelpers.CreateConnectionListenerFactory(LoggerFactory);

            var options = QuicTestHelpers.CreateClientConnectionOptions(connectionListener.EndPoint);
            using var quicConnection = new QuicConnection(QuicImplementationProviders.MsQuic, options);
            await quicConnection.ConnectAsync().DefaultTimeout();

            await using var serverConnection = await connectionListener.AcceptAndAddFeatureAsync().DefaultTimeout();

            // Act
            var acceptTask = serverConnection.AcceptAsync();

            await using var clientStream = quicConnection.OpenUnidirectionalStream();
            await clientStream.WriteAsync(TestData);

            await using var serverStream = await acceptTask.DefaultTimeout();

            // Assert
            Assert.NotNull(serverStream);
            Assert.False(serverStream.ConnectionClosed.IsCancellationRequested);

            var closedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            serverStream.ConnectionClosed.Register(() => closedTcs.SetResult());

            // Read data from client.
            var read = await serverStream.Transport.Input.ReadAtLeastAsync(TestData.Length).DefaultTimeout();
            Assert.Equal(TestData, read.Buffer.ToArray());
            serverStream.Transport.Input.AdvanceTo(read.Buffer.End);

            // Shutdown client.
            clientStream.Shutdown();

            // Receive shutdown on server.
            read = await serverStream.Transport.Input.ReadAsync().DefaultTimeout();
            Assert.True(read.IsCompleted);

            await closedTcs.Task.DefaultTimeout();
        }

        [ConditionalFact]
        [MsQuicSupported]
        public async Task AcceptAsync_ClientStartsAndStopsBidirectionStream_ServerAccepts()
        {
            // Arrange
            await using var connectionListener = await QuicTestHelpers.CreateConnectionListenerFactory(LoggerFactory);

            var options = QuicTestHelpers.CreateClientConnectionOptions(connectionListener.EndPoint);
            using var quicConnection = new QuicConnection(QuicImplementationProviders.MsQuic, options);
            await quicConnection.ConnectAsync().DefaultTimeout();

            var serverConnection = await connectionListener.AcceptAndAddFeatureAsync().DefaultTimeout();

            // Act
            var acceptTask = serverConnection.AcceptAsync();

            await using var clientStream = quicConnection.OpenBidirectionalStream();
            await clientStream.WriteAsync(TestData);

            await using var serverStream = await acceptTask.DefaultTimeout();
            await serverStream.Transport.Output.WriteAsync(TestData);

            // Assert
            Assert.NotNull(serverStream);
            Assert.False(serverStream.ConnectionClosed.IsCancellationRequested);

            var closedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            serverStream.ConnectionClosed.Register(() => closedTcs.SetResult());

            // Read data from client.
            var read = await serverStream.Transport.Input.ReadAtLeastAsync(TestData.Length).DefaultTimeout();
            Assert.Equal(TestData, read.Buffer.ToArray());
            serverStream.Transport.Input.AdvanceTo(read.Buffer.End);

            // Read data from server.
            var data = new List<byte>();
            var buffer = new byte[1024];
            var readCount = 0;
            while ((readCount = await clientStream.ReadAsync(buffer).DefaultTimeout()) != -1)
            {
                data.AddRange(buffer.AsMemory(0, readCount).ToArray());
                if (data.Count == TestData.Length)
                {
                    break;
                }
            }
            Assert.Equal(TestData, data);

            // Shutdown from client.
            clientStream.Shutdown();

            // Get shutdown from client.
            read = await serverStream.Transport.Input.ReadAsync().DefaultTimeout();
            Assert.True(read.IsCompleted);

            await closedTcs.Task.DefaultTimeout();
        }

        [ConditionalFact]
        [MsQuicSupported]
        public async Task AcceptAsync_ServerStartsAndStopsUnidirectionStream_ClientAccepts()
        {
            // Arrange
            await using var connectionListener = await QuicTestHelpers.CreateConnectionListenerFactory(LoggerFactory);

            var options = QuicTestHelpers.CreateClientConnectionOptions(connectionListener.EndPoint);
            using var quicConnection = new QuicConnection(QuicImplementationProviders.MsQuic, options);
            await quicConnection.ConnectAsync().DefaultTimeout();

            var serverConnection = await connectionListener.AcceptAndAddFeatureAsync().DefaultTimeout();

            // Act
            var acceptTask = quicConnection.AcceptStreamAsync();

            await using var serverStream = await serverConnection.ConnectAsync();
            await serverStream.Transport.Output.WriteAsync(TestData).DefaultTimeout();

            await using var clientStream = await acceptTask.DefaultTimeout();

            // Assert
            Assert.NotNull(clientStream);

            // Read data from server.
            var data = new List<byte>();
            var buffer = new byte[1024];
            var readCount = 0;
            while ((readCount = await clientStream.ReadAsync(buffer).DefaultTimeout()) != -1)
            {
                data.AddRange(buffer.AsMemory(0, readCount).ToArray());
                if (data.Count == TestData.Length)
                {
                    break;
                }
            }
            Assert.Equal(TestData, data);

            // Complete server.
            await serverStream.Transport.Output.CompleteAsync().DefaultTimeout();

            // Receive complete in client.
            readCount = await clientStream.ReadAsync(buffer).DefaultTimeout();
            Assert.Equal(0, readCount);
        }

        [ConditionalFact]
        [MsQuicSupported]
        public async Task StreamPool_StreamAbortedOnServer_NotPooled()
        {
            // Arrange
            await using var connectionListener = await QuicTestHelpers.CreateConnectionListenerFactory(LoggerFactory);

            var options = QuicTestHelpers.CreateClientConnectionOptions(connectionListener.EndPoint);
            using var clientConnection = new QuicConnection(QuicImplementationProviders.MsQuic, options);
            await clientConnection.ConnectAsync().DefaultTimeout();

            await using var serverConnection = await connectionListener.AcceptAndAddFeatureAsync().DefaultTimeout();

            var testHeartbeatFeature = new TestHeartbeatFeature();
            serverConnection.Features.Set<IConnectionHeartbeatFeature>(testHeartbeatFeature);

            // Act & Assert
            var quicConnectionContext = Assert.IsType<QuicConnectionContext>(serverConnection);
            Assert.Equal(0, quicConnectionContext.StreamPool.Count);

            var clientStream = clientConnection.OpenBidirectionalStream();
            await clientStream.WriteAsync(TestData, endStream: true).DefaultTimeout();
            var serverStream = await serverConnection.AcceptAsync().DefaultTimeout();
            var readResult = await serverStream.Transport.Input.ReadAtLeastAsync(TestData.Length).DefaultTimeout();
            serverStream.Transport.Input.AdvanceTo(readResult.Buffer.End);

            // Input should be completed.
            readResult = await serverStream.Transport.Input.ReadAsync();
            Assert.True(readResult.IsCompleted);

            // Complete reading and then abort.
            await serverStream.Transport.Input.CompleteAsync();
            serverStream.Abort(new ConnectionAbortedException("Test message"));

            var quicStreamContext = Assert.IsType<QuicStreamContext>(serverStream);

            // Both send and receive loops have exited.
            await quicStreamContext._processingTask.DefaultTimeout();

            await quicStreamContext.DisposeAsync();

            Assert.Equal(0, quicConnectionContext.StreamPool.Count);
        }

        [ConditionalFact]
        [MsQuicSupported]
        public async Task StreamPool_StreamAbortedOnClient_NotPooled()
        {
            // Arrange
            await using var connectionListener = await QuicTestHelpers.CreateConnectionListenerFactory(LoggerFactory);

            var options = QuicTestHelpers.CreateClientConnectionOptions(connectionListener.EndPoint);
            using var clientConnection = new QuicConnection(QuicImplementationProviders.MsQuic, options);
            await clientConnection.ConnectAsync().DefaultTimeout();

            await using var serverConnection = await connectionListener.AcceptAndAddFeatureAsync().DefaultTimeout();

            var testHeartbeatFeature = new TestHeartbeatFeature();
            serverConnection.Features.Set<IConnectionHeartbeatFeature>(testHeartbeatFeature);

            // Act & Assert
            var quicConnectionContext = Assert.IsType<QuicConnectionContext>(serverConnection);
            Assert.Equal(0, quicConnectionContext.StreamPool.Count);

            var clientStream = clientConnection.OpenBidirectionalStream();
            await clientStream.WriteAsync(TestData).DefaultTimeout();

            var serverStream = await serverConnection.AcceptAsync().DefaultTimeout();
            var readResult = await serverStream.Transport.Input.ReadAtLeastAsync(TestData.Length).DefaultTimeout();
            serverStream.Transport.Input.AdvanceTo(readResult.Buffer.End);

            clientStream.AbortWrite((long)Http3ErrorCode.InternalError);

            // Receive abort form client.
            var ex = await Assert.ThrowsAsync<ConnectionResetException>(() => serverStream.Transport.Input.ReadAsync().AsTask()).DefaultTimeout();
            Assert.Equal("Stream aborted by peer (258).", ex.Message);
            Assert.Equal((long)Http3ErrorCode.InternalError, ((QuicStreamAbortedException)ex.InnerException).ErrorCode);

            // Complete reading and then abort.
            await serverStream.Transport.Input.CompleteAsync();
            await serverStream.Transport.Output.CompleteAsync();

            var quicStreamContext = Assert.IsType<QuicStreamContext>(serverStream);

            // Both send and receive loops have exited.
            await quicStreamContext._processingTask.DefaultTimeout();

            await quicStreamContext.DisposeAsync();

            Assert.Equal(0, quicConnectionContext.StreamPool.Count);
        }

        [ConditionalFact]
        [MsQuicSupported]
        public async Task StreamPool_StreamAbortedOnClientAndServer_NotPooled()
        {
            // Arrange
            await using var connectionListener = await QuicTestHelpers.CreateConnectionListenerFactory(LoggerFactory);

            var options = QuicTestHelpers.CreateClientConnectionOptions(connectionListener.EndPoint);
            using var clientConnection = new QuicConnection(QuicImplementationProviders.MsQuic, options);
            await clientConnection.ConnectAsync().DefaultTimeout();

            await using var serverConnection = await connectionListener.AcceptAndAddFeatureAsync().DefaultTimeout();

            var testHeartbeatFeature = new TestHeartbeatFeature();
            serverConnection.Features.Set<IConnectionHeartbeatFeature>(testHeartbeatFeature);

            // Act & Assert
            var quicConnectionContext = Assert.IsType<QuicConnectionContext>(serverConnection);
            Assert.Equal(0, quicConnectionContext.StreamPool.Count);

            var clientStream = clientConnection.OpenBidirectionalStream();
            await clientStream.WriteAsync(TestData).DefaultTimeout();

            var serverStream = await serverConnection.AcceptAsync().DefaultTimeout();
            var readResult = await serverStream.Transport.Input.ReadAtLeastAsync(TestData.Length).DefaultTimeout();
            serverStream.Transport.Input.AdvanceTo(readResult.Buffer.End);

            clientStream.AbortWrite((long)Http3ErrorCode.InternalError);

            // Receive abort form client.
            var serverEx = await Assert.ThrowsAsync<ConnectionResetException>(() => serverStream.Transport.Input.ReadAsync().AsTask()).DefaultTimeout();
            Assert.Equal("Stream aborted by peer (258).", serverEx.Message);
            Assert.Equal((long)Http3ErrorCode.InternalError, ((QuicStreamAbortedException)serverEx.InnerException).ErrorCode);

            serverStream.Features.Get<IProtocolErrorCodeFeature>().Error = (long)Http3ErrorCode.RequestRejected;
            serverStream.Abort(new ConnectionAbortedException("Test message."));

            // Complete server.
            await serverStream.Transport.Input.CompleteAsync();
            await serverStream.Transport.Output.CompleteAsync();

            var buffer = new byte[1024];
            var clientEx = await Assert.ThrowsAsync<QuicStreamAbortedException>(() => clientStream.ReadAsync(buffer).AsTask()).DefaultTimeout();
            Assert.Equal((long)Http3ErrorCode.RequestRejected, clientEx.ErrorCode);

            var quicStreamContext = Assert.IsType<QuicStreamContext>(serverStream);

            // Both send and receive loops have exited.
            await quicStreamContext._processingTask.DefaultTimeout();

            await quicStreamContext.DisposeAsync();

            Assert.Equal(0, quicConnectionContext.StreamPool.Count);
        }

        [ConditionalFact]
        [MsQuicSupported]
        public async Task StreamPool_Heartbeat_ExpiredStreamRemoved()
        {
            // Arrange
            var now = new DateTimeOffset(2021, 7, 6, 12, 0, 0, TimeSpan.Zero);
            var testSystemClock = new TestSystemClock { UtcNow = now };
            await using var connectionListener = await QuicTestHelpers.CreateConnectionListenerFactory(LoggerFactory, testSystemClock);

            var options = QuicTestHelpers.CreateClientConnectionOptions(connectionListener.EndPoint);
            using var clientConnection = new QuicConnection(QuicImplementationProviders.MsQuic, options);
            await clientConnection.ConnectAsync().DefaultTimeout();

            await using var serverConnection = await connectionListener.AcceptAndAddFeatureAsync().DefaultTimeout();

            var testHeartbeatFeature = new TestHeartbeatFeature();
            serverConnection.Features.Set<IConnectionHeartbeatFeature>(testHeartbeatFeature);

            // Act & Assert
            var quicConnectionContext = Assert.IsType<QuicConnectionContext>(serverConnection);
            Assert.Equal(0, quicConnectionContext.StreamPool.Count);

            var stream1 = await QuicTestHelpers.CreateAndCompleteBidirectionalStreamGracefully(clientConnection, serverConnection);

            Assert.Equal(1, quicConnectionContext.StreamPool.Count);
            QuicStreamContext pooledStream = quicConnectionContext.StreamPool._array[0];
            Assert.Same(stream1, pooledStream);
            Assert.Equal(now.Ticks + QuicConnectionContext.StreamPoolExpiryTicks, pooledStream.PoolExpirationTicks);

            now = now.AddMilliseconds(100);
            testSystemClock.UtcNow = now;
            testHeartbeatFeature.RaiseHeartbeat();
            // Not removed.
            Assert.Equal(1, quicConnectionContext.StreamPool.Count);

            var stream2 = await QuicTestHelpers.CreateAndCompleteBidirectionalStreamGracefully(clientConnection, serverConnection);

            Assert.Equal(1, quicConnectionContext.StreamPool.Count);
            pooledStream = quicConnectionContext.StreamPool._array[0];
            Assert.Same(stream1, pooledStream);
            Assert.Equal(now.Ticks + QuicConnectionContext.StreamPoolExpiryTicks, pooledStream.PoolExpirationTicks);

            Assert.Same(stream1, stream2);

            now = now.AddTicks(QuicConnectionContext.StreamPoolExpiryTicks);
            testSystemClock.UtcNow = now;
            testHeartbeatFeature.RaiseHeartbeat();
            // Not removed.
            Assert.Equal(1, quicConnectionContext.StreamPool.Count);

            now = now.AddTicks(1);
            testSystemClock.UtcNow = now;
            testHeartbeatFeature.RaiseHeartbeat();
            // Removed.
            Assert.Equal(0, quicConnectionContext.StreamPool.Count);
        }

        [ConditionalFact]
        [MsQuicSupported]
        public async Task StreamPool_ManyConcurrentStreams_StreamPoolFull()
        {
            using var httpEventListener = new EventSourceListener(TestOutputHelper);

            const int StreamsSent = 10;
            const int StreamsPooled = 10;

            // Arrange
            await using var connectionListener = await QuicTestHelpers.CreateConnectionListenerFactory(LoggerFactory);

            var options = QuicTestHelpers.CreateClientConnectionOptions(connectionListener.EndPoint);
            using var clientConnection = new QuicConnection(QuicImplementationProviders.MsQuic, options);
            await clientConnection.ConnectAsync().DefaultTimeout();

            await using var serverConnection = await connectionListener.AcceptAndAddFeatureAsync().DefaultTimeout();

            var testHeartbeatFeature = new TestHeartbeatFeature();
            serverConnection.Features.Set<IConnectionHeartbeatFeature>(testHeartbeatFeature);

            // Act
            var quicConnectionContext = Assert.IsType<QuicConnectionContext>(serverConnection);
            Assert.Equal(0, quicConnectionContext.StreamPool.Count);

            var pauseCompleteTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var allConnectionsOnServerTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var streamTasks = new List<Task>();
            var requestState = new RequestState(clientConnection, serverConnection, allConnectionsOnServerTcs, pauseCompleteTcs.Task);

            for (var i = 0; i < StreamsSent; i++)
            {
                // TODO: Race condition in QUIC library.
                // Delay between sending streams to avoid
                // https://github.com/dotnet/runtime/issues/55249
                //await Task.Delay(100);
                streamTasks.Add(SendStream(requestState, Logger, i + 1));
            }

            await allConnectionsOnServerTcs.Task.DefaultTimeout();
            pauseCompleteTcs.SetResult();

            await Task.WhenAll(streamTasks).DefaultTimeout();

            // Assert
            // Up to 100 streams are pooled.
            Assert.Equal(StreamsPooled, quicConnectionContext.StreamPool.Count);

            static async Task SendStream(RequestState requestState, ILogger logger, int requestCount)
            {
                //logger.LogInformation($"{requestCount} Sending stream");
                var clientStream = requestState.QuicConnection.OpenBidirectionalStream();
                await clientStream.WriteAsync(TestData, endStream: true).DefaultTimeout();

                //logger.LogInformation($"{requestCount} Accepting stream on server");
                var serverStream = await requestState.ServerConnection.AcceptAsync().DefaultTimeout();

                //logger.LogInformation($"{requestCount} Accepting reading stream data on server");
                var readResult = await serverStream.Transport.Input.ReadAtLeastAsync(TestData.Length).DefaultTimeout();
                serverStream.Transport.Input.AdvanceTo(readResult.Buffer.End);

                // Input should be completed.
                readResult = await serverStream.Transport.Input.ReadAsync();
                Assert.True(readResult.IsCompleted);

                lock (requestState)
                {
                    requestState.ActiveConcurrentConnections++;
                    //logger.LogInformation($"{requestCount} Current count on server is {requestState.ActiveConcurrentConnections}");
                    if (requestState.ActiveConcurrentConnections == StreamsSent)
                    {
                        requestState.AllConnectionsOnServerTcs.SetResult();
                    }
                }

                //logger.LogInformation($"{requestCount} Waiting for all streams to have reached server");
                await requestState.PauseCompleteTask;

                // Complete reading and writing.
                await serverStream.Transport.Input.CompleteAsync();
                await serverStream.Transport.Output.CompleteAsync();

                var quicStreamContext = Assert.IsType<QuicStreamContext>(serverStream);

                // Both send and receive loops have exited.
                await quicStreamContext._processingTask.DefaultTimeout();

                //logger.LogInformation($"{requestCount} Disposing stream");
                await quicStreamContext.DisposeAsync();
            }
        }

        private sealed class EventSourceListener : EventListener
        {
            private readonly StringBuilder _messageBuilder = new StringBuilder();
            private readonly ITestOutputHelper _output;

            public EventSourceListener(ITestOutputHelper output)
            {
                _output = output;
            }

            protected override void OnEventSourceCreated(EventSource eventSource)
            {
                base.OnEventSourceCreated(eventSource);

                if (eventSource.Name.Contains("System.Net.Quic") ||
                    eventSource.Name.Contains("System.Net.Http"))
                {
                    EnableEvents(eventSource, EventLevel.LogAlways, EventKeywords.All);
                }
            }

            protected override void OnEventWritten(EventWrittenEventArgs eventData)
            {
                base.OnEventWritten(eventData);

                string message;
                lock (_messageBuilder)
                {
                    _messageBuilder.Append("<- Event ");
                    _messageBuilder.Append(eventData.EventSource.Name);
                    _messageBuilder.Append(" - ");
                    _messageBuilder.Append(eventData.EventName);
                    _messageBuilder.Append(" : ");
                    _messageBuilder.AppendJoin(',', eventData.Payload!);
                    _messageBuilder.Append(" ->");
                    message = _messageBuilder.ToString();
                    _messageBuilder.Clear();
                }
                _output.WriteLine(message);
            }

            public override string ToString()
            {
                return _messageBuilder.ToString();
            }
        }

        [ConditionalFact]
        [MsQuicSupported]
        public async Task PersistentState_StreamsReused_StatePersisted()
        {
            // Arrange
            await using var connectionListener = await QuicTestHelpers.CreateConnectionListenerFactory(LoggerFactory);

            var options = QuicTestHelpers.CreateClientConnectionOptions(connectionListener.EndPoint);
            using var clientConnection = new QuicConnection(QuicImplementationProviders.MsQuic, options);
            await clientConnection.ConnectAsync().DefaultTimeout();

            await using var serverConnection = await connectionListener.AcceptAndAddFeatureAsync().DefaultTimeout();

            // Act
            var clientStream1 = clientConnection.OpenBidirectionalStream();
            await clientStream1.WriteAsync(TestData, endStream: true).DefaultTimeout();
            var serverStream1 = await serverConnection.AcceptAsync().DefaultTimeout();
            var readResult1 = await serverStream1.Transport.Input.ReadAtLeastAsync(TestData.Length).DefaultTimeout();
            serverStream1.Transport.Input.AdvanceTo(readResult1.Buffer.End);

            serverStream1.Features.Get<IPersistentStateFeature>().State["test"] = true;

            // Input should be completed.
            readResult1 = await serverStream1.Transport.Input.ReadAsync();
            Assert.True(readResult1.IsCompleted);

            // Complete reading and writing.
            await serverStream1.Transport.Input.CompleteAsync();
            await serverStream1.Transport.Output.CompleteAsync();

            var quicStreamContext1 = Assert.IsType<QuicStreamContext>(serverStream1);
            await quicStreamContext1._processingTask.DefaultTimeout();
            await quicStreamContext1.DisposeAsync();

            var clientStream2 = clientConnection.OpenBidirectionalStream();
            await clientStream2.WriteAsync(TestData, endStream: true).DefaultTimeout();
            var serverStream2 = await serverConnection.AcceptAsync().DefaultTimeout();
            var readResult2 = await serverStream2.Transport.Input.ReadAtLeastAsync(TestData.Length).DefaultTimeout();
            serverStream2.Transport.Input.AdvanceTo(readResult2.Buffer.End);

            object state = serverStream2.Features.Get<IPersistentStateFeature>().State["test"];

            // Input should be completed.
            readResult2 = await serverStream2.Transport.Input.ReadAsync();
            Assert.True(readResult2.IsCompleted);

            // Complete reading and writing.
            await serverStream2.Transport.Input.CompleteAsync();
            await serverStream2.Transport.Output.CompleteAsync();

            var quicStreamContext2 = Assert.IsType<QuicStreamContext>(serverStream2);
            await quicStreamContext2._processingTask.DefaultTimeout();
            await quicStreamContext2.DisposeAsync();

            Assert.Same(quicStreamContext1, quicStreamContext2);

            var quicConnectionContext = Assert.IsType<QuicConnectionContext>(serverConnection);
            Assert.Equal(1, quicConnectionContext.StreamPool.Count);

            Assert.Equal(true, state);
        }

        private record RequestState(
            QuicConnection QuicConnection,
            MultiplexedConnectionContext ServerConnection,
            TaskCompletionSource AllConnectionsOnServerTcs,
            Task PauseCompleteTask)
        {
            public int ActiveConcurrentConnections { get; set; }
        };

        private class TestSystemClock : ISystemClock
        {
            public DateTimeOffset UtcNow { get; set; }
        }

        private class TestHeartbeatFeature : IConnectionHeartbeatFeature
        {
            private readonly List<(Action<object> Action, object State)> _actions = new List<(Action<object>, object)>();

            public void OnHeartbeat(Action<object> action, object state)
            {
                _actions.Add((action, state));
            }

            public void RaiseHeartbeat()
            {
                foreach (var a in _actions)
                {
                    a.Action(a.State);
                }
            }
        }
    }
}
