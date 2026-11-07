namespace VendingMachines.Api.Services;

public sealed class ShutdownWriteRequestGate
{
    private readonly object _sync = new();
    private bool _stopping;
    private int _activeRequests;
    private TaskCompletionSource _drained = CompletedTask();

    public int ActiveRequestCount
    {
        get { lock (_sync) return _activeRequests; }
    }

    public IDisposable? TryBeginWriteRequest()
    {
        lock (_sync)
        {
            if (_stopping) return null;
            if (_activeRequests++ == 0)
                _drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return new Admission(this);
        }
    }

    public void StopAccepting()
    {
        lock (_sync) _stopping = true;
    }

    public Task WaitForDrainAsync(CancellationToken cancellationToken)
    {
        Task drained;
        lock (_sync) drained = _drained.Task;
        return drained.WaitAsync(cancellationToken);
    }

    private void EndRequest()
    {
        lock (_sync)
        {
            if (--_activeRequests == 0)
                _drained.TrySetResult();
        }
    }

    private static TaskCompletionSource CompletedTask()
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        completed.SetResult();
        return completed;
    }

    private sealed class Admission(ShutdownWriteRequestGate owner) : IDisposable
    {
        private ShutdownWriteRequestGate? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.EndRequest();
    }
}
