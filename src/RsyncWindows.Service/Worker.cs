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

using System.Net;
using RsyncWindows.Core.Daemon;
using RsyncWindows.Transports;

namespace RsyncWindows.Service;

/// <summary>
/// Hosts the rsync daemon: loads rsyncd.conf-shaped config from <see cref="ConfigPath"/>,
/// starts a <see cref="TcpDaemonServerTransport"/> on its configured port, and dispatches every
/// accepted connection to <see cref="DaemonConnectionHandler"/>. Reloads the config file each
/// time it changes on disk (checked once a second) rather than requiring a service restart --
/// picking up an edited module list or a newly added user is a common enough admin action that
/// requiring `Restart-Service` for it would be an unnecessary rough edge, and reloading a small
/// text file is cheap enough to just poll for.
/// </summary>
public class Worker(ILogger<Worker> logger) : BackgroundService
{
    public static string ConfigPath => RsyncdConfig.DefaultConfigPath;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        EnsureConfigExists();

        RsyncdConfig config = LoadConfigSafely();
        DateTime lastWrite = SafeGetLastWriteTime();
        TcpDaemonServerTransport? transport = null;

        try
        {
            transport = StartTransport(config);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(1000, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                DateTime currentWrite = SafeGetLastWriteTime();
                if (currentWrite == lastWrite)
                    continue;

                lastWrite = currentWrite;
                logger.LogInformation("rsyncd.conf changed on disk, reloading");
                var newConfig = LoadConfigSafely();

                // Rebuild the listener with a fresh handler closure bound to the new config
                // (simpler and safer than mutating a shared config reference the handler reads
                // mid-connection) -- necessary even when the port is unchanged, since modules,
                // hosts allow/deny, and auth users all live in that closure too.
                await transport.StopAsync();
                transport.Dispose();
                transport = StartTransport(newConfig);
                config = newConfig;
            }
        }
        finally
        {
            if (transport != null)
            {
                await transport.StopAsync();
                transport.Dispose();
            }
        }
    }

    private TcpDaemonServerTransport StartTransport(RsyncdConfig config)
    {
        var handler = new DaemonConnectionHandler(config, msg => logger.LogInformation("{Message}", msg));
        var endpoint = new IPEndPoint(IPAddress.Any, config.Port);
        var transport = new TcpDaemonServerTransport(endpoint, handler.Handle, msg => logger.LogWarning("{Message}", msg));
        try
        {
            transport.Start();
            logger.LogInformation("rsync daemon listening on port {Port} with {ModuleCount} module(s): {Modules}",
                config.Port, config.Modules.Count, string.Join(", ", config.Modules.Select(m => m.Name)));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "failed to start listener on port {Port} -- check the port isn't in use and (for ports < 1024) that this process has permission to bind it", config.Port);
            throw;
        }
        return transport;
    }

    private RsyncdConfig LoadConfigSafely()
    {
        try
        {
            return RsyncdConfig.Load(ConfigPath);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "failed to parse {ConfigPath} -- falling back to an empty module list", ConfigPath);
            return new RsyncdConfig();
        }
    }

    private DateTime SafeGetLastWriteTime()
    {
        try
        {
            return File.GetLastWriteTimeUtc(ConfigPath);
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    private void EnsureConfigExists()
    {
        if (File.Exists(ConfigPath))
            return;

        string? dir = Path.GetDirectoryName(ConfigPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        logger.LogInformation("no config found, writing a starter config to {ConfigPath}", ConfigPath);
        File.WriteAllText(ConfigPath, StarterConfig);
    }

    private const string StarterConfig = """
        # rsyncWindows daemon config -- rsyncd.conf-shaped.
        # port = 873

        # [example]
        #     path = C:\Shares\example
        #     comment = Example module
        #     read only = yes
        #     list = yes
        #     hosts allow = 192.168.1.0/24
        #     auth users = someuser
        #     secrets file = C:\ProgramData\rsyncWindows\rsyncd.secrets

        """;
}
