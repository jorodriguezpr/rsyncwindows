// rsyncWindows
// Developer: Jose Rodriguez Arroyo
// Email: jrpcone@gmail.com
// GitHub: https://github.com/jorodriguezpr
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System.Text;
using RsyncWindows.Core.Wire;
using RsyncWindows.Transports;
using Xunit;

namespace RsyncWindows.Core.Tests.Wire;

public class ProtocolNegotiatorTests
{
    [Fact]
    public async Task Negotiate_BothSidesAgreeOnParameters()
    {
        var (left, right) = LoopbackDuplexTransport.CreatePair();

        var clientTask = Task.Run(() => ProtocolNegotiator.Negotiate(left, isServer: false));
        var serverTask = Task.Run(() => ProtocolNegotiator.Negotiate(right, isServer: true));

        var client = await clientTask;
        var server = await serverTask;

        Assert.Equal(ProtocolNegotiator.ProtocolVersion, client.ProtocolVersion);
        Assert.Equal(ProtocolNegotiator.ProtocolVersion, server.ProtocolVersion);
        Assert.Equal(server.CompatFlags, client.CompatFlags);
        Assert.Equal(server.ChecksumSeed, client.ChecksumSeed);
        Assert.True(client.ProperSeedOrder);
        Assert.True(client.CompatFlags.HasFlag(CompatFlags.VarintFlistFlags));
    }

    [Fact]
    public async Task Negotiate_BothSidesRequestCompression_BothEndUpCompressionEnabled()
    {
        var (left, right) = LoopbackDuplexTransport.CreatePair();

        var clientTask = Task.Run(() => ProtocolNegotiator.Negotiate(left, isServer: false, compress: true));
        var serverTask = Task.Run(() => ProtocolNegotiator.Negotiate(right, isServer: true, compress: true));

        var client = await clientTask;
        var server = await serverTask;

        Assert.True(client.CompressionEnabled);
        Assert.True(server.CompressionEnabled);
    }

    [Fact]
    public async Task Negotiate_NeitherSideRequestsCompression_CompressionStaysDisabled()
    {
        var (left, right) = LoopbackDuplexTransport.CreatePair();

        var clientTask = Task.Run(() => ProtocolNegotiator.Negotiate(left, isServer: false));
        var serverTask = Task.Run(() => ProtocolNegotiator.Negotiate(right, isServer: true));

        var client = await clientTask;
        var server = await serverTask;

        Assert.False(client.CompressionEnabled);
        Assert.False(server.CompressionEnabled);
    }

    [Fact]
    public async Task PostNegotiation_DataFramesAndSideChannelMessagesDemuxCorrectly()
    {
        var (left, right) = LoopbackDuplexTransport.CreatePair();
        var receivedMessages = new List<(MsgCode Code, byte[] Payload)>();

        var clientTask = Task.Run(() => ProtocolNegotiator.Negotiate(left, isServer: false));
        var serverTask = Task.Run(() => ProtocolNegotiator.Negotiate(
            right, isServer: true, onMessage: (code, payload) => receivedMessages.Add((code, payload))));

        var client = await clientTask;
        var server = await serverTask;

        // Client sends an MSG_INFO side-channel message, then some MSG_DATA, on the same stream.
        var infoText = Encoding.UTF8.GetBytes("hello from client");
        client.Output.WriteMessage(MsgCode.Info, infoText);
        var payload = Encoding.UTF8.GetBytes("the actual transfer bytes");
        client.Output.Write(payload);
        client.Output.Flush();

        // Server's Read() must see only the MSG_DATA payload...
        var buf = new byte[payload.Length];
        int totalRead = 0;
        while (totalRead < buf.Length)
        {
            int n = server.Input.Read(buf, totalRead, buf.Length - totalRead);
            Assert.True(n > 0, "stream ended before all data was read");
            totalRead += n;
        }
        Assert.Equal(payload, buf);

        // ...while the MSG_INFO frame was dispatched to the side channel, not mixed into the data.
        var info = Assert.Single(receivedMessages);
        Assert.Equal(MsgCode.Info, info.Code);
        Assert.Equal(infoText, info.Payload);
    }
}
