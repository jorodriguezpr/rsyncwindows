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

using System.IO.Pipelines;
using RsyncWindows.Core.Wire;

namespace RsyncWindows.Transports;

/// <summary>
/// Connects two in-process <see cref="DuplexStreamPair"/>s via a pair of in-memory pipes —
/// no real process/socket involved. This is the harness Phases 1-3 use to run both sides of a
/// sync as two Tasks in a single test process before any SSH/TCP transport code exists.
/// </summary>
public static class LoopbackDuplexTransport
{
    public static (DuplexStreamPair Left, DuplexStreamPair Right) CreatePair()
    {
        var leftToRight = new Pipe();
        var rightToLeft = new Pipe();

        var left = new DuplexStreamPair(
            Input: rightToLeft.Reader.AsStream(),
            Output: leftToRight.Writer.AsStream());

        var right = new DuplexStreamPair(
            Input: leftToRight.Reader.AsStream(),
            Output: rightToLeft.Writer.AsStream());

        return (left, right);
    }
}
