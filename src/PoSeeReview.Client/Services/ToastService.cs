namespace PoSeeReview.Client.Services;

/// <summary>The one transient confirmation, rendered by the <c>Toast</c> component in MainLayout.</summary>
public sealed class ToastService
{
    private int _version;
    private TaskCompletionSource<bool>? _undo;

    public string? Message { get; private set; }

    /// <summary>True while the showing toast offers Undo.</summary>
    public bool CanUndo => _undo is not null;

    public event Action? Changed;

    public void Show(string message)
    {
        Supersede();
        var version = ++_version;
        Message = message;
        Changed?.Invoke();
        _ = ClearAfterAsync(version, TimeSpan.FromSeconds(3));
    }

    /// <summary>
    /// Shows <paramref name="message"/> with an Undo button for <paramref name="window"/>.
    /// True if the user undid it; false once the window passes or another toast replaces it, which
    /// is when the caller should actually do the thing.
    /// </summary>
    public Task<bool> ShowUndoableAsync(string message, TimeSpan window)
    {
        Supersede();
        var version = ++_version;
        var undo = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _undo = undo;
        Message = message;
        Changed?.Invoke();
        _ = ClearAfterAsync(version, window);
        return undo.Task;
    }

    /// <summary>The toast's Undo button.</summary>
    public void Undo()
    {
        var undo = _undo;
        _undo = null;
        _version++;
        Message = null;
        undo?.TrySetResult(true);
        Changed?.Invoke();
    }

    /// <summary>A pending undoable action goes ahead when another toast takes its place.</summary>
    private void Supersede()
    {
        var undo = _undo;
        _undo = null;
        undo?.TrySetResult(false);
    }

    private async Task ClearAfterAsync(int version, TimeSpan after)
    {
        await Task.Delay(after);

        // A newer toast owns the element now; its own timer clears it.
        if (version == _version)
        {
            Supersede();
            Message = null;
            Changed?.Invoke();
        }
    }
}
