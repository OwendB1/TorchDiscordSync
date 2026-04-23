using System;
using System.Threading;
using System.Threading.Tasks;
using TorchDiscordSync.DiscordHost.Logging;

namespace TorchDiscordSync.DiscordHost
{
    internal static class Program
    {
        private static async Task<int> Main(string[] args)
        {
            try
            {
                string pipeName = null;
                string pluginDirectory = null;
                var parentProcessId = 0;

                for (var i = 0; i < args.Length; i++)
                {
                    switch (args[i])
                    {
                        case "--pipe":
                            pipeName = i + 1 < args.Length ? args[++i] : null;
                            break;
                        case "--parent-pid":
                            if (i + 1 < args.Length)
                                int.TryParse(args[++i], out parentProcessId);
                            break;
                        case "--plugin-dir":
                            pluginDirectory = i + 1 < args.Length ? args[++i] : null;
                            break;
                    }
                }

                HostLogger.Initialize(pluginDirectory);

                if (string.IsNullOrWhiteSpace(pipeName))
                {
                    HostLogger.Error("Missing required --pipe argument.");
                    return 2;
                }

                using (var cancellationSource = new CancellationTokenSource())
                {
                    Console.CancelKeyPress += delegate(object sender, ConsoleCancelEventArgs eventArgs)
                    {
                        eventArgs.Cancel = true;
                        cancellationSource.Cancel();
                    };

                    var server = new DiscordHostServer(pipeName, parentProcessId);
                    return await server.RunAsync(cancellationSource.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                HostLogger.Error("Discord host crashed: " + ex);
                return 1;
            }
        }
    }
}
