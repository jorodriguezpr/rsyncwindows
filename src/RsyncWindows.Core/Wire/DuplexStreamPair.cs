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

namespace RsyncWindows.Core.Wire;

/// <summary>
/// A transport connection as two independent streams. This is deliberately not a single
/// bidirectional <see cref="Stream"/>: an SSH child process gives us two distinct
/// <c>BaseStream</c> objects (stdin/stdout), while a TCP daemon connection gives us one
/// <see cref="System.Net.Sockets.NetworkStream"/> referenced twice. Modeling both cases the
/// same way is what lets <c>RsyncWindows.Core</c> stay transport-agnostic.
/// </summary>
public sealed record DuplexStreamPair(Stream Input, Stream Output);
