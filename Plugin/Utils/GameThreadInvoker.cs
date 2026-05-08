using System;
using System.Threading.Tasks;
using Torch.API;

namespace TorchDiscordSync.Plugin.Utils
{
    /// <summary>
    /// Minimal helper for routing game API work onto Torch's game thread.
    /// </summary>
    public sealed class GameThreadInvoker
    {
        private readonly ITorchBase _torch;

        public GameThreadInvoker(ITorchBase torch)
        {
            _torch = torch;
        }

        public Task RunAsync(Action action, string caller)
        {
            if (action == null)
                return Task.CompletedTask;

            if (_torch == null)
            {
                action();
                return Task.CompletedTask;
            }

            return _torch.InvokeAsync(action, caller);
        }

        public Task<T> RunAsync<T>(Func<T> func, string caller)
        {
            if (func == null)
                return Task.FromResult(default(T));

            if (_torch == null)
                return Task.FromResult(func());

            return _torch.InvokeAsync(func, caller);
        }
    }
}
