namespace Ovi.Screenshot.Views;

// UI-thread-owned serialization and an explicit idle boundary for preview shutdown.
internal sealed class PreviewOperationGate
{
    private Operation? _active;
    private TaskCompletionSource? _idle;
    internal bool IsBusy => _active is not null;
    internal bool IsAccepting { get; private set; } = true;

    internal Operation? TryBegin()
    {
        if (!IsAccepting || IsBusy) return null;
        _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return _active = new Operation(this);
    }

    internal void StopAccepting() => IsAccepting = false;
    internal Task WaitForIdleAsync() => _idle?.Task ?? Task.CompletedTask;

    private void Complete(Operation operation)
    {
        if (!ReferenceEquals(_active, operation)) return;
        _active = null;
        _idle!.TrySetResult();
    }

    internal sealed class Operation(PreviewOperationGate owner) : IDisposable
    {
        private PreviewOperationGate? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Complete(this);
    }

    internal static async Task VerifySyntheticAsync()
    {
        var gate = new PreviewOperationGate();
        var operation = gate.TryBegin() ?? throw new InvalidOperationException("A fresh preview rejected an operation.");
        Task idle = gate.WaitForIdleAsync();
        if (!gate.IsBusy || idle.IsCompleted || gate.TryBegin() is not null)
            throw new InvalidOperationException("Preview operations are not serialized.");
        gate.StopAccepting();
        if (gate.TryBegin() is not null || idle.IsCompleted)
            throw new InvalidOperationException("Shutdown discarded an active preview operation.");
        operation.Dispose(); operation.Dispose();
        await idle;
        if (gate.IsBusy || gate.TryBegin() is not null)
            throw new InvalidOperationException("Preview operation completion re-enabled shutdown input.");
    }
}
